'use strict';

const assert = require('node:assert/strict');
const { checkReadback } = require('./import-fixtures.cjs');

// These bounded, authored documents contain only the named fixture. Their
// acceptance is established by native import plus readback, not these builders.
const INTERFACE = 'http://www.siemens.com/automation/Openness/SW/Interface/v5';
const STRUCTURED_TEXT = 'http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4';
const FLG_NET = 'http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5';

function buildXmlFixtures(prefix, numbers, logicalAddress) {
  assert.match(prefix, /^[A-Za-z][A-Za-z0-9_]{0,95}$/, 'Use a short ASCII fixture prefix.');
  assert.ok(numbers && typeof numbers === 'object', 'Explicit reserved block numbers are required.');
  assert.match(logicalAddress, /^%M\d+\.[0-7]$/, 'Use an explicitly reserved Bool memory address.');
  const definitions = [
    { key: 'xml-scl-fc', kind: 'block', blockType: 'FC', language: 'SCL', suffix: 'XmlSclFC', readFormat: 'external-source' },
    { key: 'xml-lad-fb', kind: 'block', blockType: 'FB', language: 'LAD', suffix: 'XmlLadFB', readFormat: 'simatic-sd' },
    { key: 'xml-db', kind: 'block', blockType: 'DB', language: 'DB', suffix: 'XmlDB', readFormat: 'external-source' },
    { key: 'xml-udt', kind: 'udt', blockType: null, language: null, suffix: 'XmlUDT', readFormat: 'external-source' },
    { key: 'xml-tags', kind: 'tagTable', blockType: null, language: null, suffix: 'XmlTags', readFormat: null }
  ];
  return definitions.map(definition => {
    const spec = {
      ...definition, name: `${prefix}_${definition.suffix}`, extension: '.xml', sourceFormat: 'simatic-ml',
      provenance: 'authored-test-input; native acceptance requires import and readback',
      number: definition.kind === 'block' ? numbers[definition.key] : null,
      logicalAddress, tagName: `${prefix}_XmlSignal`, constantName: `${prefix}_XmlLimit`
    };
    if (spec.kind === 'block') assert.ok(Number.isInteger(spec.number) && spec.number > 0 && spec.number <= 65535,
      `An explicit reserved number is required for ${spec.key}.`);
    spec.documents = version => [{ name: `${spec.name}.xml`, content: documentFor(spec, checkVersion(version)) }];
    return spec;
  });
}

function checkVersion(version) {
  assert.ok(version === 1 || version === 2, 'Fixture version must be 1 (create) or 2 (semantic update).');
  return version;
}

function sections(content) {
  return `<Interface><Sections xmlns="${INTERFACE}">${content}</Sections></Interface>`;
}

function structMembers(version) {
  return `<Member Name="Flag" Datatype="Bool" />${version === 2 ? '<Member Name="SampleCount" Datatype="Int" />' : ''}`;
}

function codeInterface(fb) {
  return sections('<Section Name="Input" /><Section Name="Output"><Member Name="Marker" Datatype="Int" /></Section>' +
    '<Section Name="InOut" />' + (fb ? '<Section Name="Static" />' : '') +
    '<Section Name="Temp" /><Section Name="Constant" />' +
    (fb ? '' : '<Section Name="Return"><Member Name="Ret_Val" Datatype="Void"><AttributeList /></Member></Section>'));
}

function sclNetwork(marker) {
  return `<StructuredText xmlns="${STRUCTURED_TEXT}">
  <Access Scope="LocalVariable" UId="21"><Symbol UId="22"><Component Name="Marker" UId="23" /></Symbol></Access>
  <Blank UId="24" /><Token Text=":=" UId="25" /><Blank UId="26" />
  <Access Scope="LiteralConstant" UId="27"><Constant UId="28"><ConstantType Informative="true" UId="29">LInt</ConstantType><ConstantValue UId="30">${marker}</ConstantValue></Constant></Access>
  <Token Text=";" UId="31" /><NewLine UId="32" />
</StructuredText>`;
}

function ladNetwork(marker) {
  return `<FlgNet xmlns="${FLG_NET}">
  <Parts>
    <Access Scope="LiteralConstant" UId="21"><Constant><ConstantType>Int</ConstantType><ConstantValue>${marker}</ConstantValue></Constant></Access>
    <Access Scope="LocalVariable" UId="22"><Symbol><Component Name="Marker" /></Symbol></Access>
    <Part Name="Move" UId="23" DisabledENO="true"><TemplateValue Name="Card" Type="Cardinality">1</TemplateValue></Part>
  </Parts>
  <Wires>
    <Wire UId="24"><Powerrail /><NameCon UId="23" Name="en" /></Wire>
    <Wire UId="25"><IdentCon UId="21" /><NameCon UId="23" Name="in" /></Wire>
    <Wire UId="26"><NameCon UId="23" Name="out1" /><IdentCon UId="22" /></Wire>
  </Wires>
</FlgNet>`;
}

function documentFor(spec, version) {
  const value = version === 1 ? 17 : 29;
  let body;
  if (spec.kind === 'tagTable') {
    body = `<SW.Tags.PlcTagTable ID="0">
  <AttributeList><Name>${spec.name}</Name></AttributeList>
  <ObjectList>
    <SW.Tags.PlcTag ID="1" CompositionName="Tags"><AttributeList><Name>${spec.tagName}</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>${spec.logicalAddress}</LogicalAddress></AttributeList></SW.Tags.PlcTag>
    <SW.Tags.PlcUserConstant ID="2" CompositionName="UserConstants"><AttributeList><Name>${spec.constantName}</Name><DataTypeName>Int</DataTypeName><Value>${value}</Value></AttributeList></SW.Tags.PlcUserConstant>
  </ObjectList>
</SW.Tags.PlcTagTable>`;
  } else if (spec.kind === 'udt') {
    body = `<SW.Types.PlcStruct ID="0"><AttributeList>${sections(`<Section Name="None">${structMembers(version)}</Section>`)}<Name>${spec.name}</Name><Namespace /></AttributeList><ObjectList /></SW.Types.PlcStruct>`;
  } else if (spec.blockType === 'DB') {
    body = `<SW.Blocks.GlobalDB ID="0"><AttributeList>${sections(`<Section Name="Static">${structMembers(version)}</Section>`)}<MemoryLayout>Optimized</MemoryLayout><Name>${spec.name}</Name><Namespace /><Number>${spec.number}</Number><ProgrammingLanguage>DB</ProgrammingLanguage></AttributeList><ObjectList /></SW.Blocks.GlobalDB>`;
  } else {
    const fb = spec.blockType === 'FB';
    body = `<SW.Blocks.${spec.blockType} ID="0">
  <AttributeList>${codeInterface(fb)}<MemoryLayout>Optimized</MemoryLayout><Name>${spec.name}</Name><Namespace /><Number>${spec.number}</Number><ProgrammingLanguage>${spec.language}</ProgrammingLanguage></AttributeList>
  <ObjectList><SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits"><AttributeList><NetworkSource>${fb ? ladNetwork(value) : sclNetwork(value)}</NetworkSource><ProgrammingLanguage>${spec.language}</ProgrammingLanguage></AttributeList><ObjectList /></SW.Blocks.CompileUnit></ObjectList>
</SW.Blocks.${spec.blockType}>`;
  }
  return `<?xml version="1.0" encoding="utf-8"?>\n<Document>\n<Engineering version="V20" />\n${body}\n</Document>\n`;
}

function checkXmlReadback(spec, readback, version) {
  checkVersion(version);
  const value = version === 1 ? 17 : 29;
  if (spec.kind === 'tagTable') {
    assert.equal(readback.metadata?.name, spec.name);
    const entries = readback.entries;
    for (const collection of ['tags', 'userConstants', 'systemConstants']) {
      assert.ok(Array.isArray(entries?.[collection]), `The imported table must expose ${collection}.`);
    }
    assert.equal(entries.tags.length, 1);
    assert.equal(entries.userConstants.length, 1);
    assert.equal(entries.systemConstants.length, 0);
    const tag = entries.tags[0];
    const constant = entries.userConstants[0];
    assert.equal(tag.name, spec.tagName);
    assert.equal(tag.dataType, 'Bool');
    assert.equal(tag.logicalAddress, spec.logicalAddress);
    assert.equal(constant.name, spec.constantName);
    assert.equal(constant.dataType, 'Int');
    assert.equal(String(constant.value).replace(/^Int#/i, ''), String(value));
    for (const entry of [tag, constant]) {
      assert.equal(typeof entry.objectId, 'string');
      assert.ok(entry.objectId.trim(), 'Imported entries require native identities.');
    }
    assert.notEqual(tag.objectId, constant.objectId);
    return;
  }
  // The exported representations are identical to those used for external and
  // SD authored inputs. Use the same independent semantic assertions.
  return checkReadback({ ...spec, blockType: spec.kind === 'udt' ? 'UDT' : spec.blockType,
    structuralUpdate: spec.blockType === 'DB' }, readback, version);
}

module.exports = { buildXmlFixtures, checkXmlReadback };
