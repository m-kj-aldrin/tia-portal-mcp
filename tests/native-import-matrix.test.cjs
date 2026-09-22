'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { buildExternalFixtures } = require('./native-acceptance/import-fixtures.cjs');
const { describe, executeCase, isCompileCheckpoint, matrixMarkdown, reserveXmlNumbers, loadMatrixResume, validatePlan } = require('./native-acceptance/import-matrix.cjs');

const spec = () => ({ ...buildExternalFixtures('McpIM_test')[0], readFormat: 'external-source' });
const checksummed = documents => documents.map(document => ({ ...document, checksum: {
  algorithm: 'sha-256', scope: 'returned-content', encoding: 'utf-8-no-bom',
  value: createHash('sha256').update(document.content).digest('hex') } }));

function context(fixture, { ineffectiveUpdate = false, compileRequired = false, wrongAffected = false } = {}) {
  let current;
  let writes = 0;
  const ctx = { processId: 42, plcObjectId: 'cpu', report: { calls: [] }, evidenceFile: 'evidence.json',
    async step(_id, _description, action) { return action(); },
    async call(name, args) {
      ctx.report.calls.push({ number: ctx.report.calls.length + 1, request: { params: { name } } });
      if (name === 'list_blocks') return { plcObjectId: 'cpu', roots: current ? [current] : [] };
      if (name === 'write_blocks') {
        writes++;
        const documents = current && ineffectiveUpdate ? current.documents : args.documents;
        current = { kind: 'block', name: fixture.name, objectId: `generation-${writes}`, documents };
        return { format: fixture.sourceFormat, affectedObjects: [{ kind: 'block', name: wrongAffected ? 'Unowned' : fixture.name, objectId: current.objectId }] };
      }
      assert.equal(name, 'get_block');
      assert.equal(args.objectId, current.objectId);
      const metadata = { objectId: current.objectId, name: fixture.name, blockType: fixture.blockType,
        programmingLanguage: fixture.language, state: { isConsistent: !ctx.pendingCompile } };
      if (!args.includeSource) return { metadata, source: null };
      if (ctx.pendingCompile) {
        const error = new Error('Native export requires consistency');
        error.toolResponse = { payload: { metadata, source: null, complete: false, errors: [
          { origin: 'tia-openness', operation: 'sourceExport', format: fixture.readFormat } ] } };
        throw error;
      }
      return { metadata, source: { format: fixture.readFormat, dependenciesIncluded: false, documents: checksummed(current.documents) } };
    },
    pendingCompile: compileRequired,
    writes: () => writes,
    replaceIdentity() { current.objectId = 'same-name-user-replacement'; }
  };
  return ctx;
}

test('a compile checkpoint resumes readback without resubmitting a successful import', async () => {
  const fixture = spec(); const row = describe(fixture); const ctx = context(fixture, { compileRequired: true });
  await executeCase(ctx, fixture, row, 1);
  assert.equal(row.create.status, 'compile-required');
  assert.equal(row.create.writeVerified, true);
  assert.equal(ctx.writes(), 1);
  ctx.pendingCompile = false;
  await executeCase(ctx, fixture, row, 1);
  assert.equal(row.create.status, 'passed');
  assert.equal(ctx.writes(), 1);
  await executeCase(ctx, fixture, row, 2);
  assert.equal(row.update.status, 'passed');
  assert.equal(ctx.writes(), 2);
});

test('a successful native replacement with unchanged semantics fails readback', async () => {
  const fixture = spec(); const row = describe(fixture); const ctx = context(fixture, { ineffectiveUpdate: true });
  await executeCase(ctx, fixture, row, 1);
  await assert.rejects(executeCase(ctx, fixture, row, 2));
  assert.equal(row.update.status, 'failed');
  assert.equal(ctx.writes(), 2);
});

test('a same-name replacement cannot be adopted as the owned update target', async () => {
  const fixture = spec(); const row = describe(fixture); const ctx = context(fixture);
  await executeCase(ctx, fixture, row, 1);
  ctx.replaceIdentity();
  await assert.rejects(executeCase(ctx, fixture, row, 2), /owned fixture/);
  assert.equal(ctx.writes(), 1);
});

test('unexpected affected objects fail before any readback pass is recorded', async () => {
  const fixture = spec(); const row = describe(fixture); const ctx = context(fixture, { wrongAffected: true });
  await assert.rejects(executeCase(ctx, fixture, row, 1));
  assert.notEqual(row.create.status, 'passed');
  assert.equal(row.create.writeVerified, undefined);
});

test('transport, context and consistent native-export failures are not compile checkpoints', () => {
  const fixture = spec();
  assert.equal(isCompileCheckpoint(new Error('lost HTTP response'), fixture, 'owned'), false);
  const payload = { metadata: { objectId: 'owned', name: fixture.name, state: { isConsistent: true } },
    source: null, complete: false, errors: [{ origin: 'tia-openness', operation: 'sourceExport', format: fixture.readFormat }] };
  assert.equal(isCompileCheckpoint({ toolResponse: { payload } }, fixture, 'owned'), false);
  payload.metadata.state.isConsistent = false;
  assert.equal(isCompileCheckpoint({ toolResponse: { payload } }, fixture, 'owned'), true);
  payload.error = { code: 'reconnectRequired' };
  assert.equal(isCompileCheckpoint({ toolResponse: { payload } }, fixture, 'owned'), false);
});

test('import report keeps planned but unattempted writes visible and separate from passes', () => {
  const row = describe(spec());
  row.create.status = 'passed';
  const markdown = matrixMarkdown({ status: 'failed', matrix: [row], steps: [] });
  assert.match(markdown, /passed \(read back\) \| not-run/);
  assert.match(markdown, /successful write without semantic readback is not a pass/);
});

test('XML number reservations respect existing numbers independently by block kind', () => {
  assert.deepEqual(reserveXmlNumbers([{ blockType: 'FC', number: 100 }, { blockType: 'FB', number: 101 }]),
    { 'xml-scl-fc': 101, 'xml-lad-fb': 100, 'xml-db': 100 });
});

test('continuation refuses altered document contents even when stored hashes are unchanged', () => {
  const planned = [describe(spec())];
  const matrix = structuredClone(planned);
  matrix[0].documents[1][0].content += '\nFUNCTION "Unowned" : Void\nBEGIN\nEND_FUNCTION';
  assert.throws(() => validatePlan(matrix, planned), /Recorded native documents/);
});

test('explicit continuation accepts failed readback but rejects any failed or uncertain write', t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'tia-import-resume-'));
  const file = path.join(directory, 'report.json');
  t.after(() => { fs.unlinkSync(file); fs.rmdirSync(directory); });
  const row = describe(spec());
  row.create = { status: 'failed', writeVerified: true, objectId: 'owned' };
  const prior = { schemaVersion: 2, suite: 'native-import-matrix', prefix: 'McpIM_aabbccddeeff', status: 'failed', uncertainWrite: false,
    processId: 42, projectPath: 'C:\\Disposable\\Fixture.ap20', matrix: [row],
    steps: [{ id: 'external.scl.fc.create.readback', status: 'failed' }], calls: [] };
  const options = { processId: 42, projectPath: prior.projectPath, resumeReport: file };
  const save = () => fs.writeFileSync(file, JSON.stringify(prior));
  save(); assert.ok(loadMatrixResume(options));
  prior.calls.push({ step: prior.steps[0].id, request: { params: { name: 'write_blocks' } } });
  save(); assert.throws(() => loadMatrixResume(options), /attempted a write/);
  prior.calls = []; prior.uncertainWrite = true;
  save(); assert.throws(() => loadMatrixResume(options));
  prior.uncertainWrite = false; prior.steps[0].id = 'external.scl.fc.create.write';
  save(); assert.throws(() => loadMatrixResume(options), /Only a stopped source/);
});
