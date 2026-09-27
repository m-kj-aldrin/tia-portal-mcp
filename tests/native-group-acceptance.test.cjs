'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { runGroupAcceptance } = require('./native-group-acceptance.cjs');
const { GROUPS, runGroupScenarios, blockSource } = require('./native-acceptance/group-scenarios.cjs');

function context() {
  const ctx = { processId: 42, plcObjectId: 'cpu', prefix: 'McpGrp_abcd1234', owned: [], calls: [], steps: [] };
  const groups = Object.fromEntries(GROUPS.map(spec => [spec.kind, []]));
  let block = null;
  const roots = kind => [{ kind: 'scope', name: 'PLC', children: groups[kind].map(item => ({ ...item })) }];
  ctx.step = async (id, description, action) => { ctx.steps.push(id); return action(); };
  ctx.rawCall = async (name, args) => {
    ctx.calls.push({ name, args });
    assert.equal(args.processId, ctx.processId);
    if (name === 'rename' && args.groupPath && !GROUPS.find(spec => spec.kind === args.kind).renamable) {
      return { result: { isError: true }, payload: { error: { code: 'unsupportedObject', message: 'Name is not a writable attribute on this group.' }, complete: false, errors: [] } };
    }
    if (name === 'get_block' && block?.objectId === 'missing') {
      return { result: { isError: true }, payload: { error: { code: 'objectNotFound', message: 'missing' }, complete: false, errors: [] } };
    }
    return { result: { isError: false }, payload: await respond(name, args) };
  };
  async function respond(name, args) {
    if (name.startsWith('list_')) {
      const spec = GROUPS.find(item => item.list === name);
      return { complete: true, errors: [], roots: spec ? roots(spec.kind) : roots('block') };
    }
    if (name === 'create_group') {
      assert.equal(groups[args.kind].some(item => item.name === args.name), false);
      const parent = args.kind === 'block' ? 'PLC/Program blocks' : 'PLC/Parent';
      groups[args.kind].push({ kind: GROUPS.find(item => item.kind === args.kind).affectedKind, name: args.name, path: `${parent}/${args.name}`, objectId: null });
      return { operation: name, saved: false, complete: true, cleanupFailed: false, errors: [], affectedObjects: [{ kind: groups[args.kind].at(-1).kind, name: args.name, objectId: null }] };
    }
    if (name === 'rename' && args.groupPath) {
      const spec = GROUPS.find(item => item.kind === args.kind);
      const item = groups[args.kind].find(group => group.path === args.groupPath);
      assert.ok(item);
      item.name = args.name;
      item.path = item.path.replace(/\/[^/]+$/, '/' + args.name);
      return { operation: name, saved: false, complete: true, cleanupFailed: false, errors: [], affectedObjects: [{ kind: spec.affectedKind, name: args.name, objectId: null }] };
    }
    if (name === 'delete_group') {
      const spec = GROUPS.find(item => item.kind === args.kind);
      const index = groups[args.kind].findIndex(group => group.path === args.groupPath);
      assert.ok(index >= 0);
      const [item] = groups[args.kind].splice(index, 1);
      return { operation: name, saved: false, complete: true, cleanupFailed: false, errors: [], affectedObjects: [{ kind: spec.affectedKind, name: item.name, objectId: null }] };
    }
    if (name === 'write_blocks') {
      assert.equal(block, null);
      const content = args.documents[0].content;
      const declared = content.match(/FUNCTION "([^"]+)"/)[1];
      block = { objectId: 'block-id', name: declared };
      return { operation: name, saved: false, complete: true, cleanupFailed: false, errors: [], affectedObjects: [{ kind: 'block', name: declared, objectId: 'block-id' }] };
    }
    if (name === 'get_block') {
      assert.equal(args.objectId, block.objectId);
      return { complete: true, errors: [], metadata: { objectId: block.objectId, name: block.name, path: 'PLC/Program blocks/' + block.name } };
    }
    if (name === 'rename') {
      assert.equal(args.objectId, block.objectId);
      block.name = args.name;
      return { operation: name, saved: false, complete: true, cleanupFailed: false, errors: [], affectedObjects: [{ kind: 'block', objectId: block.objectId, name: args.name }] };
    }
    if (name === 'delete_block') {
      assert.equal(args.objectId, block.objectId);
      const deletedName = block.name;
      block.objectId = 'missing';
      return { operation: name, saved: false, complete: true, cleanupFailed: false, errors: [], affectedObjects: [{ kind: 'block', objectId: 'block-id', name: deletedName }] };
    }
    throw new Error('Unexpected ' + name);
  }
  ctx.call = async (name, args) => {
    const response = await ctx.rawCall(name, args);
    assert.equal(response.result.isError, false, name);
    return response.payload;
  };
  return ctx;
}

test('group scenarios create, rename or reject, and delete every owned fixture', async () => {
  const ctx = context();
  await runGroupScenarios(ctx);
  assert.deepEqual(ctx.steps, [
    ...GROUPS.flatMap(spec => [`group.${spec.kind}.create`, `group.${spec.kind}.rename`, `group.${spec.kind}.delete`]),
    'block.rename'
  ]);
  assert.equal(ctx.owned.length, 0);
  for (const spec of GROUPS) {
    const created = ctx.calls.find(call => call.name === 'create_group' && call.args.kind === spec.kind);
    assert.equal(created.args.name, `McpGrp_abcd1234_${spec.kind}`);
    const rename = ctx.calls.find(call => call.name === 'rename' && call.args.kind === spec.kind);
    if (spec.renamable) assert.equal(rename.args.name, created.args.name + 'R');
    else assert.equal(rename.args.name, created.args.name + 'R');
  }
  const blockRename = ctx.calls.find(call => call.name === 'rename' && call.args.objectId === 'block-id');
  assert.equal(blockRename.args.name, 'McpGrp_abcd1234_FCR');
  assert.match(blockSource('McpGrp_abcd1234_FC'), /FUNCTION "McpGrp_abcd1234_FC"/);
});

for (const uncertain of [false, true]) {
  test(`group cleanup retains failed and unattempted ownership after ${uncertain ? 'lost response' : 'MCP error response'}`, async () => {
    const ctx = context();
    ctx.fixture = {};
    let writeUncertain = false;
    ctx.isWriteUncertain = () => writeUncertain;
    const step = ctx.step;
    ctx.step = async (id, description, action) => {
      if (id === 'group.block.rename') {
        ctx.owned.push({ kind: 'blockObject', objectId: 'unattempted-block', name: 'Later' });
        throw new Error('Original readback failure');
      }
      return step(id, description, action);
    };
    const rawCall = ctx.rawCall;
    ctx.rawCall = async (name, args) => {
      if (name === 'delete_group') {
        ctx.calls.push({ name, args });
        if (uncertain) {
          writeUncertain = true;
          throw new Error('Cleanup response lost');
        }
        return { result: { isError: true }, payload: { complete: false, errors: [{ message: 'Native cleanup rejected' }] } };
      }
      return rawCall(name, args);
    };
    await assert.rejects(runGroupScenarios(ctx), /Original readback failure/);
    assert.equal(ctx.calls.filter(call => call.name === 'delete_group').length, 1, 'Failed cleanup is never retried.');
    assert.ok(!ctx.calls.some(call => call.name === 'delete_block'), 'Cleanup stops before another pending fixture.');
    assert.equal(ctx.owned.length, 2);
    assert.equal(ctx.owned[0].kind, 'block');
    assert.equal(ctx.owned[1].objectId, 'unattempted-block');
    assert.equal(ctx.fixture.cleanupErrors.length, 1, 'The evidence retains the cleanup failure as well as the original failure.');
    assert.match(ctx.fixture.cleanupErrors[0], uncertain ? /Cleanup response lost/ : /delete_group/);
  });
}

test('group cleanup removes ownership only for a validated successful deletion', async () => {
  const ctx = context();
  ctx.fixture = {};
  const step = ctx.step;
  ctx.step = async (id, description, action) => {
    if (id === 'group.block.rename') throw new Error('Original readback failure');
    return step(id, description, action);
  };
  await assert.rejects(runGroupScenarios(ctx), /Original readback failure/);
  assert.equal(ctx.calls.filter(call => call.name === 'delete_group').length, 1);
  assert.equal(ctx.owned.length, 0);
  assert.deepEqual(ctx.fixture.cleanupErrors, []);
  assert.equal(ctx.deleting, false);
});

const GROUP_TOOLS = ['list_tia_processes', 'get_status', 'list_devices', 'get_device', 'create_group',
  'delete_group', 'rename', 'write_blocks', 'delete_block', 'get_block', ...GROUPS.map(spec => spec.list)];
const GROUP_WRITES = new Set(['create_group', 'delete_group', 'rename', 'write_blocks', 'delete_block']);

function groupTransport(overrides = {}) {
  const ctx = context();
  const requests = [];
  const projectPath = 'C:\\Disposable\\Group.ap20';
  const read = payload => ({ complete: true, errors: [], readAtUtc: '2026-09-27T00:00:00Z', ...payload });
  const fetchImpl = async (endpoint, init) => {
    const request = JSON.parse(init.body);
    requests.push(request);
    assert.equal(endpoint, 'http://127.0.0.1:5000/mcp');
    assert.equal(init.redirect, 'error');
    assert.match(init.headers.Accept, /application\/json/);
    let result;
    if (request.method === 'initialize') {
      result = { protocolVersion: '2025-03-26', capabilities: { tools: {} }, serverInfo: { name: 'group-test', version: '1' } };
    } else if (request.method === 'tools/list') {
      result = { tools: GROUP_TOOLS.filter(name => name !== overrides.missingTool).map(name => ({ name })) };
    } else {
      assert.equal(request.method, 'tools/call');
      const { name, arguments: args } = request.params;
      if (overrides.failWrite?.(name, args)) throw new Error('Mock response lost after write');
      let response;
      if (name === 'get_status') response = { result: { isError: false }, payload: read(Object.hasOwn(args, 'processId')
        ? { processId: 42, state: 'connected', writeToolsAvailable: true, project: { path: overrides.projectPath || projectPath } }
        : {}) };
      else if (name === 'list_tia_processes') response = { result: { isError: false }, payload: read({ processes: [{ processId: 42, connectedByMcp: true }] }) };
      else if (name === 'list_devices') response = { result: { isError: false }, payload: read({ roots: [{ kind: 'device', objectId: 'device-id' }] }) };
      else if (name === 'get_device') response = { result: { isError: false }, payload: read({ deviceItems: [{ name: 'CPU', plcObjectId: 'cpu' }] }) };
      else response = await ctx.rawCall(name, args);
      const payload = read(response.payload);
      if (Object.hasOwn(args, 'processId')) payload.processId = args.processId;
      result = { ...response.result, content: [{ type: 'text', text: JSON.stringify(payload) }] };
    }
    return { status: 200, async text() { return JSON.stringify({ jsonrpc: '2.0', id: request.id, result }); } };
  };
  return { requests, fetchImpl, projectPath };
}

async function runMockAcceptance(t, transport, extraOptions = {}) {
  const output = fs.mkdtempSync(path.join(os.tmpdir(), 'tia-group-contract-'));
  t.after(() => fs.rmSync(output, { recursive: true, force: true }));
  return runGroupAcceptance({ processId: 42, projectPath: transport.projectPath, plcObjectId: 'cpu',
    endpoint: 'http://127.0.0.1:5000/mcp', timeoutMs: 1000, output, ...extraOptions }, { fetchImpl: transport.fetchImpl });
}

test('group runner shares initialization, connected CPU preflight, write guards and evidence tracing', async t => {
  const transport = groupTransport();
  const { report, output } = await runMockAcceptance(t, transport);
  assert.equal(report.status, 'passed', report.error);
  assert.deepEqual(transport.requests.slice(0, 2).map(request => request.method), ['initialize', 'tools/list']);
  assert.deepEqual(report.serverInfo, { name: 'group-test', version: '1' });
  assert.equal(report.plcObjectId, 'cpu');
  assert.equal(report.logicalAddress, undefined, 'Group scenarios need no fixture memory allocation.');
  assert.ok(!transport.requests.some(request => request.params?.name === 'get_tag_table'));
  assert.equal(report.writesAttempted, true);
  assert.equal(report.uncertainWrite, false);
  assert.ok(report.observedAffectedObjects.length > 0);
  assert.equal(report.steps[0].id, 'preflight');
  for (const [index, request] of transport.requests.entries()) {
    if (!GROUP_WRITES.has(request.params?.name)) continue;
    assert.equal(transport.requests[index - 1].params.name, 'get_status');
    assert.deepEqual(transport.requests[index - 1].params.arguments, { processId: 42 });
  }
  assert.ok(report.calls.every(call => call.transmissionState === 'started' && call.finishedAtUtc));
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(output, 'report.json'), 'utf8')), JSON.parse(JSON.stringify(report)));
});

test('group runner checks an explicitly supplied CPU against native discovery before writes', async t => {
  const transport = groupTransport();
  const { report } = await runMockAcceptance(t, transport, { plcObjectId: 'foreign-cpu' });
  assert.equal(report.status, 'failed');
  assert.match(report.error, /discovered CPU/);
  assert.equal(report.writesAttempted, false);
  assert.ok(!transport.requests.some(request => GROUP_WRITES.has(request.params?.name)));
});

test('group runner requires its scenario tool publication before native writes', async t => {
  const transport = groupTransport({ missingTool: 'rename' });
  const { report } = await runMockAcceptance(t, transport);
  assert.equal(report.status, 'failed');
  assert.match(report.error, /rename/);
  assert.equal(report.writesAttempted, false);
  assert.ok(!transport.requests.some(request => GROUP_WRITES.has(request.params?.name)));
});

test('group runner records a lost write response and suppresses subsequent fixture cleanup writes', async t => {
  const transport = groupTransport({ failWrite: (name, args) => name === 'rename' && args.kind === 'tagTable' });
  const { report } = await runMockAcceptance(t, transport);
  assert.equal(report.status, 'failed');
  assert.match(report.error, /response lost/);
  assert.equal(report.writesAttempted, true);
  assert.equal(report.uncertainWrite, true);
  assert.ok(report.fixture.owned.some(item => item.kind === 'tagTable'), 'Stopped cleanup retains the recorded owned group.');
  const failedIndex = transport.requests.findIndex(request => request.params?.name === 'rename' && request.params.arguments.kind === 'tagTable');
  assert.ok(failedIndex >= 0);
  assert.ok(!transport.requests.slice(failedIndex + 1).some(request => GROUP_WRITES.has(request.params?.name)));
  assert.ok(report.calls.some(call => call.transportError === 'Mock response lost after write'));
});
