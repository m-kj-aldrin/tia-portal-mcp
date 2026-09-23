// Opt-in against the ONE already-running managed server. Never starts a server or attaches to TIA.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const base = 'http://127.0.0.1:5000';
const enabled = process.env.TIA_MCP_HTTP_SMOKE === '1';

test('browser origins accept both supported loopback names and reject other origins', { skip: !enabled }, async () => {
  const before = await (await fetch(base + '/api/status')).json();
  const send = (target, route, origin, body) => fetch(target + route, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1', Origin: origin },
    body: JSON.stringify(body)
  });
  const tools = { jsonrpc: '2.0', id: 'origin-regression', method: 'tools/list' };
  const closedServerTab = { tabId: 'server' };
  for (const origin of [base, 'http://localhost:5000']) {
    const mcp = await send(origin, '/mcp', origin, tools);
    assert.equal(mcp.status, 200, 'MCP rejected its own supported browser origin: ' + origin);
    assert.ok(Array.isArray((await mcp.json()).result.tools));
    const dashboard = await send(origin, '/api/dashboard/tabs/dismiss', origin, closedServerTab);
    assert.equal(dashboard.status, 400, 'Dashboard origin gate rejected its own supported browser origin: ' + origin);
    assert.equal((await dashboard.json()).error.code, 'invalidRequest');
  }
  for (const origin of [
    'https://example.org', 'http://example.org:5000', 'http://localhost:5001',
    'http://127.0.0.1:5001', 'https://localhost:5000', 'null',
    'http://localhost.example.org:5000', 'http://127.0.0.1.example.org:5000',
    'http://localhost:5000/path', 'http://user@localhost:5000',
    'http://localhost:5000 http://127.0.0.1:5000'
  ]) {
    for (const [route, body] of [['/mcp', tools], ['/api/dashboard/tabs/dismiss', closedServerTab]]) {
      const response = await send(base, route, origin, body);
      assert.equal(response.status, 403, route + ' accepted the forbidden origin: ' + origin);
      await response.text();
    }
  }
  const after = await (await fetch(base + '/api/status')).json();
  assert.deepEqual(after.connections, before.connections, 'Origin checks changed attachments.');
});

test('loaded server publishes and dispatches twenty-four guarded MCP tools without native writes', { skip: !enabled }, async () => {
  // This fixed, impossible Windows PID ensures valid write requests stop at admission.
  const before = await (await fetch(base + '/api/status')).json();
  assert.equal(before.implementationPhase, 'native-compile-delete-export');
  assert.equal(before.mcpPublication, 'twenty-four-read-write-tools');
  const rpc = async (method, params) => (await (await fetch(base + '/mcp', { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) })).json()).result;
  const init = await rpc('initialize', { protocolVersion: '2025-03-26' });
  assert.equal(init.serverInfo.version, 'native-compile-delete-export-1');
  const listing = await rpc('tools/list');
  const names = ['list_tia_processes', 'get_status', 'list_devices', 'get_device', 'list_blocks',
    'get_block', 'list_udts', 'get_udt', 'list_tag_tables', 'get_tag_table', 'get_cross_references', 'export_tag_table', 'write_blocks', 'write_udts', 'create_tag_table', 'create_tag', 'create_user_constant', 'set_tag_entry_attribute', 'delete_tag_entry', 'import_tag_tables', 'delete_block', 'delete_udt', 'delete_tag_table', 'compile_plc'];
  assert.deepEqual(listing.tools.map(tool => tool.name), names);
  const payload = call => JSON.parse(call.content[0].text);
  const invoke = (name, args = {}) => rpc('tools/call', { name, arguments: args });
  const envelope = (body, processId) => {
    assert.ok(Date.parse(body.readAtUtc)); assert.ok(Array.isArray(body.errors));
    if (processId !== undefined) assert.equal(body.processId, processId);
  };
  const bridge = payload(await invoke('get_status'));
  envelope(bridge); assert.equal(bridge.accessProfile, 'full'); assert.equal(bridge.writeToolsAvailable, true);
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
    if (p.name) args.name = 'Unreachable';
    if (p.dataType) args.dataType = 'Bool';
    if (p.logicalAddress) args.logicalAddress = '%M0.0';
    if (p.value) args.value = 'false';
    if (p.attributeName) { args.attributeName = 'Name'; args.attributeValue = 'Unreachable'; }
    if (p.documents) {
      if (p.sourceFormat) args.sourceFormat = 'simatic-ml';
      args.documents = [{name:'Unreachable.xml',content:'<Document/>'}];
    }
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
  const post = async (body, headers = {}) => fetch(base + '/api/dashboard/blocks', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1', ...headers }, body: JSON.stringify(body) });
  const invalid = await post({ processId: 2147483647, plcObjectId: 'cpu', includeSource: false });
  assert.equal(invalid.status, 400);
  assert.equal((await invalid.json()).processId, 2147483647);
  const disconnected = await post({ processId: 2147483647, plcObjectId: 'cpu' });
  assert.equal(disconnected.status, 409);
  const failure = await disconnected.json();
  assert.equal(failure.processId, 2147483647);
  assert.equal(failure.error.code, 'notConnected');
  assert.equal((await post({ processId: 2147483647, plcObjectId: 'cpu' }, { Origin: 'https://example.org' })).status, 403);
  const block = async body => fetch(base + '/api/dashboard/block', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1' }, body: JSON.stringify(body) });
  assert.equal((await block({ processId: 2147483647, objectId: 'block', includeDependencies: true })).status, 400);
  assert.equal((await block({ processId: 'wrong', objectId: 'block' })).status, 400);
  const disconnectedBlock = await block({ processId: 2147483647, objectId: 'block', includeSource: false });
  assert.equal(disconnectedBlock.status, 409);
  assert.equal((await disconnectedBlock.json()).error.code, 'notConnected');
  for (const [route, selector] of [['udts', { plcObjectId: 'cpu' }], ['udt', { objectId: 'udt' }], ['tag-tables', { plcObjectId: 'cpu' }], ['tag-table', { objectId: 'table' }], ['cross-references', { objectId: 'own-tag' }]]) {
    const send = (fields, headers = {}) => fetch(base + '/api/dashboard/' + route, { method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1', ...headers },
      body: JSON.stringify({ processId: 2147483647, ...selector, ...fields }) });
    assert.equal((await send({ unexpected: true })).status, 400);
    assert.equal((await send({}, { Origin: 'https://example.org' })).status, 403);
    const response = await send({}); assert.equal(response.status, 409);
    assert.equal((await response.json()).error.code, 'notConnected');
  }
  const tableRequest = async fields => fetch(base + '/api/dashboard/tag-table', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1' },
    body: JSON.stringify({ processId: 2147483647, objectId: 'table', ...fields }) });
  for (const fields of [{includeSource:false}, {sourceFormat:'best'}, {includeDependencies:false}, {includeEntries:null}])
    assert.equal((await tableRequest(fields)).status, 400);
  assert.equal((await tableRequest({includeEntries:false, includePath:false})).status, 409);
  assert.equal((await fetch(base + '/api/devices')).status, 404);
  const loadAsset = async (url, name, contentType) => {
    const response = await fetch(base + url);
    assert.equal(response.status, 200, 'Missing dashboard asset: ' + url);
    assert.equal(response.headers.get('content-type'), contentType + '; charset=utf-8');
    assert.equal(response.headers.get('cache-control'), 'no-store');
    const contents = await response.text();
    assert.equal(contents, fs.readFileSync(path.join(__dirname, '../src/TiaOpennessMcpServer/Dashboard/wwwroot', name), 'utf8'),
      'Served dashboard asset differs from this checkout: ' + name);
    return contents;
  };
  const html = await loadAsset('/', 'index.html', 'text/html');
  assert.match(html, /<link rel="stylesheet" href="\/dashboard\/styles\.css">/);
  assert.match(html, /<script src="\/dashboard\/dashboard\.js"><\/script>/);
  assert.doesNotMatch(html, /<style>|<script>/);
  const [styles, script] = await Promise.all([
    loadAsset('/dashboard/styles.css', 'styles.css', 'text/css'),
    loadAsset('/dashboard/dashboard.js', 'dashboard.js', 'text/javascript')
  ]);
  assert.match(styles, /@media \(max-width: 640px\)/);
  assert.match(script, /Open project in TIA/);
  assert.doesNotMatch(script, /Open project in TIA is not available/);
  assert.match(script, /fetch\('\/mcp'/);
  const dashboard = await (await fetch(base + '/api/dashboard/dashboard')).json();
  assert.equal(dashboard.history.tabs[0].id, 'server');
  assert.equal(dashboard.history.maxLogEntries, 400);
  assert.equal(dashboard.history.maxHistoricalTabs, 24);
  const forms = await (await fetch(base + '/api/dashboard/tool-forms')).text();
  for (const name of names) assert.ok(forms.includes('data-tool="' + name + '"'), "Missing generated form: " + name);
  assert.match(forms, /data-enabled-when="includeSource=true,sourceFormat=external-source"/);
  assert.doesNotMatch(forms, /<script/i);
  const logs = await (await fetch(base + '/api/dashboard/logs?after=0&generation=0')).json();
  assert.equal(logs.reset, false);
  assert.ok(logs.entries.some(entry => entry.operation === 'startup' && entry.tabId === 'server'));
  const opened = await fetch(base + '/api/dashboard/projects/open', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1' }, body: JSON.stringify({ tabId: 'server' }) });
  assert.equal(opened.status, 400);
  assert.equal((await opened.json()).error.code, 'invalidRequest');
  const dismissed = await fetch(base + '/api/dashboard/tabs/dismiss', { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1' }, body: JSON.stringify({ tabId: 'server' }) });
  assert.equal(dismissed.status, 400);
  assert.equal((await dismissed.json()).error.code, 'invalidRequest');
  const removed = await fetch(base + '/api/dashboard/write-probe', { method:'POST', headers:{'Content-Type':'application/json','X-Tia-Dashboard':'1'}, body:'{}' });
  assert.equal(removed.status,404);
  for (const [headers,status] of [[{'Content-Type':'application/json',Origin:'https://example.org'},403],[{'Content-Type':'text/plain'},415]]) {
    const response=await fetch(base+'/mcp',{method:'POST',headers,body:JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/list'})});
    assert.equal(response.status,status);
  }
  const after = await (await fetch(base + '/api/status')).json();
  assert.deepEqual(after.connections, before.connections, 'Smoke checks changed attachments.');
});
