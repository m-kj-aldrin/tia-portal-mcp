// Opt-in against the ONE already-running managed server. Never starts a server or attaches to TIA.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const base = 'http://127.0.0.1:5000';
const enabled = process.env.REHAUL_HTTP_SMOKE === '1';

test('loaded transition build has disabled MCP, guarded block/UDT requests and no legacy routing', { skip: !enabled }, async () => {
  const before = await (await fetch(base + '/api/status')).json();
  assert.equal(before.implementationPhase, 'rehaul-udt-read');
  assert.equal(before.mcpPublication, 'held-eight-disabled-v1-descriptors');
  const rpc = async (method, params) => (await (await fetch(base + '/mcp', { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) })).json()).result;
  const init = await rpc('initialize', { protocolVersion: '2025-03-26' });
  assert.equal(init.serverInfo.version, 'rehaul-transition');
  const listing = await rpc('tools/list');
  assert.deepEqual(listing.tools.map(tool => tool.name), ['connect_to_tia_portal', 'get_status', 'list_devices',
    'list_plc_objects', 'find_plc_objects', 'read_plc_object', 'get_tag_table_entries', 'get_cross_references']);
  for (const tool of listing.tools) {
    assert.match(tool.description, /^DISABLED/);
    const call = await rpc('tools/call', { name: tool.name, arguments: {} });
    assert.equal(call.isError, true);
    assert.equal(JSON.parse(call.content[0].text).error.code, 'prototype-mode');
  }
  for (const name of ['list_blocks', 'get_block', 'list_udts', 'get_udt']) {
    const call = await rpc('tools/call', { name, arguments: {} });
    assert.equal(JSON.parse(call.content[0].text).error.code, 'unknownTool');
  }
  const post = async (body, headers = {}) => fetch(base + '/api/prototype/blocks', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Prototype': '1', ...headers }, body: JSON.stringify(body) });
  const invalid = await post({ processId: 2147483647, plcObjectId: 'cpu', includeSource: false });
  assert.equal(invalid.status, 400);
  assert.equal((await invalid.json()).processId, 2147483647);
  const disconnected = await post({ processId: 2147483647, plcObjectId: 'cpu' });
  assert.equal(disconnected.status, 409);
  const failure = await disconnected.json();
  assert.equal(failure.processId, 2147483647);
  assert.equal(failure.error.code, 'notConnected');
  assert.equal((await post({ processId: 2147483647, plcObjectId: 'cpu' }, { Origin: 'https://example.org' })).status, 403);
  const block = async body => fetch(base + '/api/prototype/block', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Prototype': '1' }, body: JSON.stringify(body) });
  assert.equal((await block({ processId: 2147483647, objectId: 'block', includeDependencies: true })).status, 400);
  assert.equal((await block({ processId: 'wrong', objectId: 'block' })).status, 400);
  const disconnectedBlock = await block({ processId: 2147483647, objectId: 'block', includeSource: false });
  assert.equal(disconnectedBlock.status, 409);
  assert.equal((await disconnectedBlock.json()).error.code, 'notConnected');
  for (const [route, selector] of [['udts', { plcObjectId: 'cpu' }], ['udt', { objectId: 'udt' }]]) {
    const send = (fields, headers = {}) => fetch(base + '/api/prototype/' + route, { method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Tia-Prototype': '1', ...headers },
      body: JSON.stringify({ processId: 2147483647, ...selector, ...fields }) });
    assert.equal((await send({ unexpected: true })).status, 400);
    assert.equal((await send({}, { Origin: 'https://example.org' })).status, 403);
    const response = await send({}); assert.equal(response.status, 409);
    assert.equal((await response.json()).error.code, 'notConnected');
  }
  assert.equal((await fetch(base + '/api/devices')).status, 404);
  const html = await (await fetch(base + '/')).text();
  assert.match(html, /List blocks/);
  assert.match(html, /Read block/);
  assert.match(html, /List UDTs/);
  assert.match(html, /Read UDT/);
  const after = await (await fetch(base + '/api/status')).json();
  assert.deepEqual(after.connections, before.connections, 'Smoke checks changed attachments.');
});
