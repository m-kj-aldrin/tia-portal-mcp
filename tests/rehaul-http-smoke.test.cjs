// Opt-in against the ONE already-running managed server. Never starts a server or attaches to TIA.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const base = 'http://127.0.0.1:5000';
const enabled = process.env.REHAUL_HTTP_SMOKE === '1';

test('loaded cutover publishes and dispatches eleven guarded read-only MCP tools', { skip: !enabled }, async () => {
  const before = await (await fetch(base + '/api/status')).json();
  assert.equal(before.implementationPhase, 'rehaul-mcp-read-only');
  assert.equal(before.mcpPublication, 'eleven-read-only-tools');
  const rpc = async (method, params) => (await (await fetch(base + '/mcp', { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) })).json()).result;
  const init = await rpc('initialize', { protocolVersion: '2025-03-26' });
  assert.equal(init.serverInfo.version, 'rehaul-read-only-1');
  const listing = await rpc('tools/list');
  const names = ['list_tia_processes', 'get_status', 'list_devices', 'get_device', 'list_blocks',
    'get_block', 'list_udts', 'get_udt', 'list_tag_tables', 'get_tag_table', 'get_cross_references'];
  assert.deepEqual(listing.tools.map(tool => tool.name), names);
  const payload = call => JSON.parse(call.content[0].text);
  const invoke = (name, args = {}) => rpc('tools/call', { name, arguments: args });
  const envelope = (body, processId) => {
    assert.ok(Date.parse(body.readAtUtc)); assert.ok(Array.isArray(body.errors));
    if (processId !== undefined) assert.equal(body.processId, processId);
  };
  const bridge = payload(await invoke('get_status'));
  envelope(bridge); assert.equal(bridge.accessProfile, 'read-only'); assert.equal(bridge.writeToolsAvailable, false);
  assert.equal(bridge.processId, undefined); assert.equal(bridge.connections, undefined); assert.equal(bridge.project, undefined);
  const discovery = await invoke('list_tia_processes');
  assert.equal(discovery.isError, false); envelope(payload(discovery));
  assert.ok(Array.isArray(payload(discovery).processes));
  for (const tool of listing.tools) {
    assert.doesNotMatch(tool.description, /DISABLED/);
    assert.equal(tool.inputSchema.additionalProperties, false);
    const p = tool.inputSchema.properties;
    if (p.processId) { assert.equal(p.processId.type, 'integer'); assert.equal(p.processId.minimum, 1); }
    for (const [name, property] of Object.entries(p)) {
      if (name.startsWith('include')) { assert.equal(property.type, 'boolean'); assert.equal(property.default, name !== 'includeDependencies'); }
    }
    const invalid = await invoke(tool.name, { processId: 2147483647, unexpected: true });
    assert.equal(invalid.isError, true); envelope(payload(invalid), 2147483647);
    assert.equal(payload(invalid).error.code, 'invalidRequest');
    if (tool.name === 'list_tia_processes') continue;
    const args = { processId: 2147483647 };
    if (p.objectId) args.objectId = 'not-a-native-object';
    if (p.plcObjectId) args.plcObjectId = 'not-a-native-cpu';
    const call = await invoke(tool.name, args);
    assert.equal(call.isError, true); envelope(payload(call), 2147483647);
    assert.equal(payload(call).error.code, tool.name === 'get_status' ? 'processNotFound' : 'notConnected');
    assert.ok(payload(call).errors.every(e => e.origin === 'bridge'));
    const missing = await invoke(tool.name);
    assert.equal(missing.isError, tool.name !== 'get_status');
    const duplicate = await (await fetch(base + '/mcp', { method: 'POST', headers: {'Content-Type':'application/json'},
      body: '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":' + JSON.stringify(tool.name) + ',"arguments":{"processId":2147483647,"processId":2147483647}}}' })).json();
    assert.equal(payload(duplicate.result).error.code, 'invalidRequest');
  }
  for (const name of ['connect_to_tia_portal', 'disconnect_from_tia_portal', 'open_tia_project',
    'list_plc_objects', 'find_plc_objects', 'read_plc_object', 'get_tag_table_entries'])
    assert.equal(payload(await invoke(name)).error.code, 'unknownTool');
  for (const name of ['get_block', 'get_udt']) {
    for (const options of [{includeDependencies:true}, {includeSource:false, sourceFormat:'external-source', includeDependencies:true},
      {sourceFormat:'simaticml'}, {includeSource:'false'}, {includePath:null}]) {
      const call = await invoke(name, {processId:2147483647, objectId:'id', ...options});
      assert.equal(payload(call).error.code, 'invalidRequest');
    }
  }
  for (const options of [{includeEntries:null}, {includeSource:false}, {sourceFormat:'best'}, {includeDependencies:false}])
    assert.equal(payload(await invoke('get_tag_table', {processId:2147483647, objectId:'id', ...options})).error.code, 'invalidRequest');
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
  for (const [route, selector] of [['udts', { plcObjectId: 'cpu' }], ['udt', { objectId: 'udt' }], ['tag-tables', { plcObjectId: 'cpu' }], ['tag-table', { objectId: 'table' }], ['cross-references', { objectId: 'own-tag' }]]) {
    const send = (fields, headers = {}) => fetch(base + '/api/prototype/' + route, { method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Tia-Prototype': '1', ...headers },
      body: JSON.stringify({ processId: 2147483647, ...selector, ...fields }) });
    assert.equal((await send({ unexpected: true })).status, 400);
    assert.equal((await send({}, { Origin: 'https://example.org' })).status, 403);
    const response = await send({}); assert.equal(response.status, 409);
    assert.equal((await response.json()).error.code, 'notConnected');
  }
  const tableRequest = async fields => fetch(base + '/api/prototype/tag-table', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Prototype': '1' },
    body: JSON.stringify({ processId: 2147483647, objectId: 'table', ...fields }) });
  for (const fields of [{includeSource:false}, {sourceFormat:'best'}, {includeDependencies:false}, {includeEntries:null}])
    assert.equal((await tableRequest(fields)).status, 400);
  assert.equal((await tableRequest({includeEntries:false, includePath:false})).status, 409);
  assert.equal((await fetch(base + '/api/devices')).status, 404);
  const html = await (await fetch(base + '/')).text();
  assert.match(html, /List blocks/);
  assert.match(html, /Read block/);
  assert.match(html, /List UDTs/);
  assert.match(html, /Read UDT/);
  assert.match(html, /List tag tables/);
  assert.match(html, /Read tag table/);
  assert.match(html, /Read cross-references/);
  const after = await (await fetch(base + '/api/status')).json();
  assert.deepEqual(after.connections, before.connections, 'Smoke checks changed attachments.');
});
