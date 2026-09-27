'use strict';

// Live check of technology-object list/read/create/parameter write and delete_block.
// --catalogue-only checks list_available_technology_objects and does not create or delete.
// Without that flag, the script creates one disposable object and deletes only that object.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

function option(name, fallback) {
  const index = process.argv.indexOf(name);
  if (index < 0) return fallback;
  const value = process.argv[index + 1];
  assert.ok(value && !value.startsWith('--'), `${name} needs a value`);
  return value;
}

const endpoint = 'http://127.0.0.1:5000/mcp';
const processId = Number(option('--process-id', '77188'));
const projectPath = option('--project-path', 'C:\\Users\\m\\Documents\\Robot och Automationsprogramerare\\OpennessDev\\tia-portal-mcp\\tia\\FillTank\\FillTank.ap20');
const catalogueOnly = process.argv.includes('--catalogue-only');
const timeoutMs = 120000;
assert.ok(Number.isInteger(processId) && processId > 0, '--process-id must be a positive integer');
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

function familyOf(cpu) {
  const blob = [cpu.typeName, cpu.typeIdentifier, cpu.orderNumber].filter(Boolean).join(' ');
  if (/1200\s*G2|12\d{2}[A-Z0-9]*\s*G2|S71200G2/i.test(blob)) return 'S7-1200 G2';
  if (/S7-?1200|S71200|CPU\s*12\d{2}|6ES7\s*2/i.test(blob)) return 'S7-1200';
  if (/S7-?1500|S71500|CPU\s*15\d{2}|6ES7\s*5/i.test(blob)) return 'S7-1500';
  if (/S7-?300|S7-?400|CPU\s*[34]\d{2}|6ES7\s*[34]/i.test(blob)) return 'S7-300/400';
  return null;
}

function versionParts(text) {
  const match = String(text ?? '').match(/\d+(?:\.\d+){0,3}/);
  if (!match) return null;
  const parts = match[0].split('.').map(Number);
  while (parts.length < 2) parts.push(0);
  return parts;
}

function versionAtLeast(cpu, required) {
  const length = Math.max(cpu.length, required.length);
  for (let index = 0; index < length; index++) {
    const left = cpu[index] || 0;
    const right = required[index] || 0;
    if (left !== right) return left > right;
  }
  return true;
}

function assertCatalogue(cpu, available) {
  const catalogue = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', 'data', 'technology-object-catalogue.json'), 'utf8'));
  const family = familyOf(cpu);
  const technologyCpu = family === 'S7-1500' && /15\d{2}T/i.test([cpu.typeName, cpu.orderNumber].filter(Boolean).join(' '));
  const firmware = versionParts(cpu.firmwareVersion);
  assert.equal(available.plcObjectId, cpu.plcObjectId);
  assert.equal(available.typeName, cpu.typeName);
  assert.equal(available.firmwareVersion, cpu.firmwareVersion);
  assert.equal(available.cpuFamily, family, 'The live CPU family did not match the catalogue rules.');
  assert.equal(available.technologyCpu, technologyCpu);
  assert.equal(typeof available.catalogueDescription, 'string');
  assert.ok(available.catalogueDescription.length > 0);
  const expected = catalogue.objects.filter(row => {
    if (row.cpu !== family) return false;
    if (/(\(S7-1500T\))/i.test(row.name) && !technologyCpu) return false;
    if (String(row.firmware).trim().toLowerCase() === 'any') return true;
    const required = versionParts(row.firmware);
    return firmware != null && required != null && versionAtLeast(firmware, required);
  });
  assert.deepEqual(available.technologyObjects.map(item => item.name), expected.map(row => row.name), 'The live catalogue rows did not match the committed JSON for this CPU.');
  for (const item of available.technologyObjects) {
    const row = expected.find(candidate => candidate.name === item.name);
    assert.equal(item.technology, row.technology);
    assert.equal(item.version, row.version);
    assert.equal(item.firmware, row.firmware);
    assert.equal(item.systemLibElement, item.name.replace(/\s*\(S7-1500T\)\s*$/i, ''));
    assert.deepEqual(item.notes, row.notes);
  }
}

async function main() {
  const listing = await rpc('tools/list', {});
  const names = listing.tools.map(tool => tool.name);
  const required = ['list_technology_objects', 'list_available_technology_objects', 'get_technology_object'];
  if (!catalogueOnly) required.push('create_technology_object', 'set_technology_object_parameters', 'delete_block');
  for (const name of required)
    assert.ok(names.includes(name), `Missing ${name}`);

  const status = ok(await call('get_status', { processId }), 'get_status');
  assert.equal(status.state, 'connected');
  assert.equal(status.writeToolsAvailable, true);
  assert.equal(path.win32.normalize(status.project.path).toLowerCase(), path.win32.normalize(projectPath).toLowerCase());

  const devices = ok(await call('list_devices', { processId }), 'list_devices');
  let cpu = null;
  for (const device of walk(devices.roots).filter(node => node.kind === 'device' && node.objectId)) {
    const details = ok(await call('get_device', { processId, objectId: device.objectId }), 'get_device');
    cpu = walk(details.deviceItems).find(item => item.plcObjectId) || null;
    if (cpu) break;
  }
  assert.ok(cpu?.plcObjectId, 'No CPU plcObjectId');
  const plcObjectId = cpu.plcObjectId;
  const available = ok(await call('list_available_technology_objects', { processId, plcObjectId }), 'list_available_technology_objects');
  assertCatalogue(cpu, available);
  console.log(`${available.cpuFamily} ${available.typeName} ${available.firmwareVersion} technologyCpu=${available.technologyCpu}: ${available.technologyObjects.map(item => item.systemLibElement).join(', ')}`);
  if (catalogueOnly) return;

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
