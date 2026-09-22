'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { buildXmlFixtures, checkXmlReadback } = require('./native-acceptance/xml-import-fixtures.cjs');

const numbers = { 'xml-scl-fc': 2001, 'xml-lad-fb': 2002, 'xml-db': 2003 };
const make = () => buildXmlFixtures('McpAT_xmltests', numbers, '%M210.0');
const byKey = key => make().find(spec => spec.key === key);

function sourceFor(spec, version) {
  const value = version === 1 ? 17 : 29;
  let content;
  if (spec.blockType === 'FC') {
    content = `FUNCTION "${spec.name}" : Void\nVAR_OUTPUT\n Marker : Int;\nEND_VAR\nBEGIN\n #Marker := ${value};\nEND_FUNCTION\n`;
  } else if (spec.blockType === 'FB') {
    content = `{ S7_Language := "LAD" }\nFUNCTION_BLOCK "${spec.name}"\nVAR_OUTPUT\n Marker : Int;\nEND_VAR\nNETWORK\nRUNG wire#powerrail\n Move{ Card := 1; DisableENO := TRUE }( IN := Int#${value}, OUT1 => #Marker )\nEND_RUNG\nEND_NETWORK\nEND_FUNCTION_BLOCK\n`;
  } else if (spec.blockType === 'DB') {
    content = `DATA_BLOCK "${spec.name}"\nVAR\n Flag : Bool;\n${version === 2 ? ' SampleCount : Int;\n' : ''}END_VAR\nBEGIN\nEND_DATA_BLOCK\n`;
  } else {
    content = `TYPE "${spec.name}"\nSTRUCT\n Flag : Bool;\n${version === 2 ? ' SampleCount : Int;\n' : ''}END_STRUCT;\nEND_TYPE\n`;
  }
  return [{ name: 'native-export.scl', content }];
}

function tagRead(spec, version) {
  return { metadata: { name: spec.name }, entries: {
    tags: [{ objectId: 'native-tag-id', name: spec.tagName, dataType: 'Bool', logicalAddress: spec.logicalAddress }],
    userConstants: [{ objectId: 'native-constant-id', name: spec.constantName, dataType: 'Int', value: version === 1 ? '17' : '29' }],
    systemConstants: []
  } };
}

test('XML fixture matrix covers SCL FC, LAD FB, global DB, UDT and populated tags with unique owned names', () => {
  const specs = make();
  assert.deepEqual(specs.map(spec => [spec.key, spec.kind, spec.readFormat]), [
    ['xml-scl-fc', 'block', 'external-source'], ['xml-lad-fb', 'block', 'simatic-sd'],
    ['xml-db', 'block', 'external-source'], ['xml-udt', 'udt', 'external-source'],
    ['xml-tags', 'tagTable', null]
  ]);
  assert.equal(new Set(specs.map(spec => spec.name)).size, specs.length);
  for (const spec of specs) {
    assert.equal(spec.sourceFormat, 'simatic-ml');
    assert.equal(spec.extension, '.xml');
    assert.match(spec.name, /^McpAT_xmltests_/);
    for (const version of [1, 2]) {
      const documents = spec.documents(version);
      assert.equal(documents.length, 1);
      assert.equal(documents[0].name, `${spec.name}.xml`);
      assert.match(documents[0].content, /^<\?xml version="1.0" encoding="utf-8"\?>/);
      assert.match(documents[0].content, new RegExp(`<Name>${spec.name}</Name>`));
      if (spec.kind === 'block') assert.match(documents[0].content, new RegExp(`<Number>${numbers[spec.key]}</Number>`));
    }
    assert.notEqual(spec.documents(1)[0].content, spec.documents(2)[0].content, 'Update must change authored semantics.');
  }
});

test('fixture generation rejects unsafe names, unreserved numbers, invalid versions and addresses', () => {
  assert.throws(() => buildXmlFixtures('X<&', numbers, '%M210.0'));
  assert.throws(() => buildXmlFixtures('X', {}, '%M210.0'), /reserved number/);
  assert.throws(() => buildXmlFixtures('X', { ...numbers, 'xml-lad-fb': 0 }, '%M210.0'));
  assert.throws(() => buildXmlFixtures('X', { ...numbers, 'xml-db': 65536 }, '%M210.0'));
  assert.throws(() => buildXmlFixtures('X', numbers, '%Q0.0'));
  assert.throws(() => buildXmlFixtures('X', numbers, '%M210.8'));
  assert.throws(() => make()[0].documents(3), /Fixture version/);
});

test('readback accepts each source semantic version and rejects the other version', () => {
  for (const spec of make().filter(item => item.kind !== 'tagTable')) {
    for (const version of [1, 2]) {
      const readback = sourceFor(spec, version);
      assert.doesNotThrow(() => checkXmlReadback(spec, readback, version), spec.key);
      assert.throws(() => checkXmlReadback(spec, readback, version === 1 ? 2 : 1), spec.key);
    }
  }
});

test('source readback rejects renamed, additional and incomplete declarations', () => {
  for (const spec of make().filter(item => item.kind !== 'tagTable')) {
    const original = sourceFor(spec, 1);
    assert.throws(() => checkXmlReadback(spec, [{ ...original[0], content: original[0].content.replace(spec.name, 'OtherFixture') }], 1));
    assert.throws(() => checkXmlReadback(spec, [{ ...original[0], content: original[0].content + '\nFUNCTION "OtherFixture" : Void\nEND_FUNCTION\n' }], 1));
    assert.throws(() => checkXmlReadback(spec, [{ ...original[0], content: original[0].content.replace(/END_(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)/, '') }], 1));
  }
});

test('LAD readback verifies Move operands and allows a native resource companion', () => {
  const spec = byKey('xml-lad-fb');
  const original = sourceFor(spec, 2);
  assert.doesNotThrow(() => checkXmlReadback(spec, [...original, { name: 'native.s7res', content: '<root />' }], 2));
  for (const [before, after] of [['IN := Int#29', 'IN := Int#17'], ['OUT1 => #Marker', 'OUT1 => #Other'], ['Move{', 'Add{']]) {
    assert.throws(() => checkXmlReadback(spec, [{ ...original[0], content: original[0].content.replace(before, after) }], 2));
  }
  assert.throws(() => checkXmlReadback(spec, [...original, ...original], 2));
});

test('populated tag import readback proves address, native identities and semantic constant update', () => {
  const spec = byKey('xml-tags');
  for (const version of [1, 2]) {
    assert.doesNotThrow(() => checkXmlReadback(spec, tagRead(spec, version), version));
    assert.throws(() => checkXmlReadback(spec, tagRead(spec, version), version === 1 ? 2 : 1));
  }
  for (const mutate of [
    read => { read.metadata.name = 'OtherTable'; },
    read => { read.entries.tags[0].logicalAddress = '%M211.0'; },
    read => { read.entries.tags[0].dataType = 'Int'; },
    read => { read.entries.userConstants[0].name = 'OtherLimit'; },
    read => { read.entries.userConstants[0].objectId = null; },
    read => { read.entries.userConstants[0].objectId = read.entries.tags[0].objectId; },
    read => { read.entries.tags.push({ ...read.entries.tags[0], objectId: 'extra-id' }); }
  ]) {
    const read = tagRead(spec, 2);
    mutate(read);
    assert.throws(() => checkXmlReadback(spec, read, 2));
  }
});
