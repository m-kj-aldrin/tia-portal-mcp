'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { buildExternalFixtures, buildSdFixtures, checkReadback } = require('./native-acceptance/import-fixtures.cjs');

const prefix = 'McpAT_012345abcdef';

test('external fixture inventory covers each accepted extension and FC/FB shapes', () => {
  const specs = buildExternalFixtures(prefix);
  assert.deepEqual(specs.map(spec => [spec.extension, spec.blockType, spec.language]), [
    ['.scl', 'FC', 'SCL'], ['.scl', 'FB', 'SCL'], ['.awl', 'FC', 'STL'], ['.db', 'DB', 'DB'], ['.udt', 'UDT', null]
  ]);
  assert.equal(new Set(specs.map(spec => spec.name)).size, specs.length);
});

test('SD variants distinguish declaration-only and paired-resource imports', () => {
  const specs = buildSdFixtures(prefix);
  assert.deepEqual(specs.map(spec => spec.key), ['sd.lad.fb.dcl', 'sd.lad.fb.bundle', 'sd.db', 'sd.udt']);
  assert.equal(specs[0].documents(1).length, 1);
  const bundle = specs[1].documents(1);
  assert.deepEqual(bundle.map(document => document.name), [`${specs[1].name}.s7dcl`, `${specs[1].name}.s7res`]);
  assert.equal(bundle[1].content, '<root />');
  assert.notEqual(specs[0].name, specs[1].name, 'Bundle coverage uses a separate owned object.');
});

for (const spec of [...buildExternalFixtures(prefix), ...buildSdFixtures(prefix)]) {
  test(`${spec.key} asserts a semantic change and rejects unrelated declarations`, () => {
    for (const version of [1, 2]) {
      const documents = spec.documents(version);
      checkReadback(spec, documents, version);
      assert.throws(() => checkReadback(spec, documents, version === 1 ? 2 : 1));
      assert.throws(() => checkReadback(spec, documents.map(document => ({ ...document,
        content: document.content.replaceAll(spec.name, `${spec.name}_Other`) })), version));
      assert.throws(() => checkReadback(spec, [{ name: documents[0].name,
        content: documents[0].content + '\nFUNCTION "Foreign" : Void\nBEGIN\nEND_FUNCTION\n' }], version));
    }
    assert.notDeepEqual(spec.documents(1), spec.documents(2));
    const altered = spec.documents(1);
    altered[0].content = 'changed by caller';
    assert.notEqual(spec.documents(1)[0].content, altered[0].content, 'Callers must receive independent documents.');
    assert.throws(() => spec.documents(3));
  });
}

test('readback accepts native quoted variables and typed integer literals', () => {
  for (const spec of buildExternalFixtures(prefix).filter(spec => spec.kind === 'block')) {
    const documents = spec.documents(2).map(document => ({ ...document, content: document.content
      .replaceAll('Marker', '"Marker"').replaceAll('SampleCount', '"SampleCount"').replaceAll('Count :', '"Count" :')
      .replaceAll('29', 'Int#29') }));
    checkReadback(spec, documents, 2);
  }
});

test('a UDT imported from SD can be verified through external source syntax', () => {
  const spec = buildSdFixtures(prefix).find(spec => spec.kind === 'udt');
  checkReadback(spec, [{ name: 'udt.udt', content:
    `TYPE "${spec.name}"\nVERSION : 0.1\nSTRUCT\nFlag : Bool;\nSampleCount : Int;\nEND_STRUCT;\nEND_TYPE\n` }], 2);
});

test('a DB imported from SD can be verified through quoted external source syntax', () => {
  const spec = buildSdFixtures(prefix).find(spec => spec.blockType === 'DB');
  checkReadback(spec, [{ name: 'block.db', content:
    `DATA_BLOCK "${spec.name}"\nVERSION : 0.1\nVAR\nFlag : Bool;\nSampleCount : Int;\nEND_VAR\nBEGIN\nEND_DATA_BLOCK\n` }], 2);
});

test('external DB readback accepts native BEGIN initializers and rejects ambiguous or changed values', () => {
  const spec = buildExternalFixtures(prefix).find(spec => spec.blockType === 'DB');
  const native = `DATA_BLOCK "${spec.name}"\nVERSION : 0.1\nVAR\n   SampleCount : Int;\nEND_VAR\nBEGIN\n   SampleCount := 17;\nEND_DATA_BLOCK\n`;
  const verify = content => checkReadback(spec, [{ name: 'block.db', content }], 1);
  verify(native);
  verify(native.replaceAll('SampleCount', '"SampleCount"').replace(':= 17', ':= Int#17'));
  assert.throws(() => verify(native.replace(':= 17', ':= 29')), /initial value/);
  assert.throws(() => verify(native.replace('SampleCount : Int', 'SampleCount : DInt')), /Int type/);
  assert.throws(() => verify(native.replace('SampleCount : Int;', 'SampleCount : Int;\nSampleCount : Int;')), /one SampleCount declaration/);
  assert.throws(() => verify(native.replace('SampleCount := 17;', 'SampleCount := 17;\nSampleCount := 17;')), /one SampleCount initializer/);
  assert.throws(() => verify(native.replace('SampleCount : Int;', 'SampleCount : Int := 17;')), /one SampleCount initializer/);
  assert.throws(() => verify(native.replace('SampleCount := 17;', 'Other.SampleCount := 17;')), /one SampleCount initializer/);
  assert.throws(() => verify(native.replace('SampleCount := 17;', 'OtherSampleCount := 17;')), /one SampleCount initializer/);
});

test('fixture prefixes and duplicate declaration documents cannot redirect ownership', () => {
  for (const invalid of ['A"\nEND_FUNCTION', '../Other', '', 'prefix with space', '1prefix']) {
    assert.throws(() => buildExternalFixtures(invalid));
    assert.throws(() => buildSdFixtures(invalid));
  }
  const spec = buildExternalFixtures(prefix)[0];
  assert.throws(() => checkReadback(spec, [...spec.documents(1), ...spec.documents(1)], 1));
});
