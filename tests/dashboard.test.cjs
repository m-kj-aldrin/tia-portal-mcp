// Declarative dashboard contract checks. No listener or TIA process is started.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');

const root = path.join(__dirname, '../src/TiaOpennessMcpServer/Dashboard');
const asset = name => fs.readFileSync(path.join(root, 'wwwroot', name), 'utf8');
const source = name => fs.readFileSync(path.join(root, name), 'utf8');

test('pinned local Datastar bundle and license are present and exact', () => {
  const bytes = fs.readFileSync(path.join(root, 'wwwroot/datastar.js'));
  assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'),
    '727844adfc825ee651fb93c544a2a739986f9a21820a94524b35f0cac470cf91');
  assert.match(asset('datastar.js').slice(0, 40), /Datastar v1\.0\.4/);
  assert.match(asset('DATASTAR-LICENSE.txt'), /Copyright © Star Federation/);
  assert.match(asset('DATASTAR-LICENSE.txt'), /Permission is hereby granted/);
});

test('page loads only Datastar and exposes every server patch target', () => {
  const html = asset('index.html');
  assert.equal(fs.existsSync(path.join(root, 'wwwroot/dashboard.js')), false);
  assert.doesNotMatch(source('DashboardEndpoints.cs'), /\/dashboard\/dashboard\.js/);
  assert.deepEqual([...html.matchAll(/<script\b[^>]*src="([^"]+)"/g)].map(match => match[1]),
    ['/dashboard/datastar.js']);
  for (const id of ['dashboard-state', 'tabs', 'activity', 'dashboard-contexts',
    'dashboard-forms', 'dashboard-run-views', 'logs', 'dashboard-action-message']) {
    assert.match(html, new RegExp('id="' + id + '"'), id);
  }
  assert.doesNotMatch(html, /dashboard\.js|tool-form-definitions|tool-action|connection-action|history-action/);
});

test('local choices are Datastar signals and mode controls are declarative', () => {
  const html = asset('index.html');
  for (const item of ["selectedTabId:'server'", "mode:'tools'",
    "selectedTool:'list_tia_processes'", "inspectorView:'result'",
    'serverBusy:false', 'connectionBusy:false', 'writeToolsAvailable:false']) assert.ok(html.includes(item), item);
  assert.match(html, /id="mode-tools"[^>]*data-on:click="\$mode='tools'/);
  assert.match(html, /id="mode-writes"[^>]*data-on:click="\$mode='writes'/);
  assert.match(html, /id="mode-writes"[^>]*data-show="\$selectedTabId!=='server' && \$writeToolsAvailable"/);
  assert.match(html, /data-attr:aria-pressed="\$mode===/);
  assert.match(html, /id="copy"[^>]*data-on:click="navigator\.clipboard\.writeText/);
});

test('event stream is one safe Datastar GET and sends no source document signals', () => {
  const html = asset('index.html');
  assert.equal((html.match(/@get\('\/api\/dashboard\/events'/g) || []).length, 1);
  assert.match(html, /filterSignals: \{include: \/\^\$\/\}/);
  assert.match(html, /'X-Tia-Dashboard': '1'/);
  assert.match(html, /retry: 'always'/);
  assert.match(html, /openWhenHidden: false/);
  assert.doesNotMatch(html, /data-on-interval|setInterval|setTimeout|\/mcp['"]/);
});

test('visible server-rendered contexts and tool controls have matching styles', () => {
  const css = asset('styles.css');
  for (const selector of ['.summary', '.actions', '.context-path', '.tool-operation-picker',
    '.tool-prerequisite', '.tool-field-actions', '.source-editor', '.run-inspector', '.run-history']) {
    assert.ok(css.includes(selector), selector);
  }
  assert.doesNotMatch(css, /#tool-nav\b|#prerequisite\b|#run-meta\b/);
});

test('server fragments render tabs, contexts and connection actions with Datastar attributes', () => {
  const code = source('DashboardSnapshotFragments.cs');
  assert.match(code, /id=\\\"dashboard-contexts/);
  assert.match(code, /data-signals:server-busy/);
  assert.match(code, /data-signals:write-tools-available/);
  assert.match(code, /data-signals:tab-ids/);
  assert.match(code, /data-on:click/);
  assert.match(code, /\$selectedTabId = el\.dataset\.tab/);
  assert.match(code, /data-show/);
  assert.match(code, /@post\('\/api\/dashboard\//);
  assert.match(code, /retry:'never',requestCancellation:'disabled'/);
  assert.match(code, /data-indicator:connection-busy/);
  for (const field of ['expectedRuntimeStartUtcTicks', 'expectedProjectPath', 'expectedConnectionId'])
    assert.match(code, new RegExp(field));
  assert.doesNotMatch(code, /window\.tiaDashboard|MutationObserver/);
});

test('published tool schemas render bound visible forms with direct one-shot actions', () => {
  const code = source('DashboardToolForms.cs');
  assert.match(code, /foreach \(var property in tool\.InputSchema\.Properties\)/);
  assert.match(code, /class=\\\"dashboard-tab-forms/);
  assert.match(code, /class=\\\"tool-form/);
  assert.match(code, /data-bind=/);
  assert.match(code, /data-on:submit=/);
  assert.match(code, /@post\('\/api\/dashboard\/tools\/run'/);
  assert.match(code, /contextStamp:/);
  assert.match(code, /crypto\.randomUUID\(\)/);
  assert.match(code, /retry:'never',requestCancellation:'disabled'/);
  assert.match(code, /data-attr:disabled/);
  assert.match(code, /data-attr:list/);
  assert.match(code, /RenderDocumentField/);
  assert.match(code, /DocumentPayload/);
  assert.match(code, /data-preserve-attr/);
  assert.doesNotMatch(code, /window\.tiaDashboard|\.click\(\)/);
  const runner = source('DashboardToolRunner.cs');
  assert.match(runner, /contextStamp/);
  assert.match(runner, /DashboardSelectorStore\.SignalPrefix/);
  assert.match(runner, /ExpectedConnectionId/);
});

test('run inspector and history use local view signals and a safe one-shot history GET', () => {
  const code = source('DashboardRunFragments.cs');
  assert.match(code, /id=\\\"dashboard-run-views/);
  assert.match(code, /data-run-view-tab/);
  assert.match(code, /\$selectedTabId === el\.dataset\.runViewTab/);
  assert.match(code, /\$inspectorView = el\.dataset\.inspectorView/);
  assert.match(code, /data-run-result/);
  assert.match(code, /data-run-request/);
  assert.match(code, /data-run-response/);
  assert.match(code, /@get\(el\.dataset\.historyUrl/);
  assert.match(code, /filterSignals:\{include:\/\^\$\/\}/);
  assert.match(code, /retry:'never',requestCancellation:'disabled'/);
  assert.doesNotMatch(code, /window\.tiaDashboard|MutationObserver/);
});
