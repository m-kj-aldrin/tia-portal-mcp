'use strict';

// Live check of technology-object list/read/create/parameter write and delete_block.
// --catalogue-only checks list_available_technology_objects and does not create or delete.
// Without that flag, the script creates one disposable object and deletes only that object.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { optionsFrom, successful, runSuite } = require('./runner.cjs');

function walk(nodes) {
  return (nodes || []).flatMap(node => [node, ...walk(node.children)]);
}

const ok = successful;

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

async function runScenarios(ctx) {
  const { processId, plcObjectId, catalogueOnly } = ctx;
  const call = ctx.rawCall;
  const cpu = ctx.cpu;
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
  ctx.fixture.createdObject = createdObject;
  let createdId = createdObject.objectId;
  let deletionAttempted = false;
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

    deletionAttempted = true;
    const deleted = ok(await call('delete_block', { processId, objectId: createdId }), 'delete_block');
    createdId = null;
    ctx.fixture.deleted = true;
    assert.equal(deleted.saved, false);
    assert.equal(deleted.affectedObjects[0]?.objectId, createdObject.objectId);
    const remaining = ok(await call('list_technology_objects', { processId, plcObjectId }), 'list after delete');
    assert.equal(walk(remaining.roots).some(node => node.objectId === createdObject.objectId || node.name === createdName), false);
    const missing = await call('get_technology_object', { processId, objectId: createdObject.objectId });
    assert.equal(missing.result.isError, true);
    assert.equal(missing.payload.error.code, 'objectNotFound');
    console.log(`deleted ${createdName} ${createdObject.objectId}`);
  } finally {
    if (createdId && !deletionAttempted && !ctx.isWriteUncertain()) {
      deletionAttempted = true;
      const cleanup = await call('delete_block', { processId, objectId: createdId });
      ctx.fixture.cleanup = cleanup.payload;
      console.log('cleanup delete', JSON.stringify(cleanup.payload));
    }
  }
}

function markdown(report) {
  return '# Native technology-object acceptance\n\n' +
    `Result: **${report.status}**\n\nProject: ${report.projectPath}\n\nProcess: ${report.processId}\n\n` +
    report.steps.map(step => `- ${step.id}: ${step.status}${step.error ? ' — ' + step.error : ''}`).join('\n') +
    '\n\nRequests, raw responses, affected objects and fixture cleanup are in report.json. Successful full runs delete the created object; stopped runs can leave it for inspection. Catalogue-only runs perform reads. No save, compile, attachment change, online action or automatic retry is performed.\n' +
    (report.uncertainWrite || report.contextLost || report.contextUncertain ? '\nFurther writes and cleanup stop after an uncertain write or unreadable attachment context. Inspect the recorded project before another run.\n' : '');
}

function run(options, dependencies = {}) {
  const requiredTools = ['list_available_technology_objects'];
  if (!options.catalogueOnly) requiredTools.push('list_technology_objects', 'get_technology_object', 'get_block',
    'create_technology_object', 'set_technology_object_parameters', 'delete_block');
  return runSuite(options, { name: 'native-technology-objects', prefix: 'McpTO_', requiredTools,
    allocateAddress: false, markdown,
    runScenarios: ctx => ctx.step('technology-objects', options.catalogueOnly
      ? 'Verify the CPU-filtered technology catalogue.'
      : 'Read existing technology objects, create one, verify parameters and delete the owned fixture.', () => runScenarios(ctx)) }, dependencies);
}

async function main() {
  const options = optionsFrom(process.argv.slice(2), { 'catalogue-only': 'catalogueOnly' });
  if (options.help) {
    console.log('node tests/native-acceptance/technology-objects.cjs --process-id <PID> --project-path <absolute .ap20 path> [--catalogue-only]');
    console.log('Manually connect the disposable project first. Common CPU, endpoint, output and timeout options are supported.');
    return;
  }
  const { report, output } = await run(options);
  console.log(`${report.status.toUpperCase()}: ${output}`);
  if (report.error) console.error(report.error);
  process.exitCode = report.status === 'passed' ? 0 : 1;
}

if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });

module.exports = { run, runScenarios, assertCatalogue, familyOf, versionParts, versionAtLeast, markdown };
