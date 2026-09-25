'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
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
      const name = block.name;
      block.objectId = 'missing';
      return { operation: name, saved: false, complete: true, cleanupFailed: false, errors: [], affectedObjects: [{ kind: 'block', objectId: 'block-id', name }] };
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
