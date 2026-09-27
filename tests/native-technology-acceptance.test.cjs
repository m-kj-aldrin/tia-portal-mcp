'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { TOOLS, WRITES } = require('./native-acceptance/runner.cjs');
const { run: runTechnology } = require('./native-acceptance/technology-objects.cjs');
const { run: runCatalogue } = require('./native-acceptance/technology-object-catalogue-create.cjs');
const catalogue = require('../data/technology-object-catalogue.json');

const target = { processId: 42, projectPath: 'C:\\Disposable\\Fixture.ap20', endpoint: 'http://127.0.0.1:5000/mcp', timeoutMs: 1000 };
const cpu = { name: 'CPU', plcObjectId: 'cpu-id', typeName: 'CPU 1214C', firmwareVersion: 'V4.4' };
const wrap = (payload, isError = false) => ({ isError, content: [{ type: 'text', text: JSON.stringify(payload) }] });
const envelope = payload => ({ processId: target.processId, complete: true, errors: [], readAtUtc: new Date().toISOString(), ...payload });
const available = () => envelope({ plcObjectId: cpu.plcObjectId, typeName: cpu.typeName, firmwareVersion: cpu.firmwareVersion,
  cpuFamily: 'S7-1200', technologyCpu: false, catalogueDescription: 'Offline fixture',
  technologyObjects: catalogue.objects.filter(row => row.cpu === 'S7-1200' &&
    (String(row.firmware).toLowerCase() === 'any' || Number(String(row.firmware).match(/\d+(?:\.\d+)?/)?.[0]) <= 4.4))
    .map(row => ({ ...row, systemLibElement: row.name.replace(/\s*\(S7-1500T\)\s*$/i, '') })) });

function mock(options = {}) {
  const requests = [];
  const existing = { objectId: 'existing-id', name: 'PID_Compact_Level', kind: 'technologyObject',
    ofSystemLibElement: 'TO_PID_Compact', ofSystemLibVersion: '2.0' };
  const objects = new Map([[existing.objectId, existing]]);
  let nextObjectId = 0;
  const fetchImpl = async (_url, init) => {
    const request = JSON.parse(init.body);
    requests.push(request);
    let result;
    if (request.method === 'initialize') result = { serverInfo: { name: 'offline' } };
    else if (request.method === 'tools/list') result = { tools: (options.published || TOOLS).map(name => ({ name })) };
    else {
      const { name, arguments: args } = request.params;
      const override = options.respond?.(name, args, requests);
      if (override) result = override;
      else if (name === 'get_status') result = wrap(envelope({ state: 'connected', writeToolsAvailable: options.readOnly !== true,
        project: { path: options.projectPath || target.projectPath } }));
      else if (name === 'list_tia_processes') result = wrap(envelope({ processes: [{ processId: 42, connectedByMcp: true }] }));
      else if (name === 'list_devices') result = wrap(envelope({ roots: [{ kind: 'device', objectId: 'device-id' }] }));
      else if (name === 'get_device') result = wrap(envelope({ deviceItems: options.cpus || [cpu] }));
      else if (name === 'list_available_technology_objects') result = wrap(options.available || available());
      else if (name === 'list_technology_objects') result = wrap(envelope({ roots: [...objects.values()] }));
      else if (name === 'get_technology_object') {
        const object = objects.get(args.objectId);
        result = object ? wrap(envelope({ metadata: object,
          parameters: args.includeParameters === false ? null : [{ name: 'Gain', value: 1 }, { name: 'Enabled', value: true }] }))
          : wrap(envelope({ complete: false, error: { code: 'objectNotFound' } }), true);
      } else if (name === 'get_block') result = wrap(envelope({ metadata: { ...objects.get(args.objectId), blockType: 'DB' } }));
      else if (name === 'create_technology_object') {
        const object = { kind: 'technologyObject', objectId: 'created-' + ++nextObjectId, name: args.name,
          ofSystemLibElement: args.systemLibElement, ofSystemLibVersion: args.systemLibVersion };
        objects.set(object.objectId, object);
        result = wrap(envelope({ operation: name, saved: false, cleanupFailed: false, affectedObjects: [object] }));
      } else if (name === 'set_technology_object_parameters') result = wrap(envelope({ operation: name, saved: false,
        cleanupFailed: false, affectedObjects: [objects.get(args.objectId)] }));
      else if (name === 'delete_block') {
        const object = objects.get(args.objectId);
        objects.delete(args.objectId);
        result = wrap(envelope({ operation: name, saved: false, cleanupFailed: false, affectedObjects: [object] }));
      } else throw new Error('Unexpected call ' + name);
    }
    return { status: 200, text: async () => JSON.stringify({ jsonrpc: '2.0', id: request.id, result }) };
  };
  return { fetchImpl, requests, objects };
}

async function execute(t, suite, options = {}, mockOptions = {}) {
  const output = fs.mkdtempSync(path.join(os.tmpdir(), 'tia-technology-test-'));
  t.after(() => fs.rmSync(output, { recursive: true }));
  const transport = mock(mockOptions);
  const result = await suite({ ...target, output, ...options }, { fetchImpl: transport.fetchImpl, onProgress: () => {} });
  return { ...result, ...transport };
}

for (const script of ['technology-objects.cjs', 'technology-object-catalogue-create.cjs']) {
  test(`${script} requires explicit CLI target before any transport`, () => {
    const result = spawnSync(process.execPath, [path.join(__dirname, 'native-acceptance', script)], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /process-id is required/);
    assert.doesNotMatch(result.stderr, /fetch|ECONNREFUSED/);
  });
}

test('technology suite shares CPU preflight, write guards, full readback and persisted cleanup evidence', async t => {
  const result = await execute(t, runTechnology);
  assert.equal(result.report.status, 'passed', result.report.error);
  assert.equal(result.objects.size, 1);
  assert.equal(result.report.fixture.deleted, true);
  const names = result.requests.map(request => request.params?.name);
  assert.ok(!names.includes('list_tag_tables'));
  assert.equal(result.requests[0].method, 'initialize');
  assert.equal(result.report.plcObjectId, cpu.plcObjectId);
  for (let index = 0; index < names.length; index++) if (WRITES.includes(names[index]))
    assert.equal(names[index - 1], 'get_status', 'Every write must recheck the project.');
  const stored = JSON.parse(fs.readFileSync(path.join(result.output, 'report.json'), 'utf8'));
  assert.equal(stored.observedAffectedObjects.length, 3);
  assert.ok(stored.calls.every(call => call.transmissionState === 'started' && typeof call.responseText === 'string'));
});

test('catalogue-only uses its exact read publication and permits a read-only server', async t => {
  const result = await execute(t, runTechnology, { catalogueOnly: true }, { readOnly: true,
    published: ['get_status', 'list_tia_processes', 'list_devices', 'get_device', 'list_available_technology_objects'] });
  assert.equal(result.report.status, 'passed', result.report.error);
  assert.equal(result.report.writesAttempted, false);
  assert.ok(result.requests.every(request => !WRITES.includes(request.params?.name)));
});

test('technology CPU ambiguity and explicit unknown IDs stop before writes', async t => {
  for (const options of [{}, { plcObjectId: 'unknown-cpu' }]) {
    const result = await execute(t, runTechnology, options, { cpus: [cpu, { ...cpu, plcObjectId: 'cpu-two' }] });
    assert.equal(result.report.status, 'failed');
    assert.ok(result.requests.every(request => !WRITES.includes(request.params?.name)));
    assert.equal(result.report.availableCpus.length, 2);
  }
});

test('technology uncertain parameter write stops cleanup and preserves created object evidence', async t => {
  const result = await execute(t, runTechnology, {}, { respond: name => {
    if (name === 'set_technology_object_parameters') throw new Error('Response lost after parameter write.');
  } });
  assert.equal(result.report.status, 'failed');
  assert.equal(result.report.uncertainWrite, true);
  assert.equal(result.requests.filter(request => request.params?.name === 'set_technology_object_parameters').length, 1);
  assert.ok(!result.requests.some(request => request.params?.name === 'delete_block'));
  assert.ok(result.report.fixture.createdObject.objectId);
  assert.equal(result.objects.size, 2);
});

test('technology does not retry deletion after either native rejection or transport failure', async t => {
  for (const lostResponse of [false, true]) {
    const result = await execute(t, runTechnology, {}, { respond: name => {
      if (name !== 'delete_block') return;
      if (lostResponse) throw new Error('Delete response lost.');
      return wrap(envelope({ complete: false, operation: name, saved: false, cleanupFailed: false,
        affectedObjects: [], errors: [{ origin: 'tia-openness', message: 'Native delete refusal' }] }), true);
    } });
    assert.equal(result.report.status, 'failed');
    assert.equal(result.report.uncertainWrite, lostResponse);
    assert.equal(result.requests.filter(request => request.params?.name === 'delete_block').length, 1);
  }
});

test('technology context loss on created-object read prevents cleanup writes', async t => {
  const result = await execute(t, runTechnology, {}, { respond: (name, args) => {
    if (name === 'get_technology_object' && args.objectId.startsWith('created-'))
      return wrap(envelope({ complete: false, error: { code: 'reconnectRequired', reconnectRequired: true } }), true);
  } });
  assert.equal(result.report.contextLost, true);
  assert.ok(!result.requests.some(request => request.params?.name === 'delete_block'));
});

test('technology lost read response stops cleanup without claiming a native write was uncertain', async t => {
  const result = await execute(t, runTechnology, {}, { respond: (name, args) => {
    if (name === 'get_technology_object' && args.objectId.startsWith('created-')) throw new Error('Read response lost.');
  } });
  assert.equal(result.report.contextUncertain, true);
  assert.equal(result.report.uncertainWrite, false);
  assert.ok(!result.requests.some(request => request.params?.name === 'delete_block'));
});

test('catalogue creation shares preflight and reports every created object without deleting', async t => {
  const result = await execute(t, runCatalogue);
  assert.equal(result.report.status, 'passed', result.report.error);
  assert.equal(result.report.fixture.createdObjects.length, available().technologyObjects.length);
  assert.ok(!result.requests.some(request => request.params?.name === 'delete_block' || request.params?.name === 'list_tag_tables'));
  assert.ok(result.report.calls.every(call => call.step));
});

test('catalogue creation stops after uncertain transport instead of trying the next row', async t => {
  const result = await execute(t, runCatalogue, {}, { respond: name => {
    if (name === 'create_technology_object') throw new Error('Creation response lost.');
  } });
  assert.equal(result.report.uncertainWrite, true);
  assert.equal(result.requests.filter(request => request.params?.name === 'create_technology_object').length, 1);
  assert.match(fs.readFileSync(path.join(result.output, 'report.md'), 'utf8'), /later rows unattempted/);
});

test('catalogue context loss during readback stops before the next creation', async t => {
  const result = await execute(t, runCatalogue, {}, { respond: name => {
    if (name === 'get_technology_object') return wrap(envelope({ complete: false,
      error: { code: 'notConnected', reconnectRequired: true } }), true);
  } });
  assert.equal(result.report.contextLost, true);
  assert.equal(result.requests.filter(request => request.params?.name === 'create_technology_object').length, 1);
  assert.equal(result.report.fixture.createdObjects.length, 1);
});

test('catalogue native row failures retain partial affected objects and continue each later row once', async t => {
  let first = true;
  const result = await execute(t, runCatalogue, {}, { respond: (name, args) => {
    if (name !== 'create_technology_object' || !first) return;
    first = false;
    return wrap(envelope({ complete: false, operation: name, saved: false, cleanupFailed: false,
      affectedObjects: [{ kind: 'technologyObject', name: args.name, objectId: 'partial-native-id' }],
      errors: [{ origin: 'tia-openness', message: 'Native catalogue refusal' }] }), true);
  } });
  assert.equal(result.report.status, 'failed');
  assert.equal(result.report.uncertainWrite, false);
  assert.equal(result.report.fixture.failures.length, 1);
  assert.ok(result.report.observedAffectedObjects.some(item => item.objectId === 'partial-native-id'));
  const attempts = result.requests.filter(request => request.params?.name === 'create_technology_object');
  assert.equal(attempts.length, available().technologyObjects.length);
  assert.equal(new Set(attempts.map(request => request.params.arguments.name)).size, attempts.length);
});

test('catalogue rejects incomplete metadata even when its expected name and element match', async t => {
  const result = await execute(t, runCatalogue, {}, { respond: (name, args) => {
    if (name !== 'get_technology_object') return;
    const index = Number(args.objectId.split('-').at(-1)) - 1;
    const row = available().technologyObjects[index];
    return wrap(envelope({ complete: false, metadata: { objectId: args.objectId,
      name: 'Mcp_' + row.systemLibElement, ofSystemLibElement: row.systemLibElement } }));
  } });
  assert.equal(result.report.status, 'failed');
  assert.equal(result.report.fixture.failures.length, available().technologyObjects.length);
  assert.ok(result.report.fixture.failures.every(failure => /incomplete/.test(failure.error)));
});

test('catalogue rejects write envelopes reporting failed temporary cleanup', async t => {
  const result = await execute(t, runCatalogue, {}, { respond: (name, args) => {
    if (name !== 'create_technology_object') return;
    return wrap(envelope({ operation: name, saved: false, cleanupFailed: true,
      affectedObjects: [{ kind: 'technologyObject', name: args.name, objectId: 'partial-id' }] }));
  } });
  assert.equal(result.report.status, 'failed');
  assert.ok(result.report.fixture.failures.every(failure => /cleanup/.test(failure.error)));
  assert.ok(result.requests.every(request => request.params?.name !== 'get_technology_object'));
});
