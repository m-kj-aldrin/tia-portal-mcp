'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { writeReport, RENAME_ATTEMPTS, RETRY_DELAY_MS } = require('./native-acceptance/report-writer.cjs');
const { createContext } = require('./native-acceptance/runner.cjs');

function fakeIo(failRename = () => null) {
  const files = new Map();
  const events = [];
  let renames = 0;
  const io = {
    writeFileSync(file, value) { events.push({ operation: 'write', file }); files.set(file, value); },
    renameSync(source, target) {
      events.push({ operation: 'rename', source, target });
      const code = failRename(source, target, ++renames);
      if (code) throw Object.assign(new Error(`${code}: rename denied`), { code });
      assert.ok(files.has(source)); files.set(target, files.get(source)); files.delete(source);
    }
  };
  return { files, events, io, pauses: [], dependencies: null };
}

function persistWith(store, report = { status: 'passed', calls: [{ number: 1 }] }) {
  writeReport('reports', report, current => `Result: ${current.status}; calls: ${current.calls.length}`,
    { io: store.io, sleep: milliseconds => store.pauses.push(milliseconds) });
}

test('report writer retries only transient local renames and publishes matching JSON/Markdown', () => {
  const store = fakeIo((_source, _target, attempt) => attempt < 4 ? ['EPERM', 'EACCES', 'EBUSY'][attempt - 1] : null);
  persistWith(store);
  assert.deepEqual(store.pauses, [50, 50, 50]);
  assert.equal(store.events.filter(event => event.operation === 'write').length, 2, 'Report content is written once, not regenerated per retry.');
  assert.equal(JSON.parse(store.files.get(path.join('reports', 'report.json'))).status, 'passed');
  assert.equal(store.files.get(path.join('reports', 'report.md')), 'Result: passed; calls: 1');
  assert.equal(store.files.size, 2, 'Successful commits must consume their temp files.');
});

test('report writer bounds persistent transient failures and preserves prior evidence plus temps', () => {
  const store = fakeIo(() => 'EPERM');
  const json = path.join('reports', 'report.json'); const markdown = path.join('reports', 'report.md');
  store.files.set(json, 'previous JSON'); store.files.set(markdown, 'previous Markdown');
  assert.throws(() => persistWith(store), error => error.code === 'EPERM');
  assert.equal(store.events.filter(event => event.operation === 'rename').length, RENAME_ATTEMPTS);
  assert.deepEqual(store.pauses, Array(RENAME_ATTEMPTS - 1).fill(RETRY_DELAY_MS));
  assert.equal(store.files.get(json), 'previous JSON');
  assert.equal(store.files.get(markdown), 'previous Markdown');
  assert.equal(JSON.parse(store.files.get(json + '.tmp')).status, 'passed');
  assert.equal(store.files.get(markdown + '.tmp'), 'Result: passed; calls: 1');
});

test('report writer does not retry a permanent rename failure', () => {
  const store = fakeIo(() => 'ENOENT');
  assert.throws(() => persistWith(store), error => error.code === 'ENOENT');
  assert.equal(store.events.filter(event => event.operation === 'rename').length, 1);
  assert.deepEqual(store.pauses, []);
});

test('report writer does not retry file writes or truncate existing Markdown on a failed Markdown commit', () => {
  const store = fakeIo(source => source.endsWith('report.md.tmp') ? 'EBUSY' : null);
  const markdown = path.join('reports', 'report.md');
  store.files.set(markdown, 'previous Markdown');
  assert.throws(() => persistWith(store), error => error.code === 'EBUSY');
  assert.equal(store.events.filter(event => event.operation === 'rename').length, 1 + RENAME_ATTEMPTS);
  assert.equal(JSON.parse(store.files.get(path.join('reports', 'report.json'))).status, 'passed');
  assert.equal(store.files.get(markdown), 'previous Markdown');
  assert.equal(store.files.get(markdown + '.tmp'), 'Result: passed; calls: 1');
  const broken = fakeIo();
  broken.io.writeFileSync = () => { throw Object.assign(new Error('write failed'), { code: 'EPERM' }); };
  assert.throws(() => persistWith(broken), /write failed/);
  assert.deepEqual(broken.pauses, []);
  assert.equal(broken.events.length, 0);
});

test('report writer renders both representations before changing evidence', () => {
  const store = fakeIo();
  assert.throws(() => writeReport('reports', {}, () => { throw new Error('render failed'); }, { io: store.io }), /render failed/);
  assert.equal(store.events.length, 0);
});

const options = { processId: 42, projectPath: 'C:\\Disposable\\Fixture.ap20', endpoint: 'http://127.0.0.1:5000/mcp', timeoutMs: 1000 };
function rpcFixture(persistHook) {
  const report = { prefix: 'McpLC_test', fixture: {}, steps: [], calls: [], observedAffectedObjects: [], uncertainWrite: false };
  const requests = [];
  const snapshots = [];
  const persist = () => { persistHook?.(report); snapshots.push(JSON.parse(JSON.stringify(report))); };
  const ctx = createContext(options, report, persist, async (_url, init) => {
    const request = JSON.parse(init.body); requests.push(request);
    const payload = request.params.name === 'get_status' ? { readAtUtc: new Date().toISOString(), processId: 42,
      complete: true, errors: [], state: 'connected', writeToolsAvailable: true, project: { path: options.projectPath } }
      : { processId: 42, operation: 'compile_plc', complete: true, errors: [], compilationSucceeded: true };
    const result = { isError: false, content: [{ type: 'text', text: JSON.stringify(payload) }] };
    return { status: 200, text: async () => JSON.stringify({ jsonrpc: '2.0', id: request.id, result }) };
  });
  return { ctx, report, requests, snapshots };
}

test('initial intent persistence failure records not-sent and cannot transmit or retry the native write', async () => {
  const fixture = rpcFixture(report => {
    const call = report.calls.at(-1);
    if (call?.request.params.name === 'compile_plc' && call.transmissionState === 'prepared') throw new Error('Intent persistence failed');
  });
  await assert.rejects(fixture.ctx.rawCall('compile_plc', { processId: 42, plcObjectId: 'cpu' }),
    error => error.transmissionState === 'not-sent' && /Intent persistence failed/.test(error.message));
  assert.deepEqual(fixture.requests.map(request => request.params.name), ['get_status']);
  assert.equal(fixture.report.uncertainWrite, false);
  const trace = fixture.report.calls.at(-1);
  assert.equal(trace.transmissionState, 'not-sent');
  assert.equal(trace.persistenceError, 'Intent persistence failed');
  assert.equal(trace.transportError, undefined);
  assert.equal(trace.responseText, undefined);
  assert.ok(fixture.snapshots.some(snapshot => snapshot.calls.at(-1)?.transmissionState === 'not-sent'));
});

test('post-transmission evidence failure remains uncertain and never repeats the native request', async () => {
  let failed = false;
  const fixture = rpcFixture(report => {
    const call = report.calls.at(-1);
    if (!failed && call?.request.params.name === 'compile_plc' && call.responseText) {
      failed = true; throw new Error('Response persistence failed');
    }
  });
  await assert.rejects(fixture.ctx.rawCall('compile_plc', { processId: 42, plcObjectId: 'cpu' }), /Response persistence failed/);
  assert.deepEqual(fixture.requests.map(request => request.params.name), ['get_status', 'compile_plc']);
  assert.equal(fixture.report.uncertainWrite, true);
  assert.equal(fixture.report.calls.at(-1).transmissionState, 'started');
  assert.match(fixture.report.calls.at(-1).responseText, /compilationSucceeded/);
});

test('prepared durable intent does not falsely prove that an in-flight request was not sent', async () => {
  const fixture = rpcFixture();
  await fixture.ctx.rawCall('compile_plc', { processId: 42, plcObjectId: 'cpu' });
  const states = fixture.snapshots.map(snapshot => snapshot.calls.find(call => call.request.params.name === 'compile_plc')?.transmissionState).filter(Boolean);
  assert.equal(states[0], 'prepared');
  assert.ok(states.includes('started'));
  assert.ok(!states.includes('not-sent'));
  assert.equal(fixture.requests.filter(request => request.params.name === 'compile_plc').length, 1);
});
