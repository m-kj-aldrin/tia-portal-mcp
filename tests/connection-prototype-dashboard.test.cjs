// Browser-script smoke tests with a minimal DOM and mocked fetch. No HTTP listener or TIA process starts.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

function field(name, type, extra) {
  return `<label>${name} <input name="${name}" type="${type}" ${extra}></label>`;
}
function choice(name, values, selected) {
  const options = values.map(value => `<option value="${value}"${value === selected ? ' selected' : ''}>${value}</option>`).join('');
  return `<label>${name} <select name="${name}" data-type="string" data-default="${selected}">${options}</select></label>`;
}
function form(tool, processMode, project, controls, label) {
  return `<form data-tool="${tool}" data-process="${processMode}" data-project="${project}"><p>${tool}</p>${controls}<button type="button">${label}</button></form>`;
}
const forms = [
  form('list_tia_processes', 'none', 'false', '', 'List TIA processes'),
  form('get_status', 'optional', 'false', field('processId', 'number', 'data-type="integer" readonly'), 'Get status'),
  form('list_devices', 'required', 'true', field('processId', 'number', 'data-type="integer" data-required="true" readonly'), 'List devices'),
  form('get_device', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('objectId', 'text', 'data-type="string" data-required="true"'),
    field('includePath', 'checkbox', 'data-type="boolean" data-default="true" checked')
  ].join(''), 'Read device'),
  form('list_blocks', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('plcObjectId', 'text', 'data-type="string" data-required="true"')
  ].join(''), 'List blocks'),
  form('get_block', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('objectId', 'text', 'data-type="string" data-required="true"'),
    field('includePath', 'checkbox', 'data-type="boolean" data-default="true" checked'),
    field('includeSource', 'checkbox', 'data-type="boolean" data-default="true" checked'),
    choice('sourceFormat', ['best', 'external-source', 'simatic-sd', 'simatic-ml'], 'best'),
    field('includeDependencies', 'checkbox', 'data-type="boolean" data-default="false" data-enabled-when="includeSource=true,sourceFormat=external-source"')
  ].join(''), 'Read block'),
  form('list_udts', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('plcObjectId', 'text', 'data-type="string" data-required="true"')
  ].join(''), 'List UDTs'),
  form('get_udt', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('objectId', 'text', 'data-type="string" data-required="true"'),
    field('includePath', 'checkbox', 'data-type="boolean" data-default="true" checked'),
    field('includeSource', 'checkbox', 'data-type="boolean" data-default="true" checked'),
    choice('sourceFormat', ['best', 'external-source', 'simatic-sd', 'simatic-ml'], 'best'),
    field('includeDependencies', 'checkbox', 'data-type="boolean" data-default="false" data-enabled-when="includeSource=true,sourceFormat=external-source"')
  ].join(''), 'Read UDT'),
  form('list_tag_tables', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('plcObjectId', 'text', 'data-type="string" data-required="true"')
  ].join(''), 'List tag tables'),
  form('get_tag_table', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('objectId', 'text', 'data-type="string" data-required="true"'),
    field('includeEntries', 'checkbox', 'data-type="boolean" data-default="true" checked'),
    field('includePath', 'checkbox', 'data-type="boolean" data-default="true" checked')
  ].join(''), 'Read tag table'),
  form('get_cross_references', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('objectId', 'text', 'data-type="string" data-required="true"')
  ].join(''), 'Read cross-references')
].join('');

function setup() {
  class Element {
    constructor(tag) {
      this.tag = tag;
      this.tagName = String(tag).toUpperCase();
      this.children = [];
      this.attributes = {};
      this.textContent = '';
      this.hidden = false;
    }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    insertBefore(node, before) {
      const index = this.children.indexOf(before);
      if (index < 0) this.children.push(node);
      else this.children.splice(index, 0, node);
    }
    setAttribute(name, value) { this.attributes[name] = value; }
    getAttribute(name) { return this.attributes[name]; }
    removeAttribute(name) { delete this.attributes[name]; }
    set innerHTML(_) { throw new Error('Native values must be rendered as text'); }
  }
  const elements = Object.fromEntries(['tabs', 'activity', 'banner', 'summary', 'actions', 'history-note', 'tools', 'write-probes', 'copy', 'elapsed', 'result', 'logs', 'message', 'pause']
    .map(id => [id, new Element(id)]));
  const calls = [];
  let connectionId = '11111111-1111-1111-1111-111111111111';
  let failDevices = false;
  let pending = 0;
  let second = false;
  let historical = false;
  let deferMcp = false;
  let releaseMcp = null;
  let mcpStarted = null;
  const copied = [];
  const context = vm.createContext({
    document: { hidden: true, getElementById: id => elements[id], createElement: tag => new Element(tag) },
    setTimeout() {},
    performance: { now: () => 25 },
    navigator: { clipboard: { writeText: async text => { copied.push(text); } } },
    async fetch(url, options = {}) {
      const body = options.body && JSON.parse(options.body);
      calls.push({ url: String(url), options, body });
      const respond = data => ({ ok: true, json: async () => data, text: async () => typeof data === 'string' ? data : JSON.stringify(data) });
      if (String(url).includes('tool-forms')) return { ok: true, text: async () => forms, json: async () => ({}) };
      if (String(url).includes('/dashboard')) {
        const project = { id: 'tab-20', kind: 'tia', title: 'B.ap20', processId: 20, mode: 'with-ui', runtimeState: 'running', runtimeIdentity: '100',
          connectionState: 'connected', connectionId, projectPath: 'B.ap20', projectState: 'open', canAttach: true, live: true, previous: [] };
        const tabs = [{ id: 'server', kind: 'server', title: 'Server', runtimeState: 'none', connectionState: 'none', projectState: 'none', live: false, previous: [] }, project];
        if (second) tabs.push({ id: 'tab-30', kind: 'tia', title: 'C.ap20', processId: 30, mode: 'with-ui', runtimeState: 'running', runtimeIdentity: '300',
          connectionState: 'disconnected', connectionId: null, projectPath: 'C.ap20', projectState: 'open', canAttach: true, live: true, previous: [] });
        if (historical) tabs.push({ id: 'tab-old', kind: 'tia', title: 'Old.ap20', processId: null, runtimeState: 'closed', runtimeIdentity: null,
          connectionState: 'invalidated', connectionId: null, projectPath: 'C:\\Projects\\Old.ap20', projectState: 'historical', canAttach: false, live: false, previous: [{ processId: 9, connectionId: 'old-connection' }] });
        return respond({ pendingOperations: pending, backgroundMonitoringPaused: false, history: { epoch: 'epoch-1', generation: 1, logsTruncated: false, tabsTruncated: false, maxLogEntries: 400, maxHistoricalTabs: 24, tabs } });
      }
      if (String(url).includes('/logs')) return respond({ reset: false, generation: 1, oldest: 1, next: 1, truncated: false, entries: [{ sequence: 1, atUtc: '2026-09-21T12:00:00Z', origin: 'server', operation: 'startup', outcome: 'success', tabId: 'server' }] });
      if (String(url).includes('/write-probe')) {
        if (body.action === 'arm' && body.projectFileName === 'B.ap20' && body.confirmDisposable === true)
          return respond({ action: 'arm', armed: true, saved: false, complete: true, errors: [], projectModified: true });
        return { ok: false, status: 409, json: async () => ({ error: { code: 'notArmed', message: 'Arm this disposable project again before a write probe.' } }) };
      }
      if (String(url).includes('/mcp')) {
        if (deferMcp) {
          deferMcp = false;
          return new Promise(resolve => {
            releaseMcp = resolve;
            if (mcpStarted) mcpStarted();
          });
        }
        const name = body.params && body.params.name;
        const args = (body.params && body.params.arguments) || {};
        const payload = data => respond({ result: { isError: !!data.isError, content: [{ type: 'text', text: JSON.stringify(data.body) }] } });
        if (name === 'list_devices' && failDevices) return payload({ isError: true, body: { processId: 20, error: { code: 'reconnectRequired', message: 'Retained project changed.' }, errors: [] } });
        if (name === 'list_devices') return payload({ body: { roots: [{ kind: 'deviceGroup', children: [{ kind: 'device', objectId: ' device-id ', name: 'Station' }] }], errors: [] } });
        if (name === 'get_device') return payload({ body: { metadata: { name: '<img src=x>' }, deviceItems: [{ name: 'Rack', children: [{ name: 'CPU', plcObjectId: ' cpu-id ' }] }], errors: [] } });
        if (name === 'list_blocks') return payload({ body: { roots: [{ kind: 'scope', children: [{ kind: 'blockGroup', children: [{ kind: 'block', objectId: ' block-id ', path: 'PLC/Program blocks/FC', name: 'FC', blockType: 'FC', number: 1, programmingLanguage: 'SCL' }] }] }], errors: [] } });
        if (name === 'list_udts') return payload({ body: { roots: [{ kind: 'scope', children: [{ kind: 'typeGroup', children: [{ kind: 'udt', objectId: ' udt-id ', path: 'PLC/PLC data types/T_Item', name: 'T_Item' }] }] }], errors: [] } });
        if (name === 'list_tag_tables') return payload({ body: { roots: [{ kind: 'scope', children: [{ kind: 'tagTableGroup', children: [{ kind: 'tagTable', objectId: ' table-id ', path: 'PLC/PLC tags/Signals', name: 'Signals' }] }] }], errors: [] } });
        if (name === 'get_tag_table') return payload({ body: { metadata: { name: 'Signals' }, entries: args.includeEntries ? { tags: [{ name: 'Start', objectId: ' own tag ' }, { name: 'No ID', objectId: null }], userConstants: [{ name: 'Limit', objectId: ' own constant ' }], systemConstants: [] } : null, errors: [] } });
        if (name === 'get_cross_references' && args.objectId === 'unsupported') return payload({ isError: true, body: { error: { code: 'unsupportedObject', message: 'No native cross-reference service', reconnectRequired: false } } });
        if (name === 'get_cross_references') return payload({ body: { sources: [{ name: 'Native source', children: [], references: [] }], complete: true, errors: [] } });
        return payload({ body: { processId: args.processId, complete: true, errors: [], metadata: { name: '<img src=x>' } } });
      }
      return respond({ paused: body && body.paused, dismissed: true });
    }
  });
  const html = fs.readFileSync(path.join(__dirname, '../src/TiaOpennessMcpServer/connection-prototype.html'), 'utf8');
  vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], context);
  const walk = element => [element, ...element.children.filter(child => child instanceof Element).flatMap(walk)];
  const all = () => ['tools', 'actions', 'tabs', 'summary', 'result', 'logs', 'message', 'write-probes'].flatMap(id => walk(elements[id]));
  return {
    elements, calls, context, copied, all,
    toolCalls: name => calls.filter(call => call.url.endsWith('/mcp') && call.body && call.body.params && call.body.params.name === name),
    reconnect: () => { connectionId = '22222222-2222-2222-2222-222222222222'; },
    fail: () => { failDevices = true; },
    busy: () => { pending = 1; },
    enableSecond: () => { second = true; },
    enableHistorical: () => { historical = true; },
    armLateResponse() {
      deferMcp = true;
      return new Promise(resolve => { mcpStarted = resolve; });
    },
    finishLateResponse(value) { releaseMcp(value); }
  };
}

async function ready(ui) {
  await ui.context.bootPromise;
  const tab = ui.all().find(element => element.textContent && element.textContent.startsWith('B.ap20'));
  assert.ok(tab, 'project tab missing');
  await tab.onclick();
}

function button(ui, title) { return ui.all().find(element => element.textContent === title); }
function labeled(ui, title) { return ui.all().find(element => element.attributes['aria-label'] === title + ' for process 20'); }
function choose(ui, title, value) { const input = labeled(ui, title); input.value = value; input.onchange(); }
function check(ui, title, value) { const input = labeled(ui, title); input.checked = value; input.onchange(); }

test('discovery envelope renders and Device read keeps the selected process, opaque ID and path option', async () => {
  const ui = setup();
  await ready(ui);
  const input = labeled(ui, 'Device objectId');
  input.value = ' native ID '; input.oninput();
  check(ui, 'Include path', false);
  await button(ui, 'Read device').onclick();
  const request = ui.toolCalls('get_device').at(-1);
  assert.deepEqual(request.body.params.arguments, { processId: 20, objectId: ' native ID ', includePath: false });
  assert.equal(request.options.method, 'POST');
  assert.equal(request.options.headers['X-Tia-Prototype'], '1');
  assert.match(ui.elements.result.textContent, /<img src=x>/);
});

test('reconnection clears the earlier Device selector and empty selectors never submit', async () => {
  const ui = setup();
  await ready(ui);
  const input = labeled(ui, 'Device objectId');
  input.value = 'old-id'; input.oninput();
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(labeled(ui, 'Device objectId').value, '');
  const before = ui.toolCalls('get_device').length;
  await button(ui, 'Read device').onclick();
  assert.equal(ui.toolCalls('get_device').length, before);
  assert.match(ui.elements.message.textContent, /Enter a Device objectId/);
});

test('guard errors remain inspectable and never cause automatic retry or connection', async () => {
  const ui = setup();
  await ready(ui);
  ui.fail();
  await button(ui, 'List devices').onclick();
  assert.match(ui.elements.result.textContent, /reconnectRequired/);
  assert.equal(ui.toolCalls('list_devices').length, 1);
  assert.equal(ui.calls.filter(call => call.url.endsWith('/connect')).length, 0);
});

test('block discovery uses the CPU selector and clears it after reconnection', async () => {
  const ui = setup();
  await ready(ui);
  const input = labeled(ui, 'CPU plcObjectId');
  input.value = ' native CPU '; input.oninput();
  await button(ui, 'List blocks').onclick();
  assert.deepEqual(ui.toolCalls('list_blocks').at(-1).body.params.arguments, { processId: 20, plcObjectId: ' native CPU ' });
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(labeled(ui, 'CPU plcObjectId').value, '');
  const before = ui.toolCalls('list_blocks').length;
  await button(ui, 'List blocks').onclick();
  assert.equal(ui.toolCalls('list_blocks').length, before);
  assert.match(ui.elements.message.textContent, /Enter the CPU plcObjectId/);
});

test('device and CPU selection feeds a named block read without manual IDs; modes remain explicit', async () => {
  const ui = setup();
  await ready(ui);
  await button(ui, 'List devices').onclick();
  await button(ui, 'Read device').onclick();
  assert.equal(ui.toolCalls('get_device').at(-1).body.params.arguments.objectId, ' device-id ');
  await button(ui, 'List blocks').onclick();
  assert.equal(ui.toolCalls('list_blocks').at(-1).body.params.arguments.plcObjectId, ' cpu-id ');
  choose(ui, 'Block', ' block-id ');
  await button(ui, 'Read block').onclick();
  assert.deepEqual(ui.toolCalls('get_block').at(-1).body.params.arguments, { processId: 20, objectId: ' block-id ',
    includeSource: true, includePath: true, sourceFormat: 'best', includeDependencies: false });
  choose(ui, 'Source format', 'external-source');
  check(ui, 'Include dependencies', true);
  await button(ui, 'Read block').onclick();
  assert.equal(ui.toolCalls('get_block').at(-1).body.params.arguments.includeDependencies, true);
  choose(ui, 'Source format', 'simatic-ml');
  assert.equal(labeled(ui, 'Include dependencies').checked, false);
  check(ui, 'Include source', false);
  await button(ui, 'Read block').onclick();
  assert.equal(ui.toolCalls('get_block').at(-1).body.params.arguments.includeSource, false);
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(button(ui, 'Read block').disabled, true);
  assert.equal(labeled(ui, 'Block').value, '');
});

test('UDT selection and source controls are independent and clear on CPU change/reconnect', async () => {
  const ui = setup();
  await ready(ui);
  await button(ui, 'List devices').onclick();
  await button(ui, 'Read device').onclick();
  await button(ui, 'List blocks').onclick();
  choose(ui, 'Block', ' block-id ');
  await button(ui, 'List UDTs').onclick();
  choose(ui, 'UDT', ' udt-id ');
  assert.equal(labeled(ui, 'Block').value, ' block-id ');
  assert.deepEqual(ui.toolCalls('list_udts').at(-1).body.params.arguments, { processId: 20, plcObjectId: ' cpu-id ' });
  await button(ui, 'Read UDT').onclick();
  assert.deepEqual(ui.toolCalls('get_udt').at(-1).body.params.arguments, { processId: 20, objectId: ' udt-id ', includeSource: true,
    includePath: true, sourceFormat: 'best', includeDependencies: false });
  choose(ui, 'UDT source format', 'external-source');
  check(ui, 'Include UDT dependencies', true);
  await button(ui, 'Read UDT').onclick();
  assert.equal(ui.toolCalls('get_udt').at(-1).body.params.arguments.includeDependencies, true);
  assert.equal(labeled(ui, 'Source format').value, 'best');
  choose(ui, 'UDT source format', 'simatic-sd');
  assert.equal(labeled(ui, 'Include UDT dependencies').checked, false);
  check(ui, 'Include UDT source', false);
  check(ui, 'Include UDT path', false);
  await button(ui, 'Read UDT').onclick();
  assert.equal(ui.toolCalls('get_udt').at(-1).body.params.arguments.includeSource, false);
  assert.equal(ui.toolCalls('get_udt').at(-1).body.params.arguments.includePath, false);
  choose(ui, 'CPU', 'changed-cpu');
  assert.equal(labeled(ui, 'UDT').value, '');
  assert.equal(button(ui, 'Read UDT').disabled, true);
  const count = ui.toolCalls('get_udt').length;
  await button(ui, 'Read UDT').onclick();
  assert.equal(ui.toolCalls('get_udt').length, count);
  await button(ui, 'List UDTs').onclick();
  choose(ui, 'UDT', ' udt-id ');
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(labeled(ui, 'UDT').value, '');
  assert.equal(button(ui, 'Read UDT').disabled, true);
});

test('tag table selection submits only typed-read options and clears after CPU change/reconnect', async () => {
  const ui = setup();
  await ready(ui);
  await button(ui, 'List devices').onclick();
  await button(ui, 'Read device').onclick();
  await button(ui, 'List UDTs').onclick();
  choose(ui, 'UDT', ' udt-id ');
  await button(ui, 'List tag tables').onclick();
  choose(ui, 'Tag table', ' table-id ');
  assert.equal(labeled(ui, 'UDT').value, ' udt-id ');
  assert.deepEqual(ui.toolCalls('list_tag_tables').at(-1).body.params.arguments, { processId: 20, plcObjectId: ' cpu-id ' });
  await button(ui, 'Read tag table').onclick();
  assert.deepEqual(ui.toolCalls('get_tag_table').at(-1).body.params.arguments, { processId: 20, objectId: ' table-id ', includeEntries: true, includePath: true });
  check(ui, 'Include entries', false);
  check(ui, 'Include tag table path', false);
  await button(ui, 'Read tag table').onclick();
  assert.deepEqual(ui.toolCalls('get_tag_table').at(-1).body.params.arguments, { processId: 20, objectId: ' table-id ', includeEntries: false, includePath: false });
  choose(ui, 'CPU', 'changed-cpu');
  assert.equal(labeled(ui, 'Tag table').value, '');
  assert.equal(button(ui, 'Read tag table').disabled, true);
  const count = ui.toolCalls('get_tag_table').length;
  await button(ui, 'Read tag table').onclick();
  assert.equal(ui.toolCalls('get_tag_table').length, count);
  await button(ui, 'List tag tables').onclick();
  choose(ui, 'Tag table', ' table-id ');
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(labeled(ui, 'Tag table').value, '');
  assert.equal(button(ui, 'Read tag table').disabled, true);
});

test('cross-references use each selected native ID and clear table entries on metadata-only or context changes', async () => {
  const ui = setup();
  await ready(ui);
  await button(ui, 'List devices').onclick();
  await button(ui, 'Read device').onclick();
  await button(ui, 'List blocks').onclick();
  await button(ui, 'List UDTs').onclick();
  await button(ui, 'List tag tables').onclick();
  choose(ui, 'Tag table', ' table-id ');
  await button(ui, 'Read tag table').onclick();
  const options = () => labeled(ui, 'Cross-reference object').children.map(item => item.value);
  assert.deepEqual(options(), ['', ' block-id ', ' udt-id ', ' own tag ', ' own constant ']);
  for (const objectId of [' block-id ', ' udt-id ', ' own tag ', ' own constant ']) {
    choose(ui, 'Cross-reference object', objectId);
    await button(ui, 'Read cross-references').onclick();
    assert.deepEqual(ui.toolCalls('get_cross_references').at(-1).body.params.arguments, { processId: 20, objectId });
  }
  labeled(ui, 'Cross-reference objectId').value = 'unsupported';
  labeled(ui, 'Cross-reference objectId').oninput();
  await button(ui, 'Read cross-references').onclick();
  assert.match(ui.elements.result.textContent, /unsupportedObject/);
  assert.equal(ui.calls.filter(call => call.url.endsWith('/connect')).length, 0);
  choose(ui, 'Cross-reference object', ' own tag ');
  await button(ui, 'Read cross-references').onclick();
  assert.match(ui.elements.result.textContent, /Native source/);
  check(ui, 'Include entries', false);
  await button(ui, 'Read tag table').onclick();
  assert.deepEqual(options(), ['', ' block-id ', ' udt-id ']);
  assert.equal(labeled(ui, 'Cross-reference objectId').value, '');
  choose(ui, 'Cross-reference object', ' block-id ');
  choose(ui, 'CPU', 'changed-cpu');
  assert.deepEqual(options(), ['']);
  assert.equal(labeled(ui, 'Cross-reference objectId').value, '');
  const count = ui.toolCalls('get_cross_references').length;
  await button(ui, 'Read cross-references').onclick();
  assert.equal(ui.toolCalls('get_cross_references').length, count);
  labeled(ui, 'Cross-reference objectId').value = ' arbitrary native ID ';
  labeled(ui, 'Cross-reference objectId').oninput();
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(labeled(ui, 'Cross-reference objectId').value, '');
});

test('a late result stays on the originating tab and does not refill a changed connection', async () => {
  const ui = setup();
  await ready(ui);
  const started = ui.armLateResponse();
  const pending = button(ui, 'List devices').onclick();
  await started;
  const server = ui.all().find(element => element.textContent && element.textContent.startsWith('Server'));
  await server.onclick();
  ui.finishLateResponse({ ok: true, json: async () => ({ result: { isError: false, content: [{ text: JSON.stringify({ roots: [{ kind: 'device', objectId: ' late-id ', name: 'Late' }], errors: [] }) }] } }), text: async () => '' });
  await pending;
  assert.doesNotMatch(ui.elements.result.textContent, /late-id/);
  const project = ui.all().find(element => element.textContent && element.textContent.startsWith('B.ap20'));
  await project.onclick();
  assert.match(ui.elements.result.textContent, /late-id/);
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(labeled(ui, 'Device objectId').value, '');
});

test('busy state blocks connection changes while passive logs and project tools stay available', async () => {
  const ui = setup();
  await ready(ui);
  ui.busy();
  await ui.context.refreshDashboard();
  assert.equal(button(ui, 'Connect').disabled, true);
  assert.equal(button(ui, 'Disconnect').disabled, true);
  assert.equal(button(ui, 'List devices').disabled, false);
  assert.equal(ui.elements.activity.textContent, 'Waiting for TIA…');
  const before = ui.calls.filter(call => call.url.includes('/logs')).length;
  ui.context.document.hidden = false;
  await ui.context.poll();
  assert.ok(ui.calls.filter(call => call.url.includes('/logs')).length > before);
});

test('hidden polling does not read dashboard state', async () => {
  const ui = setup();
  await ready(ui);
  const before = ui.calls.filter(call => call.url.includes('/dashboard')).length;
  ui.context.document.hidden = true;
  await ui.context.poll();
  assert.equal(ui.calls.filter(call => call.url.includes('/dashboard')).length, before);
});

test('copy reports the visible result and a second process connect uses that tab', async () => {
  const ui = setup();
  await ready(ui);
  await button(ui, 'Get status').onclick();
  await ui.elements.copy.onclick();
  assert.match(ui.copied.at(-1), /<img src=x>/);
  assert.match(ui.elements.message.textContent, /Copied result/);
  ui.enableSecond();
  await ui.context.refreshDashboard();
  const other = ui.all().find(element => element.textContent && element.textContent.startsWith('C.ap20'));
  await other.onclick();
  await button(ui, 'Connect').onclick();
  const connect = ui.calls.filter(call => call.url.endsWith('/connect')).at(-1);
  assert.deepEqual(connect.body, { processId: 30 });
  assert.equal(connect.options.headers['X-Tia-Prototype'], '1');
});

test('dismissing historical history sends only the tab id', async () => {
  const ui = setup();
  await ui.context.bootPromise;
  ui.enableHistorical();
  await ui.context.refreshDashboard();
  const old = ui.all().find(element => element.textContent && element.textContent.startsWith('Old.ap20'));
  await old.onclick();
  assert.match(ui.elements['history-note'].textContent, /Open project starts a new TIA window/);
  await button(ui, 'Open project in TIA').onclick();
  const opened = ui.calls.filter(call => call.url.includes('/projects/open')).at(-1);
  assert.deepEqual(opened.body, { tabId: 'tab-old' });
  assert.equal(opened.options.headers['X-Tia-Prototype'], '1');
  assert.equal(ui.calls.filter(call => call.url.endsWith('/connect')).length, 0);
  ui.busy();
  await ui.context.refreshDashboard();
  await old.onclick();
  const open = button(ui, 'Open project in TIA');
  assert.equal(open.disabled, true);
  const posts = ui.calls.filter(call => call.url.includes('/projects/open')).length;
  await open.onclick();
  assert.equal(ui.calls.filter(call => call.url.includes('/projects/open')).length, posts);
  await button(ui, 'Dismiss history').onclick();
  const dismiss = ui.calls.filter(call => call.url.includes('/tabs/dismiss')).at(-1);
  assert.deepEqual(dismiss.body, { tabId: 'tab-old' });
  assert.equal(ui.calls.filter(call => call.url.endsWith('/connect')).length, 0);
});

test('write probes stay outside the eleven tool forms until the disposable project is armed', async () => {
  const ui = setup();
  await ready(ui);
  const forms = ui.elements.tools.children.filter(child => child.getAttribute && child.getAttribute('data-tool'));
  assert.equal(forms.length, 11);
  assert.equal(forms.some(form => String(form.getAttribute('data-tool')).includes('write')), false);
  assert.ok(ui.elements['write-probes'].children.length > 0);
  assert.equal(button(ui, 'Create copy').disabled, true);
  assert.equal(button(ui, 'Arm write probes').disabled, true);
  const file = labeled(ui, 'Probe project file');
  file.value = 'B.ap20';
  file.oninput();
  check(ui, 'Disposable project confirmation', true);
  assert.equal(button(ui, 'Arm write probes').disabled, false);
  const mcpBefore = ui.calls.filter(call => String(call.url).includes('/mcp')).length;
  await button(ui, 'Arm write probes').onclick();
  const armed = ui.calls.filter(call => String(call.url).includes('/write-probe')).at(-1);
  assert.equal(armed.options.method, 'POST');
  assert.equal(armed.options.headers['X-Tia-Prototype'], '1');
  assert.deepEqual(armed.body, { action: 'arm', processId: 20, projectFileName: 'B.ap20', confirmDisposable: true });
  assert.equal(button(ui, 'Create copy').disabled, false);
  assert.match(ui.elements.result.textContent, /"saved": false/);
  await button(ui, 'Create copy').onclick();
  assert.match(ui.elements.message.textContent, /Choose a block or UDT/);
  assert.equal(ui.calls.filter(call => String(call.url).includes('/mcp')).length, mcpBefore);
  ui.reconnect();
  await ui.context.refreshDashboard();
  assert.equal(button(ui, 'Create copy').disabled, true);
});

test('dashboard page keeps the tool runner on MCP and remains narrow-layout capable', () => {
  const html = fs.readFileSync(path.join(__dirname, '../src/TiaOpennessMcpServer/connection-prototype.html'), 'utf8');
  assert.match(html, /\/mcp/);
  assert.match(html, /tool-forms/);
  assert.match(html, /Copy result/);
  assert.match(html, /@media \(max-width: 640px\)/);
  assert.match(html, /prefers-color-scheme: dark/);
  assert.doesNotMatch(html, /color-scheme:\s*light dark/);
  assert.match(html, /flex-wrap/);
  assert.match(html, /Open project in TIA/);
  assert.doesNotMatch(html, /Open project in TIA is not available/);
  assert.match(html, /projects\/open/);
  assert.match(html, /&#39;\|&apos;/);
  assert.doesNotMatch(html, /\/api\/prototype\/(?:devices|device|blocks|block|udts|udt|tag-tables|tag-table|cross-references)/);
});
