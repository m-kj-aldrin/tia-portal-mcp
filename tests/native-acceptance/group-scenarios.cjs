'use strict';

const assert = require('node:assert/strict');

// Describes create/delete/rename calls for an acceptance client. It does not
// attach to TIA, open a transport, save, or compile.
const GROUPS = [
  { kind: 'block', list: 'list_blocks', affectedKind: 'blockGroup', renamable: false },
  { kind: 'udt', list: 'list_udts', affectedKind: 'typeGroup', renamable: false },
  { kind: 'tagTable', list: 'list_tag_tables', affectedKind: 'tagTableGroup', renamable: true },
  { kind: 'technologyObject', list: 'list_technology_objects', affectedKind: 'technologyObjectGroup', renamable: true }
];

const walk = nodes => (nodes || []).flatMap(node => [node, ...walk(node.children)]);

function named(payload, name) {
  return walk(payload.roots).filter(node => node.name === name);
}

function groupWrite(payload, operation, kind, name) {
  assert.equal(payload.operation, operation);
  assert.equal(payload.saved, false);
  assert.equal(payload.complete, true);
  assert.equal(payload.cleanupFailed, false);
  assert.equal(payload.affectedObjects?.length, 1);
  const item = payload.affectedObjects[0];
  assert.equal(item.kind, kind);
  assert.equal(item.name, name);
  assert.equal(item.objectId ?? null, null, 'V20 user groups have no native objectId.');
  return item;
}

async function exerciseGroup(ctx, spec) {
  const name = `${ctx.prefix}_${spec.kind}`;
  const renamed = `${name}R`;
  await ctx.step(`group.${spec.kind}.create`, `Create one ${spec.kind} user group and read it back from inventory.`, async () => {
    const before = await ctx.call(spec.list, { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
    assert.equal(named(before, name).length, 0, 'A same-name group already exists.');
    groupWrite(await ctx.call('create_group', {
      processId: ctx.processId, plcObjectId: ctx.plcObjectId, kind: spec.kind, name
    }), 'create_group', spec.affectedKind, name);
    const created = named(await ctx.call(spec.list, { processId: ctx.processId, plcObjectId: ctx.plcObjectId }), name);
    assert.equal(created.length, 1);
    assert.equal(created[0].kind, spec.affectedKind);
    assert.equal(created[0].path, `${created[0].path.split('/').slice(0, -1).join('/')}/${name}`);
    ctx.owned.push({ ...spec, name, path: created[0].path });
    return { path: created[0].path };
  });
  await ctx.step(`group.${spec.kind}.rename`, spec.renamable
    ? `Rename the ${spec.kind} user group through its typed Name setter and read the new path.`
    : `Reject rename of the ${spec.kind} user group because Name is not writable, and keep the original name.`, async () => {
    const current = ctx.owned.find(item => item.kind === spec.kind && item.name === name);
    assert.ok(current, 'The owned group was not recorded.');
    if (spec.renamable) {
      groupWrite(await ctx.call('rename', {
        processId: ctx.processId, plcObjectId: ctx.plcObjectId, kind: spec.kind, groupPath: current.path, name: renamed
      }), 'rename', spec.affectedKind, renamed);
      const inventory = await ctx.call(spec.list, { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
      assert.equal(named(inventory, name).length, 0);
      const moved = named(inventory, renamed);
      assert.equal(moved.length, 1);
      assert.equal(moved[0].path.endsWith('/' + renamed), true);
      current.name = renamed;
      current.path = moved[0].path;
      return { path: current.path };
    }
    const rejected = await ctx.rawCall('rename', {
      processId: ctx.processId, plcObjectId: ctx.plcObjectId, kind: spec.kind, groupPath: current.path, name: renamed
    });
    assert.equal(rejected.result.isError, true);
    assert.equal(rejected.payload.error?.code, 'unsupportedObject');
    assert.match(rejected.payload.error?.message || '', /not a writable attribute/);
    const inventory = await ctx.call(spec.list, { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
    assert.equal(named(inventory, renamed).length, 0);
    assert.equal(named(inventory, name).length, 1);
    return { rejected: true };
  });
  await ctx.step(`group.${spec.kind}.delete`, `Delete the owned ${spec.kind} user group and verify it is absent.`, async () => {
    const current = ctx.owned.find(item => item.kind === spec.kind);
    assert.ok(current);
    ctx.deleting = true;
    groupWrite(await ctx.call('delete_group', {
      processId: ctx.processId, plcObjectId: ctx.plcObjectId, kind: spec.kind, groupPath: current.path
    }), 'delete_group', spec.affectedKind, current.name);
    ctx.owned = ctx.owned.filter(item => item !== current);
    ctx.deleting = false;
    const inventory = await ctx.call(spec.list, { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
    assert.equal(named(inventory, current.name).length, 0);
  });
}

function blockSource(name) {
  return `FUNCTION "${name}" : Void\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nBEGIN\r\nEND_FUNCTION\r\n`;
}

async function exerciseBlock(ctx) {
  const name = `${ctx.prefix}_FC`;
  const renamed = `${name}R`;
  await ctx.step('block.rename', 'Create one SCL function, rename it, keep its object ID, then delete it.', async () => {
    const absent = named(await ctx.call('list_blocks', { processId: ctx.processId, plcObjectId: ctx.plcObjectId }), name);
    assert.equal(absent.length, 0);
    const created = await ctx.call('write_blocks', {
      processId: ctx.processId, plcObjectId: ctx.plcObjectId, sourceFormat: 'external-source',
      documents: [{ name: `${name}.scl`, content: blockSource(name) }]
    });
    assert.equal(created.saved, false);
    const item = created.affectedObjects.find(object => object.kind === 'block' && object.name === name);
    assert.ok(item?.objectId, 'The created block needs a native objectId.');
    const read = await ctx.call('get_block', { processId: ctx.processId, objectId: item.objectId, includeSource: false, includePath: true });
    assert.equal(read.metadata.name, name);
    assert.equal(read.metadata.objectId, item.objectId);
    ctx.owned.push({ kind: 'blockObject', objectId: item.objectId, name });
    const renamedPayload = await ctx.call('rename', { processId: ctx.processId, objectId: item.objectId, name: renamed });
    assert.equal(renamedPayload.saved, false);
    assert.equal(renamedPayload.affectedObjects[0].objectId, item.objectId);
    assert.equal(renamedPayload.affectedObjects[0].name, renamed);
    const after = await ctx.call('get_block', { processId: ctx.processId, objectId: item.objectId, includeSource: false, includePath: true });
    assert.equal(after.metadata.objectId, item.objectId);
    assert.equal(after.metadata.name, renamed);
    assert.equal(after.metadata.path.endsWith('/' + renamed), true);
    ctx.deleting = true;
    const deleted = await ctx.call('delete_block', { processId: ctx.processId, objectId: item.objectId });
    assert.equal(deleted.affectedObjects[0].objectId, item.objectId);
    assert.equal(deleted.affectedObjects[0].name, renamed);
    ctx.owned = ctx.owned.filter(owned => owned.objectId !== item.objectId);
    ctx.deleting = false;
    const inventory = await ctx.call('list_blocks', { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
    assert.equal(named(inventory, renamed).length, 0);
    const missing = await ctx.rawCall('get_block', { processId: ctx.processId, objectId: item.objectId, includeSource: false });
    assert.equal(missing.result.isError, true);
    assert.equal(missing.payload.error?.code, 'objectNotFound');
  });
}

async function cleanupOwned(ctx) {
  if (ctx.isWriteUncertain?.() || ctx.deleting) return ['Cleanup stopped after an uncertain write, lost context or failed deletion.'];
  const pending = [...(ctx.owned || [])];
  const failures = [];
  for (const item of pending) {
    if (ctx.isWriteUncertain?.()) { failures.push('Cleanup stopped after an uncertain write or lost context.'); break; }
    ctx.deleting = true;
    try {
      if (item.kind === 'blockObject') {
        const deleted = await ctx.call('delete_block', { processId: ctx.processId, objectId: item.objectId });
        assert.ok(deleted.affectedObjects?.some(object => object.kind === 'block' && object.objectId === item.objectId),
          'Cleanup deletion must identify the owned block.');
      } else {
        groupWrite(await ctx.call('delete_group', {
          processId: ctx.processId, plcObjectId: ctx.plcObjectId, kind: item.kind, groupPath: item.path
        }), 'delete_group', item.affectedKind, item.name);
      }
      ctx.owned = ctx.owned.filter(owned => owned !== item);
      ctx.deleting = false;
    } catch (error) { failures.push(error.message); break; }
  }
  return failures;
}

async function runGroupScenarios(ctx) {
  assert.match(ctx.prefix, /^McpGrp_[0-9a-f]{8}$/);
  ctx.owned = [];
  let failure = null;
  try {
    for (const spec of GROUPS) await exerciseGroup(ctx, spec);
    await exerciseBlock(ctx);
  } catch (error) { failure = error; }
  const leftovers = await cleanupOwned(ctx);
  if (ctx.fixture) ctx.fixture.cleanupErrors = leftovers;
  if (failure) throw failure;
  assert.equal(leftovers.length, 0, 'Fixture cleanup failed: ' + leftovers.join('; '));
}

module.exports = { GROUPS, walk, named, groupWrite, runGroupScenarios, blockSource };
