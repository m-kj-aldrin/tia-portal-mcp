'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const { createHash, randomBytes } = require('node:crypto');
const { createContext, preflight, prepareOutput, targetStatus } = require('./runner.cjs');
const { runSourceScenarios } = require('./source-scenarios.cjs');
const { inventoryTables, entryNamed } = require('./tag-scenarios.cjs');
const { writeReport } = require('./report-writer.cjs');

const REQUIRED_TOOLS = ['compile_plc', 'delete_block', 'delete_udt', 'delete_tag_table', 'export_tag_table'];
const now = () => new Date().toISOString();
const walk = nodes => (nodes || []).flatMap(node => [node, ...walk(node.children)]);
const digest = content => createHash('sha256').update(content, 'utf8').digest('hex');

function affected(payload, operation, kind, name) {
  assert.equal(payload.operation, operation);
  assert.equal(payload.saved, false);
  assert.equal(payload.cleanupFailed, false);
  assert.equal(payload.affectedObjects?.length, 1, 'A fixture write must affect exactly one owned object.');
  const item = payload.affectedObjects[0];
  assert.equal(item.kind, kind);
  assert.equal(item.name, name);
  assert.equal(typeof item.objectId, 'string');
  assert.ok(item.objectId.trim().length, 'A native identity is required before subsequent fixture mutations.');
  return item;
}

async function tables(ctx) {
  const read = await ctx.call('list_tag_tables', { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
  assert.equal(read.plcObjectId, ctx.plcObjectId);
  return inventoryTables(read);
}

async function ownedTable(ctx) {
  const fixture = ctx.fixture;
  const matches = (await tables(ctx)).filter(item => item.name?.toLowerCase() === fixture.tableName.toLowerCase());
  assert.equal(matches.length, 1, 'The owned table must remain unique.');
  assert.equal(matches[0].name, fixture.tableName);
  assert.equal(matches[0].objectId, fixture.tableId, 'Refusing to mutate a same-name replacement table.');
  const read = await ctx.call('get_tag_table', { processId: ctx.processId, objectId: fixture.tableId, includeEntries: true, includePath: false });
  assert.equal(read.metadata.objectId, fixture.tableId);
  assert.equal(read.metadata.name, fixture.tableName);
  for (const key of ['tags', 'userConstants', 'systemConstants']) assert.ok(Array.isArray(read.entries?.[key]));
  return read.entries;
}

function populatedTable(ctx, entries) {
  assert.equal(entries.tags.length, 1);
  assert.equal(entries.userConstants.length, 1);
  assert.equal(entries.systemConstants.length, 0);
  const tag = entryNamed(entries, 'tags', ctx.fixture.tagName);
  const constant = entryNamed(entries, 'userConstants', ctx.fixture.constantName);
  assert.equal(tag.dataType, 'Bool');
  assert.equal(tag.logicalAddress, ctx.logicalAddress);
  assert.equal(constant.dataType, 'Int');
  assert.equal(constant.value, '37');
  return { tag, constant };
}

function compilerMessages(messages) {
  assert.ok(Array.isArray(messages), 'Compiler diagnostics must be a readable native hierarchy.');
  return messages.flatMap(message => {
    for (const field of ['path', 'description']) assert.ok(message[field] === null || typeof message[field] === 'string', `Compiler ${field} must preserve its native string or null value.`);
    for (const field of ['dateTime', 'state']) assert.equal(typeof message[field], 'string', `Compiler ${field} must be readable.`);
    assert.ok(Number.isFinite(Date.parse(message.dateTime)), 'Compiler timestamps must be parseable.');
    assert.match(message.dateTime, /Z$/, 'Compiler timestamps must be returned in UTC.');
    for (const field of ['errorCount', 'warningCount']) assert.ok(Number.isInteger(message[field]) && message[field] >= 0);
    return [message, ...compilerMessages(message.messages)];
  });
}

function assertCompilation(response, ctx, expectedSuccess) {
  const { result, payload } = response;
  assert.equal(result.isError, !expectedSuccess, 'MCP must distinguish a compiler error from compilation success.');
  assert.equal(payload.processId, ctx.processId);
  assert.equal(payload.plcObjectId, ctx.plcObjectId);
  assert.equal(payload.operation, 'compile_plc');
  assert.equal(payload.saved, false);
  assert.equal(payload.complete, true, 'Native compiler errors must still return complete diagnostics.');
  assert.deepEqual(payload.errors, [], 'Compiler messages must not be disguised as bridge/API failures.');
  assert.equal(payload.compilationSucceeded, expectedSuccess);
  assert.ok(['Success', 'Information', 'Warning', 'Error'].includes(payload.state));
  assert.ok(Number.isInteger(payload.errorCount) && payload.errorCount >= 0);
  assert.ok(Number.isInteger(payload.warningCount) && payload.warningCount >= 0);
  assert.ok(Number.isFinite(Date.parse(payload.readAtUtc)));
  const messages = compilerMessages(payload.messages);
  if (expectedSuccess) {
    assert.notEqual(payload.state, 'Error');
    assert.equal(payload.errorCount, 0, 'Unrelated compile errors must stop this test before it changes the tag reference.');
  } else {
    assert.equal(payload.state, 'Error');
    assert.ok(payload.errorCount > 0, 'The missing fixture tag must cause a genuine native compiler error.');
    const tagName = ctx.fixture.tagName.toLowerCase();
    const blockName = ctx.fixture.blockName.toLowerCase();
    assert.ok(messages.some(message => message.errorCount > 0 &&
      (String(message.description || '').toLowerCase().includes(tagName) || String(message.path || '').toLowerCase().includes(blockName))),
    'Compiler errors must identify the deliberately broken fixture tag or FC.');
  }
  return { state: payload.state, compilationSucceeded: payload.compilationSucceeded,
    errorCount: payload.errorCount, warningCount: payload.warningCount, messageCount: messages.length };
}

async function compile(ctx, expectedSuccess) {
  return assertCompilation(await ctx.rawCall('compile_plc', { processId: ctx.processId, plcObjectId: ctx.plcObjectId }), ctx, expectedSuccess);
}

async function createTable(ctx) {
  const { processId, plcObjectId, prefix } = ctx;
  Object.assign(ctx.fixture, { tableName: `${prefix}_Tags`, tagName: `${prefix}_Signal`, constantName: `${prefix}_Limit` });
  await ctx.step('fixtures.table-create', 'Create one owned table with a Bool tag and Int user constant, then verify typed entries.', async () => {
    assert.ok(!(await tables(ctx)).some(item => item.name?.toLowerCase() === ctx.fixture.tableName.toLowerCase()), 'A same-name table already exists.');
    const created = affected(await ctx.call('create_tag_table', { processId, plcObjectId, name: ctx.fixture.tableName }), 'create_tag_table', 'tagTable', ctx.fixture.tableName);
    ctx.fixture.tableId = created.objectId;
    const empty = await ownedTable(ctx);
    assert.ok(Object.values(empty).every(entries => entries.length === 0));
    const tag = affected(await ctx.call('create_tag', { processId, objectId: ctx.fixture.tableId,
      name: ctx.fixture.tagName, dataType: 'Bool', logicalAddress: ctx.logicalAddress }), 'create_tag', 'tag', ctx.fixture.tagName);
    assert.equal(tag.parentObjectId, ctx.fixture.tableId);
    ctx.fixture.tagId = tag.objectId;
    await ownedTable(ctx);
    const constant = affected(await ctx.call('create_user_constant', { processId, objectId: ctx.fixture.tableId,
      name: ctx.fixture.constantName, dataType: 'Int', value: '37' }), 'create_user_constant', 'userConstant', ctx.fixture.constantName);
    assert.equal(constant.parentObjectId, ctx.fixture.tableId);
    ctx.fixture.constantId = constant.objectId;
    const entries = populatedTable(ctx, await ownedTable(ctx));
    assert.equal(entries.tag.objectId, ctx.fixture.tagId);
    assert.equal(entries.constant.objectId, ctx.fixture.constantId);
  });
}

async function exportRoundtrip(ctx) {
  await ctx.step('tags.xml-export-roundtrip', 'Export the populated owned table as exact SimaticML, import it and verify fresh native identities and typed entries.', async () => {
    const before = populatedTable(ctx, await ownedTable(ctx));
    assert.equal(before.tag.objectId, ctx.fixture.tagId);
    const exported = await ctx.call('export_tag_table', { processId: ctx.processId, objectId: ctx.fixture.tableId });
    assert.equal(exported.objectId, ctx.fixture.tableId);
    assert.equal(exported.source?.format, 'simatic-ml');
    assert.equal(exported.source?.documents?.length, 1);
    const document = exported.source.documents[0];
    assert.equal(typeof document.content, 'string');
    assert.match(document.name, /\.xml$/i);
    assert.deepEqual(document.checksum, { algorithm: 'sha-256', scope: 'returned-content', encoding: 'utf-8-no-bom', value: digest(document.content) });
    assert.equal((document.content.match(/<SW\.Tags\.PlcTagTable\b/g) || []).length, 1, 'Only one owned table may be replayed.');
    for (const name of [ctx.fixture.tableName, ctx.fixture.tagName, ctx.fixture.constantName])
      assert.ok(document.content.includes(`<Name>${name}</Name>`), `Export must contain ${name}.`);
    // Recheck ownership after export before replaying the native document.
    populatedTable(ctx, await ownedTable(ctx));
    const written = affected(await ctx.call('import_tag_tables', { processId: ctx.processId, plcObjectId: ctx.plcObjectId,
      documents: [{ name: document.name, content: document.content }] }), 'import_tag_tables', 'tagTable', ctx.fixture.tableName);
    const found = (await tables(ctx)).filter(item => item.name === ctx.fixture.tableName);
    assert.equal(found.length, 1);
    assert.equal(found[0].objectId, written.objectId);
    ctx.fixture.tableId = found[0].objectId;
    const after = populatedTable(ctx, await ownedTable(ctx));
    ctx.fixture.tagId = after.tag.objectId;
    ctx.fixture.constantId = after.constant.objectId;
    return { format: 'simatic-ml', sha256: document.checksum.value, tableId: ctx.fixture.tableId,
      tagId: ctx.fixture.tagId, constantId: ctx.fixture.constantId };
  });
}

async function deleteOwned(ctx, kind) {
  const table = kind === 'tagTable';
  const key = table ? 'table' : kind;
  const objectId = ctx.fixture[`${key}Id`];
  const name = ctx.fixture[`${key}Name`];
  const suffix = table ? 'tag_table' : kind;
  const listTool = table ? 'list_tag_tables' : kind === 'block' ? 'list_blocks' : 'list_udts';
  const readTool = `get_${suffix}`;
  const list = async () => {
    const read = await ctx.call(listTool, { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
    assert.equal(read.plcObjectId, ctx.plcObjectId);
    assert.ok(Array.isArray(read.roots));
    return walk(read.roots).filter(item => item.kind === kind);
  };
  await ctx.step(`delete.${suffix}`, `Delete the owned ${kind}, verify absence by ID/name and verify its former ID no longer resolves.`, async () => {
    const before = (await list()).filter(item => item.name?.toLowerCase() === name.toLowerCase());
    assert.equal(before.length, 1);
    assert.equal(before[0].name, name);
    assert.equal(before[0].objectId, objectId, 'Refusing to delete a same-name replacement fixture.');
    const read = await ctx.call(readTool, { processId: ctx.processId, objectId, includePath: false,
      ...(table ? { includeEntries: false } : { includeSource: false }) });
    assert.equal(read.metadata.objectId, objectId);
    assert.equal(read.metadata.name, name);
    const deleted = affected(await ctx.call(`delete_${suffix}`, { processId: ctx.processId, objectId }), `delete_${suffix}`, kind, name);
    assert.equal(deleted.objectId, objectId);
    const after = await list();
    assert.ok(!after.some(item => item.objectId === objectId || item.name?.toLowerCase() === name.toLowerCase()), 'A reported deletion must remove the object from fresh inventory.');
    const missing = await ctx.rawCall(readTool, { processId: ctx.processId, objectId, includePath: false,
      ...(table ? { includeEntries: false } : { includeSource: false }) });
    assert.equal(missing.result.isError, true);
    assert.equal(missing.payload.error?.code, 'objectNotFound', 'A deleted native ID must fail as objectNotFound.');
    ctx.fixture[`${key}Deleted`] = true;
    return { objectId, name, deleted: true };
  });
}

async function runLifecycleScenarios(ctx) {
  assert.match(ctx.prefix, /^[A-Za-z][A-Za-z0-9_]{0,70}$/);
  assert.ok(ctx.fixture && typeof ctx.logicalAddress === 'string');
  await createTable(ctx);
  const previousDefer = ctx.deferFormats;
  ctx.deferFormats = true;
  try { await runSourceScenarios(ctx); }
  finally { ctx.deferFormats = previousDefer; }
  await exportRoundtrip(ctx);
  await ctx.step('compile.initial', 'Compile PLC software and retain complete native success/warning diagnostics.', () => compile(ctx, true));
  await ctx.step('compile.remove-reference-tag', 'Delete only the owned referenced tag and verify it is absent before the expected compiler failure.', async () => {
    const entries = populatedTable(ctx, await ownedTable(ctx));
    assert.equal(entries.tag.objectId, ctx.fixture.tagId);
    const deleted = affected(await ctx.call('delete_tag_entry', { processId: ctx.processId, objectId: ctx.fixture.tagId }), 'delete_tag_entry', 'tag', ctx.fixture.tagName);
    assert.equal(deleted.objectId, ctx.fixture.tagId);
    const after = await ownedTable(ctx);
    assert.equal(after.tags.length, 0);
    assert.equal(entryNamed(after, 'userConstants', ctx.fixture.constantName).objectId, ctx.fixture.constantId);
    ctx.fixture.tagTemporarilyDeleted = true;
  });
  await ctx.step('compile.expected-error', 'Compile the deliberate missing-tag reference and verify native diagnostic errors identify the owned fixture.', () => compile(ctx, false));
  await ctx.step('compile.restore-reference-tag', 'Recreate the owned tag at its original address and verify its fresh native identity.', async () => {
    const before = await ownedTable(ctx);
    assert.equal(before.tags.length, 0, 'Refusing to recreate a tag when the table changed unexpectedly.');
    const tag = affected(await ctx.call('create_tag', { processId: ctx.processId, objectId: ctx.fixture.tableId,
      name: ctx.fixture.tagName, dataType: 'Bool', logicalAddress: ctx.logicalAddress }), 'create_tag', 'tag', ctx.fixture.tagName);
    assert.equal(tag.parentObjectId, ctx.fixture.tableId);
    ctx.fixture.tagId = tag.objectId;
    assert.equal(populatedTable(ctx, await ownedTable(ctx)).tag.objectId, tag.objectId);
    ctx.fixture.tagTemporarilyDeleted = false;
  });
  await completeLifecycleScenarios(ctx);
}

// The CLI never auto-resumes. This bounded tail is also available to a manually
// reviewed continuation after exact prior evidence and current ownership checks.
async function completeLifecycleScenarios(ctx) {
  await ctx.step('compile.repaired', 'Compile after tag restoration and verify the compiler errors are gone.', () => compile(ctx, true));
  for (const kind of ['block', 'udt', 'tagTable']) await deleteOwned(ctx, kind);
  await ctx.step('compile.final', 'Compile PLC software after fixture deletion and verify the remaining project still compiles.', () => compile(ctx, true));
}

function lifecycleMarkdown(report) {
  const clean = value => String(value || '').replaceAll('|', '\\|').replace(/[\r\n]+/g, ' ');
  return '# Native compile, deletion and table-export acceptance\n\n' +
    `Result: **${report.status}**\n\nProject: ${report.projectPath}\n\nProcess: ${report.processId}; prefix: ${report.prefix}\n\n` +
    '| Scenario | Result | Details |\n|---|---|---|\n' +
    report.steps.map(step => `| ${step.id} | ${step.status} | ${clean(step.error || step.description)} |`).join('\n') +
    '\n\nUncalled required tools: ' + (report.uncalledTools?.join(', ') || 'none') + '\n\n' +
    'Compiler errors are intentionally induced only by deleting this run\'s referenced tag, then repaired before fixture cleanup. Native responses, exact XML, checksums and the full compile diagnostic hierarchy are retained in report.json.\n\n' +
    'This suite explicitly compiles offline PLC software. It never saves, uploads, downloads, connects, closes or retries. Passed cleanup verifies deletion through fresh inventories and objectNotFound read results; a stopped run leaves remaining recorded fixtures for inspection. Runtime behavior is not tested.\n\n' +
    (report.uncertainWrite ? '**A write outcome is uncertain. Inspect the recorded native state; do not automatically retry.**\n\n' : '') +
    '```json\n' + JSON.stringify(report.fixture, null, 2) + '\n```\n';
}

async function runLifecycleAcceptance(options, dependencies = {}) {
  assert.ok(!options.resumeReport, 'Lifecycle acceptance does not resume or retry a stopped run. Inspect its evidence first.');
  const prefix = 'McpLC_' + randomBytes(6).toString('hex');
  const output = path.resolve(options.output || path.join(__dirname, '../../test-results/native-lifecycle-acceptance', now().replace(/[:.]/g, '-') + '_' + prefix));
  prepareOutput(output);
  const report = { schemaVersion: 1, suite: 'native-lifecycle', status: 'running', startedAtUtc: now(), endpoint: options.endpoint,
    processId: options.processId, projectPath: options.projectPath, prefix, fixture: {}, steps: [], calls: [], gaps: [],
    requiredTools: [...REQUIRED_TOOLS], observedAffectedObjects: [], writesAttempted: false, uncertainWrite: false };
  const persist = () => writeReport(output, report, lifecycleMarkdown);
  const ctx = createContext({ ...options, onProgress: dependencies.onProgress || (step => console.log(`${step.status.toUpperCase()}: ${step.id}`)) }, report, persist, dependencies.fetchImpl);
  try {
    await (dependencies.preflight || preflight)(ctx, report);
    await (dependencies.runScenarios || runLifecycleScenarios)(ctx);
    await ctx.step('final-status', 'Verify the same user-connected disposable project remains attached.', async () => {
      report.after = await ctx.call('get_status', { processId: ctx.processId });
      targetStatus(report.after, ctx);
    });
    report.status = 'passed';
  } catch (error) { report.status = 'failed'; report.error = error.message; }
  finally {
    const called = new Set(report.calls.map(call => call.request.params?.name));
    report.uncalledTools = REQUIRED_TOOLS.filter(name => !called.has(name));
    if (report.status === 'passed' && report.uncalledTools.length) report.status = 'incomplete';
    report.finishedAtUtc = now(); persist();
  }
  return { report, output };
}

module.exports = { REQUIRED_TOOLS, compilerMessages, assertCompilation, runLifecycleScenarios, completeLifecycleScenarios, lifecycleMarkdown, runLifecycleAcceptance };
