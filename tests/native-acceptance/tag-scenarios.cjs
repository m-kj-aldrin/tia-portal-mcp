'use strict';

const assert = require('node:assert/strict');

function nonblankId(value, description) {
  assert.equal(typeof value, 'string', `${description} must have a native string objectId.`);
  assert.ok(value.trim().length, `${description} must have a nonblank native objectId.`);
  return value;
}

function inventoryTables(payload) {
  assert.ok(Array.isArray(payload.roots), 'list_tag_tables must return roots.');
  const tables = [];
  function visit(nodes) {
    for (const node of nodes) {
      if (node.kind === 'tagTable') tables.push(node);
      if (Array.isArray(node.children)) visit(node.children);
    }
  }
  visit(payload.roots);
  return tables;
}

function affectedObject(payload, operation, kind, name) {
  assert.equal(payload.operation, operation);
  assert.equal(payload.saved, false, 'Native acceptance must never save the project.');
  assert.equal(payload.cleanupFailed, false, 'A successful write must report successful cleanup.');
  assert.ok(Array.isArray(payload.affectedObjects), 'A write must report affectedObjects.');
  assert.equal(payload.affectedObjects.length, 1, `${operation} should affect one test-owned object.`);
  const item = payload.affectedObjects[0];
  assert.equal(item.kind, kind);
  assert.equal(item.name, name);
  nonblankId(item.objectId, name);
  return item;
}

function tableEntries(payload, tableId, tableName) {
  assert.equal(payload.metadata.objectId, tableId);
  assert.equal(payload.metadata.name, tableName);
  assert.ok(payload.entries && typeof payload.entries === 'object', 'Entry-enabled read must return entries.');
  for (const key of ['tags', 'userConstants', 'systemConstants']) {
    assert.ok(Array.isArray(payload.entries[key]), `${key} must be a readable collection.`);
  }
  return payload.entries;
}

function entryNamed(entries, collection, name) {
  const matches = entries[collection].filter(entry => entry.name === name);
  assert.equal(matches.length, 1, `Expected exactly one ${name} in the test-owned table's ${collection}.`);
  nonblankId(matches[0].objectId, name);
  return matches[0];
}

async function runTagScenarios(ctx) {
  const { processId, plcObjectId, prefix } = ctx;
  assert.ok(ctx.fixture && typeof ctx.fixture === 'object', 'The runner must supply fixture tracking.');
  assert.equal(typeof ctx.logicalAddress, 'string', 'The runner must supply a reviewed, unused Bool logicalAddress.');
  assert.ok(ctx.logicalAddress.trim().length, 'The test tag requires an explicit logicalAddress.');
  const tableName = `${prefix}_Tags`;
  const tagName = `${prefix}_Signal`;
  const constantName = `${prefix}_Limit`;
  const disposableTagName = `${prefix}_DeleteTag`;
  const disposableName = `${prefix}_DeleteMe`;
  Object.assign(ctx.fixture, { tableName, tagName, constantName });
  let tableId;
  let initialVisible;
  const read = async (includeEntries = true, includePath = true) => ctx.call('get_tag_table', {
    processId, objectId: tableId, includeEntries, includePath
  });
  const entries = async () => tableEntries(await read(), tableId, tableName);

  await ctx.step('tags.create-table', 'Create a unique tag table and find its native identity in inventory.', async () => {
    const before = inventoryTables(await ctx.call('list_tag_tables', { processId, plcObjectId }));
    assert.ok(!before.some(table => table.name === tableName), 'The fixture table name already exists; refusing to reuse it.');
    const created = affectedObject(await ctx.call('create_tag_table', {
      processId, plcObjectId, name: tableName
    }), 'create_tag_table', 'tagTable', tableName);
    tableId = created.objectId;
    ctx.fixture.tableId = tableId;
    assert.ok(!before.some(table => table.objectId === tableId), 'The new table must not reuse a pre-existing table.');
    const after = inventoryTables(await ctx.call('list_tag_tables', { processId, plcObjectId }));
    const found = after.filter(table => table.objectId === tableId);
    assert.equal(found.length, 1, 'The created table must appear once by its returned native ID.');
    assert.equal(found[0].name, tableName);
    const empty = await entries();
    assert.equal(empty.tags.length, 0);
    assert.equal(empty.userConstants.length, 0);
  });

  await ctx.step('tags.metadata-only', 'Read the new table with entries and path disabled.', async () => {
    const metadataOnly = await read(false, false);
    assert.equal(metadataOnly.metadata.objectId, tableId);
    assert.equal(metadataOnly.metadata.name, tableName);
    assert.equal(metadataOnly.metadata.path, null);
    assert.equal(metadataOnly.entries, null);
  });

  await ctx.step('tags.delete-tag', 'Create and delete a separate Bool tag, then verify its absence before reusing its address.', async () => {
    const created = affectedObject(await ctx.call('create_tag', {
      processId, objectId: tableId, name: disposableTagName, dataType: 'Bool', logicalAddress: ctx.logicalAddress
    }), 'create_tag', 'tag', disposableTagName);
    ctx.fixture.disposableTagId = created.objectId;
    assert.equal(created.parentObjectId, tableId);
    const disposable = entryNamed(await entries(), 'tags', disposableTagName);
    assert.equal(disposable.objectId, created.objectId);
    assert.equal(disposable.dataType, 'Bool');
    assert.equal(disposable.logicalAddress, ctx.logicalAddress);
    const deleted = affectedObject(await ctx.call('delete_tag_entry', {
      processId, objectId: disposable.objectId
    }), 'delete_tag_entry', 'tag', disposableTagName);
    assert.equal(deleted.objectId, disposable.objectId);
    assert.equal(deleted.parentObjectId, tableId);
    const after = await entries();
    assert.ok(!after.tags.some(entry => entry.objectId === disposable.objectId || entry.name === disposableTagName),
      'Deleted tag must be absent by both ID and name.');
    ctx.fixture.disposableTagDeleted = true;
  });

  await ctx.step('tags.create-tag', 'Create a Bool tag and verify its ID, type and logical address through get_tag_table.', async () => {
    const created = affectedObject(await ctx.call('create_tag', {
      processId, objectId: tableId, name: tagName, dataType: 'Bool', logicalAddress: ctx.logicalAddress
    }), 'create_tag', 'tag', tagName);
    ctx.fixture.tagId = created.objectId;
    assert.equal(created.parentObjectId, tableId);
    const tag = entryNamed(await entries(), 'tags', tagName);
    assert.equal(tag.objectId, created.objectId);
    assert.notEqual(tag.objectId, tableId);
    assert.equal(tag.dataType, 'Bool');
    assert.equal(tag.logicalAddress, ctx.logicalAddress);
    assert.equal(typeof tag.typeSpecific.ExternalVisible, 'boolean', 'The fixture tag must expose native ExternalVisible.');
    initialVisible = tag.typeSpecific.ExternalVisible;
    ctx.fixture.tagId = tag.objectId;
  });

  await ctx.step('tags.create-constant', 'Create a populated Int user constant and verify its native identity and value.', async () => {
    const created = affectedObject(await ctx.call('create_user_constant', {
      processId, objectId: tableId, name: constantName, dataType: 'Int', value: '100'
    }), 'create_user_constant', 'userConstant', constantName);
    ctx.fixture.constantId = created.objectId;
    assert.equal(created.parentObjectId, tableId);
    const constant = entryNamed(await entries(), 'userConstants', constantName);
    assert.equal(constant.objectId, created.objectId);
    assert.notEqual(constant.objectId, tableId);
    assert.notEqual(constant.objectId, ctx.fixture.tagId);
    assert.equal(constant.dataType, 'Int');
    assert.equal(constant.value, '100');
    ctx.fixture.constantId = constant.objectId;
  });

  await ctx.step('tags.edit-boolean', 'Change the test tag ExternalVisible boolean and read back the changed value.', async () => {
    const before = entryNamed(await entries(), 'tags', tagName);
    assert.equal(before.objectId, ctx.fixture.tagId, 'The tag identity changed; refusing to edit a replacement entry.');
    const changed = affectedObject(await ctx.call('set_tag_entry_attribute', {
      processId, objectId: before.objectId, attributeName: 'ExternalVisible', attributeValue: !initialVisible
    }), 'set_tag_entry_attribute', 'tag', tagName);
    assert.equal(changed.parentObjectId, tableId);
    const after = entryNamed(await entries(), 'tags', tagName);
    assert.equal(after.objectId, changed.objectId);
    assert.equal(after.typeSpecific.ExternalVisible, !initialVisible);
    assert.equal(after.dataType, before.dataType);
    assert.equal(after.logicalAddress, before.logicalAddress);
    ctx.fixture.tagId = after.objectId;
  });

  await ctx.step('tags.edit-constant', 'Change the test constant Value from 100 to 200 and read it back.', async () => {
    const before = entryNamed(await entries(), 'userConstants', constantName);
    assert.equal(before.objectId, ctx.fixture.constantId, 'The constant identity changed; refusing to edit a replacement entry.');
    const changed = affectedObject(await ctx.call('set_tag_entry_attribute', {
      processId, objectId: before.objectId, attributeName: 'Value', attributeValue: '200'
    }), 'set_tag_entry_attribute', 'userConstant', constantName);
    assert.equal(changed.parentObjectId, tableId);
    const after = entryNamed(await entries(), 'userConstants', constantName);
    assert.equal(after.objectId, changed.objectId);
    assert.equal(after.dataType, 'Int');
    assert.equal(after.value, '200');
    ctx.fixture.constantId = after.objectId;
  });

  await ctx.step('tags.native-error', 'Reject an unknown native attribute on the test tag, then verify its readable state is unchanged.', async () => {
    const before = entryNamed(await entries(), 'tags', tagName);
    assert.equal(before.objectId, ctx.fixture.tagId, 'The tag identity changed; refusing the negative test on a replacement entry.');
    const { result, payload } = await ctx.rawCall('set_tag_entry_attribute', {
      processId, objectId: before.objectId, attributeName: `${prefix}_NoSuchAttribute`, attributeValue: true
    });
    assert.equal(result.isError, true, 'An invalid native attribute must return MCP isError.');
    assert.equal(payload.complete, false);
    assert.equal(payload.operation, 'set_tag_entry_attribute');
    assert.equal(payload.saved, false);
    assert.equal(payload.cleanupFailed, false);
    assert.ok(Array.isArray(payload.errors));
    assert.ok(payload.errors.some(error => error.origin === 'tia-openness' &&
      error.operation === 'set_tag_entry_attribute' && typeof error.message === 'string' && error.message.trim()),
    'The invalid attribute must preserve a nonempty native tia-openness error.');
    const after = entryNamed(await entries(), 'tags', tagName);
    assert.deepEqual(after, before, 'The failed attribute write must leave the test tag readable and unchanged.');
    ctx.fixture.tagId = after.objectId;
  });

  await ctx.step('tags.delete-entry', 'Create and delete a separate user constant, then verify its absence and the retained fixtures.', async () => {
    const created = affectedObject(await ctx.call('create_user_constant', {
      processId, objectId: tableId, name: disposableName, dataType: 'Int', value: '7'
    }), 'create_user_constant', 'userConstant', disposableName);
    ctx.fixture.disposableConstantId = created.objectId;
    assert.equal(created.parentObjectId, tableId);
    const populated = await entries();
    const disposable = entryNamed(populated, 'userConstants', disposableName);
    assert.equal(disposable.objectId, created.objectId);
    assert.equal(disposable.value, '7');
    const retainedTag = entryNamed(populated, 'tags', tagName);
    const retainedConstant = entryNamed(populated, 'userConstants', constantName);
    const deleted = affectedObject(await ctx.call('delete_tag_entry', {
      processId, objectId: disposable.objectId
    }), 'delete_tag_entry', 'userConstant', disposableName);
    assert.equal(deleted.objectId, disposable.objectId);
    assert.equal(deleted.parentObjectId, tableId);
    const after = await entries();
    assert.ok(!after.userConstants.some(entry => entry.objectId === disposable.objectId || entry.name === disposableName),
      'Deleted user constant must be absent by both ID and name.');
    assert.deepEqual(entryNamed(after, 'tags', tagName), retainedTag);
    assert.deepEqual(entryNamed(after, 'userConstants', constantName), retainedConstant);
    ctx.fixture.disposableConstantDeleted = true;
  });

  // Whole-table deletion is not exposed. Retained entries support block/cross-reference
  // follow-on scenarios; the report identifies everything left in the disposable project.
  return { fixtures: ctx.fixture };
}

module.exports = { runTagScenarios, inventoryTables, entryNamed };
