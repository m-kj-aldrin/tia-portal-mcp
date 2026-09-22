'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { emptyTagTableDocument, runImportScenario } = require('./native-acceptance/tag-import-fixture.cjs');
const { createHash } = require('node:crypto');
const { runTagScenarios } = require('./native-acceptance/tag-scenarios.cjs');
const { runSourceScenarios } = require('./native-acceptance/source-scenarios.cjs');

// These are client-side scenario checks using managed JSON fixtures. They do not
// simulate Siemens parsing or establish that any native operation works in TIA.
const clone = value => JSON.parse(JSON.stringify(value));

function context() {
  const ctx = {
    processId: 42, plcObjectId: 'cpu-id', prefix: 'Acceptance_Test', logicalAddress: '%M10.0',
    fixture: {}, calls: [], steps: [],
    async step(id, description, action) {
      this.steps.push(id);
      try { return await action(); }
      catch (error) { error.scenarioId = id; throw error; }
    }
  };
  ctx.record = (name, args) => {
    assert.equal(args.processId, ctx.processId);
    if (args.plcObjectId !== undefined) assert.equal(args.plcObjectId, ctx.plcObjectId);
    ctx.calls.push({ name, args: clone(args) });
  };
  return ctx;
}

function successfulWrite(operation, object, extra = {}) {
  return {
    operation, complete: true, errors: [], saved: false, cleanupFailed: false,
    affectedObjects: [clone(object)], ...extra
  };
}

function tagContext({ ineffectiveDelete, ineffectiveAttribute } = {}) {
  const ctx = context();
  let nextId = 0;
  let table = null;
  const allEntries = () => [...table.tags, ...table.userConstants];
  const kindOf = entry => entry.logicalAddress === undefined ? 'userConstant' : 'tag';
  const affected = entry => ({
    objectId: entry.objectId, name: entry.name, kind: kindOf(entry), parentObjectId: 'table-id'
  });
  ctx.call = async (name, args) => {
    ctx.record(name, args);
    switch (name) {
      case 'list_tag_tables':
        return { plcObjectId: ctx.plcObjectId, roots: table
          ? [{ kind: 'tagTableGroup', children: [{ kind: 'tagTable', name: table.name, objectId: 'table-id' }] }]
          : [] };
      case 'create_tag_table':
        assert.equal(table, null, 'A scenario must not recreate an existing table.');
        table = { name: args.name, tags: [], userConstants: [], systemConstants: [] };
        return successfulWrite(name, { kind: 'tagTable', name: args.name, objectId: 'table-id' });
      case 'get_tag_table':
        assert.equal(args.objectId, 'table-id');
        return clone({ metadata: { objectId: 'table-id', name: table.name, path: args.includePath ? 'PLC/Tags' : null },
          entries: args.includeEntries ? { tags: table.tags, userConstants: table.userConstants, systemConstants: [] } : null });
      case 'create_tag':
      case 'create_user_constant': {
        assert.equal(args.objectId, 'table-id');
        const tag = name === 'create_tag';
        const entry = { objectId: `entry-${++nextId}`, name: args.name, dataType: args.dataType,
          ...(tag ? { logicalAddress: args.logicalAddress, typeSpecific: { ExternalVisible: true } }
            : { value: args.value, typeSpecific: {} }) };
        table[tag ? 'tags' : 'userConstants'].push(entry);
        return successfulWrite(name, affected(entry));
      }
      case 'set_tag_entry_attribute': {
        const entry = allEntries().find(item => item.objectId === args.objectId);
        assert.ok(entry, 'Attribute writes may target only fixture entries.');
        if (args.attributeName !== ineffectiveAttribute) {
          if (args.attributeName === 'Value') entry.value = args.attributeValue;
          else entry.typeSpecific[args.attributeName] = args.attributeValue;
        }
        return successfulWrite(name, affected(entry));
      }
      case 'delete_tag_entry': {
        const entry = allEntries().find(item => item.objectId === args.objectId);
        assert.ok(entry, 'Deletion may target only fixture entries.');
        const result = successfulWrite(name, affected(entry));
        if (kindOf(entry) !== ineffectiveDelete) {
          table.tags = table.tags.filter(item => item !== entry);
          table.userConstants = table.userConstants.filter(item => item !== entry);
        }
        return result;
      }
      default: throw new Error(`Unexpected tag scenario call: ${name}`);
    }
  };
  ctx.rawCall = async (name, args) => {
    ctx.record(name, args);
    assert.equal(name, 'set_tag_entry_attribute');
    assert.ok(allEntries().some(item => item.objectId === args.objectId));
    assert.equal(args.attributeName, `${ctx.prefix}_NoSuchAttribute`);
    return { result: { isError: true }, payload: { operation: name, complete: false, saved: false,
      cleanupFailed: false, errors: [{ origin: 'tia-openness', operation: name, message: 'Unknown native attribute.' }] } };
  };
  ctx.table = () => clone(table);
  return ctx;
}

function checksummed(document) {
  return { ...document, checksum: { algorithm: 'sha-256', scope: 'returned-content', encoding: 'utf-8-no-bom',
    value: createHash('sha256').update(document.content, 'utf8').digest('hex') } };
}

function sourceContext({ ineffectiveUpdateKind, omitReportedId = false, consistent = true, unavailableBlockSd = false, exportTransportError = false } = {}) {
  const ctx = context();
  ctx.fixture.tagName = `${ctx.prefix}_Signal`;
  const objects = new Map();
  ctx.generations = [];
  ctx.call = async (name, args) => {
    ctx.record(name, args);
    const kind = /udt/.test(name) ? 'udt' : 'block';
    const existing = objects.get(kind);
    if (name.startsWith('list_')) return { plcObjectId: ctx.plcObjectId, roots: existing
      ? [{ kind: 'scope', children: [{ kind, name: existing.name, objectId: existing.id }] }] : [] };
    if (name.startsWith('write_')) {
      assert.ok(args.documents.every(document => Object.keys(document).sort().join() === 'content,name'),
        'Reader-only checksum fields must not be submitted to writes.');
      const declaration = kind === 'block' ? 'FC' : 'UDT';
      const generation = (existing?.generation || 0) + 1;
      const native = { name: `${ctx.prefix}_${declaration}`, id: `${kind}-generation-${generation}`, generation,
        // Alternate-format fixtures are opaque envelopes, not a native parser.
        content: args.sourceFormat === 'external-source' && !(existing && kind === ineffectiveUpdateKind)
          ? args.documents[0].content : existing.content };
      objects.set(kind, native);
      ctx.generations.push(native.id);
      return successfulWrite(name, { kind, name: native.name, objectId: omitReportedId ? null : native.id },
        { format: args.sourceFormat });
    }
    if (name.startsWith('get_')) {
      assert.ok(existing);
      assert.equal(args.objectId, existing.id, 'Every read must use the latest inventory ID after replacement.');
      const metadata = { objectId: existing.id, name: existing.name, state: { isConsistent: consistent } };
      if (!args.includeSource) return { metadata, source: null };
      if (kind === 'block' && args.sourceFormat === 'simatic-sd' && (unavailableBlockSd || exportTransportError)) {
        const error = new Error('Export did not succeed.');
        if (!exportTransportError) error.toolResponse = { payload: { metadata, source: null, complete: false,
          errors: [{ origin: 'tia-openness', operation: 'sourceExport', format: 'simatic-sd', message: 'Native export unavailable.' }] } };
        throw error;
      }
      assert.equal(args.includeDependencies, false);
      const extension = args.sourceFormat === 'external-source' ? (kind === 'block' ? 'scl' : 'udt')
        : args.sourceFormat === 'simatic-sd' ? 's7dcl' : 'xml';
      const document = { name: `${existing.name}.${extension}`, content: args.sourceFormat === 'external-source'
        ? existing.content : `Opaque ${args.sourceFormat} fixture for ${existing.name}` };
      return { metadata, source: { format: args.sourceFormat, dependenciesIncluded: false,
        documents: [checksummed(document)] } };
    }
    throw new Error(`Unexpected source scenario call: ${name}`);
  };
  return ctx;
}

async function rejectsAt(run, ctx, scenarioId) {
  await assert.rejects(run(ctx), error => error.code === 'ERR_ASSERTION' && error.scenarioId === scenarioId);
  assert.equal(ctx.steps.at(-1), scenarioId, 'Dependent scenarios must not continue after a failed readback.');
}

test('tag scenarios verify reads and retain only the primary test entries', async () => {
  const ctx = tagContext();
  const result = await runTagScenarios(ctx);
  const table = ctx.table();
  assert.equal(table.tags.length, 1);
  assert.equal(table.tags[0].typeSpecific.ExternalVisible, false);
  assert.equal(table.userConstants.length, 1);
  assert.equal(table.userConstants[0].value, '200');
  assert.equal(ctx.fixture.disposableTagDeleted, true);
  assert.equal(ctx.fixture.disposableConstantDeleted, true);
  assert.equal(result.fixtures, ctx.fixture);
  assert.ok(ctx.calls.some(call => call.name === 'get_tag_table' && call.args.includeEntries === false));
});

for (const [kind, scenarioId] of [['tag', 'tags.delete-tag'], ['userConstant', 'tags.delete-entry']]) {
  test(`tag scenarios reject successful ${kind} deletion responses when readback still contains the entry`, async () => {
    await rejectsAt(runTagScenarios, tagContext({ ineffectiveDelete: kind }), scenarioId);
  });
}

for (const [attribute, scenarioId] of [['ExternalVisible', 'tags.edit-boolean'], ['Value', 'tags.edit-constant']]) {
  test(`tag scenarios reject successful ${attribute} writes when readback remains unchanged`, async () => {
    await rejectsAt(runTagScenarios, tagContext({ ineffectiveAttribute: attribute }), scenarioId);
  });
}

for (const omitReportedId of [false, true]) {
  test(`source scenarios reacquire replacement IDs from inventory${omitReportedId ? ' when write IDs are null' : ''}`, async () => {
    const ctx = sourceContext({ omitReportedId });
    await runSourceScenarios(ctx);
    assert.equal(ctx.fixture.blockId, 'block-generation-4');
    assert.equal(ctx.fixture.udtId, 'udt-generation-4');
    assert.equal(new Set(ctx.generations).size, 8);
    assert.deepEqual(ctx.calls.filter(call => call.name === 'write_blocks').map(call => call.args.sourceFormat),
      ['external-source', 'external-source', 'simatic-ml', 'simatic-sd']);
  });
}

for (const kind of ['block', 'udt']) {
  test(`source scenarios reject successful ${kind} update responses when source semantics remain unchanged`, async () => {
    const ctx = sourceContext({ ineffectiveUpdateKind: kind });
    await rejectsAt(runSourceScenarios, ctx, `${kind}.external.update`);
    assert.equal(ctx.fixture[`${kind}Id`], `${kind}-generation-2`,
      'The new identity alone must not count as a verified semantic update.');
  });
}

test('tag-table import reacquires a replacement ID and reads the imported table', async () => {
  const ctx = context();
  let generation = 0;
  let current;
  ctx.call = async (tool, args) => {
    if (tool === 'list_tag_tables') return { plcObjectId: ctx.plcObjectId, roots: current ? [current] : [] };
    if (tool === 'import_tag_tables') {
      generation++;
      current = { kind: 'tagTable', name: `${ctx.prefix}_ImportTags`, objectId: `import-${generation}` };
      assert.deepEqual(args.documents[0], emptyTagTableDocument(current.name));
      return successfulWrite(tool, current, { format: 'simatic-ml' });
    }
    assert.equal(tool, 'get_tag_table');
    assert.equal(args.objectId, current.objectId);
    return { metadata: current, entries: { tags: [], userConstants: [], systemConstants: [] } };
  };
  await runImportScenario(ctx);
  assert.equal(ctx.fixture.importedTableId, 'import-2');
  assert.equal(generation, 2);
});

test('tag-table import rejects success when no table appears in native inventory', async () => {
  const ctx = context();
  ctx.call = async tool => tool === 'list_tag_tables' ? { plcObjectId: ctx.plcObjectId, roots: [] }
    : successfulWrite(tool, { kind: 'tagTable', name: `${ctx.prefix}_ImportTags`, objectId: 'missing' }, { format: 'simatic-ml' });
  await rejectsAt(runImportScenario, ctx, 'tags.import.create');
});

test('authored tag XML refuses names that could inject another declaration', () => {
  assert.throws(() => emptyTagTableDocument('Owned</Name><Name>Other'), /ASCII identifier/);
});

test('inconsistent native source blocks at an explicit compile checkpoint before format writes', async () => {
  const ctx = sourceContext({ consistent: false });
  await assert.rejects(runSourceScenarios(ctx), error => error.code === 'compileRequired');
  assert.equal(ctx.steps.at(-1), 'source-consistency');
  assert.ok(ctx.calls.filter(call => call.name.startsWith('write_')).every(call => call.args.sourceFormat === 'external-source'));
  assert.ok(!ctx.calls.some(call => call.args.sourceFormat === 'simatic-ml'));
});

test('unavailable native format remains a gap while independent supported formats are verified', async () => {
  const ctx = sourceContext({ unavailableBlockSd: true });
  await runSourceScenarios(ctx);
  assert.equal(ctx.gaps.length, 1);
  assert.equal(ctx.gaps[0].id, 'block.simatic-sd.export');
  assert.ok(!ctx.calls.some(call => call.name === 'write_blocks' && call.args.sourceFormat === 'simatic-sd'));
  assert.ok(ctx.calls.some(call => call.name === 'write_udts' && call.args.sourceFormat === 'simatic-sd'));
  assert.ok(ctx.calls.some(call => call.name === 'write_blocks' && call.args.sourceFormat === 'simatic-ml'));
});

test('export transport failure stops the run instead of becoming an optional capability gap', async () => {
  const ctx = sourceContext({ exportTransportError: true });
  await assert.rejects(runSourceScenarios(ctx), /Export did not succeed/);
  assert.ok(!ctx.calls.some(call => call.name.startsWith('write_') && call.args.sourceFormat !== 'external-source'));
  assert.equal(ctx.gaps, undefined);
});

for (const [scenario, collection] of [['tags.edit-boolean', 'tags'], ['tags.edit-constant', 'userConstants'], ['tags.native-error', 'tags']]) {
  test(`${scenario} refuses a same-name replacement entry before mutation`, async () => {
    const ctx = tagContext();
    const step = ctx.step.bind(ctx);
    const call = ctx.call;
    let replaced = false;
    ctx.call = async (name, args) => {
      const result = await call(name, args);
      if (replaced && name === 'get_tag_table' && result.entries)
        result.entries[collection][0].objectId = 'replacement-not-owned';
      return result;
    };
    let callsBefore;
    ctx.step = (id, description, action) => step(id, description, async () => {
      if (id === scenario) {
        replaced = true;
        callsBefore = ctx.calls.length;
      }
      return action();
    });
    await rejectsAt(runTagScenarios, ctx, scenario);
    assert.ok(ctx.calls.slice(callsBefore).every(call => call.name === 'get_tag_table'));
  });
}
