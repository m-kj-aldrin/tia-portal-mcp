'use strict';

// Live check of technology-object list/read/create/parameter write and delete_block.
// Creates one disposable object and deletes only that object.
const assert = require('node:assert/strict');
const path = require('node:path');

const endpoint = 'http://127.0.0.1:5000/mcp';
const processId = 77188;
const projectPath = 'C:\\Users\\m\\Documents\\Robot och Automationsprogramerare\\OpennessDev\\tia-portal-mcp\\tia\\FillTank\\FillTank.ap20';
const timeoutMs = 120000;
let nextId = 0;

function walk(nodes) {
  return (nodes || []).flatMap(node => [node, ...walk(node.children)]);
}

async function rpc(method, params) {
  const id = ++nextId;
  const response = await fetch(endpoint, {
    method: 'POST', redirect: 'error',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
    body: JSON.stringify({ jsonrpc: '2.0', id, method, params }),
    signal: AbortSignal.timeout(timeoutMs)
  });
  const text = await response.text();
  assert.equal(response.status, 200, text);
  const envelope = JSON.parse(text);
  assert.equal(envelope.id, id);
  assert.ok(!envelope.error, JSON.stringify(envelope.error));
  return envelope.result;
}

async function call(name, args) {
  const result = await rpc('tools/call', { name, arguments: args });
  const text = result.content.find(item => item.type === 'text')?.text;
  assert.equal(typeof text, 'string', name);
  const payload = JSON.parse(text);
  return { result, payload };
}

function ok(response, name) {
  assert.equal(response.result.isError, false, `${name}: ${JSON.stringify(response.payload)}`);
  assert.equal(response.payload.errors?.length ?? 0, 0, `${name} errors: ${JSON.stringify(response.payload.errors)}`);
  assert.equal(response.payload.complete, true, name);
  return response.payload;
}

async function main() {
  const listing = await rpc('tools/list', {});
  const names = listing.tools.map(tool => tool.name);
  for (const name of ['list_technology_objects', 'get_technology_object', 'create_technology_object', 'set_technology_object_parameters', 'delete_block'])
    assert.ok(names.includes(name), `Missing ${name}`);

  const status = ok(await call('get_status', { processId }), 'get_status');
  assert.equal(status.state, 'connected');
  assert.equal(status.writeToolsAvailable, true);
  assert.equal(path.win32.normalize(status.project.path).toLowerCase(), path.win32.normalize(projectPath).toLowerCase());

  const devices = ok(await call('list_devices', { processId }), 'list_devices');
  let plcObjectId = null;
  for (const device of walk(devices.roots).filter(node => node.kind === 'device' && node.objectId)) {
    const details = ok(await call('get_device', { processId, objectId: device.objectId }), 'get_device');
    const cpu = walk(details.deviceItems).find(item => item.plcObjectId);
    if (cpu) { plcObjectId = cpu.plcObjectId; break; }
  }
  assert.ok(plcObjectId, 'No CPU plcObjectId');

  const inventory = ok(await call('list_technology_objects', { processId, plcObjectId }), 'list_technology_objects');
  const objects = walk(inventory.roots).filter(node => node.kind === 'technologyObject');
  assert.ok(objects.length >= 1, 'Expected existing technology objects');
  for (const item of objects) {
    assert.ok(item.objectId && item.name && item.ofSystemLibElement && item.ofSystemLibVersion, JSON.stringify(item));
  }
  const sample = objects.find(item => item.name === 'PID_Compact_Level') || objects[0];
  const read = ok(await call('get_technology_object', { processId, objectId: sample.objectId }), 'get_technology_object');
  assert.equal(read.metadata.objectId, sample.objectId);
  assert.equal(read.metadata.name, sample.name);
  assert.ok(Array.isArray(read.parameters), 'parameters must be an array');
  console.log(`existing ${sample.name}: ${read.parameters.length} parameters`);
  console.log(JSON.stringify(read.parameters.slice(0, 12), null, 2));

  const createdName = `McpTechnologyProbe${Date.now().toString(36)}`;
  const created = ok(await call('create_technology_object', {
    processId, plcObjectId, name: createdName,
    systemLibElement: sample.ofSystemLibElement,
    systemLibVersion: sample.ofSystemLibVersion
  }), 'create_technology_object');
  assert.equal(created.saved, false);
  const createdObject = created.affectedObjects.find(item => item.kind === 'technologyObject' && item.name === createdName);
  assert.ok(createdObject?.objectId, JSON.stringify(created.affectedObjects));
  let createdId = createdObject.objectId;
  try {
    const createdRead = ok(await call('get_technology_object', { processId, objectId: createdId }), 'get created');
    assert.equal(createdRead.metadata.name, createdName);
    assert.equal(createdRead.metadata.ofSystemLibElement, sample.ofSystemLibElement);
    const block = ok(await call('get_block', { processId, objectId: createdId, includeSource: false }), 'get_block created');
    assert.equal(block.metadata.blockType, 'DB');
    assert.equal(block.metadata.name, createdName);

    const settable = (createdRead.parameters.length ? createdRead.parameters : read.parameters)
      .filter(parameter => parameter.name && ['string', 'boolean', 'number'].includes(typeof parameter.value));
    assert.ok(settable.length >= 1, `No settable parameters. Created count=${createdRead.parameters.length}; sample count=${read.parameters.length}`);
    const assignments = settable.slice(0, 2).map(parameter => ({ name: parameter.name, value: parameter.value }));
    const written = ok(await call('set_technology_object_parameters', {
      processId, objectId: createdId, parameters: assignments
    }), 'set_technology_object_parameters');
    assert.equal(written.saved, false);
    assert.ok(written.affectedObjects.length >= 1, JSON.stringify(written));
    const after = ok(await call('get_technology_object', { processId, objectId: createdId }), 'read back parameters');
    for (const assignment of assignments) {
      const found = after.parameters.find(parameter => parameter.name === assignment.name);
      assert.ok(found, assignment.name);
      assert.equal(found.value, assignment.value);
    }
    console.log(`set ${assignments.map(item => item.name).join(', ')} on ${createdName}`);

    const deleted = ok(await call('delete_block', { processId, objectId: createdId }), 'delete_block');
    createdId = null;
    assert.equal(deleted.saved, false);
    assert.equal(deleted.affectedObjects[0]?.objectId, createdObject.objectId);
    const remaining = ok(await call('list_technology_objects', { processId, plcObjectId }), 'list after delete');
    assert.equal(walk(remaining.roots).some(node => node.objectId === createdObject.objectId || node.name === createdName), false);
    const missing = await call('get_technology_object', { processId, objectId: createdObject.objectId });
    assert.equal(missing.result.isError, true);
    assert.equal(missing.payload.error.code, 'objectNotFound');
    console.log(`deleted ${createdName} ${createdObject.objectId}`);
  } finally {
    if (createdId) {
      const cleanup = await call('delete_block', { processId, objectId: createdId });
      console.log('cleanup delete', JSON.stringify(cleanup.payload));
    }
  }
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
