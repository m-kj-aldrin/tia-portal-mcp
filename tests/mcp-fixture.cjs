'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const { createHash } = require('node:crypto');

const PROJECT_PATH = path.resolve(__dirname, '../tia/Demo/Demo.ap20');
const CPU_TYPE = 'OrderNumber:6ES7 510-1DJ01-0AB0/V2.0';
const READS = ['list_tia_processes', 'get_status', 'list_devices', 'get_device', 'list_blocks', 'get_block',
  'list_udts', 'get_udt', 'list_tag_tables', 'get_tag_table', 'get_cross_references', 'export_tag_table',
  'list_technology_objects', 'list_available_technology_objects', 'get_technology_object'];
const WRITES = ['create_device', 'delete_device', 'write_blocks', 'write_udts', 'create_tag_table', 'create_tag',
  'create_user_constant', 'set_tag_entry_attribute', 'delete_tag_entry', 'import_tag_tables', 'delete_block',
  'delete_udt', 'delete_tag_table', 'create_technology_object', 'set_technology_object_parameters',
  'create_group', 'delete_group', 'rename', 'compile_plc'];
const TOOLS = [...READS, ...WRITES];
const GROUPS = [
  { kind: 'block', list: 'list_blocks', nodeKind: 'blockGroup' },
  { kind: 'udt', list: 'list_udts', nodeKind: 'typeGroup' },
  { kind: 'tagTable', list: 'list_tag_tables', nodeKind: 'tagTableGroup' },
  { kind: 'technologyObject', list: 'list_technology_objects', nodeKind: 'technologyObjectGroup' }
];
const canonical = value => typeof value === 'string' ? path.win32.normalize(value).toLowerCase() : null;
const walk = nodes => (nodes || []).flatMap(node => [node, ...walk(node.children)]);
const leaves = (payload, kind) => { assert.ok(Array.isArray(payload.roots)); return walk(payload.roots).filter(node => node.kind === kind); };
function id(value) { assert.equal(typeof value, 'string'); assert.ok(value.trim().length > 0, 'Native identity is required.'); return value; }
function one(nodes, name) {
  const matches = nodes.filter(node => node.name === name);
  assert.equal(matches.length, 1, 'Expected one ' + name);
  return matches[0];
}
function affected(payload, kind, name, objectId) {
  assert.equal(payload.affectedObjects.length, 1, 'A single fixture mutation must affect exactly one object.');
  const item = payload.affectedObjects[0];
  assert.equal(item.kind, kind);
  assert.equal(item.name, name);
  if (objectId !== undefined) assert.equal(item.objectId, objectId);
  return item;
}
function checksums(source, format) {
  assert.equal(source?.format, format);
  assert.ok(Array.isArray(source.documents) && source.documents.length > 0);
  for (const document of source.documents) {
    assert.equal(typeof document.name, 'string');
    assert.equal(typeof document.content, 'string');
    assert.ok(document.content.trim().length > 0);
    assert.deepEqual(document.checksum, { algorithm: 'sha-256', scope: 'returned-content', encoding: 'utf-8-no-bom',
      value: createHash('sha256').update(document.content, 'utf8').digest('hex') });
  }
  return source.documents;
}
function sourceSpec(kind, prefix, tagName) {
  const name = prefix + (kind === 'block' ? '_FC' : '_UDT');
  return {
    kind, name, list: kind === 'block' ? 'list_blocks' : 'list_udts', read: kind === 'block' ? 'get_block' : 'get_udt',
    write: kind === 'block' ? 'write_blocks' : 'write_udts', filename: name + (kind === 'block' ? '.scl' : '.udt'),
    content: kind === 'block'
      ? `FUNCTION "${name}" : Void\nVERSION : 0.1\nVAR_OUTPUT\n  Marker : Int;\n  ReferencedTag : Bool;\nEND_VAR\nBEGIN\n  #Marker := 17;\n  #ReferencedTag := "${tagName}";\nEND_FUNCTION\n`
      : `TYPE "${name}"\nVERSION : 0.1\nSTRUCT\n  Flag : Bool;\nEND_STRUCT;\nEND_TYPE\n`,
    verify(documents, updated) {
      assert.equal(documents.length, 1);
      const content = documents[0].content;
      const declarations = [...content.matchAll(/^\s*(FUNCTION|FUNCTION_BLOCK|DATA_BLOCK|ORGANIZATION_BLOCK|TYPE)\s+"([^"]+)"/gmi)];
      assert.equal(declarations.length, 1);
      assert.equal(declarations[0][1].toUpperCase(), kind === 'block' ? 'FUNCTION' : 'TYPE');
      assert.equal(declarations[0][2], name);
      if (kind === 'block') {
        assert.match(content, new RegExp(`#(?:"Marker"|Marker)\\s*:=\\s*(?:Int#)?${updated ? 29 : 17}\\s*;`, 'i'));
        assert.match(content, new RegExp(`#(?:"ReferencedTag"|ReferencedTag)\\s*:=\\s*"${tagName}"\\s*;`, 'i'));
        if (updated) assert.doesNotMatch(content, /#(?:"Marker"|Marker)\s*:=\s*(?:Int#)?17\s*;/i);
      } else {
        assert.match(content, /(?:"Flag"|\bFlag)\s*(?:\{[^}]*\}\s*)?:\s*Bool\b/i);
        if (updated) assert.match(content, /(?:"SampleCount"|\bSampleCount)\s*(?:\{[^}]*\}\s*)?:\s*Int\b/i);
        else assert.doesNotMatch(content, /\bSampleCount\b/i);
      }
    },
    edit(content) {
      if (kind === 'block') {
        const marker = /(#(?:"Marker"|Marker)\s*:=\s*)(?:Int#)?17(\s*;)/gi;
        assert.equal([...content.matchAll(marker)].length, 1);
        return content.replace(marker, (_, before, after) => before + '29' + after);
      }
      assert.equal([...content.matchAll(/\bEND_STRUCT\s*;/gi)].length, 1);
      assert.doesNotMatch(content, /\bSampleCount\b/i);
      return content.replace(/\bEND_STRUCT\s*;/i, '  SampleCount : Int;\nEND_STRUCT;');
    }
  };
}
function compilation(payload, expectedSuccess, names) {
  assert.equal(payload.complete, true, 'Compiler diagnostics must be complete.');
  assert.deepEqual(payload.errors, []);
  assert.equal(payload.compilationSucceeded, expectedSuccess);
  assert.ok(['Success', 'Information', 'Warning', 'Error'].includes(payload.state));
  assert.ok(Number.isInteger(payload.errorCount) && payload.errorCount >= 0);
  assert.ok(Number.isInteger(payload.warningCount) && payload.warningCount >= 0);
  const messages = [];
  function visit(items) {
    assert.ok(Array.isArray(items));
    for (const item of items) {
      assert.equal(typeof item.description, 'string');
      assert.ok(['Success', 'Information', 'Warning', 'Error'].includes(item.state));
      assert.ok(Number.isFinite(Date.parse(item.dateTime)));
      assert.match(item.dateTime, /Z$/);
      assert.ok(Number.isInteger(item.errorCount) && Number.isInteger(item.warningCount));
      messages.push(item); visit(item.messages);
    }
  }
  visit(payload.messages);
  if (expectedSuccess) { assert.equal(payload.errorCount, 0); assert.notEqual(payload.state, 'Error'); }
  else {
    assert.equal(payload.state, 'Error'); assert.ok(payload.errorCount > 0);
    assert.ok(messages.some(item => item.errorCount > 0 && names.some(name =>
      (String(item.description) + ' ' + String(item.path)).toLowerCase().includes(name.toLowerCase()))),
    'Compiler error must identify this run\'s missing tag or FC.');
  }
  return { state: payload.state, errorCount: payload.errorCount, warningCount: payload.warningCount, messages: messages.length };
}
function crossReference(payload, tagId, blockId) {
  assert.ok(Array.isArray(payload.sources));
  const sources = walk(payload.sources).filter(source => source.objectId === tagId);
  assert.ok(sources.length > 0, 'The cross-reference source must identify the fixture tag.');
  const references = sources.flatMap(source => [source, ...walk(source.children)]).flatMap(source => source.references || []);
  assert.ok(references.some(reference => reference.objectId === blockId && reference.locations?.some(location =>
    location.referenceType === 'UsedBy' && String(location.access).split(',').map(value => value.trim()).includes('Read'))),
  'Expected a native UsedBy/Read relation to the fixture FC.');
}

module.exports = { PROJECT_PATH, CPU_TYPE, READS, WRITES, TOOLS, GROUPS, canonical, walk, leaves, id, one, affected,
  checksums, sourceSpec, compilation, crossReference };
