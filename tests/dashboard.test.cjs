// Browser-side stage 2 contract checks. No listener or TIA process is started.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const asset = name => fs.readFileSync(path.join(__dirname, '../src/TiaOpennessMcpServer/Dashboard/wwwroot', name), 'utf8');

function scriptHarness() {
  class Element {
    constructor(id = '') {
      this.id = id;
      this.tagName = id.toUpperCase();
      this.attributes = {};
      this.children = [];
      this.textContent = '';
      this.innerHTML = '';
      this.outerHTML = id ? '<div id="' + id + '"></div>' : '';
      this.clicks = 0;
    }
    getAttribute(name) { return Object.hasOwn(this.attributes, name) ? this.attributes[name] : null; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    removeAttribute(name) { delete this.attributes[name]; }
    click() { this.clicks++; }
    append(...items) { for (const item of items) { item.parentNode = this; this.children.push(item); } }
    replaceChildren(...items) { this.children = []; this.append(...items); }
    insertBefore(item, before) {
      const at = this.children.indexOf(before);
      item.parentNode = this;
      if (at < 0) this.children.push(item);
      else this.children.splice(at, 0, item);
    }
    contains(item) { return this.children.includes(item); }
  }
  const ids = [...asset('index.html').matchAll(/id="([^"]+)"/g)].map(match => match[1]);
  const elements = Object.fromEntries(ids.map(id => [id, new Element(id)]));
  const listeners = new Map();
  const document = {
    documentElement: new Element('html'),
    getElementById: id => elements[id] || null,
    createElement: tag => new Element(tag),
    addEventListener(name, handler) {
      if (!listeners.has(name)) listeners.set(name, []);
      listeners.get(name).push(handler);
    }
  };
  class MutationObserver {
    constructor(callback) { this.callback = callback; }
    observe() {}
  }
  const context = vm.createContext({ document, MutationObserver, Uint8Array, Math, Date,
    navigator: { clipboard: { writeText: async () => {} } } });
  context.window = context;
  vm.runInContext(asset('dashboard.js'), context, { filename: 'dashboard.js' });
  const evaluate = code => vm.runInContext(code, context);
  const emitFetch = (element, type = 'finished') => {
    for (const listener of listeners.get('datastar-fetch') || [])
      listener({ detail: { el:element, type } });
  };
  return { Element, elements, context, evaluate, emitFetch, listeners };
}

test('pinned local Datastar bundle and license are present and exact', () => {
  const bytes = fs.readFileSync(path.join(__dirname, '../src/TiaOpennessMcpServer/Dashboard/wwwroot/datastar.js'));
  assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'),
    '727844adfc825ee651fb93c544a2a739986f9a21820a94524b35f0cac470cf91');
  assert.match(asset('datastar.js').slice(0, 40), /Datastar v1\.0\.4/);
  assert.match(asset('DATASTAR-LICENSE.txt'), /Copyright © Star Federation/);
  assert.match(asset('DATASTAR-LICENSE.txt'), /Permission is hereby granted/);
});

test('page uses Datastar HTML SSE actions with the dashboard header and no POST retry', () => {
  const html = asset('index.html');
  assert.match(html, /type="module" src="\/dashboard\/datastar\.js"/);
  assert.match(html, /data-init="@get\('\/api\/dashboard\/events'/);
  assert.match(html, /openWhenHidden: false/);
  assert.match(html, /@post\('\/api\/dashboard\/tools\/run'/);
  assert.match(html, /@post\(window\.tiaDashboardConnectionUrl/);
  assert.match(html, /@get\(window\.tiaDashboardHistoryUrl/);
  assert.match(html, /'X-Tia-Dashboard': '1'/);
  assert.equal((html.match(/retry: 'never'/g) || []).length, 3);
  assert.match(html, /requestCancellation: 'disabled'/);
  for (const id of ['dashboard-state','tool-form-definitions','dashboard-run-views','dashboard-action-message'])
    assert.match(html, new RegExp('id="' + id + '"'));
});

test('dashboard script has no browser polling, direct MCP transport or browser history storage', () => {
  const js = asset('dashboard.js');
  assert.doesNotMatch(js, /fetch\s*\(/);
  assert.doesNotMatch(js, /setInterval|setTimeout\s*\(/);
  assert.doesNotMatch(js, /sessionStorage|localStorage|persistRuns|restoreRuns/);
  assert.doesNotMatch(js, /['"]\/mcp['"]/);
  assert.doesNotMatch(js, /\/api\/dashboard\/(?:dashboard|logs|tool-forms)/);
  assert.match(js, /MutationObserver\(syncServerFragments\)/);
  assert.match(js, /pendingRuns/);
  assert.match(js, /rememberResult/);
  assert.match(js, /refreshAfterWrite/);
  assert.match(js, /data-history-url/);
});

test('dashboard observer ignores mutations caused by its own rendering', () => {
  const ui = scriptHarness();
  ui.evaluate('formsReady = true; renderInspector = () => { window.renderCount = (window.renderCount || 0) + 1; }');
  ui.context.runRecord = { target: ui.elements['dashboard-run-views'], addedNodes:[], removedNodes:[] };
  ui.context.copyRecord = { target: ui.elements.copy, addedNodes:[], removedNodes:[] };
  ui.evaluate('syncServerFragments([runRecord])');
  assert.equal(ui.context.renderCount, 1);
  ui.evaluate('syncServerFragments([copyRecord])');
  assert.equal(ui.context.renderCount, 1);
});

test('SSE state attributes drive target and pending state without a JSON fetch', () => {
  const ui = scriptHarness();
  const root = ui.elements['dashboard-state'];
  root.setAttribute('data-epoch', 'epoch-one');
  root.setAttribute('data-pending-operations', '2');
  const server = new ui.Element();
  server.setAttribute('data-tab-id', 'server');
  server.setAttribute('data-kind', 'server');
  server.setAttribute('data-title', 'Server');
  const project = new ui.Element();
  for (const [key, value] of Object.entries({
    'tab-id':'tab-20', kind:'tia', title:'B.ap20', 'process-id':'20',
    'runtime-identity':'runtime-20', 'connection-state':'connected',
    'connection-id':'attachment-1', 'project-path':'B.ap20',
    'project-state':'open', live:'true', 'can-attach':'true'
  })) project.setAttribute('data-' + key, value);
  root.children = [server, project];
  ui.evaluate('refreshDashboard()');
  assert.equal(ui.evaluate('serverBusy'), true);
  assert.equal(ui.evaluate('tabs[1].processId'), 20);
  assert.equal(ui.evaluate('tabs[1].projectPath'), 'B.ap20');
  assert.equal(ui.evaluate('projectReady(tabs[1])'), true);
  assert.equal(ui.elements.banner.getAttribute('data-logs'), '');
});

test('tool action sends one UUID-scoped payload and waits for server HTML response', async () => {
  const ui = scriptHarness();
  const tab = { id:'tab-20', kind:'tia', title:'B.ap20', processId:20, live:true,
    runtimeIdentity:'runtime-20', connectionId:'attachment-1', projectPath:'B.ap20',
    connectionState:'connected', projectState:'open' };
  ui.context.tabFixture = tab;
  ui.evaluate('tabs = [tabFixture]; selectedId = "tab-20"');
  const pending = ui.evaluate('executeTool(tabFixture, "get_status", {processId:20})');
  assert.equal(ui.elements['tool-action'].clicks, 1);
  const payload = ui.context.tiaDashboardToolPayload;
  assert.match(payload.requestId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  assert.deepEqual(JSON.parse(JSON.stringify(payload)),
    { tabId:'tab-20', requestId:payload.requestId, name:'get_status', arguments:{processId:20} });
  const inspector = new ui.Element('run-inspector-tab-20');
  inspector.setAttribute('data-request-id', payload.requestId);
  inspector.setAttribute('data-run-failed', 'false');
  inspector.setAttribute('data-run-partial', 'false');
  const response = new ui.Element();
  response.setAttribute('data-run-response', '');
  response.textContent = JSON.stringify({ result:{isError:false,content:[{text:'{"status":"ok"}'}]} });
  inspector.children = [response];
  ui.elements['run-inspector-tab-20'] = inspector;
  ui.evaluate('consumeToolResults()');
  ui.emitFetch(ui.elements['tool-action']);
  const result = await pending;
  assert.equal(result.failed, false);
  assert.equal(result.currentContext, true);
  assert.deepEqual(JSON.parse(JSON.stringify(result.body)), { status:'ok' });
  assert.equal(ui.elements['tool-action'].clicks, 1);
});

test('a result for a changed connection does not refill local selectors', async () => {
  const ui = scriptHarness();
  const tab = { id:'tab-20', kind:'tia', processId:20, runtimeIdentity:'runtime-20',
    connectionId:'attachment-1', projectPath:'B.ap20', connectionState:'connected', projectState:'open' };
  ui.context.tabFixture = tab;
  ui.evaluate('tabs = [tabFixture]; selectedId = "server"');
  const pending = ui.evaluate('executeTool(tabFixture, "list_devices", {processId:20})');
  const payload = ui.context.tiaDashboardToolPayload;
  ui.evaluate('tabs = [Object.assign({}, tabFixture, {connectionId:"attachment-2"})]');
  const inspector = new ui.Element('run-inspector-tab-20');
  inspector.setAttribute('data-request-id', payload.requestId);
  inspector.setAttribute('data-run-failed', 'false');
  inspector.setAttribute('data-run-partial', 'false');
  const response = new ui.Element();
  response.setAttribute('data-run-response', '');
  response.textContent = JSON.stringify({ result:{isError:false,content:[{text:JSON.stringify({
    roots:[{kind:'device',objectId:'device-1',name:'Late device'}]})}]} });
  inspector.children = [response];
  ui.elements['run-inspector-tab-20'] = inspector;
  ui.evaluate('consumeToolResults()');
  ui.emitFetch(ui.elements['tool-action']);
  const result = await pending;
  assert.equal(result.currentContext, false);
  assert.equal(ui.evaluate('stateFor("tab-20").choices.device.length'), 0);
});

test('missing terminal response is unresolved, reported once, and never retried', async () => {
  const ui = scriptHarness();
  const tab = { id:'server', kind:'server' };
  ui.context.tabFixture = tab;
  ui.evaluate('tabs = [tabFixture]; selectedId = "server"');
  const pending = ui.evaluate('executeTool(tabFixture, "list_tia_processes", {})');
  ui.emitFetch(ui.elements['tool-action']);
  const result = await pending;
  assert.equal(result.failed, true);
  assert.match(ui.elements.message.textContent, /not confirmed/);
  assert.equal(ui.elements['tool-action'].clicks, 1);
});

test('published plcObjectId fields all follow the chosen CPU', () => {
  const ui = scriptHarness();
  const makeForm = tool => {
    const form = new ui.Element('form');
    form.tagName = 'FORM';
    form.setAttribute('data-tool', tool);
    const input = new ui.Element('input');
    input.name = 'plcObjectId';
    input.value = '';
    form.children = [input];
    return form;
  };
  ui.elements.tools.children = [makeForm('list_blocks'), makeForm('list_technology_objects'), makeForm('create_group')];
  ui.evaluate('setPlc("tab-20", "cpu-native-id")');
  assert.equal(ui.evaluate('stateFor("tab-20").values["list_blocks.plcObjectId"]'), 'cpu-native-id');
  assert.equal(ui.evaluate('stateFor("tab-20").values["list_technology_objects.plcObjectId"]'), 'cpu-native-id');
  assert.equal(ui.evaluate('stateFor("tab-20").values["create_group.plcObjectId"]'), 'cpu-native-id');
});

test('completed read data feeds local selectors from the server inspector response', () => {
  const ui = scriptHarness();
  ui.evaluate('rememberResult("list_devices", {roots:[{kind:"device", objectId:"device-id", name:"Device"}]}, "tab-20")');
  assert.equal(ui.evaluate('stateFor("tab-20").choices.device[0].id'), 'device-id');
  ui.evaluate('rememberResult("get_device", {deviceItems:[{plcObjectId:"cpu-id", name:"CPU"}]}, "tab-20")');
  assert.equal(ui.evaluate('stateFor("tab-20").selected.cpu'), 'cpu-id');
  ui.evaluate('rememberResult("list_blocks", {roots:[{kind:"block", objectId:"block-id", name:"Main", number:1, blockType:"OB", programmingLanguage:"SCL"}]}, "tab-20")');
  assert.equal(ui.evaluate('stateFor("tab-20").choices.block[0].id'), 'block-id');
});

test('a published tool form retains typed arguments and gains a CPU picker from its schema', () => {
  const ui = scriptHarness();
  const markup = '<form data-tool="list_technology_objects" data-process="required" data-project="true">' +
    '<p>Catalogue</p><label>processId <input name="processId" type="number" data-type="integer" readonly></label>' +
    '<label>plcObjectId <input name="plcObjectId" type="text" data-type="string" data-required="true"></label>' +
    '<button type="button">List technology objects</button></form>';
  ui.context.markup = markup;
  ui.evaluate('mountForms(markup)');
  const form = ui.evaluate('formByTool("list_technology_objects")');
  assert.ok(form);
  const picker = ui.evaluate('fieldsOf(formByTool("list_technology_objects")).find(x => x.getAttribute("data-selector") === "cpu")');
  assert.ok(picker);
  ui.evaluate('fieldBy("list_technology_objects", "plcObjectId").value = " native-cpu-id "');
  ui.context.tabFixture = { id:'tab-20', kind:'tia', processId:20 };
  assert.deepEqual(JSON.parse(JSON.stringify(ui.evaluate('collect(formByTool("list_technology_objects"), tabFixture)'))),
    { plcObjectId:' native-cpu-id ', processId:20 });
});
