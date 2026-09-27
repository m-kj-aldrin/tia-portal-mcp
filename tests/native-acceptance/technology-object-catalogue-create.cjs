'use strict';

// Opt-in live check. Not part of the regular suite: run this file yourself.
// It creates every technology object returned by list_available_technology_objects
// and leaves those objects in the project. It does not save or compile.
//
// node tests/native-acceptance/technology-object-catalogue-create.cjs --process-id <pid> --project-path <absolute .ap20 path>
const assert = require('node:assert/strict');
const { optionsFrom, runSuite, successful } = require('./runner.cjs');

function libraryVersion(cell) {
  const match = String(cell ?? '').match(/\d+(?:\.\d+)?/);
  if (!match) return null;
  const [major, minor = '0'] = match[0].split('.');
  return `${Number(major)}.${Number(minor)}`;
}

function failureText(response, name) {
  const payload = response.payload;
  if (Array.isArray(payload?.errors) && payload.errors.length) return JSON.stringify(payload.errors);
  if (payload?.error) return JSON.stringify(payload.error);
  if (response.result?.isError) return JSON.stringify(payload);
  if (name) {
    try { successful(response, name); }
    catch (error) { return error.message; }
  }
  return null;
}

async function runScenarios(ctx) {
  const { processId, plcObjectId } = ctx;
  const call = ctx.rawCall;
  const available = await ctx.call('list_available_technology_objects', { processId, plcObjectId });
  const rows = available.technologyObjects;
  console.log(`${available.cpuFamily} ${available.typeName} ${available.firmwareVersion}: ${rows.length} catalogue rows`);
  const failures = [];
  ctx.fixture.catalogueRows = rows;
  ctx.fixture.failures = failures;
  ctx.fixture.createdObjects = [];
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
    assert.ok(!ctx.isWriteUncertain(), 'Creation lost its attachment context; stop before another catalogue write.');
    const createdError = failureText(created, 'create_technology_object');
    const createdObject = created.payload.affectedObjects?.find(item => item.kind === 'technologyObject' && item.name === name);
    if (createdError || created.result.isError || created.payload.complete !== true || created.payload.saved !== false || !createdObject?.objectId) {
      failures.push({ name, systemLibElement: row.systemLibElement, version, error: createdError || JSON.stringify(created.payload) });
      console.log(`failed ${name} ${version}: ${createdError || 'create did not return the object'}`);
      continue;
    }
    ctx.fixture.createdObjects.push(createdObject);
    const read = await call('get_technology_object', { processId, objectId: createdObject.objectId, includeParameters: false });
    assert.ok(!ctx.isWriteUncertain(), 'Read-back lost its attachment context; stop before another catalogue write.');
    const readError = failureText(read, 'get_technology_object');
    if (readError || read.result.isError || read.payload.metadata?.objectId !== createdObject.objectId ||
      read.payload.metadata?.name !== name || read.payload.metadata?.ofSystemLibElement !== row.systemLibElement) {
      failures.push({ name, systemLibElement: row.systemLibElement, version, objectId: createdObject.objectId, error: readError || JSON.stringify(read.payload.metadata) });
      console.log(`left ${name} ${createdObject.objectId}; read-back failed: ${readError || 'metadata did not match'}`);
      continue;
    }
    console.log(`left ${name} ${version} ${createdObject.objectId}`);
  }
  console.log(`${rows.length - failures.length} created, ${failures.length} failed`);
  if (failures.length) {
    throw new Error(`${failures.length} catalogue creation/read-back cases failed: ${JSON.stringify(failures)}`);
  }
}

function markdown(report) {
  return '# Native technology catalogue creation\n\n' +
    `Result: **${report.status}**\n\nProject: ${report.projectPath}\n\nProcess: ${report.processId}\n\n` +
    report.steps.map(step => `- ${step.id}: ${step.status}${step.error ? ' — ' + step.error : ''}`).join('\n') +
    '\n\nEach attempted catalogue row is submitted once; stopped runs can leave later rows unattempted. Created objects remain in the disposable project; report.json retains their identities, row failures, raw responses and partial affected objects. No deletion, save, compile, attachment change, online action or automatic retry is performed.\n' +
    (report.uncertainWrite || report.contextLost || report.contextUncertain ? '\nCreation stops after an uncertain write or unreadable attachment context. Inspect the recorded project before another run.\n' : '');
}

function run(options, dependencies = {}) {
  return runSuite(options, { name: 'native-technology-object-catalogue', prefix: 'McpTC_',
    requiredTools: ['list_available_technology_objects', 'create_technology_object', 'get_technology_object'],
    allocateAddress: false, markdown,
    runScenarios: ctx => ctx.step('catalogue-create', 'Create each CPU-supported catalogue row once and read its metadata.', () => runScenarios(ctx)) }, dependencies);
}

async function main() {
  const options = optionsFrom(process.argv.slice(2));
  if (options.help) {
    console.log('node tests/native-acceptance/technology-object-catalogue-create.cjs --process-id <PID> --project-path <absolute .ap20 path>');
    console.log('Manually connect a disposable project first. Creates every supported catalogue row and leaves the objects for inspection.');
    console.log('Common CPU, endpoint, output and timeout options are supported. Never saves, compiles or retries.');
    return;
  }
  const { report, output } = await run(options);
  console.log(`${report.status.toUpperCase()}: ${output}`);
  if (report.error) console.error(report.error);
  process.exitCode = report.status === 'passed' ? 0 : 1;
}

if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });

module.exports = { run, runScenarios, libraryVersion, failureText, markdown };
