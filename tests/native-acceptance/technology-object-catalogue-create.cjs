'use strict';

// Opt-in live check. Not part of the regular suite: run this file yourself.
// It creates every technology object returned by list_available_technology_objects
// and leaves those objects in the project. It does not save or compile.
//
// node tests/native-acceptance/technology-object-catalogue-create.cjs --process-id <pid> --project-path <absolute .ap20 path>
const assert = require('node:assert/strict');
const path = require('node:path');

function option(name) {
  const index = process.argv.indexOf(name);
  const value = index >= 0 ? process.argv[index + 1] : '';
  assert.ok(value && !value.startsWith('--'), `${name} is required.`);
  return value;
}

const endpoint = 'http://127.0.0.1:5000/mcp';
const processId = Number(option('--process-id'));
const projectPath = option('--project-path');
const timeoutMs = 120000;
assert.ok(Number.isInteger(processId) && processId > 0, '--process-id must be a positive integer.');
assert.ok(path.win32.isAbsolute(projectPath), '--project-path must be absolute.');
let nextId = 0;

function walk(nodes) {
  return (nodes || []).flatMap(node => [node, ...walk(node.children)]);
}

function libraryVersion(cell) {
  const match = String(cell ?? '').match(/\d+(?:\.\d+)?/);
  if (!match) return null;
  const [major, minor = '0'] = match[0].split('.');
  return `${Number(major)}.${Number(minor)}`;
}

function failureText(response) {
  const payload = response.payload;
  if (Array.isArray(payload?.errors) && payload.errors.length) return JSON.stringify(payload.errors);
  if (payload?.error) return JSON.stringify(payload.error);
  if (response.result?.isError) return JSON.stringify(payload);
  return null;
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
  return { result, payload: JSON.parse(text) };
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
  for (const name of ['list_available_technology_objects', 'create_technology_object', 'get_technology_object'])
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

  const available = ok(await call('list_available_technology_objects', { processId, plcObjectId }), 'list_available_technology_objects');
  const rows = available.technologyObjects;
  console.log(`${available.cpuFamily} ${available.typeName} ${available.firmwareVersion}: ${rows.length} catalogue rows`);
  const failures = [];
  for (const row of rows) {
    const version = libraryVersion(row.version);
    const name = `Mcp_${row.systemLibElement}`;
    if (!version) {
      failures.push({ name, systemLibElement: row.systemLibElement, version: row.version, error: 'The catalogue version cell has no major.minor number.' });
      console.log(`failed ${name}: no version in ${JSON.stringify(row.version)}`);
      continue;
    }
    const created = await call('create_technology_object', {
      processId, plcObjectId, name,
      systemLibElement: row.systemLibElement,
      systemLibVersion: version
    });
    const createdError = failureText(created);
    const createdObject = created.payload.affectedObjects?.find(item => item.kind === 'technologyObject' && item.name === name);
    if (createdError || created.result.isError || created.payload.complete !== true || created.payload.saved !== false || !createdObject?.objectId) {
      failures.push({ name, systemLibElement: row.systemLibElement, version, error: createdError || JSON.stringify(created.payload) });
      console.log(`failed ${name} ${version}: ${createdError || 'create did not return the object'}`);
      continue;
    }
    const read = await call('get_technology_object', { processId, objectId: createdObject.objectId, includeParameters: false });
    const readError = failureText(read);
    if (readError || read.result.isError || read.payload.metadata?.name !== name || read.payload.metadata?.ofSystemLibElement !== row.systemLibElement) {
      failures.push({ name, systemLibElement: row.systemLibElement, version, objectId: createdObject.objectId, error: readError || JSON.stringify(read.payload.metadata) });
      console.log(`left ${name} ${createdObject.objectId}; read-back failed: ${readError || 'metadata did not match'}`);
      continue;
    }
    console.log(`left ${name} ${version} ${createdObject.objectId}`);
  }
  console.log(`${rows.length - failures.length} created, ${failures.length} failed`);
  if (failures.length) {
    console.error(JSON.stringify(failures, null, 2));
    process.exitCode = 1;
  }
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
