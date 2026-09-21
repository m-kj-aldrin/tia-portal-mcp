// Browser-script smoke tests with a minimal DOM and mocked fetch. No HTTP listener or TIA process starts.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

function setup() {
  class Element {
    constructor(tag) { this.tag = tag; this.children = []; this.attributes = {}; this.textContent = ''; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this.attributes[name] = value; }
    set innerHTML(_) { throw new Error('Native values must be rendered as text'); }
  }
  const elements = Object.fromEntries(['refresh', 'pause', 'message', 'activity', 'processes', 'state', 'result']
    .map(id => [id, new Element(id)]));
  const calls = [];
  let connectionId = 'first';
  let failDevices = false;
  const context = vm.createContext({
    document: { hidden: true, getElementById: id => elements[id], createElement: tag => new Element(tag) },
    setTimeout() {},
    async fetch(url, options = {}) {
      const body = options.body && JSON.parse(options.body);
      calls.push({ url, options, body });
      let data;
      if (url.endsWith('/status')) data = { connections: [{ processId: 20, connectionId,
        state: 'connected', approvedProjectPath: 'B.ap20' }], pendingOperations: 0, backgroundMonitoringPaused: false };
      else if (url.endsWith('/processes')) data = { processes: [{ processId: 20, mode: 'with-ui',
        primaryProjectPath: 'B.ap20', connectedByMcp: true, canAttach: true }], errors: [] };
      else if (url.endsWith('/devices') && failDevices) return { ok: false, json: async () => ({
        processId: 20, error: { code: 'reconnectRequired', message: 'Retained project changed.' } }) };
      else if (url.endsWith('/devices')) data = { roots: [{ kind: 'deviceGroup', children: [
        { kind: 'device', objectId: ' device-id ', name: 'Station' }] }], errors: [] };
      else if (url.endsWith('/device')) data = { metadata: { name: '<img src=x>' }, deviceItems: [
        { name: 'Rack', children: [{ name: 'CPU', plcObjectId: ' cpu-id ' }] }], errors: [] };
      else if (url.endsWith('/blocks')) data = { roots: [{ kind: 'scope', children: [{ kind: 'blockGroup', children: [
        { kind: 'block', objectId: ' block-id ', path: 'PLC/Program blocks/FC', name: 'FC', blockType: 'FC', number: 1, programmingLanguage: 'SCL' }
      ] }] }], errors: [] };
      else data = { processId: body.processId, complete: true, errors: [], metadata: { name: '<img src=x>' } };
      return { ok: true, json: async () => data };
    }
  });
  const html = fs.readFileSync(path.join(__dirname, '../src/TiaOpennessMcpServer/connection-prototype.html'), 'utf8');
  vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], context);
  const all = element => [element, ...element.children.filter(child => child instanceof Element).flatMap(all)];
  return { elements, calls, context, all: () => all(elements.processes),
    reconnect: () => { connectionId = 'second'; }, fail: () => { failDevices = true; } };
}

test('discovery envelope renders and Device read keeps the selected process, opaque ID and path option', async () => {
  const ui = setup();
  await vm.runInContext("act('processes')", ui.context);
  const input = ui.all().find(element => element.attributes['aria-label'] === 'Device objectId for process 20');
  assert.ok(input);
  input.value = ' native ID '; input.oninput();
  const checkbox = ui.all().find(element => element.type === 'checkbox');
  checkbox.checked = false; checkbox.onchange();
  await ui.all().find(element => element.textContent === 'Read device').onclick();
  const request = ui.calls.find(call => call.url.endsWith('/device'));
  assert.deepEqual(request.body, { processId: 20, objectId: ' native ID ', includePath: false });
  assert.equal(request.options.method, 'POST');
  assert.equal(request.options.headers['X-Tia-Prototype'], '1');
  assert.match(ui.elements.result.textContent, /<img src=x>/);
});

test('reconnection clears the earlier Device selector and empty selectors never submit', async () => {
  const ui = setup();
  await vm.runInContext("act('processes')", ui.context);
  const input = ui.all().find(element => element.attributes['aria-label'] === 'Device objectId for process 20');
  input.value = 'old-id'; input.oninput();
  ui.reconnect();
  await vm.runInContext('status()', ui.context);
  assert.equal(ui.all().find(element => element.attributes['aria-label'] === 'Device objectId for process 20').value, '');
  await ui.all().find(element => element.textContent === 'Read device').onclick();
  assert.equal(ui.calls.filter(call => call.url.endsWith('/device')).length, 0);
  assert.match(ui.elements.message.textContent, /Enter a Device objectId/);
});

test('guard errors remain inspectable and never cause automatic retry or connection', async () => {
  const ui = setup();
  await vm.runInContext("act('processes')", ui.context);
  ui.fail();
  await ui.all().find(element => element.textContent === 'List devices').onclick();
  assert.match(ui.elements.result.textContent, /reconnectRequired/);
  assert.equal(ui.calls.filter(call => call.url.endsWith('/devices')).length, 1);
  assert.equal(ui.calls.filter(call => call.url.endsWith('/connect')).length, 0);
});

test('block discovery uses the CPU selector and clears it after reconnection', async () => {
  const ui = setup();
  await vm.runInContext("act('processes')", ui.context);
  const input = ui.all().find(element => element.attributes['aria-label'] === 'CPU plcObjectId for process 20');
  input.value = ' native CPU '; input.oninput();
  await ui.all().find(element => element.textContent === 'List blocks').onclick();
  const request = ui.calls.find(call => call.url.endsWith('/blocks'));
  assert.deepEqual(request.body, { processId: 20, plcObjectId: ' native CPU ' });
  assert.equal(request.options.headers['X-Tia-Prototype'], '1');
  ui.reconnect();
  await vm.runInContext('status()', ui.context);
  assert.equal(ui.all().find(element => element.attributes['aria-label'] === 'CPU plcObjectId for process 20').value, '');
  await ui.all().find(element => element.textContent === 'List blocks').onclick();
  assert.equal(ui.calls.filter(call => call.url.endsWith('/blocks')).length, 1);
  assert.match(ui.elements.message.textContent, /Enter the CPU plcObjectId/);
});

test('device and CPU selection feeds a named block read without manual IDs; modes remain explicit', async () => {
  const ui = setup();
  await vm.runInContext("act('processes')", ui.context);
  await ui.all().find(element => element.textContent === 'List devices').onclick();
  await ui.all().find(element => element.textContent === 'Read device').onclick();
  assert.equal(ui.calls.find(call => call.url.endsWith('/device')).body.objectId, ' device-id ');
  await ui.all().find(element => element.textContent === 'List blocks').onclick();
  assert.equal(ui.calls.find(call => call.url.endsWith('/blocks')).body.plcObjectId, ' cpu-id ');
  const choose = (label, value) => {
    const select = ui.all().find(element => element.attributes['aria-label'] === label);
    select.value = value; select.onchange();
  };
  choose('Block for process 20', ' block-id ');
  await ui.all().find(element => element.textContent === 'Read block').onclick();
  assert.deepEqual(ui.calls.find(call => call.url.endsWith('/block')).body, { processId: 20, objectId: ' block-id ',
    includeSource: true, includePath: true, sourceFormat: 'best', includeDependencies: false });
  choose('Source format for process 20', 'external-source');
  const dependencies = ui.all().find(element => element.attributes['aria-label'] === 'Include dependencies for process 20');
  dependencies.checked = true; dependencies.onchange();
  choose('Source format for process 20', 'simatic-ml');
  assert.equal(ui.all().find(element => element.attributes['aria-label'] === 'Include dependencies for process 20').checked, false);
  const source = ui.all().find(element => element.attributes['aria-label'] === 'Include source for process 20');
  source.checked = false; source.onchange();
  await ui.all().find(element => element.textContent === 'Read block').onclick();
  assert.equal(ui.calls.filter(call => call.url.endsWith('/block')).at(-1).body.includeSource, false);
  ui.reconnect(); await vm.runInContext('status()', ui.context);
  assert.equal(ui.all().find(element => element.textContent === 'Read block').disabled, true);
  assert.equal(ui.all().find(element => element.attributes['aria-label'] === 'Block for process 20').value, '');
});
