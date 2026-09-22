'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { randomBytes } = require('node:crypto');
const { runTagScenarios, inventoryTables } = require('./tag-scenarios.cjs');
const { runSourceScenarios, runFormatScenarios } = require('./source-scenarios.cjs');
const { runImportScenario, TAG_IMPORT_PROVENANCE } = require('./tag-import-fixture.cjs');
const { loadResume } = require('./resume-report.cjs');

const READS = ['list_tia_processes', 'get_status', 'list_devices', 'get_device', 'list_blocks',
  'get_block', 'list_udts', 'get_udt', 'list_tag_tables', 'get_tag_table', 'get_cross_references'];
const WRITES = ['write_blocks', 'write_udts', 'create_tag_table', 'create_tag', 'create_user_constant',
  'set_tag_entry_attribute', 'delete_tag_entry', 'import_tag_tables'];
const TOOLS = [...READS, ...WRITES];
const now = () => new Date().toISOString();
const canonical = value => path.win32.normalize(value).toLowerCase();

function walk(nodes) {
  return (nodes || []).flatMap(node => [node, ...walk(node.children)]);
}

function optionsFrom(argv) {
  const known = new Set(['process-id', 'project-path', 'plc-object-id', 'endpoint', 'address', 'output', 'timeout-ms', 'resume-report']);
  const values = {};
  for (let index = 0; index < argv.length; index++) {
    const key = argv[index].replace(/^--/, '');
    if (key === 'help') return { help: true };
    if (!argv[index].startsWith('--') || !known.has(key) || Object.hasOwn(values, key) || !argv[index + 1] || argv[index + 1].startsWith('--'))
      throw new Error('Supply unique --name value arguments; use --help for usage.');
    values[key] = argv[++index];
  }
  const processId = Number(values['process-id']);
  assert.ok(Number.isInteger(processId) && processId > 0 && processId <= 2147483647, '--process-id is required.');
  assert.ok(values['project-path'] && path.win32.isAbsolute(values['project-path']), '--project-path must be the expected absolute disposable project path.');
  const endpoint = values.endpoint || 'http://127.0.0.1:5000/mcp';
  const url = new URL(endpoint);
  assert.ok(url.protocol === 'http:' && ['127.0.0.1', 'localhost'].includes(url.hostname) &&
    url.pathname === '/mcp' && !url.username && !url.password && !url.search && !url.hash,
  'Use the existing local HTTP /mcp endpoint.');
  const timeoutMs = Number(values['timeout-ms'] || 120000);
  assert.ok(Number.isInteger(timeoutMs) && timeoutMs >= 1000 && timeoutMs <= 600000, '--timeout-ms must be 1000..600000.');
  if (values.address) assert.match(values.address, /^%M\d+\.[0-7]$/, '--address must be a memory Bool address, such as %M0.0.');
  return { processId, projectPath: values['project-path'], plcObjectId: values['plc-object-id'], endpoint,
    logicalAddress: values.address, output: values.output, timeoutMs, resumeReport: values['resume-report'] };
}

// Reserve whole bytes occupied by existing memory tags, including overlapping W/D
// addresses. Unknown memory syntax fails closed; no address implies an empty project.
function unusedAddress(tags, requested) {
  const occupied = new Set();
  for (const tag of tags) {
    const address = tag.logicalAddress;
    if (typeof address !== 'string' || !/^%?M/i.test(address)) continue;
    const match = /^%?M(?:([BWD])?(\d+)(?:\.([0-7]))?)$/i.exec(address);
    assert.ok(match, `Cannot determine occupied memory for ${address}.`);
    const width = { B: 1, W: 2, D: 4 }[(match[1] || '').toUpperCase()] || 1;
    for (let byte = Number(match[2]); byte < Number(match[2]) + width; byte++) occupied.add(byte);
  }
  if (requested) {
    assert.match(requested, /^%M\d+\.[0-7]$/);
    assert.ok(!occupied.has(Number(/^%M(\d+)/.exec(requested)[1])), 'The requested memory byte overlaps an existing tag.');
    return requested;
  }
  for (let byte = 0; byte < 256; byte++) if (!occupied.has(byte)) return `%M${byte}.0`;
  throw new Error('No unused memory byte found in M0..M255; supply --address in the disposable PLC range.');
}

function successful({ result, payload }, name) {
  assert.equal(result.isError, false, `${name} returned MCP isError: ${JSON.stringify(payload)}`);
  assert.ok(payload && typeof payload === 'object', `${name} needs a JSON payload.`);
  assert.ok(Array.isArray(payload.errors), `${name} must return errors.`);
  assert.equal(payload.errors.length, 0, `${name} returned errors: ${JSON.stringify(payload.errors)}`);
  assert.notEqual(payload.complete, false, `${name} returned incomplete data.`);
  if (name !== 'list_tia_processes' && (name !== 'get_status' || Object.hasOwn(payload, 'processId')))
    assert.equal(payload.complete, true, `${name} must explicitly return complete:true.`);
  assert.ok(Number.isFinite(Date.parse(payload.readAtUtc)), `${name} must include readAtUtc.`);
  if (WRITES.includes(name)) {
    assert.equal(payload.complete, true);
    assert.equal(payload.operation, name);
    assert.equal(payload.saved, false);
    assert.equal(payload.cleanupFailed, false, 'Native temporary cleanup failed.');
    assert.ok(Array.isArray(payload.affectedObjects));
  }
  return payload;
}

function targetStatus(payload, options) {
  assert.equal(payload.processId, options.processId);
  assert.equal(payload.state, 'connected', 'Connect the disposable project manually in the dashboard first.');
  assert.equal(payload.writeToolsAvailable, true, 'The server must publish full access for native write acceptance.');
  assert.equal(typeof payload.project?.path, 'string', 'The connected process must retain a primary project.');
  assert.equal(canonical(payload.project.path), canonical(options.projectPath), 'The connected project does not match --project-path.');
}

function assertFixtureReference(sources, tagId, blockId) {
  assert.ok(Array.isArray(sources), 'Cross-reference sources must be readable.');
  const tagSources = walk(sources).filter(source => source.objectId === tagId);
  assert.ok(tagSources.length, 'Cross-reference source must identify the fixture tag.');
  const references = tagSources.flatMap(source => [source, ...walk(source.children)])
    .flatMap(source => source.references || []).filter(reference => reference.objectId === blockId);
  assert.ok(references.some(reference => (reference.locations || []).some(location =>
    location.referenceType === 'UsedBy' && String(location.access).split(',').map(value => value.trim()).includes('Read'))),
  'The fixture tag must have a UsedBy/Read reference to the fixture FC, not unrelated matching IDs.');
}

function prepareOutput(output) {
  for (const name of ['report.json', 'report.json.tmp', 'report.md'])
    assert.ok(!fs.existsSync(path.join(output, name)), 'This output directory already contains acceptance evidence. Choose a new directory.');
  fs.mkdirSync(output, { recursive: true });
}

function createContext(options, report, persist = () => {}, fetchImpl = fetch) {
  let nextId = 0;
  const ctx = { ...options, prefix: report.prefix, fixture: report.fixture, phase: null };
  ctx.rpc = async (method, params) => {
    const request = { jsonrpc: '2.0', id: ++nextId, method, params };
    const trace = { number: nextId, step: ctx.phase, startedAtUtc: now(), request };
    report.calls.push(trace);
    persist(); // Record intent before transmission, including writes with uncertain outcomes.
    try {
      const response = await fetchImpl(options.endpoint, { method: 'POST', redirect: 'error',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
        body: JSON.stringify(request), signal: AbortSignal.timeout(options.timeoutMs) });
      trace.httpStatus = response.status;
      trace.responseText = await response.text();
      assert.equal(response.status, 200, `MCP returned HTTP ${response.status}.`);
      const envelope = JSON.parse(trace.responseText);
      assert.equal(envelope.jsonrpc, '2.0');
      assert.equal(envelope.id, request.id, 'MCP response ID mismatch.');
      assert.ok(!envelope.error, `JSON-RPC error: ${JSON.stringify(envelope.error)}`);
      assert.ok(envelope.result, 'MCP response has no result.');
      return envelope.result;
    } catch (error) {
      trace.transportError = error.message;
      if (WRITES.includes(params?.name)) report.uncertainWrite = true;
      throw error;
    } finally { trace.finishedAtUtc = now(); persist(); }
  };
  const invoke = async (name, args) => {
    assert.ok(TOOLS.includes(name), `The acceptance runner cannot call ${name}.`);
    if (Object.hasOwn(args, 'processId')) assert.equal(args.processId, options.processId, 'Cross-process request refused.');
    if (name !== 'list_tia_processes' && name !== 'get_status') assert.equal(args.processId, options.processId);
    const result = await ctx.rpc('tools/call', { name, arguments: args });
    assert.ok(Array.isArray(result.content));
    const text = result.content.find(item => item.type === 'text')?.text;
    assert.equal(typeof text, 'string', 'The MCP tool must return its JSON envelope as text.');
    const payload = JSON.parse(text);
    if (Object.hasOwn(args, 'processId')) assert.equal(payload.processId, args.processId, 'Tool response processId mismatch.');
    if (WRITES.includes(name)) {
      report.writesAttempted = true;
      for (const object of payload.affectedObjects || []) report.observedAffectedObjects.push({ call: report.calls.length, ...object });
      if (payload.error?.reconnectRequired || ['reconnectRequired', 'notConnected'].includes(payload.error?.code)) report.uncertainWrite = true;
    }
    persist();
    return { result, payload };
  };
  ctx.rawCall = async (name, args = {}) => {
    if (WRITES.includes(name)) {
      targetStatus(successful(await invoke('get_status', { processId: options.processId }), 'get_status'), options);
      report.writesAttempted = true;
      persist();
    }
    try { return await invoke(name, args); }
    catch (error) {
      // A response that cannot be decoded/correlated is just as uncertain as a
      // lost HTTP response after a write. Never turn it into a safe retry.
      if (WRITES.includes(name)) { report.uncertainWrite = true; persist(); }
      throw error;
    }
  };
  ctx.call = async (name, args = {}) => {
    const response = await ctx.rawCall(name, args);
    try { return successful(response, name); }
    catch (error) { error.toolName = name; error.toolResponse = response; throw error; }
  };
  ctx.step = async (id, description, action) => {
    const step = { id, description, status: 'running', startedAtUtc: now() };
    report.steps.push(step); ctx.phase = id; persist();
    try { step.details = await action(); step.status = 'passed'; }
    catch (error) { step.status = error.code === 'compileRequired' ? 'blocked' : error.code === 'nativeExportUnavailable' ? 'unavailable' : 'failed'; step.error = error.message; throw error; }
    finally { step.finishedAtUtc = now(); ctx.phase = null; persist(); options.onProgress?.(step); }
  };
  return ctx;
}

async function preflight(ctx, report) {
  await ctx.step('preflight', 'Verify publication, manually connected target, CPU and unused fixture address.', async () => {
    const initialized = await ctx.rpc('initialize', { protocolVersion: '2025-03-26', capabilities: {},
      clientInfo: { name: 'tia-native-acceptance', version: '1' } });
    report.serverInfo = initialized.serverInfo;
    const listing = await ctx.rpc('tools/list', {});
    assert.deepEqual(listing.tools.map(tool => tool.name).sort(), [...TOOLS].sort(), 'Expected the current nineteen published tools.');
    report.toolDefinitions = listing.tools;
    await ctx.call('get_status');
    const discovery = await ctx.call('list_tia_processes');
    const selected = discovery.processes.find(process => process.processId === ctx.processId);
    assert.ok(selected?.connectedByMcp, 'The selected TIA process must already be connected by the user.');
    const status = await ctx.call('get_status', { processId: ctx.processId });
    targetStatus(status, ctx); report.before = status;
    const devices = await ctx.call('list_devices', { processId: ctx.processId });
    const cpus = [];
    for (const device of walk(devices.roots).filter(node => node.kind === 'device')) {
      assert.ok(device.objectId, 'A Device identity is unavailable.');
      const details = await ctx.call('get_device', { processId: ctx.processId, objectId: device.objectId });
      for (const item of walk(details.deviceItems)) if (item.plcObjectId) cpus.push({ name: item.name, path: item.path, objectId: item.plcObjectId });
    }
    const unique = [...new Map(cpus.map(cpu => [cpu.objectId, cpu])).values()];
    report.availableCpus = unique;
    if (!ctx.plcObjectId) {
      assert.equal(unique.length, 1, 'Multiple/no PLC CPUs found. Select one with --plc-object-id from report.availableCpus.');
      ctx.plcObjectId = unique[0].objectId;
    }
    assert.ok(unique.some(cpu => cpu.objectId === ctx.plcObjectId), '--plc-object-id must identify a discovered CPU.');
    report.plcObjectId = ctx.plcObjectId;
    if (ctx.importMatrixMode) {
      if (ctx.resumeMode) return { project: status.project, plcObjectId: ctx.plcObjectId, resumed: true };
    } else if (ctx.resumeMode) {
      const table = await ctx.call('get_tag_table', { processId: ctx.processId, objectId: ctx.fixture.tableId });
      assert.equal(table.metadata.name, ctx.fixture.tableName);
      assert.ok(table.entries?.tags.some(tag => tag.objectId === ctx.fixture.tagId && tag.name === ctx.fixture.tagName),
        'The retained fixture tag must still belong to the recorded table.');
      return { project: status.project, plcObjectId: ctx.plcObjectId, resumed: true };
    }
    const tags = [];
    const tables = inventoryTables(await ctx.call('list_tag_tables', { processId: ctx.processId, plcObjectId: ctx.plcObjectId }));
    for (const table of tables) {
      const detail = await ctx.call('get_tag_table', { processId: ctx.processId, objectId: table.objectId });
      assert.ok(Array.isArray(detail.entries?.tags), 'All existing tag addresses must be readable.');
      tags.push(...detail.entries.tags);
    }
    ctx.logicalAddress = unusedAddress(tags, ctx.logicalAddress);
    report.logicalAddress = ctx.logicalAddress;
    return { project: status.project, plcObjectId: ctx.plcObjectId, logicalAddress: ctx.logicalAddress };
  });
}

function markdown(report) {
  const clean = text => String(text || '').replaceAll('|', '\\|').replaceAll('\n', ' ');
  return `# Native MCP acceptance\n\nResult: **${report.status}**\n\nProject: ${report.projectPath}\n\nProcess: ${report.processId}; fixture prefix: ${report.prefix}\n\n` +
    (report.basedOn ? `Continues: ${report.basedOn.path}\n\nPreviously passed scenarios remain in that evidence; they were not rerun.\n\n` : '') +
    '| Scenario | Result | Details |\n|---|---|---|\n' + report.steps.map(step => `| ${step.id} | ${step.status} | ${clean(step.error || step.description)} |`).join('\n') +
    `\n\nUncalled tools: ${report.uncalledTools?.join(', ') || 'none'}\n\n` +
    (report.gaps.length ? 'Unverified coverage:\n\n' + report.gaps.map(gap => `- ${gap.id}: ${gap.reason}`).join('\n') + '\n\n' : '') +
    '## Fixture and evidence\n\n```json\n' + JSON.stringify(report.fixture, null, 2) + '\n```\n\n' +
    'Requests and raw responses are in report.json. A passed scenario verifies its stated engineering readback only, not PLC runtime behavior.\n\n' +
    (report.writesAttempted ? 'Test objects may remain in this disposable project. Whole-block, UDT and table deletion are not published. No automatic save, compile, download, project close or rollback was performed.\n' : 'No native write was attempted.\n') +
    (report.uncertainWrite ? '\nA write outcome is uncertain. Do not automatically rerun this run; inspect the project and recorded response first.\n' : '');
}

async function run(options, dependencies = {}) {
  const resume = loadResume(options, WRITES);
  const prefix = resume?.prior.prefix || 'McpAT_' + randomBytes(6).toString('hex');
  const output = path.resolve(options.output || path.join(__dirname, '../../test-results/native-acceptance', now().replace(/[:.]/g, '-') + '_' + prefix));
  prepareOutput(output);
  const report = { schemaVersion: 1, status: 'running', startedAtUtc: now(), endpoint: options.endpoint,
    processId: options.processId, projectPath: options.projectPath, prefix, fixture: resume ? { ...resume.prior.fixture } : {}, steps: [], calls: [], gaps: [],
    observedAffectedObjects: [], writesAttempted: false, uncertainWrite: false };
  if (resume) {
    report.basedOn = { path: resume.file, sha256: resume.sha256 };
    report.inheritedPassedSteps = resume.inherited;
    report.inheritedCalledTools = [...new Set([...(resume.prior.inheritedCalledTools || []),
      ...resume.prior.calls.map(call => call.request.params?.name).filter(name => TOOLS.includes(name))])];
  }
  const persist = () => {
    fs.writeFileSync(path.join(output, 'report.json.tmp'), JSON.stringify(report, null, 2));
    fs.renameSync(path.join(output, 'report.json.tmp'), path.join(output, 'report.json'));
    fs.writeFileSync(path.join(output, 'report.md'), markdown(report));
  };
  const ctx = createContext({ ...options, plcObjectId: resume?.prior.plcObjectId || options.plcObjectId,
    resumeMode: !!resume, deferFormats: true,
    onProgress: dependencies.onProgress || (step => console.log(`${step.status.toUpperCase()}: ${step.id}`)) }, report, persist, dependencies.fetchImpl);
  ctx.completedStepIds = resume?.completed;
  ctx.gaps = report.gaps;
  ctx.beforeFormats = async () => {
    await ctx.step('cross-references', 'Verify the consistent fixture tag has a UsedBy/Read reference to the FC before format imports.', async () => {
      const references = await ctx.call('get_cross_references', { processId: ctx.processId, objectId: ctx.fixture.tagId });
      assertFixtureReference(references.sources, ctx.fixture.tagId, ctx.fixture.blockId);
    });
  };
  try {
    await preflight(ctx, report);
    if (!resume) {
      await (dependencies.runTags || runTagScenarios)(ctx);
      report.tagImportFixture = TAG_IMPORT_PROVENANCE;
      await (dependencies.runImport || runImportScenario)(ctx);
      await (dependencies.runSources || runSourceScenarios)(ctx);
    }
    await (dependencies.runFormats || runFormatScenarios)(ctx);
    await ctx.step('final-status', 'Verify the same disposable project remains connected.', async () => {
      report.after = await ctx.call('get_status', { processId: ctx.processId });
      targetStatus(report.after, ctx);
    });
    report.status = report.gaps.length ? 'incomplete' : 'passed';
  } catch (error) { report.status = error.code === 'compileRequired' ? 'blocked' : 'failed'; report.error = error.message; }
  finally {
    const called = new Set([...(report.inheritedCalledTools || []), ...report.calls.map(call => call.request.params?.name)]);
    report.uncalledTools = TOOLS.filter(name => !called.has(name));
    if (report.status === 'passed' && report.uncalledTools.length) report.status = 'incomplete';
    report.finishedAtUtc = now(); persist();
  }
  return { report, output };
}

module.exports = { TOOLS, WRITES, optionsFrom, unusedAddress, successful, targetStatus, assertFixtureReference, prepareOutput, createContext, preflight, markdown, run };
