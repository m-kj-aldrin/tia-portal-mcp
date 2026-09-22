'use strict';

const assert = require('node:assert/strict');

// Authored acceptance inputs, not a source compiler. A native import and a
// subsequent semantic readback are required before any fixture counts as passed.
function checkedPrefix(prefix) {
  assert.match(prefix, /^[A-Za-z][A-Za-z0-9_]{0,70}$/, 'Use a short ASCII fixture prefix.');
  return prefix;
}

function value(version) {
  assert.ok(version === 1 || version === 2, 'Fixture version must be 1 or 2.');
  return version === 1 ? 17 : 29;
}

function fixture(prefix, key, suffix, kind, blockType, language, extension, body, extra = {}) {
  const name = `${prefix}_${suffix}`;
  return {
    key, kind, blockType, language, extension, name,
    sourceFormat: extension === '.s7dcl' ? 'simatic-sd' : 'external-source',
    readFormat: language === 'LAD' ? 'simatic-sd' : 'external-source',
    provenance: 'authored-test-input; native acceptance requires import and readback',
    ...extra,
    documents(version) {
      value(version);
      const documents = [{ name: `${name}${extension}`, content: body(name, version) }];
      if (extra.resources) documents.push({ name: `${name}.s7res`, content: '<root />' });
      return documents;
    }
  };
}

function buildExternalFixtures(prefix) {
  checkedPrefix(prefix);
  const code = (type, name, version, language) =>
    `${type} "${name}"${type === 'FUNCTION' ? ' : Void' : ''}\n` +
    (type === 'FUNCTION_BLOCK' ? `{ S7_Optimized_Access := 'TRUE' }\n` : '') +
    'VERSION : 0.1\n   VAR_OUTPUT\n      Marker : Int;\n   END_VAR\n' +
    (type === 'FUNCTION_BLOCK' ? '   VAR\n      Count : Int;\n   END_VAR\n' : '') +
    'BEGIN\n' + (language === 'STL' ? `   L ${value(version)};\n   T #Marker;\n` : `   #Marker := ${value(version)};\n`) +
    `END_${type}\n`;
  return [
    fixture(prefix, 'external.scl.fc', 'ExtSclFC', 'block', 'FC', 'SCL', '.scl',
      (name, version) => code('FUNCTION', name, version, 'SCL')),
    fixture(prefix, 'external.scl.fb', 'ExtSclFB', 'block', 'FB', 'SCL', '.scl',
      (name, version) => code('FUNCTION_BLOCK', name, version, 'SCL')),
    fixture(prefix, 'external.awl.fc', 'ExtStlFC', 'block', 'FC', 'STL', '.awl',
      (name, version) => code('FUNCTION', name, version, 'STL')),
    fixture(prefix, 'external.db', 'ExtDB', 'block', 'DB', 'DB', '.db', (name, version) =>
      `DATA_BLOCK "${name}"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\nNON_RETAIN\n   VAR\n      SampleCount : Int := ${value(version)};\n   END_VAR\nBEGIN\nEND_DATA_BLOCK\n`),
    fixture(prefix, 'external.udt', 'ExtUDT', 'udt', 'UDT', null, '.udt', (name, version) =>
      `TYPE "${name}"\nVERSION : 0.1\n   STRUCT\n      Flag : Bool;\n${version === 2 ? '      SampleCount : Int;\n' : ''}   END_STRUCT;\nEND_TYPE\n`)
  ];
}

function buildSdFixtures(prefix) {
  checkedPrefix(prefix);
  const lad = (name, version) =>
    `{ S7_Optimized := "TRUE"; S7_Language := "LAD"; }\nFUNCTION_BLOCK "${name}"\n` +
    '   VAR_OUTPUT\n      Marker : Int;\n   END_VAR\n' +
    '   { S7_Language := "LAD" }\n   NETWORK\n      RUNG wire#powerrail\n' +
    `         Move{ Card := 1; DisableENO := TRUE }( IN := ${value(version)}, OUT1 => #Marker )\n` +
    '      END_RUNG\n   END_NETWORK\nEND_FUNCTION_BLOCK\n';
  return [
    fixture(prefix, 'sd.lad.fb.dcl', 'SdLadFB', 'block', 'FB', 'LAD', '.s7dcl', lad),
    fixture(prefix, 'sd.lad.fb.bundle', 'SdLadBundleFB', 'block', 'FB', 'LAD', '.s7dcl', lad,
      { resources: true, resourceProvenance: 'Native Main and WaterTank_Core exports returned an empty <root /> resource document.' }),
    fixture(prefix, 'sd.db', 'SdDB', 'block', 'DB', 'DB', '.s7dcl', (name, version) =>
      `{ S7_Optimized := "TRUE"; S7_StandardRetain := "FALSE" }\nDATA_BLOCK ${name}\n   VAR\n      Flag : Bool;\n${version === 2 ? '      SampleCount : Int;\n' : ''}   END_VAR\nEND_DATA_BLOCK\n`,
      { structuralUpdate: true }),
    fixture(prefix, 'sd.udt', 'SdUDT', 'udt', 'UDT', null, '.s7dcl', (name, version) =>
      `TYPE\n   ${name} : STRUCT\n      Flag : Bool;\n${version === 2 ? '      SampleCount : Int;\n' : ''}   END_STRUCT;\nEND_TYPE\n`)
  ];
}

function escaped(value) { return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

function declaration(spec, content) {
  const expectedType = { FC: 'FUNCTION', FB: 'FUNCTION_BLOCK', DB: 'DATA_BLOCK', UDT: 'TYPE' }[spec.blockType];
  assert.ok(expectedType, 'Unknown fixture block type.');
  const declarations = [...content.matchAll(/^\s*(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\b/gmi)];
  assert.equal(declarations.length, 1, 'Only one complete owned declaration may be read or submitted.');
  assert.equal(declarations[0][1].toUpperCase(), expectedType);
  const name = escaped(spec.name);
  const head = spec.kind === 'udt'
    ? new RegExp(`\\bTYPE\\s+(?:"${name}"(?=\\s|$)|${name}(?=\\s*:))`, 'i')
    : spec.blockType === 'DB'
    ? new RegExp(`\\bDATA_BLOCK\\s+(?:"${name}"|${name})(?=\\s|$)`, 'i')
    : new RegExp(`\\b${expectedType}\\s+"${name}"(?=\\s|:|$)`, 'i');
  assert.match(content, head, 'Declaration must use the exact owned fixture name.');
  assert.equal([...content.matchAll(new RegExp(`\\bEND_${expectedType}\\b`, 'gi'))].length, 1,
    'A complete fixture must have exactly one declaration terminator.');
}

function checkReadback(spec, documents, version) {
  const expected = value(version);
  assert.ok(Array.isArray(documents));
  const primary = documents.filter(document => !/\.s7res$/i.test(document.name));
  assert.equal(primary.length, 1, 'Readback needs exactly one source declaration document.');
  assert.ok(documents.length <= 2, 'Only an optional SD resource may accompany the declaration.');
  for (const document of documents) {
    assert.equal(typeof document.name, 'string');
    assert.equal(typeof document.content, 'string');
    assert.ok(document.content.trim().length);
  }
  const content = primary[0].content;
  declaration(spec, content);
  const member = name => `(?:"${name}"|\\b${name})\\s*(?:\\{[^}]*\\}\\s*)?`;
  if (spec.kind === 'udt' || spec.structuralUpdate) {
    assert.match(content, new RegExp(`${member('Flag')}:\\s*Bool\\b`, 'i'));
    if (version === 2) assert.match(content, new RegExp(`${member('SampleCount')}:\\s*Int\\b`, 'i'));
    else assert.doesNotMatch(content, /\bSampleCount\b/i);
  } else if (spec.blockType === 'DB') {
    const sections = [...content.matchAll(/\bVAR\b([\s\S]*?)\bEND_VAR\b/gi)];
    assert.equal(sections.length, 1, 'The global DB fixture must have exactly one VAR section.');
    const fields = [...sections[0][1].matchAll(/^\s*(?:"SampleCount"|SampleCount)\s*(?:\{[^}]*\}\s*)?:\s*(\w+)\s*(?:\s*:=\s*([^;]+))?\s*;/gmi)];
    assert.equal(fields.length, 1, 'Exactly one SampleCount declaration is expected.');
    assert.equal(fields[0][1].toLowerCase(), 'int', 'SampleCount must retain its Int type.');
    const initializers = fields[0][2] === undefined ? [] : [fields[0][2]];
    const bodies = [...content.matchAll(/\bBEGIN\b([\s\S]*?)\bEND_DATA_BLOCK\b/gi)];
    assert.equal(bodies.length, 1, 'The external DB source must have exactly one initialization body.');
    for (const assignment of bodies[0][1].matchAll(/^\s*(?:"SampleCount"|SampleCount)\s*:=\s*([^;]+)\s*;/gmi))
      initializers.push(assignment[1]);
    assert.equal(initializers.length, 1, 'Exactly one SampleCount initializer is expected.');
    assert.match(initializers[0].trim(), new RegExp(`^(?:Int#)?${expected}$`, 'i'), 'SampleCount must retain the expected initial value.');
  } else {
    assert.match(content, new RegExp(`\\bVAR_OUTPUT\\b[\\s\\S]*?${member('Marker')}:\\s*Int\\s*;[\\s\\S]*?\\bEND_VAR\\b`, 'i'));
    if (spec.language === 'SCL') {
      const assignments = [...content.matchAll(/#(?:"Marker"|Marker)\s*:=\s*(?:Int#)?(\d+)\s*;/gi)];
      assert.equal(assignments.length, 1, 'Exactly one Marker assignment is expected.');
      assert.equal(Number(assignments[0][1]), expected);
      if (spec.blockType === 'FB') assert.match(content, new RegExp(`${member('Count')}:\\s*Int\\b`, 'i'));
    } else if (spec.language === 'STL') {
      assert.match(content, new RegExp(`\\bL\\s+(?:Int#)?${expected}\\s*;\\s*T\\s+#(?:"Marker"|Marker)\\s*;`, 'i'));
      assert.equal([...content.matchAll(/\bT\s+#(?:"Marker"|Marker)\s*;/gi)].length, 1);
    } else if (spec.language === 'LAD') {
      assert.match(content, /S7_Language\s*:=\s*"LAD"/i);
      const moves = [...content.matchAll(/\bMove\s*\{[^}]*\}\s*\(\s*IN\s*:=\s*(?:Int#)?(\d+)\s*,\s*OUT1\s*=>\s*#(?:"Marker"|Marker)\s*\)/gi)];
      assert.equal(moves.length, 1, 'Exactly one LAD Move to Marker is expected.');
      assert.equal(Number(moves[0][1]), expected);
    } else assert.fail('No readback assertion for this fixture language.');
  }
  return { name: spec.name, blockType: spec.blockType, language: spec.language, version,
    semanticValue: spec.kind === 'udt' || spec.structuralUpdate ? undefined : expected };
}

module.exports = { buildExternalFixtures, buildSdFixtures, checkReadback };
