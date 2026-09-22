'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createHash, randomBytes } = require('node:crypto');
const { createContext, preflight, prepareOutput, targetStatus, WRITES } = require('./runner.cjs');
const { buildExternalFixtures, buildSdFixtures, checkReadback } = require('./import-fixtures.cjs');
const { buildXmlFixtures, checkXmlReadback } = require('./xml-import-fixtures.cjs');

const now = () => new Date().toISOString();
const digest = value => createHash('sha256').update(typeof value === 'string' ? value : JSON.stringify(value), 'utf8').digest('hex');
const walk = nodes => (nodes || []).flatMap(node => [node, ...walk(node.children)]);
const canonical = value => path.win32.normalize(value).toLowerCase();
const kindTools = kind => kind === 'udt' ? { list: 'list_udts', read: 'get_udt', write: 'write_udts' }
  : kind === 'tagTable' ? { list: 'list_tag_tables', read: 'get_tag_table', write: 'import_tag_tables' }
    : { list: 'list_blocks', read: 'get_block', write: 'write_blocks' };

function describe(spec) {
  const documents = [spec.documents(1), spec.documents(2)];
  assert.notEqual(digest(documents[0]), digest(documents[1]), 'Creation and update must submit different native contents.');
  return { key: spec.key, name: spec.name, kind: spec.kind, blockType: spec.blockType || null,
    language: spec.language || null, format: spec.sourceFormat, readFormat: spec.readFormat,
    nativeOperation: spec.sourceFormat === 'external-source' ? 'ExternalSources.CreateFromFile + GenerateBlocksFromSource(None)'
      : spec.sourceFormat === 'simatic-sd' ? 'ImportFromDocuments(Override)' : 'Import(Override)',
    documentContents: documents.map(bundle => bundle.map(document => ({ name: document.name, sha256: digest(document.content) }))),
    writeTool: kindTools(spec.kind).write, documents, documentSha256: documents.map(digest),
    create: { status: 'not-run' }, update: { status: 'not-run' } };
}

function loadMatrixResume(options) {
  if (!options.resumeReport) return null;
  const file = path.resolve(options.resumeReport);
  const text = fs.readFileSync(file, 'utf8');
  const prior = JSON.parse(text);
  assert.equal(prior.schemaVersion, 2);
  assert.equal(prior.suite, 'native-import-matrix');
  assert.ok(['blocked', 'failed'].includes(prior.status), 'Only a stopped readback can be resumed; failed writes require investigation.');
  assert.equal(prior.uncertainWrite, false);
  assert.equal(prior.processId, options.processId);
  assert.equal(canonical(prior.projectPath), canonical(options.projectPath));
  if (options.plcObjectId) assert.equal(options.plcObjectId, prior.plcObjectId);
  assert.match(prior.prefix, /^McpIM_[a-f0-9]{12}$/);
  assert.ok(prior.matrix.length > 0 && prior.matrix.some(row => [row.create, row.update].some(stage => ['compile-required', 'failed'].includes(stage.status))));
  for (const step of prior.steps.filter(item => item.status !== 'passed')) {
    assert.ok(/\.(?:create|update)\.readback$/.test(step.id), 'Only a stopped source/entry readback can be resumed.');
    assert.ok(!prior.calls.some(call => call.step === step.id && WRITES.includes(call.request.params?.name)),
      'A stopped scenario attempted a write; it cannot be resumed.');
  }
  for (const row of prior.matrix) for (const stage of [row.create, row.update]) {
    assert.ok(['not-run', 'passed', 'compile-required', 'failed'].includes(stage.status), 'Never resume an attempted or failed write.');
    if (stage.status !== 'not-run') {
      assert.equal(stage.writeVerified, true, 'A native write response and affected object must have been verified.');
      assert.equal(typeof stage.objectId, 'string');
    }
  }
  return { file, prior, sha256: digest(text) };
}

function matrixMarkdown(report) {
  const clean = value => String(value || '').replaceAll('|', '\\|').replace(/[\r\n]+/g, ' ');
  const cell = stage => stage.status === 'passed' ? 'passed (read back)' : stage.status;
  return '# Native import acceptance\n\n' + `Result: **${report.status}**\n\nProject: ${report.projectPath}\n\nProcess: ${report.processId}; prefix: ${report.prefix}\n\n` +
    (report.basedOn ? `Continues [previous evidence](${report.basedOn.path.replaceAll('\\', '/')}) (SHA-256 ${report.basedOn.sha256}). Previously verified writes were not repeated.\n\n` : '') +
    '| Import case | Tool | Format / files | Create | Semantic update |\n|---|---|---|---|---|\n' +
    report.matrix.map(row => `| ${row.key} | ${row.writeTool} | ${row.format}: ${row.documents[0].map(document => path.extname(document.name)).join(' + ')} | ${cell(row.create)} | ${cell(row.update)} |`).join('\n') +
    '\n\n' + (report.error ? `Stopped: ${clean(report.error)}\n\n` : '') +
    '## Evidence\n\nEvery matrix cell records the exact submitted documents, checksums, native affected objects, fresh identity and source/entry readback. Raw MCP requests and responses are retained in report.json. A successful write without semantic readback is not a pass.\n\n' +
    '| Step | Result | Detail |\n|---|---|---|\n' + report.steps.map(step => `| ${step.id} | ${step.status} | ${clean(step.error || step.description)} |`).join('\n') +
    '\n\n## Scope\n\nThis matrix covers the listed native import routes and fixture shapes. It does not establish every IEC type, instruction, CPU, unit/group destination, multi-object document, protected/safety block, localized resource payload or PLC runtime behavior. SCL SIMATIC SD is not a required case on this installation; LAD is used for that block route.\n\n' +
    'Unique fixture objects remain for inspection. No save, explicit compile, download, project close or rollback is performed. Only the user connects the project and performs requested offline compilation.\n';
}

async function inventory(ctx, kind) {
  const result = await ctx.call(kindTools(kind).list, { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
  assert.equal(result.plcObjectId, ctx.plcObjectId);
  assert.ok(Array.isArray(result.roots), 'A readable native inventory is required before interpreting object absence.');
  return walk(result.roots).filter(node => node.kind === kind);
}

async function find(ctx, spec) {
  return (await inventory(ctx, spec.kind)).filter(node => node.name?.toLowerCase() === spec.name.toLowerCase());
}

async function owned(ctx, spec, objectId) {
  const found = await find(ctx, spec);
  assert.equal(found.length, 1, `Expected exactly one fixture ${spec.name}.`);
  assert.equal(found[0].name, spec.name);
  assert.equal(found[0].objectId, objectId, 'A same-name replacement is not this run\'s owned fixture.');
  const detail = await ctx.call(kindTools(spec.kind).read, { processId: ctx.processId, objectId, includePath: false,
    ...(spec.kind === 'tagTable' ? { includeEntries: false } : { includeSource: false }) });
  assert.equal(detail.metadata.objectId, objectId);
  assert.equal(detail.metadata.name, spec.name);
  if (spec.kind === 'block' && spec.blockType) assert.equal(detail.metadata.blockType, spec.blockType);
  if (spec.language && spec.kind === 'block') assert.equal(detail.metadata.programmingLanguage, spec.language);
  return detail;
}

function sourceChecksums(read, format) {
  assert.equal(read.source?.format, format, 'Explicit readback must not fall back to another representation.');
  assert.equal(read.source.dependenciesIncluded, false);
  assert.ok(read.source.documents.length > 0);
  for (const document of read.source.documents) assert.deepEqual(document.checksum, {
    algorithm: 'sha-256', scope: 'returned-content', encoding: 'utf-8-no-bom', value: digest(document.content)
  });
}

function isCompileCheckpoint(error, spec, objectId) {
  const payload = error.toolResponse?.payload;
  return payload?.metadata?.objectId === objectId && payload.metadata.name === spec.name &&
    payload.metadata.state?.isConsistent === false && payload.source === null && !payload.error &&
    payload.complete === false && payload.errors?.length > 0 && payload.errors.every(item =>
      item.origin === 'tia-openness' && item.operation === 'sourceExport' && item.format === spec.readFormat);
}

async function verify(ctx, spec, stage, version) {
  await owned(ctx, spec, stage.objectId);
  const read = await ctx.call(kindTools(spec.kind).read, { processId: ctx.processId, objectId: stage.objectId,
    includePath: false, ...(spec.kind === 'tagTable' ? { includeEntries: true } : {
      includeSource: true, sourceFormat: spec.readFormat, includeDependencies: false }) });
  assert.equal(read.metadata.objectId, stage.objectId);
  assert.equal(read.metadata.name, spec.name);
  if (spec.kind !== 'tagTable') sourceChecksums(read, spec.readFormat);
  (spec.sourceFormat === 'simatic-ml' ? checkXmlReadback : checkReadback)(spec,
    spec.kind === 'tagTable' ? read : read.source.documents, version);
  stage.readback = read;
  stage.status = 'passed';
  stage.verifiedAtUtc = now();
  stage.evidenceReport = ctx.evidenceFile;
}

async function executeCase(ctx, spec, row, version) {
  const action = version === 1 ? 'create' : 'update';
  const stage = row[action];
  if (stage.status === 'passed') return;
  if (stage.status === 'not-run') {
    await ctx.step(`${spec.key}.${action}.write`, `${action} ${spec.name} with complete ${spec.sourceFormat} documents`, async () => {
      if (version === 1) assert.equal((await find(ctx, spec)).length, 0, 'Refuse a pre-existing same-name object.');
      else {
        assert.equal(row.create.status, 'passed', 'Creation must pass readback before replacement.');
        await verify(ctx, spec, { ...row.create }, 1); // Recheck owned source immediately before replacement.
      }
      if (spec.kind === 'block' && spec.sourceFormat === 'simatic-ml') {
        const match = /<Number>(\d+)<\/Number>/.exec(row.documents[version - 1][0].content);
        assert.ok(match, 'XML block fixtures must reserve an explicit unused block number.');
        assert.ok((await inventory(ctx, 'block')).every(block => block.blockType !== spec.blockType ||
          block.number !== Number(match[1]) || block.name === spec.name), 'The reserved XML block number is now occupied by another block.');
      }
      // Persisted intent becomes non-resumable before any native mutation.
      stage.status = 'write-attempted';
      stage.evidenceReport = ctx.evidenceFile;
      const result = await ctx.call(kindTools(spec.kind).write, { processId: ctx.processId, plcObjectId: ctx.plcObjectId,
        ...(spec.kind === 'tagTable' ? {} : { sourceFormat: spec.sourceFormat }), documents: row.documents[version - 1] });
      assert.equal(result.format, spec.sourceFormat);
      assert.equal(result.affectedObjects.length, 1, 'These fixtures declare exactly one target object.');
      assert.equal(result.affectedObjects[0].kind, spec.kind);
      assert.equal(result.affectedObjects[0].name, spec.name);
      stage.affectedObjects = result.affectedObjects;
      const found = await find(ctx, spec);
      assert.equal(found.length, 1);
      assert.equal(found[0].name, spec.name);
      assert.ok(found[0].objectId);
      if (result.affectedObjects[0].objectId != null) assert.equal(found[0].objectId, result.affectedObjects[0].objectId);
      stage.objectId = found[0].objectId;
      stage.writeVerified = true;
      stage.status = 'written';
      stage.writeCall = ctx.report.calls.findLast(call => call.request.params?.name === kindTools(spec.kind).write)?.number;
      return { documentsSha256: row.documentSha256[version - 1], objectId: stage.objectId, affectedObjects: stage.affectedObjects };
    });
  }
  try {
    await ctx.step(`${spec.key}.${action}.readback`, `Verify ${action} ${spec.name} through ${spec.readFormat || 'typed tag-table entries'}`, async () => {
      try { await verify(ctx, spec, stage, version); }
      catch (error) {
        if (isCompileCheckpoint(error, spec, stage.objectId)) {
          error.code = 'compileRequired';
          error.message = `Readback of ${spec.name} requires native consistency. Compile PLC software offline in TIA, then resume this report.`;
        }
        throw error;
      }
      return { objectId: stage.objectId, source: stage.readback.source, entries: stage.readback.entries };
    });
  } catch (error) {
    if (error.code !== 'compileRequired') { stage.status = 'failed'; throw error; }
    stage.status = 'compile-required';
    stage.reason = error.message;
    // The write response succeeded. Only a read is pending. Independent cases
    // may run; no write is repeated by the explicit compile continuation.
  }
}

function reserveXmlNumbers(blocks) {
  const numbers = {};
  for (const [key, type] of [['xml-scl-fc', 'FC'], ['xml-lad-fb', 'FB'], ['xml-db', 'DB']]) {
    const used = new Set(blocks.filter(block => block.blockType === type).map(block => block.number));
    let number = 100;
    while (used.has(number)) number++;
    numbers[key] = number;
  }
  return numbers;
}

function validatePlan(matrix, planned) {
  assert.deepEqual(matrix.map(row => [row.key, row.name, row.kind, row.format, row.readFormat, row.documentSha256]),
    planned.map(row => [row.key, row.name, row.kind, row.format, row.readFormat, row.documentSha256]),
    'Fixture contents changed since this report. Do not silently continue a different test plan.');
  assert.deepEqual(matrix.map(row => row.documents), planned.map(row => row.documents),
    'Recorded native documents must match their current authored plan, not only stored hash fields.');
}

async function captureExistingLad(ctx, report, blocks) {
  report.existingLad = [];
  for (const block of blocks.filter(block => block.programmingLanguage === 'LAD' && !/^Mcp(?:AT|IM)_/.test(block.name))) {
    await ctx.step(`existing-lad.${block.name}.exports`, `Read ${block.name} as SIMATIC SD and SimaticML before fixture imports`, async () => {
      const sources = {};
      for (const format of ['simatic-sd', 'simatic-ml']) {
        const read = await ctx.call('get_block', { processId: ctx.processId, objectId: block.objectId,
          sourceFormat: format, includeSource: true, includeDependencies: false, includePath: false });
        sourceChecksums(read, format);
        assert.equal(read.metadata.objectId, block.objectId);
        assert.equal(read.metadata.name, block.name);
        sources[format] = read.source;
      }
      report.existingLad.push({ name: block.name, objectId: block.objectId, sources });
    });
  }
}

async function runImportMatrix(options, dependencies = {}) {
  const resume = loadMatrixResume(options);
  const prefix = resume?.prior.prefix || `McpIM_${randomBytes(6).toString('hex')}`;
  const output = path.resolve(options.output || path.join(__dirname, '../../test-results/native-import-acceptance', `${now().replace(/[:.]/g, '-')}_${prefix}`));
  prepareOutput(output);
  const report = { schemaVersion: 2, suite: 'native-import-matrix', status: 'running', startedAtUtc: now(),
    processId: options.processId, projectPath: options.projectPath, endpoint: options.endpoint, prefix,
    fixture: {}, matrix: [], steps: [], calls: [], gaps: [], observedAffectedObjects: [], writesAttempted: false, uncertainWrite: false };
  if (resume) {
    report.basedOn = { path: resume.file, sha256: resume.sha256 };
    for (const key of ['matrix', 'numbers', 'logicalAddress', 'existingLad']) report[key] = structuredClone(resume.prior[key]);
  }
  const persist = () => {
    fs.writeFileSync(path.join(output, 'report.json.tmp'), JSON.stringify(report, null, 2));
    fs.renameSync(path.join(output, 'report.json.tmp'), path.join(output, 'report.json'));
    fs.writeFileSync(path.join(output, 'report.md'), matrixMarkdown(report));
  };
  const ctx = createContext({ ...options, plcObjectId: resume?.prior.plcObjectId || options.plcObjectId,
    importMatrixMode: true, resumeMode: !!resume, onProgress: dependencies.onProgress || (step => console.log(`${step.status.toUpperCase()}: ${step.id}`)) }, report, persist, dependencies.fetchImpl);
  ctx.report = report;
  ctx.evidenceFile = path.join(output, 'report.json');
  try {
    await preflight(ctx, report);
    if (!resume) {
      const blocks = await inventory(ctx, 'block');
      report.numbers = reserveXmlNumbers(blocks);
      await captureExistingLad(ctx, report, blocks);
    }
    const specs = [...buildExternalFixtures(prefix), ...buildSdFixtures(prefix),
      ...buildXmlFixtures(prefix, report.numbers, report.logicalAddress)];
    const planned = specs.map(describe);
    if (resume) {
      validatePlan(report.matrix, planned);
    } else report.matrix = planned;
    persist();
    // Exercise XML first: imported blocks have explicit reserved numbers and
    // this does not let subsequent auto-number generation consume them.
    const executionOrder = specs.map((spec, index) => ({ spec, row: report.matrix[index] }))
      .sort((a, b) => Number(b.spec.sourceFormat === 'simatic-ml') - Number(a.spec.sourceFormat === 'simatic-ml'));
    for (const version of [1, 2]) {
      for (const { spec, row } of executionOrder) await executeCase(ctx, spec, row, version);
      const pending = report.matrix.filter(row => row[version === 1 ? 'create' : 'update'].status === 'compile-required');
      if (pending.length) {
        const error = new Error(`${pending.length} successful imports await source readback. Compile the selected PLC software offline, then resume this report. No import will be repeated.`);
        error.code = 'compileRequired'; throw error;
      }
    }
    await ctx.step('existing-lad.unchanged', 'Verify the pre-existing LAD source remained unchanged', async () => {
      for (const block of report.existingLad) {
        const read = await ctx.call('get_block', { processId: ctx.processId, objectId: block.objectId,
          sourceFormat: 'simatic-sd', includeSource: true, includeDependencies: false, includePath: false });
        sourceChecksums(read, 'simatic-sd');
        assert.equal(read.metadata.name, block.name);
        assert.deepEqual(read.source.documents, block.sources['simatic-sd'].documents);
      }
    });
    await ctx.step('final-status', 'Verify the original connected disposable project', async () => {
      report.after = await ctx.call('get_status', { processId: ctx.processId }); targetStatus(report.after, ctx);
    });
    assert.ok(report.matrix.every(row => row.create.status === 'passed' && row.update.status === 'passed'));
    report.status = 'passed';
  } catch (error) {
    report.status = error.code === 'compileRequired' ? 'blocked' : 'failed';
    report.error = error.message;
  } finally { report.finishedAtUtc = now(); persist(); }
  return { report, output };
}

module.exports = { describe, loadMatrixResume, validatePlan, matrixMarkdown, reserveXmlNumbers, sourceChecksums, isCompileCheckpoint, executeCase, runImportMatrix };
