// Opt-in read-only transport verification against the single managed server.
// Never starts a listener, connects to TIA or submits a reachable native write.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const enabled = process.env.TIA_MCP_HTTP_SMOKE === '1';

function managedState() {
  return JSON.parse(fs.readFileSync(path.join(__dirname, '..', '.tia-mcp-server-state.json'), 'utf8').replace(/^\uFEFF/, ''));
}

test('managed lifecycle identity requires its token and matches the tracked executable', { skip: !enabled }, async () => {
  const state = managedState();
  const base = `http://127.0.0.1:${state.port}`;
  for (const headers of [{}, { 'X-Tia-Mcp-Control-Token': 'invalid-instance-token' }]) {
    const rejected = await fetch(base + '/api/lifecycle/health', { headers, redirect: 'error' });
    assert.equal(rejected.status, 403);
    const body = await rejected.json();
    assert.equal(body.processId, undefined);
    assert.equal(body.executablePath, undefined);
  }
  const response = await fetch(base + '/api/lifecycle/health', {
    headers: { 'X-Tia-Mcp-Control-Token': state.controlToken }, redirect: 'error'
  });
  assert.equal(response.status, 200);
  assert.equal(response.headers.get('cache-control'), 'no-store');
  const raw = await response.text();
  assert.ok(!raw.includes(state.controlToken), 'Health leaked its authentication token.');
  const body = JSON.parse(raw);
  assert.equal(body.status, 'ready');
  assert.equal(body.processId, state.pid);
  assert.equal(body.executablePath.toLowerCase(), state.executablePath.toLowerCase());
});

test('MCP rejects malformed envelopes on the wire and preserves null error IDs', { skip: !enabled }, async () => {
  const state = managedState();
  const base = `http://127.0.0.1:${state.port}`;
  const before = await (await fetch(base + '/api/status')).json();
  const post = body => fetch(base + '/mcp', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body
  });
  // Even if envelope validation regresses, this impossible PID cannot reach a native write.
  const request = { jsonrpc: '2.0', id: 1, method: 'tools/call',
    params: { name: 'delete_block', arguments: { processId: 2147483647, objectId: 'unreachable' } } };
  const missingVersion = { ...request };
  delete missingVersion.jsonrpc;
  for (const [input, code] of [
    ['{', -32700],
    [JSON.stringify({ ...request, jsonrpc: '1.0', id: {} }), -32600],
    [JSON.stringify(missingVersion), -32600],
    [JSON.stringify({ ...request, id: null }), -32600],
    [JSON.stringify([request]), -32600]
  ]) {
    const response = await post(input);
    assert.equal(response.status, 400);
    const body = await response.json();
    assert.equal(body.jsonrpc, '2.0');
    assert.equal(body.error.code, code);
    assert.ok(Object.hasOwn(body, 'id'));
    assert.equal(body.id, null);
    assert.equal(body.result, undefined);
  }
  const notification = await post(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }));
  assert.equal(notification.status, 202);
  assert.equal(await notification.text(), '');
  const unknown = await (await post(JSON.stringify({ jsonrpc: '2.0', id: 'unknown-method', method: 'unknown' }))).json();
  assert.equal(unknown.id, 'unknown-method');
  assert.equal(unknown.error.code, -32601);
  const after = await (await fetch(base + '/api/status')).json();
  assert.deepEqual(after.connections, before.connections, 'Protocol checks changed attachments.');
});
