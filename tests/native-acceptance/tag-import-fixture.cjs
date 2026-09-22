'use strict';

const assert = require('node:assert/strict');
const { inventoryTables } = require('./tag-scenarios.cjs');

const TAG_IMPORT_PROVENANCE = Object.freeze({
  kind: 'authored-minimal-simatic-ml',
  priorNativeEvidence: 'none; acceptance is established only by this run',
  source: 'https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/tag-tables/importing-plc-tag-table',
  note: 'The Siemens V20 documentation specifies TagTables.Import with Override, but does not supply this XML. This minimal empty-table document is authored for acceptance testing; only a successful native import and readback validates it on the selected target.'
});

function emptyTagTableDocument(name) {
  assert.match(name, /^[A-Za-z][A-Za-z0-9_]{0,110}$/, 'An authored fixture name must be a short ASCII identifier.');
  return {
    name: `${name}.xml`,
    content: `<?xml version="1.0" encoding="utf-8"?>\n<Document>\n  <Engineering version="V20" />\n  <SW.Tags.PlcTagTable ID="0">\n    <AttributeList>\n      <Name>${name}</Name>\n    </AttributeList>\n    <ObjectList />\n  </SW.Tags.PlcTagTable>\n</Document>\n`
  };
}

async function runImportScenario(ctx) {
  const name = `${ctx.prefix}_ImportTags`;
  const document = emptyTagTableDocument(name);
  ctx.fixture.importedTableName = name;
  let objectId;
  const find = async () => {
    const inventory = await ctx.call('list_tag_tables', { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
    assert.equal(inventory.plcObjectId, ctx.plcObjectId);
    return inventoryTables(inventory).filter(table =>
      typeof table.name === 'string' && table.name.toLowerCase() === name.toLowerCase());
  };
  const readEmpty = async () => {
    const read = await ctx.call('get_tag_table', {
      processId: ctx.processId, objectId, includeEntries: true, includePath: false
    });
    assert.equal(read.metadata.objectId, objectId);
    assert.equal(read.metadata.name, name);
    for (const collection of ['tags', 'userConstants', 'systemConstants']) {
      assert.ok(Array.isArray(read.entries?.[collection]), `The imported table must expose ${collection}.`);
      assert.equal(read.entries[collection].length, 0, 'An imported empty fixture must remain empty.');
    }
    return read;
  };
  const importAndRead = async () => {
    const written = await ctx.call('import_tag_tables', {
      processId: ctx.processId, plcObjectId: ctx.plcObjectId, documents: [{ ...document }]
    });
    assert.equal(written.operation, 'import_tag_tables');
    assert.equal(written.format, 'simatic-ml');
    assert.equal(written.saved, false);
    assert.equal(written.cleanupFailed, false);
    assert.ok(Array.isArray(written.affectedObjects));
    assert.equal(written.affectedObjects.length, 1, 'The empty fixture must affect only its one owned table.');
    const affected = written.affectedObjects[0];
    assert.equal(affected.kind, 'tagTable');
    assert.equal(affected.name, name);
    const found = await find();
    assert.equal(found.length, 1);
    assert.equal(found[0].name, name);
    assert.equal(typeof found[0].objectId, 'string');
    assert.ok(found[0].objectId.length > 0);
    if (affected.objectId !== null && affected.objectId !== undefined) assert.equal(found[0].objectId, affected.objectId);
    objectId = found[0].objectId;
    ctx.fixture.importedTableId = objectId;
    await readEmpty();
    return { objectId, name, format: 'simatic-ml', fixtureProvenance: TAG_IMPORT_PROVENANCE };
  };

  await ctx.step('tags.import.create', 'Import an authored empty SimaticML table and verify native inventory and entries.', async () => {
    assert.equal((await find()).length, 0, 'Refusing to import over a same-name pre-existing table.');
    return importAndRead();
  });

  await ctx.step('tags.import.override', 'Import the same owned empty table again and reacquire its native identity.', async () => {
    assert.equal(typeof objectId, 'string', 'Initial native import and readback must succeed first.');
    await readEmpty();
    const before = await find();
    assert.equal(before.length, 1);
    assert.equal(before[0].name, name);
    assert.equal(before[0].objectId, objectId, 'The replacement target must still be the table created by this run.');
    // No entries are omitted from a populated table. This case does not assume
    // that native Override deletes entries absent from a source document.
    return importAndRead();
  });
}

module.exports = { emptyTagTableDocument, runImportScenario, TAG_IMPORT_PROVENANCE };
