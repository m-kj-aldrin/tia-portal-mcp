// Browser-script smoke tests with a minimal DOM and mocked fetch. No HTTP listener or TIA process starts.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const asset = name => fs.readFileSync(path.join(__dirname, '../src/TiaOpennessMcpServer/Dashboard/wwwroot', name), 'utf8');

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
const readForms = [
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
  ].join(''), 'Read cross-references'),
  form('export_tag_table', 'required', 'true', [
    field('processId', 'number', 'data-type="integer" data-required="true" readonly'),
    field('objectId', 'text', 'data-type="string" data-required="true"')
  ].join(''), 'Export tag table')
].join('');

const writeNames = ['write_blocks','write_udts','create_tag_table','create_tag','create_user_constant','set_tag_entry_attribute','delete_tag_entry','import_tag_tables','delete_block','delete_udt','delete_tag_table','compile_plc'];
const writeForms = writeNames.map(tool => {
  const text = name => field(name,'text','data-type="string" data-required="true"');
  const optional = name => field(name,'text','data-type="string"');
  let fields = field('processId','number','data-type="integer" readonly');
  if (['write_blocks','write_udts','create_tag_table','import_tag_tables'].includes(tool))
    fields += text('plcObjectId') + optional('groupObjectId') + optional('groupPath');
  else if (tool === 'compile_plc') fields += text('plcObjectId');
  else fields += text('objectId');
  if (['create_tag','create_user_constant','create_tag_table'].includes(tool)) fields += text('name');
  if (['create_tag','create_user_constant'].includes(tool)) fields += text('dataType') + text(tool === 'create_tag' ? 'logicalAddress' : 'value');
  if (tool === 'set_tag_entry_attribute') fields += text('attributeName') + '<label>attributeValue <textarea name="attributeValue" data-type="json" data-required="true"></textarea></label>';
  if (['write_blocks','write_udts'].includes(tool)) fields += choice('sourceFormat',['external-source','simatic-sd','simatic-ml'],'external-source');
  if (['write_blocks','write_udts','import_tag_tables'].includes(tool)) fields += '<label>documents <textarea name="documents" data-type="documents" data-required="true"></textarea></label>';
  return form(tool,'required','true',fields,tool).replace('data-project="true"','data-project="true" data-write="true"');
}).join('');
const forms = readForms + writeForms;

async function plcReady(ui) {
  await ready(ui);
  await ui.context.submit(ui.context.formByTool('list_devices'));
  await ui.context.submit(ui.context.formByTool('get_device'));
}
function setParameter(ui,tool,name,value) {
  ui.context.setField('tab-20',tool,name,value);
}
function helperButton(ui,tool,kind) {
  const found=[];
  ui.context.walk(ui.context.formByTool(tool),item => { if(item.getAttribute('data-helper') === kind) found.push(item); });
  return found[0];
}

test('read and write modes use one MCP runner and read-only publication has no writes', async () => {
  const ui=setup(); await ui.context.bootPromise;
  assert.equal(ui.elements['mode-writes'].hidden,true);
  await ready(ui); ui.elements['mode-writes'].onclick();
  assert.equal(ui.elements['mode-writes'].hidden,false);
  assert.equal(ui.elements['runner-title'].textContent,'Write operations');
  assert.equal(ui.elements['tool-nav'].choice.children.length,12);
  assert.equal(ui.calls.filter(call=>call.url==='/mcp').length,0);
  ui.context.showTool('get_block');
  assert.equal(ui.elements['mode-tools'].attributes['aria-pressed'],'true');
  const readOnly=setup(undefined,true); await ready(readOnly);
  assert.equal(readOnly.elements['mode-writes'].hidden,true);
  assert.equal(readOnly.context.formElements().length,12);
});

test('existing table entries populate attribute editing and submit native ID and typed boolean', async () => {
  const ui=setup(); await plcReady(ui); ui.context.showTool('set_tag_entry_attribute');
  await helperButton(ui,'set_tag_entry_attribute','inventory').onclick();
  await ui.context.loadEntries(' table-id ');
  const entries=ui.context.fieldsOf(ui.context.formByTool('set_tag_entry_attribute')).find(f=>f.getAttribute('data-selector')==='entry');
  assert.deepEqual(Array.from(entries.children).map(option=>option.value),['',' own tag ',' own constant ']);
  assert.equal(ui.toolCalls('get_tag_table').at(-1).body.params.arguments.includeEntries,true);
  entries.value=' own tag '; entries.onchange();
  setParameter(ui,'set_tag_entry_attribute','attributeName','ExternalAccessible');
  setParameter(ui,'set_tag_entry_attribute','attributeValue','false');
  ui.replyToWrite({body:{complete:true,saved:false,errors:[],affectedObjects:[{objectId:' own tag ',kind:'tag',parentObjectId:' table-id '}]}});
  await ui.context.submit(ui.context.formByTool('set_tag_entry_attribute'));
  assert.deepEqual(ui.toolCalls('set_tag_entry_attribute')[0].body.params.arguments,{objectId:' own tag ',attributeName:'ExternalAccessible',attributeValue:false,processId:20});
  assert.equal(ui.toolCalls('set_tag_entry_attribute').length,1);
  assert.equal(ui.toolCalls('get_tag_table').length,2);
  assert.equal(vm.runInContext("results.get('tab-20').operation",ui.context),'set_tag_entry_attribute');
  assert.equal(ui.calls.some(call=>call.url.includes('write-probe')),false);
});

test('all twelve modifying-operation forms send parameters through MCP without an arming step', async () => {
  const samples={
    write_blocks:{plcObjectId:' cpu ',sourceFormat:'external-source',documents:[{name:'A.scl',content:'source\r\n'}]},
    write_udts:{plcObjectId:' cpu ',sourceFormat:'simatic-sd',documents:[{name:'T.s7dcl',content:'decl'},{name:'T.s7res',content:'resource'}]},
    create_tag_table:{plcObjectId:' cpu ',groupPath:'PLC/PLC tags/Folder',name:'Signals'},
    create_tag:{objectId:' table ',name:'Start',dataType:'Bool',logicalAddress:'%M0.0'},
    create_user_constant:{objectId:' table ',name:'Limit',dataType:'Int',value:'100'},
    set_tag_entry_attribute:{objectId:' constant ',attributeName:'Name',attributeValue:'Renamed'},
    delete_tag_entry:{objectId:' tag '},
    import_tag_tables:{plcObjectId:' cpu ',documents:[{name:'Tags.xml',content:'<Document />'}]},
    delete_block:{objectId:' block '},delete_udt:{objectId:' udt '},delete_tag_table:{objectId:' table '},
    compile_plc:{plcObjectId:' cpu '}
  };
  for(const [tool,args] of Object.entries(samples)) {
    const ui=setup(); await ready(ui); ui.context.showTool(tool);
    if(args.plcObjectId) ui.context.setPlc('tab-20',args.plcObjectId);
    for(const [name,value] of Object.entries(args)) setParameter(ui,tool,name,['documents','attributeValue'].includes(name)?JSON.stringify(value):value);
    await ui.context.submit(ui.context.formByTool(tool));
    assert.equal(ui.toolCalls(tool).length,1,tool+' did not dispatch once');
    assert.deepEqual(ui.toolCalls(tool)[0].body.params.arguments,{...args,processId:20});
  }
});

test('source loading preserves exact documents and edits are sent without checksums or substitution', async () => {
  const ui=setup(); await plcReady(ui); ui.context.showTool('write_blocks');
  await helperButton(ui,'write_blocks','inventory').onclick();
  const source=ui.context.fieldsOf(ui.context.formByTool('write_blocks')).find(f=>f.getAttribute('data-selector')==='sourceBlock');
  source.value=' block-id ';source.onchange();
  await helperButton(ui,'write_blocks','source').onclick();
  const field=ui.context.fieldBy('write_blocks','documents');
  assert.deepEqual(JSON.parse(field.value),[{name:'Example.scl',content:'  FUNCTION "Example" : Void\r\nEND_FUNCTION\r\n'}]);
  const editor=ui.all().find(element=>element.attributes['aria-label']==='Document 1 source content');
  editor.value='  FUNCTION "Changed" : Void\r\n// Edited\r\nEND_FUNCTION\r\n';editor.oninput();
  await ui.context.submit(ui.context.formByTool('write_blocks'));
  assert.deepEqual(ui.toolCalls('write_blocks')[0].body.params.arguments.documents,[{name:'Example.scl',content:editor.value}]);
});

test('entry loading distinguishes empty tables and errors and clears on CPU/reconnection', async () => {
  const ui=setup();await plcReady(ui);ui.context.showTool('delete_tag_entry');
  ui.replyToTable({body:{complete:true,errors:[],entries:{tags:[],userConstants:[],systemConstants:[{name:'Read only',objectId:'system'}]}}});
  await ui.context.loadEntries('empty');
  assert.match(vm.runInContext("stateFor('tab-20').entryState",ui.context),/no writable entries/);
  ui.replyToTable({isError:true,body:{error:{message:'Native read failed'}}});
  await ui.context.loadEntries('failed');
  assert.match(vm.runInContext("stateFor('tab-20').entryState",ui.context),/could not be loaded/);
  await ui.context.loadEntries(' table-id ');
  setParameter(ui,'delete_tag_entry','objectId',' own tag ');
  ui.context.setPlc('tab-20','other-cpu');
  assert.equal(ui.context.fieldBy('delete_tag_entry','objectId').value,'');
  assert.equal(vm.runInContext("stateFor('tab-20').entryTable",ui.context),'');
  await ui.context.loadEntries(' table-id ');setParameter(ui,'delete_tag_entry','objectId',' own constant ');
  ui.reconnect();await ui.context.refreshDashboard();
  assert.equal(ui.context.fieldBy('delete_tag_entry','objectId').value,'');
  assert.equal(ui.toolCalls('delete_tag_entry').length,0);
});

test('partial writes and failed readback stay inspectable and are never retried', async () => {
  const ui=setup();await plcReady(ui);ui.context.showTool('delete_tag_entry');
  await ui.context.loadEntries(' table-id ');setParameter(ui,'delete_tag_entry','objectId',' own tag ');
  ui.replyToWrite({isError:true,body:{complete:false,saved:false,errors:[{message:'Native partial error'}],affectedObjects:[{objectId:' own tag ',kind:'tag',parentObjectId:' table-id '}]}});
  ui.replyToTable({isError:true,body:{error:{message:'Readback failed'}}});
  await ui.context.submit(ui.context.formByTool('delete_tag_entry'));
  assert.equal(ui.toolCalls('delete_tag_entry').length,1);
  assert.equal(vm.runInContext("results.get('tab-20').operation",ui.context),'delete_tag_entry');
  assert.equal(vm.runInContext("results.get('tab-20').partial",ui.context),true);
  assert.match(ui.elements.message.textContent,/Refresh\/readback failed/);
  assert.equal(ui.context.fieldBy('delete_tag_entry','objectId').value,'');
  assert.match(ui.elements.result.textContent,/Native partial error/);
});

test('invalid attribute JSON never sends a write and direct native IDs remain usable', async () => {
  const ui=setup();await ready(ui);ui.context.showTool('set_tag_entry_attribute');
  setParameter(ui,'set_tag_entry_attribute','objectId',' direct-native-entry ');
  setParameter(ui,'set_tag_entry_attribute','attributeName','Name');
  setParameter(ui,'set_tag_entry_attribute','attributeValue','unquoted string');
  await ui.context.submit(ui.context.formByTool('set_tag_entry_attribute'));
  assert.equal(ui.toolCalls('set_tag_entry_attribute').length,0);
  assert.match(ui.elements.message.textContent,/valid JSON/);
  setParameter(ui,'set_tag_entry_attribute','attributeValue','"NewName"');
  await ui.context.submit(ui.context.formByTool('set_tag_entry_attribute'));
  assert.equal(ui.toolCalls('set_tag_entry_attribute')[0].body.params.arguments.objectId,' direct-native-entry ');
});

test('published field help survives form mounting and document editor rebuilds as literal text', async () => {
  const transported = forms.replaceAll('</label>', '<small class="field-help" data-document-name-help="Required filename &quot;A.udt&quot; &amp; &lt;path&gt;" data-document-content-help="Complete TYPE text; literal &amp;lt; and &apos;quotes&apos;">Required. Type: string. Schema &lt;script&gt; &amp; &quot;quote&quot; &amp;lt;</small></label>');
  const ui=setup(undefined,false,transported); await plcReady(ui);
  const helpOf = field => field.parentNode.children.find(child=>child.tagName==='SMALL');
  for (const [tool,name] of [['get_block','objectId'],['get_block','includeSource'],['get_block','sourceFormat'],['set_tag_entry_attribute','attributeValue']]) {
    const field=ui.context.fieldBy(tool,name);
    assert.equal(helpOf(field).textContent,'Required. Type: string. Schema <script> & "quote" &lt;');
    assert.equal(field.getAttribute('aria-describedby'),helpOf(field).getAttribute('id'));
  }
  ui.context.showTool('write_udts');
  const documents=ui.context.fieldBy('write_udts','documents');
  const inspectDocuments=()=>{
    const box=documents.editors.children[0];
    const name=box.children[0].children[0], content=box.children[1].children[0];
    assert.equal(helpOf(name).textContent,'Required filename "A.udt" & <path>');
    assert.equal(helpOf(content).textContent,"Complete TYPE text; literal &lt; and 'quotes'");
    assert.equal(content.getAttribute('aria-describedby'),helpOf(content).getAttribute('id'));
  };
  inspectDocuments();
  setParameter(ui,'write_udts','documents',JSON.stringify([{name:'Changed.udt',content:'TYPE "Changed"\nEND_TYPE\n'}]));
  inspectDocuments();
  assert.deepEqual(JSON.parse(documents.value),[{name:'Changed.udt',content:'TYPE "Changed"\nEND_TYPE\n'}]);
  assert.equal(ui.toolCalls('write_udts').length,0);
});

function setup(savedStorage, readOnly = false, publishedForms = forms) {
  class Element {
    constructor(tag) {
      this.tag = tag;
      this.tagName = String(tag).toUpperCase();
      this.children = [];
      this.attributes = {};
      this.textContent = '';
      this.hidden = false;
      this.parentNode = null;
    }
    append(...children) { for (const child of children) { if (child.parentNode) child.parentNode.children = child.parentNode.children.filter(item => item !== child); child.parentNode = this; this.children.push(child); } }
    replaceChildren(...children) { this.children.forEach(child => { child.parentNode = null; }); this.children = []; this.append(...children); }
    insertBefore(node, before) {
      const index = this.children.indexOf(before);
      if (index < 0) this.children.push(node);
      else this.children.splice(index, 0, node);
      node.parentNode = this;
    }
    setAttribute(name, value) { this.attributes[name] = value; }
    getAttribute(name) { return this.attributes[name]; }
    removeAttribute(name) { delete this.attributes[name]; }
    set innerHTML(_) { throw new Error('Native values must be rendered as text'); }
  }
  const html = asset('index.html');
  const elements = Object.fromEntries([...html.matchAll(/id="([^"]+)"/g)].map(match => match[1])
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
  let historyEpoch = 'epoch-1';
  const writeReplies = [];
  const tableReplies = [];
  const copied = [];
  const storage = new Map(savedStorage || []);
  const context = vm.createContext({
    document: { hidden: true, getElementById: id => elements[id], createElement: tag => new Element(tag) },
    setTimeout() {},
    performance: { now: () => 25 },
    navigator: { clipboard: { writeText: async text => { copied.push(text); } } },
    sessionStorage: { getItem: key => storage.get(key) || null, setItem: (key,value) => storage.set(key,value) },
    async fetch(url, options = {}) {
      assert.ok(url === '/mcp' || String(url).startsWith('/api/dashboard/'), 'Unexpected endpoint: ' + url);
      const body = options.body && JSON.parse(options.body);
      calls.push({ url: String(url), options, body });
      const respond = data => ({ ok: true, json: async () => data, text: async () => typeof data === 'string' ? data : JSON.stringify(data) });
      if (url === '/api/dashboard/tool-forms') return { ok: true, text: async () => readOnly ? readForms : publishedForms, json: async () => ({}) };
      if (url === '/api/dashboard/dashboard') {
        const project = { id: 'tab-20', kind: 'tia', title: 'B.ap20', processId: 20, mode: 'with-ui', runtimeState: 'running', runtimeIdentity: '100',
          connectionState: 'connected', connectionId, projectPath: 'B.ap20', projectState: 'open', canAttach: true, live: true, previous: [] };
        const tabs = [{ id: 'server', kind: 'server', title: 'Server', runtimeState: 'none', connectionState: 'none', projectState: 'none', live: false, previous: [] }, project];
        if (second) tabs.push({ id: 'tab-30', kind: 'tia', title: 'C.ap20', processId: 30, mode: 'with-ui', runtimeState: 'running', runtimeIdentity: '300',
          connectionState: 'disconnected', connectionId: null, projectPath: 'C.ap20', projectState: 'open', canAttach: true, live: true, previous: [] });
        if (historical) tabs.push({ id: 'tab-old', kind: 'tia', title: 'Old.ap20', processId: null, runtimeState: 'closed', runtimeIdentity: null,
          connectionState: 'invalidated', connectionId: null, projectPath: 'C:\\Projects\\Old.ap20', projectState: 'historical', canAttach: false, live: false, previous: [{ processId: 9, connectionId: 'old-connection' }] });
        return respond({ pendingOperations: pending, backgroundMonitoringPaused: false, history: { epoch: historyEpoch, generation: 1, logsTruncated: false, tabsTruncated: false, maxLogEntries: 400, maxHistoricalTabs: 24, tabs } });
      }
      if (String(url).startsWith('/api/dashboard/logs?')) return respond({ reset: false, generation: 1, oldest: 1, next: 1, truncated: false, entries: [{ sequence: 1, atUtc: '2026-09-21T12:00:00Z', origin: 'server', operation: 'startup', outcome: 'success', tabId: 'server' }] });
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
        if (writeNames.includes(name)) return payload(writeReplies.length ? writeReplies.shift() : { body:{ operation:name, processId:args.processId, complete:true, errors:[], saved:false, affectedObjects:[] } });
        if (name === 'get_tag_table' && tableReplies.length) return payload(tableReplies.shift());
        if (name === 'get_block' || name === 'get_udt') return payload({ body:{ processId:args.processId, complete:true, errors:[], metadata:{ name:'<img src=x>' },
          source:args.includeSource ? { format:args.sourceFormat, documents:[{ name:'Example.scl', content:'  FUNCTION "Example" : Void\r\nEND_FUNCTION\r\n', checksum:{ value:'unused' } }] } : null } });
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
      assert.ok(['/connect', '/disconnect', '/projects/open', '/tabs/dismiss', '/monitor'].some(route => url === '/api/dashboard' + route), 'Unexpected dashboard action: ' + url);
      assert.equal(options.method, 'POST');
      assert.equal(options.headers['X-Tia-Dashboard'], '1');
      return respond({ paused: body && body.paused, dismissed: true });
    }
  });
  vm.runInContext(asset('dashboard.js'), context);
  const walk = element => [element, ...element.children.filter(child => child instanceof Element).flatMap(walk)];
  const all = () => Object.values(elements).flatMap(walk);
  return {
    elements, calls, context, copied, all, storage,
    replyToWrite: data => writeReplies.push(data),
    replyToTable: data => tableReplies.push(data),
    restart: () => { historyEpoch = 'epoch-2'; },
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
  assert.equal(request.options.headers['X-Tia-Dashboard'], '1');
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
  assert.equal(ui.elements.activity.textContent, 'TIA busy');
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
  assert.equal(connect.options.headers['X-Tia-Dashboard'], '1');
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
  assert.equal(opened.options.headers['X-Tia-Dashboard'], '1');
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

test('dashboard page keeps the tool runner on MCP and remains narrow-layout capable', () => {
  const html = asset('index.html');
  const script = asset('dashboard.js');
  const styles = asset('styles.css');
  assert.match(html, /href="\/dashboard\/styles.css"/);
  assert.match(html, /src="\/dashboard\/dashboard.js"/);
  assert.doesNotMatch(html, /<style>|<script>/);
  assert.match(script, /\/mcp/);
  assert.match(script, /tool-forms/);
  assert.match(html, /Copy result/);
  assert.match(styles, /@media \(max-width: 640px\)/);
  assert.match(styles, /prefers-color-scheme: dark/);
  assert.doesNotMatch(styles, /color-scheme:\s*light dark/);
  assert.match(styles, /flex-wrap/);
  assert.match(script, /Open project in TIA/);
  assert.doesNotMatch(script, /Open project in TIA is not available/);
  assert.match(script, /projects\/open/);
  assert.match(script, /&#39;\|&apos;/);
  assert.doesNotMatch(script, /\/api\/dashboard\/(?:devices|device|blocks|block|udts|udt|tag-tables|tag-table|cross-references)/);
  assert.doesNotMatch(script, /\/api\/prototype\/|X-Tia-Prototype/);
});

test('direct native ID reads work before discovery and retain the exact ID', async () => {
  const ui = setup(); await ready(ui);
  const input = labeled(ui, 'Block objectId');
  input.value = ' exact native id '; input.oninput();
  assert.equal(button(ui, 'Read block').disabled, false);
  await button(ui, 'Read block').onclick();
  assert.equal(ui.toolCalls('get_block')[0].body.params.arguments.objectId, ' exact native id ');
});

test('inspector reopens exact request and envelope without repeating an operation', async () => {
  const ui = setup(); await ready(ui);
  await button(ui, 'List devices').onclick();
  const request = ui.toolCalls('list_devices')[0].body;
  await ui.elements['view-request'].onclick();
  assert.deepEqual(JSON.parse(ui.elements.result.textContent), { endpoint:'/mcp', method:'POST', body:request });
  await ui.elements['view-response'].onclick();
  assert.equal(JSON.parse(ui.elements.result.textContent).result.isError, false);
  await button(ui, 'Get status').onclick();
  const calls = ui.calls.length;
  const previous = ui.elements.runs.children[1];
  await previous.onclick();
  assert.match(ui.elements.result.textContent, /device-id/);
  assert.equal(ui.calls.length, calls, 'history selection must not replay a request');
  await ui.elements['view-request'].onclick();
  await ui.elements.copy.onclick();
  assert.equal(JSON.parse(ui.copied.at(-1)).body.params.name, 'list_devices');
});

test('browser captures survive refresh while old context never refills selectors', async () => {
  const ui = setup(); await ready(ui);
  await button(ui, 'List devices').onclick();
  const restored = setup(ui.storage); await ready(restored);
  assert.match(restored.elements.result.textContent, /device-id/);
  assert.equal(restored.toolCalls('list_devices').length, 0);
  assert.equal(labeled(restored, 'Device objectId').value, '');
  restored.reconnect(); await restored.context.refreshDashboard();
  assert.match(restored.elements['run-context'].textContent, /Earlier connection/);
  assert.equal(labeled(restored, 'Device objectId').value, '');
  restored.restart(); await restored.context.refreshDashboard();
  assert.match(restored.elements.result.textContent, /No operation yet/);
  assert.equal(restored.elements.runs.children.length, 1);
});

test('browser run retention is bounded and storage failure never loses the current result', async () => {
  const ui = setup(); await ready(ui);
  for (let count=0; count<43; count++) await button(ui, 'Get status').onclick();
  assert.equal(ui.elements.runs.children.length, 40);
  ui.context.sessionStorage.setItem = () => { throw new Error('Quota exceeded'); };
  await button(ui, 'List devices').onclick();
  assert.match(ui.elements.result.textContent, /device-id/);
  assert.match(ui.elements['retention-note'].textContent, /may not survive refresh/);
});


test('new object operations clear IDs on CPU changes and compilation has only a CPU selector', async () => {
  const ui=setup(); await plcReady(ui);
  for (const tool of ['delete_block','delete_udt','delete_tag_table','export_tag_table'])
    setParameter(ui,tool,'objectId','old-native-id');
  ui.context.setPlc('tab-20','another-cpu');
  for (const tool of ['delete_block','delete_udt','delete_tag_table','export_tag_table'])
    assert.equal(ui.context.fieldBy(tool,'objectId').value,'',tool);
  assert.equal(ui.context.fieldBy('compile_plc','plcObjectId').value,'another-cpu');
  assert.equal(helperButton(ui,'compile_plc','inventory'),undefined);
});

test('whole-object deletion refreshes its inventory without reading a deleted object or parent group as a table', async () => {
  for (const [tool,kind,id,inventory] of [
    ['delete_block','block',' block-id ','list_blocks'],
    ['delete_udt','udt',' udt-id ','list_udts'],
    ['delete_tag_table','tagTable',' table-id ','list_tag_tables']]) {
    const ui=setup(); await plcReady(ui);ui.context.showTool(tool);
    setParameter(ui,tool,'objectId',id);
    ui.replyToWrite({body:{complete:true,saved:false,errors:[],affectedObjects:[{objectId:id,kind,parentObjectId:'parent-group'}]}});
    await ui.context.submit(ui.context.formByTool(tool));
    assert.equal(ui.toolCalls(tool).length,1);
    assert.equal(ui.toolCalls(inventory).length,1);
    assert.equal(ui.toolCalls('get_block').length+ui.toolCalls('get_udt').length+ui.toolCalls('get_tag_table').length,0);
    assert.equal(vm.runInContext("results.get('tab-20').operation",ui.context),tool);
  }
});
