'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { runLifecycleScenarios, completeLifecycleScenarios, assertCompilation, lifecycleMarkdown, runLifecycleAcceptance } = require('./native-acceptance/lifecycle-scenarios.cjs');

// Managed client contract fixtures only: these assertions do not simulate the Siemens compiler.
const copy = value => JSON.parse(JSON.stringify(value));
const checksummed = content => ({ name: 'tag-table.xml', content,
  checksum: { algorithm: 'sha-256', scope: 'returned-content', encoding: 'utf-8-no-bom',
    value: createHash('sha256').update(content, 'utf8').digest('hex') } });
const written = (operation, item, extra = {}) => ({ operation, complete: true, errors: [], saved: false,
  cleanupFailed: false, affectedObjects: [copy(item)], ...extra });

function fakeContext(options = {}) {
  let nextId = 0;
  const objects = new Map();
  const ctx = { processId: 42, plcObjectId: 'cpu-id', prefix: 'Lifecycle_Test', logicalAddress: '%M10.0',
    fixture: {}, calls: [], steps: [], objects,
    async step(id, description, action) { this.steps.push(id); this.phase = id; return await action(); } };
  const identity = (kind, name) => ({ kind, name, objectId: `${kind}-${++nextId}` });
  const affectedEntry = (table, entry) => ({ kind: entry.logicalAddress === undefined ? 'userConstant' : 'tag',
    objectId: entry.objectId, name: entry.name, parentObjectId: table.objectId });
  const record = (name, args) => {
    assert.equal(args.processId, ctx.processId);
    if (args.plcObjectId !== undefined) assert.equal(args.plcObjectId, ctx.plcObjectId);
    ctx.calls.push({ name, args: copy(args), phase: ctx.phase });
  };
  const findObject = id => { const object = objects.get(id); assert.ok(object, `Unknown mock native identity ${id}`); return object; };
  const table = () => [...objects.values()].find(item => item.kind === 'tagTable');
  ctx.call = async (name, args) => {
    record(name, args);
    if (['list_blocks', 'list_udts', 'list_tag_tables'].includes(name)) {
      const kind = { list_blocks: 'block', list_udts: 'udt', list_tag_tables: 'tagTable' }[name];
      const roots = [...objects.values()].filter(item => item.kind === kind).map(({ objectId, name, kind }) => ({ objectId, name, kind }));
      if (options.replaceBeforeDelete === kind && ctx.phase === `delete.${kind === 'tagTable' ? 'tag_table' : kind}` && roots.length)
        roots[0].objectId = 'foreign-replacement-id';
      return { plcObjectId: ctx.plcObjectId, roots };
    }
    if (name === 'create_tag_table') {
      const item = { ...identity('tagTable', args.name), tags: [], userConstants: [], systemConstants: [] };
      objects.set(item.objectId, item);
      return written(name, item);
    }
    if (name === 'get_tag_table') {
      const item = findObject(args.objectId);
      return copy({ metadata: { objectId: item.objectId, name: item.name },
        entries: args.includeEntries ? { tags: item.tags, userConstants: item.userConstants, systemConstants: item.systemConstants } : null });
    }
    if (name === 'create_tag' || name === 'create_user_constant') {
      const item = findObject(args.objectId); const isTag = name === 'create_tag';
      const entry = { objectId: `entry-${++nextId}`, name: args.name, dataType: args.dataType,
        ...(isTag ? { logicalAddress: args.logicalAddress } : { value: args.value }) };
      item[isTag ? 'tags' : 'userConstants'].push(entry);
      return written(name, affectedEntry(item, entry));
    }
    if (name === 'write_blocks' || name === 'write_udts') {
      const source = args.documents[0].content;
      const declared = /(?:FUNCTION|TYPE) "([^"]+)"/.exec(source)[1];
      const kind = name === 'write_blocks' ? 'block' : 'udt';
      const old = [...objects.values()].find(item => item.kind === kind && item.name === declared);
      if (old) objects.delete(old.objectId);
      const item = { ...identity(kind, declared), documents: copy(args.documents) };
      objects.set(item.objectId, item);
      return written(name, item, { format: args.sourceFormat });
    }
    if (name === 'get_block' || name === 'get_udt') {
      const item = findObject(args.objectId);
      return { metadata: { objectId: item.objectId, name: item.name, state: { isConsistent: true } },
        source: args.includeSource ? { format: args.sourceFormat, dependenciesIncluded: false,
          documents: item.documents.map(document => ({ ...checksummed(document.content), name: document.name })) } : null };
    }
    if (name === 'export_tag_table') {
      const item = findObject(args.objectId);
      const content = `<Document><SW.Tags.PlcTagTable><Name>${item.name}</Name><Name>${item.tags[0].name}</Name><Name>${item.userConstants[0].name}</Name></SW.Tags.PlcTagTable></Document>`;
      const document = checksummed(content);
      if (options.corruptChecksum) document.checksum.value = 'incorrect';
      ctx.exportedDocument = copy(document);
      return { objectId: item.objectId, source: { format: 'simatic-ml', documents: [document] } };
    }
    if (name === 'import_tag_tables') {
      if (options.failImport) throw new Error('Native import rejected the document');
      assert.deepEqual(args.documents, [{ name: ctx.exportedDocument.name, content: ctx.exportedDocument.content }], 'Reimport must use the exact exported document.');
      const old = table(); objects.delete(old.objectId);
      const item = { ...copy(old), ...identity('tagTable', old.name) };
      for (const entry of [...item.tags, ...item.userConstants]) entry.objectId = `entry-${++nextId}`;
      objects.set(item.objectId, item);
      return written(name, item, { format: 'simatic-ml' });
    }
    if (name === 'delete_tag_entry') {
      const item = table(); const entry = item.tags.find(tag => tag.objectId === args.objectId);
      assert.ok(entry);
      if (options.ineffectiveDelete !== name) item.tags = item.tags.filter(tag => tag !== entry);
      return written(name, affectedEntry(item, entry));
    }
    if (['delete_block', 'delete_udt', 'delete_tag_table'].includes(name)) {
      const item = findObject(args.objectId);
      if (options.ineffectiveDelete !== name) objects.delete(args.objectId);
      return written(name, item);
    }
    throw new Error(`Unexpected call ${name}`);
  };
  ctx.rawCall = async (name, args) => {
    record(name, args);
    if (name === 'compile_plc') {
      const missingTag = !!table() && table().tags.length === 0;
      const error = missingTag || !!options.initialCompileError;
      const expectedFailure = ctx.phase === 'compile.expected-error';
      const payload = { operation: name, processId: ctx.processId, plcObjectId: ctx.plcObjectId,
        readAtUtc: '2026-09-23T00:00:00Z', saved: false, complete: true, errors: [],
        compilationSucceeded: !error, state: error ? 'Error' : 'Warning', errorCount: error ? 1 : 0, warningCount: 1,
        messages: [{ path: options.unrelatedDiagnostic ? 'unrelated' : `PLC/${ctx.fixture.blockName}`,
          description: options.unrelatedDiagnostic ? 'Unrelated error' : `Tag ${ctx.fixture.tagName} ${error ? 'not defined' : 'resolved'}`,
          dateTime: '2026-09-23T00:00:00.0000000Z', state: error ? 'Error' : 'Warning',
          errorCount: error ? 1 : 0, warningCount: 1, messages: [] }] };
      if (expectedFailure && options.apiFailureInstead) {
        payload.complete = false; payload.compilationSucceeded = null;
        payload.errors = [{ origin: 'tia-openness', operation: name, message: 'Service unavailable' }];
      }
      return { result: { isError: error }, payload };
    }
    assert.ok(['get_block', 'get_udt', 'get_tag_table'].includes(name));
    assert.ok(!objects.has(args.objectId), 'A missing-object assertion must target a deleted ID.');
    return { result: { isError: true }, payload: { processId: ctx.processId, error: { code: 'objectNotFound' } } };
  };
  return ctx;
}

test('lifecycle exercises XML replay with new identities, compiler error/repair and independently verified object deletions', async () => {
  const ctx = fakeContext();
  await runLifecycleScenarios(ctx);
  assert.equal(ctx.objects.size, 0);
  assert.equal(ctx.fixture.blockDeleted, true);
  assert.equal(ctx.fixture.udtDeleted, true);
  assert.equal(ctx.fixture.tableDeleted, true);
  assert.equal(ctx.fixture.tagTemporarilyDeleted, false);
  assert.deepEqual(ctx.calls.filter(call => call.name === 'compile_plc').map(call => call.phase),
    ['compile.initial', 'compile.expected-error', 'compile.repaired', 'compile.final']);
  assert.ok(ctx.calls.every(call => !/save|download|upload|connect|close/.test(call.name)));
  assert.equal(ctx.calls.filter(call => call.name === 'import_tag_tables').length, 1);
});

for (const operation of ['delete_tag_entry', 'delete_block', 'delete_udt', 'delete_tag_table']) {
  test(`lifecycle fails when ${operation} reports success without removing its target`, async () => {
    const ctx = fakeContext({ ineffectiveDelete: operation });
    await assert.rejects(runLifecycleScenarios(ctx));
    assert.equal(ctx.calls.filter(call => call.name === operation).length, 1, 'No retry is allowed.');
    assert.ok(!ctx.steps.includes('compile.final'));
    if (operation === 'delete_tag_entry') assert.ok(!ctx.steps.includes('compile.expected-error'));
  });
}

test('lifecycle refuses deletion of a same-name replacement', async () => {
  const ctx = fakeContext({ replaceBeforeDelete: 'block' });
  await assert.rejects(runLifecycleScenarios(ctx), /same-name replacement/);
  assert.ok(!ctx.calls.some(call => call.name === 'delete_block'));
});

test('lifecycle refuses altered XML and does not write an unverified export', async () => {
  const ctx = fakeContext({ corruptChecksum: true });
  await assert.rejects(runLifecycleScenarios(ctx));
  assert.ok(!ctx.calls.some(call => call.name === 'import_tag_tables'));
});

test('lifecycle stops after a failed import with no retry or cleanup mutation', async () => {
  const ctx = fakeContext({ failImport: true });
  await assert.rejects(runLifecycleScenarios(ctx), /Native import rejected/);
  assert.equal(ctx.calls.filter(call => call.name === 'import_tag_tables').length, 1);
  assert.ok(!ctx.calls.some(call => call.name === 'compile_plc' || call.name.startsWith('delete_')));
});

test('lifecycle requires initial successful compilation before deliberately breaking a reference', async () => {
  const ctx = fakeContext({ initialCompileError: true });
  await assert.rejects(runLifecycleScenarios(ctx));
  assert.ok(!ctx.calls.some(call => call.name === 'delete_tag_entry'));
});

test('lifecycle does not confuse API failure with an expected compiler diagnostic', async () => {
  const ctx = fakeContext({ apiFailureInstead: true });
  await assert.rejects(runLifecycleScenarios(ctx), /complete diagnostics/);
  assert.ok(!ctx.steps.includes('compile.restore-reference-tag'));
});

test('lifecycle requires the expected compiler error to identify its own broken fixture', async () => {
  const ctx = fakeContext({ unrelatedDiagnostic: true });
  await assert.rejects(runLifecycleScenarios(ctx), /deliberately broken fixture/);
  assert.ok(!ctx.steps.includes('compile.restore-reference-tag'));
});

test('compiler acceptance rejects nested unavailable messages and isError false on error results', () => {
  const ctx = fakeContext(); ctx.fixture.tagName = 'Signal'; ctx.fixture.blockName = 'FC';
  const payload = { operation: 'compile_plc', processId: 42, plcObjectId: 'cpu-id', readAtUtc: '2026-09-23T00:00:00Z',
    saved: false, complete: true, errors: [], compilationSucceeded: false, state: 'Error', errorCount: 1, warningCount: 0,
    messages: [{ path: 'FC', description: 'Signal not defined', dateTime: '2026-09-23T00:00:00Z',
      state: 'Error', errorCount: 1, warningCount: 0, messages: null }] };
  assert.throws(() => assertCompilation({ result: { isError: true }, payload }, ctx, false), /readable native hierarchy/);
  payload.messages[0].messages = [];
  assert.throws(() => assertCompilation({ result: { isError: false }, payload }, ctx, false), /distinguish a compiler error/);
});

test('lifecycle has no implicit resume and reports compile scope honestly', async () => {
  await assert.rejects(runLifecycleAcceptance({ resumeReport: 'prior-report.json' }), /does not resume or retry/);
  const rendered = lifecycleMarkdown({ status: 'failed', projectPath: 'project.ap20', processId: 42, prefix: 'Test',
    steps: [], fixture: {}, uncertainWrite: true });
  assert.match(rendered, /explicitly compiles offline PLC software/);
  assert.match(rendered, /write outcome is uncertain/);
  assert.doesNotMatch(rendered, /No automatic.*compile/);
});

test('explicitly reviewed lifecycle completion only compiles and deletes the retained owned fixtures', async () => {
  const ctx = fakeContext();
  const step = ctx.step;
  let stopped = false;
  ctx.step = async function (id, description, action) {
    if (!stopped && id === 'compile.repaired') { stopped = true; throw new Error('Stopped before repaired compile'); }
    return step.call(this, id, description, action);
  };
  await assert.rejects(runLifecycleScenarios(ctx), /Stopped before repaired compile/);
  assert.equal(ctx.fixture.tagTemporarilyDeleted, false);
  const previousCalls = ctx.calls.length;
  await completeLifecycleScenarios(ctx);
  const tail = ctx.calls.slice(previousCalls);
  assert.ok(tail.every(call => !/^(?:create_|write_|import_|export_)/.test(call.name)), 'Reviewed completion cannot replay fixture creation/import.');
  assert.equal(tail.filter(call => call.name === 'compile_plc').length, 2);
  assert.deepEqual(tail.filter(call => call.name.startsWith('delete_')).map(call => call.name), ['delete_block', 'delete_udt', 'delete_tag_table']);
  assert.equal(ctx.objects.size, 0);
});
