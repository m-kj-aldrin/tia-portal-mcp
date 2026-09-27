'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { optionsFrom, unusedAddress, successful, assertFixtureReference, prepareOutput, createContext, preflight, TOOLS, WRITES } = require('./native-acceptance/runner.cjs');
const { CORE_STEPS, loadResume } = require('./native-acceptance/resume-report.cjs');

const options = { processId: 42, projectPath: 'C:\\Disposable\\Fixture.ap20',
  endpoint: 'http://127.0.0.1:5000/mcp', timeoutMs: 1000 };
const status = (projectPath = options.projectPath) => ({ readAtUtc: new Date().toISOString(), processId: 42,
  complete: true, errors: [], state: 'connected', writeToolsAvailable: true, project: { path: projectPath } });
const wrap = (payload, isError = false) => ({ isError, content: [{ type: 'text', text: JSON.stringify(payload) }] });
function setup(handler) {
  const report = { prefix: 'McpAT_test', fixture: {}, steps: [], calls: [], observedAffectedObjects: [] };
  const requests = [];
  const ctx = createContext(options, report, () => {}, async (_url, init) => {
    const request = JSON.parse(init.body); requests.push(request);
    const result = await handler(request);
    return { status: 200, text: async () => JSON.stringify({ jsonrpc: '2.0', id: request.id, result }) };
  });
  return { ctx, report, requests };
}

test('native runner requires explicit target and rejects nonlocal endpoint or duplicate options', () => {
  assert.throws(() => optionsFrom([]), /process-id/);
  assert.throws(() => optionsFrom(['--process-id', '42']), /project-path/);
  const args = ['--process-id', '42', '--project-path', options.projectPath];
  assert.equal(optionsFrom(args).processId, 42);
  assert.throws(() => optionsFrom([...args, '--endpoint', 'https://example.org/mcp']), /local HTTP/);
  assert.throws(() => optionsFrom([...args, '--process-id', '43']), /unique/);
  assert.throws(() => optionsFrom([...args, '--connect', '42']), /unique/);
});

test('runner tool and write inventories match the production publication', () => {
  const sources = directory => fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) return ['bin', 'obj'].includes(entry.name) ? [] : sources(file);
    return entry.name.endsWith('.cs') ? [fs.readFileSync(file, 'utf8')] : [];
  });
  const boundary = sources(path.join(__dirname, '../src/TiaOpennessMcpServer'))
    .find(code => code.includes('internal sealed class McpBoundary'));
  assert.ok(boundary, 'Production MCP boundary is missing.');
  const names = code => [...code.matchAll(/McpT\("([^"]+)"/g)].map(match => match[1]).sort();
  assert.deepEqual([...TOOLS].sort(), names(boundary));
  assert.deepEqual([...WRITES].sort(), names(boundary.slice(boundary.indexOf('if (writesEnabled)'))));
});

test('suite flags retain explicit target requirements and reject duplicate or unknown flags', () => {
  const flags = { 'catalogue-only': 'catalogueOnly' };
  const args = ['--process-id', '42', '--project-path', options.projectPath];
  assert.equal(optionsFrom([...args, '--catalogue-only'], flags).catalogueOnly, true);
  assert.equal(optionsFrom(args, flags).catalogueOnly, false);
  assert.throws(() => optionsFrom(['--catalogue-only'], flags), /process-id/);
  assert.throws(() => optionsFrom([...args, '--catalogue-only', '--catalogue-only'], flags), /unique/);
  assert.throws(() => optionsFrom([...args, '--catalogue-only']), /unique/);
});

const addedWrites = ['create_technology_object', 'set_technology_object_parameters', 'create_group', 'delete_group', 'rename'];
for (const tool of addedWrites) {
  test(`${tool} rejects a changed target before transmission`, async () => {
    const { ctx, report, requests } = setup(() => wrap(status('C:\\Production\\Other.ap20')));
    await assert.rejects(ctx.rawCall(tool, { processId: 42, objectId: 'fixture' }), /does not match/);
    assert.deepEqual(requests.map(request => request.params.name), ['get_status']);
    assert.equal(report.writesAttempted, undefined);
  });

  test(`${tool} records uncertain transport and refuses retry or later cleanup writes`, async () => {
    const { ctx, report, requests } = setup(request => {
      if (request.params.name === 'get_status') return wrap(status());
      throw new Error('Response lost after transmission.');
    });
    await assert.rejects(ctx.rawCall(tool, { processId: 42, objectId: 'fixture' }), /Response lost/);
    assert.equal(report.uncertainWrite, true);
    assert.equal(report.writesAttempted, true);
    await assert.rejects(ctx.rawCall(tool, { processId: 42 }), /previous write outcome/);
    await assert.rejects(ctx.rawCall('delete_block', { processId: 42, objectId: 'fixture' }), /previous write outcome/);
    assert.deepEqual(requests.map(request => request.params.name), ['get_status', tool]);
  });

  test(`${tool} retains partial native effects without misclassifying a readable native failure`, async () => {
    const partial = { ...status(), operation: tool, saved: false, cleanupFailed: false,
      complete: false, errors: [{ origin: 'tia-openness', message: 'Native rejection' }],
      affectedObjects: [{ kind: 'technologyObject', name: 'fixture', objectId: 'native-id' }] };
    const { ctx, report } = setup(request => wrap(request.params.name === 'get_status' ? status() : partial,
      request.params.name !== 'get_status'));
    const response = await ctx.rawCall(tool, { processId: 42 });
    assert.equal(response.result.isError, true);
    assert.equal(report.observedAffectedObjects[0].objectId, 'native-id');
    assert.notEqual(report.uncertainWrite, true);
    assert.match(report.calls.at(-1).responseText, /Native rejection/);
  });
}

test('new writes mark undecodable and mismatched responses uncertain', async () => {
  for (const tool of addedWrites) {
    for (const payload of ['invalid JSON', JSON.stringify({ ...status(), processId: 99 })]) {
      const { ctx, report } = setup(request => request.params.name === 'get_status' ? wrap(status()) :
        { isError: false, content: [{ type: 'text', text: payload }] });
      await assert.rejects(ctx.rawCall(tool, { processId: 42 }));
      assert.equal(report.uncertainWrite, true, tool);
    }
  }
});

test('context loss reported by a read blocks later writes', async () => {
  const { ctx, report, requests } = setup(() => wrap({ ...status(), complete: false,
    error: { code: 'reconnectRequired', reconnectRequired: true } }, true));
  await ctx.rawCall('get_technology_object', { processId: 42, objectId: 'fixture' });
  assert.equal(report.contextLost, true);
  await assert.rejects(ctx.rawCall('delete_block', { processId: 42, objectId: 'fixture' }), /previous write outcome/);
  assert.deepEqual(requests.map(request => request.params.name), ['get_technology_object']);
});

test('evidence persistence failure after a new write blocks subsequent writes', async () => {
  for (const tool of addedWrites) {
    const report = { prefix: 'fixture', fixture: {}, steps: [], calls: [], observedAffectedObjects: [] };
    const requests = [];
    const ctx = createContext(options, report, () => {
      const last = report.calls.at(-1);
      if (last?.request.params.name === tool && last.finishedAtUtc) throw new Error('Evidence write failed.');
    }, async (_url, init) => {
      const request = JSON.parse(init.body); requests.push(request);
      const payload = request.params.name === 'get_status' ? status() : { ...status(), operation: tool,
        saved: false, cleanupFailed: false, affectedObjects: [] };
      return { status: 200, text: async () => JSON.stringify({ jsonrpc: '2.0', id: request.id, result: wrap(payload) }) };
    });
    await assert.rejects(ctx.rawCall(tool, { processId: 42 }), /Evidence write failed/);
    assert.equal(report.uncertainWrite, true, tool);
    await assert.rejects(ctx.rawCall('delete_block', { processId: 42, objectId: 'fixture' }), /previous write outcome/);
    assert.deepEqual(requests.map(request => request.params.name), ['get_status', tool]);
  }
});

test('memory allocation avoids overlapping bit, word and dword tag addresses', () => {
  assert.equal(unusedAddress([{ logicalAddress: '%M0.7' }, { logicalAddress: '%MW1' }, { logicalAddress: '%MD3' }]), '%M7.0');
  assert.throws(() => unusedAddress([{ logicalAddress: '%MD3' }], '%M5.7'), /overlaps/);
  assert.throws(() => unusedAddress([{ logicalAddress: '%Munknown' }]), /Cannot determine/);
});

test('a successful HTTP/MCP reply cannot conceal incomplete engineering results', () => {
  const good = status();
  assert.equal(successful({ result: { isError: false }, payload: good }, 'get_status'), good);
  assert.throws(() => successful({ result: { isError: false }, payload: { ...good, complete: false } }, 'get_status'), /incomplete/);
  assert.throws(() => successful({ result: { isError: false }, payload: { ...good, errors: [{ message: 'partial' }] } }, 'get_status'), /returned errors/);
  assert.throws(() => successful({ result: { isError: false }, payload: { ...good, operation: 'write_blocks', saved: false,
    cleanupFailed: true, affectedObjects: [] } }, 'write_blocks'), /cleanup/);
});

test('preflight stops on the wrong project before any engineering write', async () => {
  const { ctx, report, requests } = setup(request => {
    if (request.method === 'initialize') return { serverInfo: { name: 'fake' } };
    if (request.method === 'tools/list') return { tools: [...TOOLS, 'create_group'].map(name => ({ name })) };
    if (request.params.name === 'list_tia_processes') return wrap({ readAtUtc: new Date().toISOString(), errors: [], processes: [{ processId: 42, connectedByMcp: true }] });
    return wrap(status('C:\\Production\\Actual.ap20'));
  });
  await assert.rejects(preflight(ctx, report), /does not match/);
  assert.equal(report.steps[0].status, 'failed');
  assert.ok(requests.every(request => !['create_tag_table', 'write_blocks'].includes(request.params?.name)));
});

test('each native write rechecks manually connected target and refuses changed project', async () => {
  const { ctx, requests } = setup(() => wrap(status('C:\\Other\\Other.ap20')));
  await assert.rejects(ctx.call('create_tag_table', { processId: 42, plcObjectId: 'cpu', name: 'fixture' }), /does not match/);
  assert.deepEqual(requests.map(request => request.params.name), ['get_status']);
});

test('uncertain write transport outcome is recorded and never retried', async () => {
  const { ctx, report, requests } = setup(request => {
    if (request.params.name === 'get_status') return wrap(status());
    throw new Error('Connection disappeared after transmission.');
  });
  await assert.rejects(ctx.call('write_blocks', { processId: 42, plcObjectId: 'cpu' }), /Connection disappeared/);
  assert.deepEqual(requests.map(request => request.params.name), ['get_status', 'write_blocks']);
  assert.equal(report.uncertainWrite, true);
  assert.ok(report.calls[1].request && report.calls[1].transportError);
});

test('runner rejects attachment actions and cross-process selectors before sending them', async () => {
  const { ctx, requests } = setup(() => { throw new Error('Unexpected transport'); });
  await assert.rejects(ctx.call('connect_to_tia_portal', { processId: 42 }), /cannot call/);
  await assert.rejects(ctx.call('get_block', { processId: 99, objectId: 'x' }), /Cross-process/);
  assert.equal(requests.length, 0);
});

test('expected native failures preserve the full response and partial affected objects', async () => {
  const partial = { ...status(), operation: 'write_blocks', saved: false, cleanupFailed: false,
    complete: false, errors: [{ origin: 'tia-openness', operation: 'write_blocks', message: 'Native exact text' }],
    affectedObjects: [{ kind: 'block', name: 'McpAT_test_FC', objectId: 'new-native-id' }] };
  const { ctx, report } = setup(request => wrap(request.params.name === 'get_status' ? status() : partial,
    request.params.name !== 'get_status'));
  const response = await ctx.rawCall('write_blocks', { processId: 42, plcObjectId: 'cpu' });
  assert.equal(response.result.isError, true);
  assert.equal(response.payload.errors[0].message, 'Native exact text');
  assert.equal(report.observedAffectedObjects[0].objectId, 'new-native-id');
  assert.match(report.calls.at(-1).responseText, /Native exact text/);
});

test('undecodable tool response after a write is uncertain even when HTTP succeeds', async () => {
  const { ctx, report, requests } = setup(request => request.params.name === 'get_status' ? wrap(status())
    : { isError: false, content: [{ type: 'text', text: 'not JSON' }] });
  await assert.rejects(ctx.call('write_blocks', { processId: 42, plcObjectId: 'cpu' }));
  assert.equal(report.uncertainWrite, true);
  assert.equal(requests.length, 2);
  assert.match(report.calls.at(-1).responseText, /not JSON/);
});

test('scoped reads must explicitly carry complete:true', () => {
  const payload = status();
  delete payload.complete;
  assert.throws(() => successful({ result: { isError: false }, payload }, 'get_status'), /complete:true/);
  assert.throws(() => successful({ result: { isError: false }, payload: { ...payload, complete: 'true' } }, 'get_block'), /complete:true/);
  delete payload.processId;
  assert.doesNotThrow(() => successful({ result: { isError: false }, payload }, 'get_status'));
});

test('cross-reference acceptance requires an actual UsedBy/Read relationship', () => {
  assert.throws(() => assertFixtureReference([
    { objectId: 'tag', references: [] }, { objectId: 'block', references: [] }
  ], 'tag', 'block'), /UsedBy\/Read/);
  const sources = [{ objectId: 'tag', children: [], references: [{ objectId: 'block',
    locations: [{ referenceType: 'UsedBy', access: 'Read' }] }] }];
  assert.doesNotThrow(() => assertFixtureReference(sources, 'tag', 'block'));
  sources[0].references[0].locations[0].access = 'Write';
  assert.throws(() => assertFixtureReference(sources, 'tag', 'block'), /UsedBy\/Read/);
});

test('an explicit output directory cannot overwrite an earlier run report', t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'tia-acceptance-report-test-'));
  t.after(() => fs.rmSync(directory, { recursive: true }));
  prepareOutput(directory);
  const prior = path.join(directory, 'report.json');
  fs.writeFileSync(prior, '{"uncertainWrite":true}');
  assert.throws(() => prepareOutput(directory), /already contains acceptance evidence/);
  assert.equal(fs.readFileSync(prior, 'utf8'), '{"uncertainWrite":true}');
});

function resumeFixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'tia-acceptance-resume-test-'));
  t.after(() => fs.rmSync(directory, { recursive: true }));
  const file = path.join(directory, 'report.json');
  const prefix = 'McpAT_0123456789ab';
  const report = { schemaVersion: 1, status: 'blocked', uncertainWrite: false, processId: options.processId,
    projectPath: options.projectPath, prefix, plcObjectId: 'cpu', fixture: {},
    steps: [...CORE_STEPS.map(id => ({ id, status: 'passed' })), { id: 'source-consistency', status: 'blocked' }], calls: [] };
  for (const [kind, suffix] of [['block', 'FC'], ['udt', 'UDT'], ['tag', 'Signal'], ['table', 'Tags']]) {
    report.fixture[`${kind}Name`] = `${prefix}_${suffix}`;
    report.fixture[`${kind}Id`] = kind + '-id';
  }
  const load = () => { fs.writeFileSync(file, JSON.stringify(report)); return loadResume({ ...options, resumeReport: file }, ['write_blocks']); };
  return { report, load, file };
}

test('resume retains verified fixture identities and links previous evidence', t => {
  const fixture = resumeFixture(t);
  const loaded = fixture.load();
  assert.equal(loaded.prior.fixture.blockId, 'block-id');
  assert.equal(loaded.inherited.length, CORE_STEPS.length);
  assert.equal(loaded.file, fixture.file);
  assert.match(loaded.sha256, /^[0-9a-f]{64}$/);
});

test('resume refuses uncertain writes or a failed scenario that attempted a write', t => {
  const fixture = resumeFixture(t);
  fixture.report.uncertainWrite = true;
  assert.throws(fixture.load, /uncertain write/);
  fixture.report.uncertainWrite = false;
  fixture.report.calls.push({ step: 'source-consistency', request: { params: { name: 'write_blocks' } } });
  assert.throws(fixture.load, /attempted a write/);
});

test('resume rejects a different target or missing passed prerequisite', t => {
  const fixture = resumeFixture(t);
  fixture.report.projectPath = 'C:\\Other\\Other.ap20';
  assert.throws(fixture.load, /Resume project/);
  fixture.report.projectPath = options.projectPath;
  fixture.report.steps = fixture.report.steps.filter(step => step.id !== 'udt.external.update');
  assert.throws(fixture.load, /verified core scenario udt.external.update/);
});
