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
  const input = ui.all().find(element => element.attributes['aria-label']);
  input.value = 'old-id'; input.oninput();
  ui.reconnect();
  await vm.runInContext('status()', ui.context);
  assert.equal(ui.all().find(element => element.attributes['aria-label']).value, '');
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
