  const $ = id => document.getElementById(id);
  const results = new Map();
  const formState = new Map();
  const stamps = new Map();
  let runs = [];
  let runCounter = 0;
  let inspectorView = 'result';
  let historyView = 'runs';
  let storageAvailable = true;
  const runLimit = 40;
  const storageKey = 'tia-workbench-runs-v1';
  let tabs = [];
  let selectedId = 'server';
  let serverBusy = false;
  let inflight = false;
  let logs = [];
  let logCursor = 0;
  let logGeneration = 0;
  let epoch = '';
  let formsReady = false;
  const ariaNames = {
    'get_device.objectId': 'Device objectId', 'get_device.includePath': 'Include path', 'get_block.objectId': 'Block objectId', 'get_udt.objectId': 'UDT objectId',
    'get_tag_table.objectId': 'Tag table objectId', 'get_cross_references.objectId': 'Cross-reference objectId',
    'list_blocks.plcObjectId': 'CPU plcObjectId', 'list_udts.plcObjectId': 'CPU plcObjectId', 'list_tag_tables.plcObjectId': 'CPU plcObjectId',
    'get_block.includeSource': 'Include source', 'get_block.includePath': 'Include block path', 'get_block.includeDependencies': 'Include dependencies', 'get_block.sourceFormat': 'Source format',
    'get_udt.includeSource': 'Include UDT source', 'get_udt.includePath': 'Include UDT path', 'get_udt.includeDependencies': 'Include UDT dependencies', 'get_udt.sourceFormat': 'UDT source format',
    'get_tag_table.includeEntries': 'Include entries', 'get_tag_table.includePath': 'Include tag table path'
  };
  const selectorTitles = { entry: 'Tag or user constant', entryTable: 'Table containing the entry', group: 'Destination group', sourceBlock: 'Existing block source', sourceUdt: 'Existing UDT source', device: 'Device', cpu: 'CPU', block: 'Block', udt: 'UDT', tagTable: 'Tag table', cross: 'Cross-reference object' };
  function list(node) { return Array.prototype.slice.call((node && node.children) || []); }
  function tagOf(node) { return String((node && (node.tagName || node.tag)) || '').toLowerCase(); }
  function walk(node, visit) { for (const child of list(node)) { visit(child); walk(child, visit); } }
  function fieldsOf(root) {
    const found = [];
    walk(root, node => { if (node.name || (node.getAttribute && node.getAttribute('data-selector'))) found.push(node); });
    return found;
  }
  function formElements() { return list($('tools')).filter(node => tagOf(node) === 'form'); }
  function formByTool(tool) { return formElements().find(form => form.getAttribute('data-tool') === tool); }
  function fieldBy(tool, name) { const form = formByTool(tool); return form && fieldsOf(form).find(field => field.name === name && field.getAttribute('data-selector') !== 'true'); }
  function selected() { return tabs.find(tab => tab.id === selectedId) || tabs.find(tab => tab.kind === 'server'); }
  function now() { return (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now(); }
  function decode(value) { return String(value || '').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;|&apos;/g, "'").replace(/&amp;/g, '&'); }
  function attrs(tag) {
    const found = {};
    const body = String(tag).replace(/^<\w+/, '').replace(/>$/, '');
    const pattern = /([:\w-]+)(?:="([^"]*)")?/g;
    let match;
    while ((match = pattern.exec(body))) found[match[1]] = match[2] === undefined ? true : decode(match[2]);
    return found;
  }
  function blankState() { return { values: {}, choices: { device: [], cpu: [], block: [], udt: [], tagTable: [], cross: [], group: [] }, entries: [], entryTable: '', entryState: 'Select a table to load its tags and user constants.', sources: {}, selected: {} }; }
  function stateFor(tabId) {
    if (!formState.has(tabId)) formState.set(tabId, blankState());
    return formState.get(tabId);
  }
  function tabStamp(tab) {
    if (!tab || tab.kind !== 'tia') return 'server';
    return [tab.runtimeIdentity || '', tab.connectionId || '', tab.projectPath || '', tab.connectionState || '', tab.projectState || ''].join('|');
  }
  function conceal(element, hidden) {
    element.hidden = hidden;
    if (hidden) element.setAttribute('hidden', '');
    else if (element.removeAttribute) element.removeAttribute('hidden');
  }
  function flatten(nodes, children) {
    const key = children || 'children';
    return (nodes || []).flatMap(node => [node].concat(flatten(node[key], key)));
  }
  function parseInput(tag) {
    const input = document.createElement('input');
    const values = attrs(tag);
    input.name = values.name || '';
    input.type = values.type || 'text';
    if (values['data-type']) input.setAttribute('data-type', values['data-type']);
    if (values['data-required']) input.setAttribute('data-required', 'true');
    if (values['data-default']) input.setAttribute('data-default', values['data-default']);
    if (values['data-enabled-when']) input.setAttribute('data-enabled-when', values['data-enabled-when']);
    input.checked = values.checked === true || values['data-default'] === 'true';
    input.readOnly = values.readonly === true;
    return input;
  }
  function parseSelect(fragment) {
    const select = document.createElement('select');
    const open = fragment.slice(0, fragment.indexOf('>'));
    const values = attrs(open);
    select.name = values.name || '';
    select.setAttribute('data-type', 'string');
    if (values['data-default']) select.setAttribute('data-default', values['data-default']);
    if (values['data-required']) select.setAttribute('data-required','true');
    fragment.split('<option').slice(1).forEach(optionHtml => {
      const option = document.createElement('option');
      const head = optionHtml.slice(0, optionHtml.indexOf('>'));
      option.value = attrs(head).value || '';
      option.textContent = decode(optionHtml.slice(optionHtml.indexOf('>') + 1, optionHtml.indexOf('</option>')));
      select.append(option);
    });
    if (values['data-default']) select.value = values['data-default'];
    return select;
  }
  function parseTextarea(fragment) {
    const field = document.createElement('textarea');
    const values = attrs(fragment.slice(0, fragment.indexOf('>')));
    field.name = values.name || '';
    field.setAttribute('data-type', values['data-type'] || 'json');
    field.setAttribute('data-required', 'true');
    return field;
  }
  function isWrite(tool) {
    const form = formByTool(tool);
    return !!form && form.getAttribute('data-write') === 'true';
  }
  function cpuTools() {
    return ['list_blocks', 'list_udts', 'list_tag_tables', 'write_blocks', 'write_udts', 'create_tag_table', 'import_tag_tables', 'compile_plc'];
  }
  function entryTools() { return ['set_tag_entry_attribute', 'delete_tag_entry']; }
  function tableTools() { return ['get_tag_table', 'export_tag_table', 'delete_tag_table', 'create_tag', 'create_user_constant']; }
  function objectTools() { return ['get_block', 'delete_block', 'get_udt', 'delete_udt', 'get_tag_table', 'export_tag_table', 'delete_tag_table', 'get_cross_references']; }
  function appendFieldHelp(label, field, text, id) {
    if (!text) return;
    const hint = node('small',text,'field-help');
    hint.setAttribute('id',id); field.setAttribute('aria-describedby',id); label.append(hint);
  }
  function mountForms(html) {
    const parent = $('tools');
    parent.replaceChildren();
    String(html).split('</form>').forEach(chunk => {
      const start = chunk.indexOf('<form');
      if (start < 0) return;
      const open = chunk.slice(start, chunk.indexOf('>', start));
      const form = document.createElement('form');
      const values = attrs(open);
      form.setAttribute('data-tool', values['data-tool'] || '');
      form.setAttribute('data-process', values['data-process'] || '');
      form.setAttribute('data-project', values['data-project'] || 'false');
      form.setAttribute('data-write', values['data-write'] || 'false');
      const body = chunk.slice(chunk.indexOf('>', start) + 1);
      const description = body.indexOf('<p>') >= 0 ? body.slice(body.indexOf('<p>') + 3, body.indexOf('</p>')) : '';
      if (description) {
        const details = document.createElement('details'); details.className = 'tool-description';
        const summary = document.createElement('summary'); summary.textContent = 'Tool behavior & format details';
        const paragraph = document.createElement('p'); paragraph.textContent = decode(description);
        details.append(summary, paragraph); form.append(details);
      }
      body.split('<label>').slice(1).forEach(part => {
        const inner = part.slice(0, part.indexOf('</label>'));
        const label = document.createElement('label');
        const field = inner.indexOf('<textarea') >= 0 ? parseTextarea(inner.slice(inner.indexOf('<textarea'))) : inner.indexOf('<select') >= 0 ? parseSelect(inner.slice(inner.indexOf('<select'))) : parseInput(inner.slice(inner.indexOf('<input'), inner.indexOf('>', inner.indexOf('<input')) + 1));
        const friendly = { objectId:'Native object ID', plcObjectId:'CPU native ID', includePath:'Include navigation path', includeSource:'Include native source', groupObjectId:'Destination group native ID', groupPath:'Destination group path', name:'Name', dataType:'Data type', logicalAddress:'Logical address', value:'Constant value', attributeName:'Attribute name', attributeValue:'Attribute value (JSON string, boolean or number)', documents:'Source documents', sourceFormat:'Source format', includeDependencies:'Include source dependencies', includeEntries:'Include tags and constants' };
        label.textContent = (friendly[field.name] || decode(inner.slice(0, inner.indexOf('<')).trim())) + ' ';
        if (field.name === 'processId') label.className = 'process-id';
        label.append(field);
        const helpAt = inner.indexOf('<small class="field-help"');
        if (helpAt >= 0) {
          const helpEnd = inner.indexOf('>',helpAt);
          const helpAttrs = attrs(inner.slice(helpAt,helpEnd));
          appendFieldHelp(label,field,decode(inner.slice(helpEnd+1,inner.indexOf('</small>',helpEnd))),values['data-tool']+'-'+field.name+'-help');
          ['name','content'].forEach(key => {
            const attribute = 'data-document-'+key+'-help';
            if (helpAttrs[attribute]) field.setAttribute(attribute,helpAttrs[attribute]);
          });
        }
        form.append(label);
      });
      const buttonAt = body.indexOf('<button');
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = buttonAt >= 0 ? decode(body.slice(body.indexOf('>', buttonAt) + 1, body.indexOf('</button>'))) : values['data-tool'];
      button.onclick = () => submit(form);
      form.append(button);
      parent.append(form);
      bindForm(form);
    });
    addSelectors();
    addWriteHelpers();
    formsReady = true;
  }
  function bindForm(form) {
    const tool = form.getAttribute('data-tool');
    fieldsOf(form).forEach(field => {
      if (!field.name || field.getAttribute('data-selector') === 'true') return;
      const handler = () => {
        syncConstraints(form);
        const state = stateFor(selectedId);
        state.values[tool + '.' + field.name] = field.type === 'checkbox' ? !!field.checked : field.value;
        if (field.name === 'plcObjectId') setPlc(selectedId, field.value);
        else render();
      };
      field.oninput = handler;
      field.onchange = handler;
    });
  }
  function addChoice(tool, inputName, kind, onPick) {
    const form = formByTool(tool);
    const anchor = fieldBy(tool, inputName);
    if (!form || !anchor) return;
    const select = document.createElement('select');
    select.setAttribute('data-selector', kind);
    select.onchange = () => onPick(select.value || '');
    const label = document.createElement('label');
    label.textContent = (selectorTitles[kind] || kind) + ' ';
    label.append(select);
    const host = list(form).find(child => list(child).indexOf(anchor) >= 0);
    if (host && form.insertBefore) form.insertBefore(label, host);
    else form.append(label);
  }
  function addSelectors() {
    addChoice('get_device', 'objectId', 'device', value => { setField(selectedId, 'get_device', 'objectId', value); setPlc(selectedId, ''); });
    addChoice('list_blocks', 'plcObjectId', 'cpu', value => setPlc(selectedId, value));
    addChoice('list_udts', 'plcObjectId', 'cpu', value => setPlc(selectedId, value));
    addChoice('list_tag_tables', 'plcObjectId', 'cpu', value => setPlc(selectedId, value));
    addChoice('get_block', 'objectId', 'block', value => setField(selectedId, 'get_block', 'objectId', value));
    addChoice('get_udt', 'objectId', 'udt', value => setField(selectedId, 'get_udt', 'objectId', value));
    addChoice('get_tag_table', 'objectId', 'tagTable', value => {
      setField(selectedId, 'get_tag_table', 'objectId', value);
      const state = stateFor(selectedId);
      state.entries = [];
      rebuildCross(state);
      state.selected.cross = '';
      setField(selectedId, 'get_cross_references', 'objectId', '');
    });
    addChoice('get_cross_references', 'objectId', 'cross', value => setField(selectedId, 'get_cross_references', 'objectId', value));
    addChoice('export_tag_table', 'objectId', 'tagTable', value => setField(selectedId, 'export_tag_table', 'objectId', value));
  }
  function setField(tabId, tool, name, value) {
    const state = stateFor(tabId);
    state.values[tool + '.' + name] = value;
    if (name === 'objectId') {
      const kind = { get_block: 'block', delete_block: 'block', get_udt: 'udt', delete_udt: 'udt', get_tag_table: 'tagTable', export_tag_table: 'tagTable', delete_tag_table: 'tagTable', get_cross_references: 'cross', get_device: 'device', create_tag: 'tagTable', create_user_constant: 'tagTable', set_tag_entry_attribute:'entry', delete_tag_entry:'entry' }[tool];
      if (kind) state.selected[kind] = value;
    }
    if (tabId === selectedId) {
      const field = fieldBy(tool, name);
      if (field && field.value !== value) field.value = value;
    }
    if (formsReady) render();
  }
  function setPlc(tabId, value) {
    const state = stateFor(tabId);
    const previous = state.values['list_blocks.plcObjectId'] || '';
    cpuTools().forEach(tool => { state.values[tool + '.plcObjectId'] = value; });
    state.selected.cpu = value;
    if (previous !== value) {
      state.choices.block = []; state.choices.udt = []; state.choices.tagTable = []; state.choices.group = []; state.entries = [];
      state.entryTable = ''; state.sources = {}; state.entryState = 'Select a table to load its tags and user constants.';
      ['create_tag','create_user_constant'].concat(entryTools()).forEach(tool => { state.values[tool + '.objectId'] = ''; });
      cpuTools().forEach(tool => { state.values[tool + '.groupObjectId'] = ''; state.values[tool + '.groupPath'] = ''; });
      state.selected.block = ''; state.selected.udt = ''; state.selected.tagTable = ''; state.selected.cross = '';
      objectTools().forEach(tool => { state.values[tool + '.objectId'] = ''; });
      rebuildCross(state);
    }
    if (tabId === selectedId) {
      cpuTools().forEach(tool => {
        const field = fieldBy(tool, 'plcObjectId');
        if (field && field.value !== value) field.value = value;
      });
      if (previous !== value) ['create_tag','create_user_constant'].concat(entryTools(), objectTools()).forEach(tool => {
        const field = fieldBy(tool, 'objectId');
        if (field) field.value = '';
      });
    }
    if (previous !== value && tabId === selectedId) cpuTools().forEach(tool => ['groupObjectId','groupPath'].forEach(name => {
      const field = fieldBy(tool, name); if (field) field.value = '';
    }));
    if (formsReady) render();
  }
  function rebuildCross(state) {
    state.choices.cross = state.choices.block.map(item => ({ id: item.id, label: 'Block: ' + item.label }))
      .concat(state.choices.udt.map(item => ({ id: item.id, label: 'UDT: ' + item.label })))
      .concat(state.entries || []);
  }
  function syncConstraints(form) {
    fieldsOf(form).forEach(field => {
      const rule = field.getAttribute && field.getAttribute('data-enabled-when');
      if (!rule) return;
      const enabled = rule.split(',').every(pair => {
        const splitAt = pair.indexOf('=');
        const other = fieldsOf(form).find(item => item.name === pair.slice(0, splitAt));
        if (!other) return false;
        const actual = other.type === 'checkbox' ? String(!!other.checked) : String(other.value || '');
        return actual === pair.slice(splitAt + 1);
      });
      field.disabled = !enabled;
      if (!enabled && field.type === 'checkbox') field.checked = false;
    });
  }
  function rememberResult(tool, body, tabId, args = {}) {
    const state = stateFor(tabId);
    const nodes = kind => flatten(body && body.roots).filter(node => node.kind === kind && node.objectId)
      .map(node => ({ id: node.objectId, label: node.path || node.name || node.objectId }));
    if (tool === 'list_devices') {
      state.choices.device = nodes('device');
      state.choices.cpu = [];
      setField(tabId, 'get_device', 'objectId', state.choices.device.length === 1 ? state.choices.device[0].id : '');
      setPlc(tabId, '');
    } else if (tool === 'get_device') {
      state.choices.cpu = flatten(body && body.deviceItems).filter(node => node.plcObjectId)
        .map(node => ({ id: node.plcObjectId, label: node.name || node.plcObjectId }));
      setPlc(tabId, state.choices.cpu.length === 1 ? state.choices.cpu[0].id : '');
    } else if (tool === 'list_blocks' || tool === 'list_udts' || tool === 'list_tag_tables') {
      const kind = tool === 'list_blocks' ? 'block' : tool === 'list_udts' ? 'udt' : 'tagTable';
      const target = tool === 'list_blocks' ? 'get_block' : tool === 'list_udts' ? 'get_udt' : 'get_tag_table';
      state.choices[kind] = tool === 'list_blocks' ? nodes('block').map(node => {
        const source = flatten(body.roots).find(item => item.objectId === node.id) || {};
        return { id: node.id, label: (source.path || source.name) + ' (' + source.blockType + ' ' + source.number + ', ' + source.programmingLanguage + ')' };
      }) : nodes(tool === 'list_udts' ? 'udt' : 'tagTable');
      const previous = state.selected[kind] || '';
      state.selected[kind] = state.choices[kind].some(item => item.id === previous) ? previous : '';
      const groupKind = tool === 'list_blocks' ? 'blockGroup' : tool === 'list_udts' ? 'typeGroup' : 'tagTableGroup';
      const groups = flatten(body && body.roots).filter(node => node.kind === groupKind && !node.isSystem && (node.objectId || node.path))
        .map(node => ({ id: node.objectId ? 'id:' + node.objectId : 'path:' + node.path, label: node.path || node.name || node.objectId, source: tool }));
      state.choices.group = (state.choices.group || []).filter(item => item.source !== tool).concat(groups);
      setField(tabId, target, 'objectId', state.selected[kind]);
      const deletion = tool === 'list_blocks' ? 'delete_block' : tool === 'list_udts' ? 'delete_udt' : 'delete_tag_table';
      const deleteId = state.values[deletion + '.objectId'];
      if (deleteId && !state.choices[kind].some(item => item.id === deleteId)) setField(tabId, deletion, 'objectId', '');
      if (tool !== 'list_tag_tables') { /* inventories keep unrelated choices */ }
      if (tool === 'list_blocks' || tool === 'list_udts' || tool === 'list_tag_tables') {
        if (tool === 'list_tag_tables') {
          tableTools().forEach(name => {
            const value = state.values[name + '.objectId'];
            if (value && !state.choices.tagTable.some(item => item.id === value)) setField(tabId, name, 'objectId', '');
          });
          if (state.entryTable && !state.choices.tagTable.some(item => item.id === state.entryTable)) clearEntries(tabId, '');
        }
        rebuildCross(state);
        state.selected.cross = '';
        setField(tabId, 'get_cross_references', 'objectId', '');
      }
    } else if (tool === 'get_tag_table') {
      const groups = [['tags', 'Tag'], ['userConstants', 'User constant'], ['systemConstants', 'System constant']];
      state.entries = body && body.entries ? groups.flatMap(([key, label]) => ((body.entries[key]) || []).filter(entry => entry.objectId)
        .map(entry => ({ id: entry.objectId, label: label + ': ' + entry.name, kind:key }))) : [];
      state.entryTable = args.objectId || state.values['get_tag_table.objectId'] || '';
      state.entryState = body && body.complete === false ? 'Some entries could not be read. Inspect the read result.'
        : state.entries.some(item => item.kind !== 'systemConstants') ? '' : 'This table has no writable entries with native IDs.';
      entryTools().forEach(name => {
        const value = state.values[name + '.objectId'];
        if (value && !state.entries.some(item => item.id === value && item.kind !== 'systemConstants')) setField(tabId, name, 'objectId', '');
      });
      rebuildCross(state);
      state.selected.cross = '';
      setField(tabId, 'get_cross_references', 'objectId', '');
    }
  }
  function missingMessage(tool, form) {
    const raw = name => { const field = fieldsOf(form).find(item => item.name === name && item.getAttribute('data-selector') !== 'true'); return field ? String(field.value || '') : ''; };
    if (tool === 'get_device' && !raw('objectId').trim()) return 'Enter a Device objectId from List devices for this process.';
    if ((tool === 'list_blocks' || tool === 'list_udts' || tool === 'list_tag_tables') && !raw('plcObjectId').trim()) return 'Enter the CPU plcObjectId from Read device for this process.';
    if (tool === 'get_block' && !raw('objectId').trim()) return 'Choose a block from List blocks for this process.';
    if (tool === 'get_udt' && !raw('objectId').trim()) return 'Choose a UDT from List UDTs for this process.';
    if (tool === 'get_tag_table' && !raw('objectId').trim()) return 'Choose a tag table from List tag tables for this process.';
    if (tool === 'get_cross_references' && !raw('objectId').trim()) return 'Choose an object or enter its native objectId for cross-references.';
    const required = fieldsOf(form).find(field => field.name !== 'processId' && field.getAttribute('data-required') === 'true' && !String(field.value || '').trim());
    if (required) return 'Enter ' + required.name + ' to run this operation.';
    return '';
  }
  function collect(form, tab) {
    const args = {};
    fieldsOf(form).forEach(field => {
      if (!field.name || field.getAttribute('data-selector') === 'true' || field.name === 'processId') return;
      if (field.type === 'checkbox') { args[field.name] = !!field.checked; return; }
      if (String(field.value || '') === '') return;
      const type = field.getAttribute('data-type');
      if (type === 'documents' || type === 'json') {
        try { args[field.name] = JSON.parse(field.value); }
        catch (error) { throw new Error(field.name + ' must contain valid JSON. Strings need quotation marks.'); }
        if (type === 'json' && (args[field.name] === null || !['string','boolean','number'].includes(typeof args[field.name])))
          throw new Error('Attribute value must be a JSON string, boolean or number.');
        if (type === 'json' && typeof args[field.name] === 'number' &&
            (!Number.isFinite(args[field.name]) || (Number.isInteger(args[field.name]) && !Number.isSafeInteger(args[field.name]))))
          throw new Error('Use a finite number within the browser’s exact integer range.');
        if (type === 'documents' && (!Array.isArray(args[field.name]) || !args[field.name].length || args[field.name].some(doc => !doc.name || !doc.content)))
          throw new Error('Each source document needs a file name and content.');
      } else args[field.name] = type === 'integer' ? Number(field.value) : field.value;
    });
    if (tab && tab.kind === 'tia' && tab.processId) args.processId = tab.processId;
    return args;
  }
  async function executeTool(tab, tool, args) {
    const stamp = tabStamp(tab);
    const cpu = stateFor(tab.id).selected.cpu;
    const request = { jsonrpc:'2.0', id:++runCounter, method:'tools/call', params:{ name:tool, arguments:args } };
    const capture = beginRun(tab, 'MCP', tool, '/mcp', request);
    const started = now();
    try {
      const response = await fetch('/mcp', { method:'POST', headers:{ 'Content-Type':'application/json', 'X-Tia-Dashboard':'1' }, body:JSON.stringify(request) });
      const envelope = await response.json();
      capture.response = envelope; capture.httpStatus = response.status || (response.ok ? 200 : null);
      if (!response.ok || envelope.error) {
        const error = new Error((envelope.error && envelope.error.message) || 'MCP request failed'); error.details = envelope; throw error;
      }
      const text = envelope.result && envelope.result.content && envelope.result.content[0] ? envelope.result.content[0].text : '';
      let body = text; try { body = JSON.parse(text); } catch (ignore) {}
      const failed = !!(envelope.result && envelope.result.isError);
      const partial = !!(body && (body.complete === false || (Array.isArray(body.errors) && body.errors.length))) &&
        (!failed || !!(body.affectedObjects && body.affectedObjects.length));
      finishRun(capture, { text:typeof body === 'string' ? body : JSON.stringify(body,null,2), failed:failed && !partial, partial,
        message:partial ? 'Completed with errors. Inspect the result before another operation.'
          : failed ? ((body && body.error && body.error.message) || 'Tool call failed. Inspect the native errors.')
          : 'Completed.', elapsed:Math.round(now()-started) + ' ms round trip' });
      const current = tabs.find(item => item.id === tab.id);
      const currentContext = current && tabStamp(current) === stamp && stateFor(tab.id).selected.cpu === cpu;
      if (!failed && currentContext && body && typeof body === 'object') rememberResult(tool,body,tab.id,args);
      return { body, failed, capture, currentContext };
    } catch (error) {
      finishRun(capture, { text:JSON.stringify(error.details || { error:error.message },null,2), failed:true, partial:false, message:error.message, elapsed:Math.round(now()-started) + ' ms round trip' });
      return { failed:true, capture, currentContext:false };
    }
  }
  async function submit(form) {
    const tab = selected();
    const tool = form.getAttribute('data-tool');
    const button = list(form).find(child => tagOf(child) === 'button');
    const missing = missingMessage(tool, form);
    if (!tab || (button && button.disabled) || missing || inflight) {
      $('message').textContent = missing || 'This tool is unavailable for the selected workspace.'; return;
    }
    let args;
    try { args = collect(form,tab); }
    catch (error) { $('message').textContent = error.message; $('message').className = 'error'; return; }
    inflight = true; render(); $('message').textContent = 'Working…';
    try {
      const run = await executeTool(tab,tool,args);
      if (isWrite(tool) && run.currentContext && run.body && Array.isArray(run.body.affectedObjects))
        await refreshAfterWrite(tab,tool,args,run);
    } finally {
      inflight = false;
      try { await refreshDashboard(); await refreshLogs(); } catch (error) { $('message').textContent = error.message; }
      render();
    }
  }
  async function dashboardPost(path, payload) {
    const response = await fetch('/api/dashboard/' + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Tia-Dashboard': '1' }, body: JSON.stringify(payload) });
    const data = await response.json();
    if (!response.ok) { const error = new Error((data.error && data.error.message) || data.error || 'Request failed'); error.details = data; throw error; }
    return data;
  }
  async function connectionAction(path, payload, waiting) {
    const tab = selected();
    const capture = beginRun(tab, 'Dashboard', path, '/api/dashboard/' + path, payload);
    const started = now();
    inflight = true;
    $('message').className = '';
    $('message').textContent = waiting;
    render();
    try {
      const data = await dashboardPost(path, payload);
      capture.response = data;
      finishRun(capture, { text: JSON.stringify(data, null, 2), message: 'Completed.', elapsed: Math.round(now() - started) + ' ms round trip' });
    } catch (error) {
      $('message').className = 'error';
      $('message').textContent = error.message;
      capture.response = error.details || null;
      finishRun(capture, { text: JSON.stringify(error.details || { error: error.message }, null, 2), failed: true, message: error.message, elapsed: Math.round(now() - started) + ' ms round trip' });
    } finally {
      inflight = false;
      try { await refreshDashboard(); await refreshLogs(); } catch (error) { $('message').textContent = error.message; }
      render();
    }
  }
  function projectReady(tab) { return !!(tab && tab.kind === 'tia' && tab.live && tab.connectionState === 'connected' && tab.projectPath && tab.projectState === 'open'); }
  function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text != null) element.textContent = text;
    if (className) element.className = className;
    return element;
  }
  function beginRun(tab, kind, operation, endpoint, request) {
    return { id: Date.now() + '-' + (++runCounter), tabId: tab ? tab.id : 'server', title: tab ? tab.title : 'Server', processId: tab && tab.processId,
      connectionId: tab && tab.connectionId, projectPath: tab && tab.projectPath, stamp: tabStamp(tab), at: new Date().toISOString(), kind, operation, endpoint, request };
  }
  function finishRun(capture, result) {
    const run = Object.assign(capture, result);
    runs.push(run);
    if (runs.length > runLimit) runs.splice(0, runs.length - runLimit);
    results.set(run.tabId, run);
    inspectorView = 'result';
    persistRuns();
  }
  function persistRuns() {
    try {
      if (typeof sessionStorage === 'undefined') { storageAvailable = false; return; }
      // Keep exact results. Evict whole older runs rather than silently truncate a response.
      const retained = runs.slice();
      let saved = JSON.stringify({ epoch, runs: retained });
      while (saved.length > 2000000 && retained.length > 1) { retained.shift(); saved = JSON.stringify({ epoch, runs: retained }); }
      sessionStorage.setItem(storageKey, saved);
      storageAvailable = retained.length === runs.length;
    } catch (error) { storageAvailable = false; }
  }
  function restoreRuns() {
    try {
      if (typeof sessionStorage === 'undefined') { storageAvailable = false; return; }
      const saved = JSON.parse(sessionStorage.getItem(storageKey) || 'null');
      if (!saved || saved.epoch !== epoch || !Array.isArray(saved.runs)) return;
      runs = saved.runs.filter(run => run && typeof run.id === 'string' && typeof run.text === 'string' && typeof run.tabId === 'string').slice(-runLimit);
      runs.forEach(run => results.set(run.tabId, run));
    } catch (error) { storageAvailable = false; }
  }
  function timeLabel(at) {
    const time = new Date(at);
    return isNaN(time.getTime()) ? String(at || '') : time.toLocaleTimeString([], { hour:'2-digit', minute:'2-digit', second:'2-digit', hour12:false });
  }
  function showTool(name) {
    const state = stateFor(selectedId);
    state.mode = isWrite(name) ? 'writes' : 'tools'; state.tool = name; render();
  }
  function jumpButton(title, tool) {
    const button = node('button', title); button.type = 'button'; button.onclick = () => showTool(tool); return button;
  }
  function prerequisite(tool, tab) {
    const state = stateFor(selectedId);
    const form = formByTool(tool);
    if (!form || form.getAttribute('data-project') !== 'true') return null;
    if (!projectReady(tab)) return { text: tab && tab.live ? tab.projectState === 'open' ? 'Connect this TIA project above to use project tools.' : 'Open a project in this TIA window before using project tools.' : 'This is a historical workspace. Open its project in TIA to continue.' };
    const missing = missingMessage(tool, form);
    if (!missing) return null;
    if (tool === 'get_device') return state.choices.device.length
      ? { text:'Choose a device below, or paste its native ID.' }
      : { text:'Discover devices in this project first.', tool:'list_devices', label:'Go to List devices' };
    const inventories = { get_block:'list_blocks', get_udt:'list_udts', get_tag_table:'list_tag_tables' };
    const kinds = { get_block:'block', get_udt:'udt', get_tag_table:'tagTable' };
    if (inventories[tool] && state.choices[kinds[tool]].length) return { text:missing + ' You can also paste a native ID below.' };
    if (!state.values['list_blocks.plcObjectId'] && tool !== 'get_cross_references') {
      if (!state.choices.device.length) return { text:'Start by discovering the project’s devices, then read a device to select its PLC.', tool:'list_devices', label:'Go to List devices' };
      if (!state.choices.cpu.length) return { text:'Read a device to discover its PLC.', tool:'get_device', label:'Go to Read device' };
      if (inventories[tool]) return { text:'Select a PLC before loading this inventory.', tool:inventories[tool], label:'Choose PLC' };
      return { text:'Choose a PLC below, or paste its native CPU ID.' };
    }
    if (inventories[tool]) return { text:'Load this inventory to populate the selector, or paste a native ID below.', tool:inventories[tool], label:'Go to ' + ({ get_block:'List blocks', get_udt:'List UDTs', get_tag_table:'List tag tables' })[tool] };
    return { text:missing };
  }
  function renderPrerequisite(tool, tab) {
    const host = $('prerequisite');
    const needed = prerequisite(tool, tab);
    host.replaceChildren(); conceal(host, !needed);
    if (!needed) return;
    host.append(node('p', needed.text, 'hint'));
    if (needed.tool) host.append(jumpButton(needed.label, needed.tool));
  }
  function renderInspector(tab) {
    const stored = results.get(selectedId);
    const content = !stored ? 'No operation yet. Run a tool or select a previous run below.'
      : inspectorView === 'request' ? JSON.stringify({ endpoint:stored.endpoint, method:'POST', body:stored.request }, null, 2)
      : inspectorView === 'response' ? JSON.stringify(stored.response == null ? { message:'No response received from the server.' } : stored.response, null, 2)
      : stored.text;
    if ($('result').textContent !== content) $('result').textContent = content;
    $('result').className = stored ? '' : 'empty-result';
    $('elapsed').textContent = stored && stored.elapsed || '';
    const outcome = !stored ? 'No run selected' : stored.failed ? 'Error' : stored.partial ? 'Partial' : 'Success';
    $('run-outcome').textContent = outcome;
    $('run-outcome').className = 'badge' + (stored ? stored.failed ? ' error' : stored.partial ? ' warn' : ' good' : '');
    $('run-context').textContent = stored ? [stored.kind, stored.operation, timeLabel(stored.at), stored.title, stored.processId ? 'PID ' + stored.processId : '', stored.httpStatus ? 'HTTP ' + stored.httpStatus : '', stored.stamp !== tabStamp(tab) ? 'Earlier connection — retained result' : '', ''].filter(Boolean).join(' · ') : 'The request, response and target stay together for every captured run.';
    ['result','request','response'].forEach(view => $('view-' + view).setAttribute('aria-pressed', String(view === inspectorView)));
    $('copy').textContent = 'Copy ' + inspectorView;
    $('copy').disabled = !stored;
    if (!inflight) {
      $('message').className = stored && stored.failed ? 'error' : stored && stored.partial ? 'partial' : '';
      $('message').textContent = runMessage(stored);
    }
  }
  function runMessage(stored) { return stored ? 'Last run · ' + stored.operation + ' · ' + (stored.message || '') : ''; }
  function renderHistory() {
    const host = $('runs');
    const visible = runs.filter(run => run.tabId === selectedId).slice().reverse();
    const current = results.get(selectedId);
    const signature = JSON.stringify([selectedId, visible.map(run => run.id), current && current.id]);
    if (host.getAttribute('data-signature') !== signature) {
      host.replaceChildren();
      if (!visible.length) host.append(node('p', 'No runs captured here yet. Run a tool to start a request and response history.', 'log-row muted'));
      visible.forEach(run => {
        const button = node('button', null, 'run-row'); button.type = 'button'; button.setAttribute('data-run', run.id);
        button.setAttribute('aria-pressed', String(!!current && current.id === run.id));
        button.append(node('small', timeLabel(run.at)), node('span', run.kind + ' · ' + run.operation, 'run-name'), node('span', run.failed ? 'Error' : run.partial ? 'Partial' : 'Success', run.failed ? 'error' : run.partial ? 'partial' : ''));
        button.onclick = () => { results.set(selectedId, run); inspectorView = 'result'; render(); };
        host.append(button);
      });
      host.setAttribute('data-signature', signature);
    }
    conceal(host, historyView !== 'runs'); conceal($('logs'), historyView !== 'server');
    $('history-runs').setAttribute('aria-pressed', String(historyView === 'runs'));
    $('history-server').setAttribute('aria-pressed', String(historyView === 'server'));
    $('retention-note').textContent = historyView === 'runs' ? 'Up to 40 runs across this browser tab · ' + (storageAvailable ? 'retained on refresh' : 'some runs may not survive refresh') + ' · reset on server restart'
      : 'Server-wide calls, filtered to this workspace. Full payloads are available only for runs captured in this browser.';
  }
  function render() {
    const tab = selected();
    const locked = serverBusy || inflight;
    $('activity').textContent = inflight ? 'Request running…' : serverBusy ? 'TIA busy' : 'Server online';
    $('activity').className = locked ? 'badge warn' : 'badge good';
    $('pause').checked = $('pause').checked;
    const bar = $('tabs');
    const ids = tabs.map(item => item.id).join('|');
    if (bar.getAttribute('data-ids') !== ids) {
      bar.replaceChildren();
      tabs.forEach(item => {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('data-tab', item.id);
        button.onclick = () => {
          if (selectedId !== item.id) { selectedId = item.id; loadState(item.id); }
          render();
          const stored = results.get(selectedId);
          $('message').className = stored && stored.failed ? 'error' : stored && stored.partial ? 'partial' : '';
          $('message').textContent = runMessage(stored);
        };
        bar.append(button);
      });
      bar.setAttribute('data-ids', ids);
    }
    list(bar).forEach(button => {
      const item = tabs.find(candidate => candidate.id === button.getAttribute('data-tab'));
      if (!item) return;
      const runtime = item.kind === 'server' ? 'server' : (item.runtimeState === 'running' ? 'running' : 'closed');
      const project = item.projectState === 'open' ? 'project open' : item.projectState === 'historical' ? 'historical' : item.kind === 'server' ? 'bridge' : 'no project';
      const connection = item.kind === 'server' ? 'logs' : (item.connectionState || 'disconnected');
      button.textContent = item.title;
      const stateLabel = item.kind === 'server' ? 'Bridge & process discovery' : runtime + ' · ' + connection;
      button.append(node('span', stateLabel, 'tab-state'));
      button.setAttribute('aria-label', item.title + ' — ' + runtime + ' · ' + project + ' · ' + connection);
      button.setAttribute('aria-pressed', button.getAttribute('data-tab') === selectedId ? 'true' : 'false');
    });
    const summary = [];
    if (tab && tab.kind === 'tia') {
      summary.push(tab.live ? 'Runtime running' : 'Runtime closed');
      if (tab.processId) summary.push('process ' + tab.processId);
      if (tab.mode) summary.push(tab.mode);
      summary.push(tab.projectState === 'open' ? 'project open' : tab.projectState === 'historical' ? 'historical project' : 'no project');
      if (tab.projectPath) summary.push(tab.projectPath);
      summary.push(tab.connectionState || 'disconnected');
      if (tab.connectionId) summary.push('connection ' + tab.connectionId);
      if (tab.reason) summary.push(tab.reason);
      if (tab.unavailableReason) summary.push(tab.unavailableReason);
      if (tab.cleanupError) summary.push(tab.cleanupError);
      (tab.previous || []).forEach(item => summary.push('earlier runtime ' + item.processId + (item.connectionId ? ' connection ' + item.connectionId : '')));
    } else summary.push('Server events, process discovery and bridge status. TIA tabs keep their own project tools and logs.');
    $('context-title').textContent = tab ? tab.title : 'Server';
    $('context-path').textContent = summary.join('\n');
    $('summary').replaceChildren();
    if (tab && tab.kind === 'tia') {
      $('summary').append(node('span', tab.connectionState || 'disconnected', 'badge ' + (projectReady(tab) ? 'good' : '')));
      $('summary').append(node('span', 'PID ' + (tab.processId || '—'), 'badge'));
      $('summary').append(node('span', tab.projectState === 'open' ? 'Project open' : tab.projectState === 'historical' ? 'Historical project' : 'No project open', 'badge'));
      const cpu = stateFor(selectedId).choices.cpu.find(item => item.id === stateFor(selectedId).selected.cpu);
      if (cpu) $('summary').append(node('span', 'PLC · ' + cpu.label, 'badge'));
    } else $('summary').append(node('span', formElements().length + ' MCP tools · ' + (formElements().some(form => form.getAttribute('data-write') === 'true') ? 'read + write' : 'read-only'), 'badge good'), node('span', '/mcp', 'badge mono'));
    $('history-note').textContent = tab && !tab.live && tab.projectPath
      ? 'Stored path: ' + tab.projectPath + '. Open project starts a new TIA window for this project.' : '';
    const actions = $('actions');
    actions.replaceChildren();
    if (tab && tab.kind === 'tia') {
      const connect = document.createElement('button');
      connect.type = 'button'; connect.textContent = 'Connect'; connect.className = 'primary';
      connect.disabled = locked || !(tab.live && tab.connectionState !== 'connected' && tab.canAttach !== false && !tab.cleanupError);
      connect.onclick = () => connectionAction('connect', { processId: tab.processId }, 'Connecting. Check TIA for an access approval prompt.');
      const disconnect = document.createElement('button');
      disconnect.type = 'button'; disconnect.textContent = 'Disconnect';
      disconnect.disabled = locked || !(tab.processId && (tab.connectionState === 'connected' || tab.cleanupError));
      disconnect.onclick = () => connectionAction('disconnect', { processId: tab.processId }, 'Disconnecting…');
      actions.append(connect, disconnect);
      conceal(connect, !tab.live || tab.connectionState === 'connected');
      conceal(disconnect, tab.connectionState !== 'connected' && !tab.cleanupError);
      if (!tab.live && tab.projectPath) {
        const open = document.createElement('button');
        open.type = 'button'; open.textContent = 'Open project in TIA';
        open.disabled = locked;
        open.onclick = () => {
          if (open.disabled) return;
          connectionAction('projects/open', { tabId: tab.id }, 'Opening the project in a new TIA window. Approve access in TIA if prompted.');
        };
        actions.append(open);
      }
      if (!tab.live) {
        const dismiss = document.createElement('button');
        dismiss.type = 'button'; dismiss.textContent = 'Dismiss history';
        dismiss.onclick = () => connectionAction('tabs/dismiss', { tabId: tab.id }, 'Removing dashboard history…');
        actions.append(dismiss);
      }
    }
    const state = stateFor(selectedId);
    const activeTool = ensureTool(tab);
    formElements().forEach(form => {
      const project = form.getAttribute('data-project') === 'true';
      const tool = form.getAttribute('data-tool');
      conceal(form, contextHidden(form, tab) || tool !== activeTool);
      const button = list(form).find(child => tagOf(child) === 'button');
      if (!button) return;
      let disabled = false;
      if (project) disabled = !projectReady(tab);
      else if (tool === 'get_status' && tab && tab.kind === 'tia') disabled = !tab.processId;
      button.disabled = disabled || inflight || !!missingMessage(tool, form);
      fieldsOf(form).forEach(field => {
        if (field.name === 'processId') field.value = tab && tab.processId ? String(tab.processId) : '';
      });
      syncConstraints(form);
    });
    renderToolNav(tab, activeTool);
    const writing = !!(tab && tab.kind === 'tia' && state.mode === 'writes');
    conceal($('mode-writes'), !tab || tab.kind !== 'tia' || !formElements().some(form => form.getAttribute('data-write') === 'true'));
    $('mode-tools').textContent = tab && tab.kind === 'tia' ? 'Read operations' : 'Server tools';
    $('mode-tools').setAttribute('aria-pressed', String(!writing));
    $('mode-writes').setAttribute('aria-pressed', String(writing));
    $('runner-kind').textContent = writing ? 'MCP · changes stay unsaved in TIA' : 'Read-only MCP';
    $('runner-title').textContent = writing ? 'Write operations' : 'Read operations';
    renderPrerequisite(activeTool, tab);
    labelize(); refreshSelectors(); renderWriteHelpers();
    renderInspector(tab);
    const visible = logs.filter(entry => entry.tabId === (tab ? tab.id : 'server'));
    const logBox = $('logs');
    const logSignature = JSON.stringify([selectedId, visible]);
    if (logBox.getAttribute('data-signature') !== logSignature) {
      logBox.replaceChildren();
      if (!visible.length) logBox.append(node('p', 'No server events for this workspace.', 'log-row muted'));
      visible.slice().reverse().forEach(entry => {
        const row = node('details', null, 'log-row');
        const summary = node('summary', [timeLabel(entry.atUtc), entry.origin, entry.operation, entry.outcome, entry.durationMs != null ? Math.round(entry.durationMs) + ' ms' : ''].filter(Boolean).join(' · '));
        if (entry.outcome === 'error') summary.className = 'error';
        else if (entry.outcome === 'partial') summary.className = 'partial';
        row.append(summary, node('pre', JSON.stringify(entry, null, 2))); logBox.append(row);
      });
      logBox.setAttribute('data-signature', logSignature);
    }
    renderHistory();
    const history = tabs.length ? { logsTruncated: $('banner').getAttribute('data-logs') === 'true', tabsTruncated: $('banner').getAttribute('data-tabs') === 'true' } : {};
    conceal($('banner'), !(history.logsTruncated || history.tabsTruncated));
  }
  function labelize() {
    const processId = selected() && selected().processId ? selected().processId : '';
    const suffix = ' for process ' + processId;
    formElements().forEach(form => {
      const tool = form.getAttribute('data-tool');
      fieldsOf(form).forEach(field => {
        if (field.getAttribute('data-selector')) field.setAttribute('aria-label', (selectorTitles[field.getAttribute('data-selector')] || 'Choice') + suffix);
        else if (ariaNames[tool + '.' + field.name]) field.setAttribute('aria-label', ariaNames[tool + '.' + field.name] + suffix);
      });
    });
  }
  function refreshSelectors() {
    const state = stateFor(selectedId);
    const ready = projectReady(selected());
    const map = { entry:state.entries.filter(item => item.kind !== 'systemConstants'), entryTable:state.choices.tagTable, sourceBlock:state.choices.block, sourceUdt:state.choices.udt, device: state.choices.device, cpu: state.choices.cpu, block: state.choices.block, udt: state.choices.udt, tagTable: state.choices.tagTable, cross: state.choices.cross };
    formElements().forEach(form => fieldsOf(form).forEach(field => {
      const tool = form.getAttribute('data-tool');
      const kind = field.getAttribute && field.getAttribute('data-selector');
      if (!kind) return;
      const choices = kind === 'group' ? state.choices.group.filter(item => item.source === inventoryFor(tool)) : map[kind] || [];
      const signature = JSON.stringify(choices);
      if (field.getAttribute('data-signature') !== signature) {
        field.replaceChildren();
        const blank = document.createElement('option');
        blank.value = ''; blank.textContent = kind === 'group' ? 'PLC root (default)' : choices.length ? 'Choose…' : kind === 'entry' ? state.entryState : 'Load inventory to choose…';
        field.append(blank);
        choices.forEach(choice => { const option = document.createElement('option'); option.value = choice.id; option.textContent = choice.label; field.append(option); });
        field.setAttribute('data-signature', signature);
      }
      const value = kind === 'entryTable' ? state.entryTable
        : kind === 'sourceBlock' || kind === 'sourceUdt' ? state.sources[tool] || ''
        : kind === 'group' ? (state.values[tool+'.groupObjectId'] ? 'id:'+state.values[tool+'.groupObjectId'] : state.values[tool+'.groupPath'] ? 'path:'+state.values[tool+'.groupPath'] : '')
        : kind === 'cpu' ? state.values[tool+'.plcObjectId'] || ''
        : state.values[tool+'.objectId'] || state.selected[kind] || '';
      if (field.value !== value) field.value = value;
      field.disabled = inflight || !ready || (kind !== 'cross' && kind !== 'group' && !choices.length);
    }));
  }
  function loadState(tabId) {
    const state = stateFor(tabId);
    formElements().forEach(form => {
      const tool = form.getAttribute('data-tool');
      fieldsOf(form).forEach(field => {
        if (!field.name || field.getAttribute('data-selector') === 'true') return;
        const key = tool + '.' + field.name;
        if (Object.prototype.hasOwnProperty.call(state.values, key)) {
          if (field.type === 'checkbox') field.checked = !!state.values[key];
          else field.value = state.values[key];
        } else if (field.type === 'checkbox') field.checked = field.getAttribute('data-default') === 'true';
        else if (field.getAttribute('data-default')) field.value = field.getAttribute('data-default');
        else if (field.name !== 'processId') field.value = '';
      });
      syncConstraints(form);
    });
  }
  async function refreshDashboard() {
    const response = await fetch('/api/dashboard/dashboard');
    const data = await response.json();
    if (!response.ok) throw new Error((data.error && data.error.message) || 'Dashboard state failed');
    serverBusy = data.pendingOperations > 0;
    $('pause').checked = !!data.backgroundMonitoringPaused;
    const history = data.history || { tabs: [] };
    if (epoch && history.epoch && history.epoch !== epoch) { logs = []; logCursor = 0; logGeneration = 0; results.clear(); runs = []; formState.clear(); stamps.clear(); }
    epoch = history.epoch || epoch;
    tabs = history.tabs || [];
    if (!tabs.some(tab => tab.id === selectedId)) selectedId = 'server';
    tabs.forEach(tab => {
      if (tab.kind !== 'tia') return;
      const stamp = tabStamp(tab);
      if (stamps.has(tab.id) && stamps.get(tab.id) !== stamp) {
        formState.set(tab.id, blankState());
        if (tab.id === selectedId) loadState(tab.id);
      }
      stamps.set(tab.id, stamp);
    });
    $('banner').setAttribute('data-logs', history.logsTruncated ? 'true' : 'false');
    $('banner').setAttribute('data-tabs', history.tabsTruncated ? 'true' : 'false');
    $('banner').textContent = 'Older dashboard history was discarded. This server keeps ' + (history.maxLogEntries || 400) + ' log entries and ' + (history.maxHistoricalTabs || 24) + ' historical tabs.';
    render();
  }
  async function refreshLogs() {
    const response = await fetch('/api/dashboard/logs?after=' + logCursor + '&generation=' + logGeneration);
    const data = await response.json();
    if (!response.ok) throw new Error((data.error && data.error.message) || 'Log read failed');
    if (data.reset || logCursor === 0) logs = data.entries || [];
    else logs = logs.concat(data.entries || []).slice(-400);
    logCursor = data.next || 0;
    logGeneration = data.generation || 0;
    if (data.truncated) $('banner').setAttribute('data-logs', 'true');
    render();
  }
  async function loadForms() {
    const response = await fetch('/api/dashboard/tool-forms');
    const html = await response.text();
    if (!response.ok) throw new Error('Tool forms failed');
    mountForms(html);
  }
  let polling = false;
  async function poll() {
    if (!document.hidden && !polling) {
      polling = true;
      try { await refreshDashboard(); await refreshLogs(); }
      catch (error) { $('activity').textContent = 'Server unavailable'; }
      finally { polling = false; }
    }
    setTimeout(poll, 1500);
  }
  $('copy').onclick = async () => {
    const text = $('result').textContent || '';
    const copied = async () => {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        return;
      }
      throw new Error('Clipboard is unavailable.');
    };
    try {
      await copied();
    } catch (error) {
      const area = document.createElement('textarea');
      area.value = text;
      document.body.append(area);
      area.select();
      const ok = document.execCommand && document.execCommand('copy');
      area.remove();
      if (!ok) {
        $('message').className = 'error';
        $('message').textContent = 'Could not copy the result.';
        return;
      }
    }
    $('message').className = '';
    $('message').textContent = 'Copied ' + inspectorView + '.';
  };
  $('mode-tools').onclick = () => { stateFor(selectedId).mode = 'tools'; render(); };
  $('mode-writes').onclick = () => { stateFor(selectedId).mode = 'writes'; render(); };
  ['result','request','response'].forEach(view => { $('view-' + view).onclick = () => { inspectorView = view; render(); }; });
  $('history-runs').onclick = () => { historyView = 'runs'; render(); };
  $('history-server').onclick = () => { historyView = 'server'; render(); };
  $('pause').onchange = () => connectionAction('monitor', { paused: $('pause').checked }, $('pause').checked ? 'Pausing background checks…' : 'Resuming background checks…');
  function contextHidden(form, tab) {
    const process = form.getAttribute('data-process');
    if (!tab || tab.kind === 'server') return process === 'required';
    return process === 'none' || (form.getAttribute('data-write') === 'true') !== (stateFor(tab.id).mode === 'writes');
  }
  function ensureTool(tab) {
    const state = stateFor(tab ? tab.id : 'server');
    const available = formElements().filter(form => !contextHidden(form, tab)).map(form => form.getAttribute('data-tool'));
    const fallback = !tab || tab.kind === 'server' ? 'list_tia_processes' : state.mode === 'writes' ? 'create_tag' : 'list_devices';
    if (!available.includes(state.tool)) state.tool = available.includes(fallback) ? fallback : (available[0] || fallback);
    return state.tool;
  }
  function renderToolNav(tab, tool) {
    const nav = $('tool-nav');
    if (!nav) return;
    const forms = formElements().filter(form => !contextHidden(form, tab));
    const signature = forms.map(form => form.getAttribute('data-tool')).join('|');
    if (nav.getAttribute('data-signature') !== signature) {
      nav.replaceChildren();
      const label = node('label', 'Operation');
      const choice = document.createElement('select'); choice.setAttribute('aria-label', 'MCP operation');
      forms.forEach(form => {
        const name = form.getAttribute('data-tool');
        const action = list(form).find(child => tagOf(child) === 'button');
        const option = node('option', (action ? action.textContent : name) + ' · ' + name); option.value = name; choice.append(option);
      });
      choice.onchange = () => showTool(choice.value);
      label.append(choice); nav.append(label); nav.choice = choice;
      nav.setAttribute('data-signature', signature);
    }
    if (nav.choice && nav.choice.value !== tool) nav.choice.value = tool;
  }

  function inventoryFor(tool) {
    return ['write_blocks','delete_block'].includes(tool) ? 'list_blocks' : ['write_udts','delete_udt'].includes(tool) ? 'list_udts' : 'list_tag_tables';
  }
  function clearEntries(tabId, table) {
    const state = stateFor(tabId);
    state.entryTable = table; state.entries = []; state.selected.entry = '';
    state.entryState = table ? 'Loading entries…' : 'Select a table to load its tags and user constants.';
    entryTools().forEach(tool => setField(tabId,tool,'objectId',''));
    rebuildCross(state);
  }
  async function helperRead(tool, args, after) {
    const tab = selected();
    if (inflight || !projectReady(tab)) return;
    inflight = true; render();
    try {
      const run = await executeTool(tab,tool,Object.assign({ processId:tab.processId },args));
      if (after) after(run,tab);
    } finally {
      inflight = false;
      try { await refreshDashboard(); await refreshLogs(); } catch (error) { $('message').textContent = error.message; }
      render();
    }
  }
  function loadEntries(value) {
    if (inflight) return;
    clearEntries(selectedId,value);
    if (!value) { render(); return; }
    return helperRead('get_tag_table',{ objectId:value, includeEntries:true, includePath:false },(run,tab) => {
      if (run.failed && tabStamp(tabs.find(item => item.id === tab.id)) === tabStamp(tab))
        stateFor(tab.id).entryState = 'Entries could not be loaded. Inspect the read result and refresh.';
    });
  }
  function addWriteHelpers() {
    formElements().filter(form => form.getAttribute('data-write') === 'true').forEach(form => {
      const tool = form.getAttribute('data-tool');
      const state = () => stateFor(selectedId);
      if (fieldBy(tool,'plcObjectId')) addChoice(tool,'plcObjectId','cpu',value => setPlc(selectedId,value));
      if (tool === 'compile_plc') return;
      if (fieldBy(tool,'groupObjectId')) addChoice(tool,'groupObjectId','group',value => {
        setField(selectedId,tool,'groupObjectId',value.startsWith('id:') ? value.slice(3) : '');
        setField(selectedId,tool,'groupPath',value.startsWith('path:') ? value.slice(5) : '');
      });
      if (tool === 'create_tag' || tool === 'create_user_constant')
        addChoice(tool,'objectId','tagTable',value => setField(selectedId,tool,'objectId',value));
      const deletedKind = { delete_block:'block', delete_udt:'udt', delete_tag_table:'tagTable' }[tool];
      if (deletedKind) addChoice(tool,'objectId',deletedKind,value => setField(selectedId,tool,'objectId',value));
      if (entryTools().includes(tool)) {
        addChoice(tool,'objectId','entryTable',value => loadEntries(value));
        addChoice(tool,'objectId','entry',value => setField(selectedId,tool,'objectId',value));
      }
      if (tool === 'write_blocks' || tool === 'write_udts')
        addChoice(tool,'documents',tool === 'write_blocks' ? 'sourceBlock' : 'sourceUdt',value => { state().sources[tool] = value; render(); });
      const helper = node('div',null,'write-helper');
      const load = node('button','Load / refresh ' + (inventoryFor(tool) === 'list_blocks' ? 'blocks and groups' : inventoryFor(tool) === 'list_udts' ? 'UDTs and groups' : 'tag tables'));
      load.type = 'button'; load.setAttribute('data-helper','inventory');
      load.onclick = () => {
        const cpu = state().selected.cpu;
        if (!cpu) { $('message').textContent = 'Choose a PLC through List devices → Read device, or enter its native CPU ID.'; return; }
        return helperRead(inventoryFor(tool),{ plcObjectId:cpu });
      };
      helper.append(load);
      if (entryTools().includes(tool)) {
        const refresh = node('button','Refresh entries'); refresh.type = 'button'; refresh.setAttribute('data-helper','entries');
        refresh.onclick = () => loadEntries(state().entryTable); helper.append(refresh);
        const hint = node('p','','hint'); hint.setAttribute('data-entry-hint','true'); helper.append(hint);
      }
      if (tool === 'write_blocks' || tool === 'write_udts') {
        const source = node('button','Load selected source'); source.type = 'button'; source.setAttribute('data-helper','source');
        source.onclick = () => helperRead(tool === 'write_blocks' ? 'get_block' : 'get_udt', {
          objectId:state().sources[tool], includePath:false, includeSource:true, sourceFormat:fieldBy(tool,'sourceFormat').value || 'best'
        },(run,tab) => {
          if (run.failed || !run.currentContext || !run.body || !run.body.source) return;
          const documents = run.body.source.documents.map(document => ({ name:document.name, content:document.content }));
          setField(tab.id,tool,'documents',JSON.stringify(documents));
          setField(tab.id,tool,'sourceFormat',run.body.source.format);
        });
        helper.append(source,node('p','Load existing source to edit or copy it. The declarations in your documents determine which names TIA creates or replaces.','hint'));
      }
      form.insertBefore(helper,list(form)[1] || list(form)[0]);
      const documents = fieldBy(tool,'documents');
      if (documents) {
        conceal(documents,true);
        const editors = node('div'); form.insertBefore(editors,list(form)[list(form).indexOf(documents.parentNode)+1]); documents.editors = editors;
      }
    });
  }
  function renderDocuments(field, tool) {
    const editors = field.editors;
    if (!editors) return;
    const raw = field.value || '';
    if (editors.getAttribute('data-content') !== raw) {
      let documents;
      try { documents = JSON.parse(raw); } catch (ignore) { documents = []; }
      if (!Array.isArray(documents) || !documents.length) documents = [{ name:'',content:'' }];
      editors.replaceChildren();
      const commit = () => {
        field.value = JSON.stringify(documents);
        stateFor(selectedId).values[tool+'.documents'] = field.value;
        editors.setAttribute('data-content',field.value);
        render();
      };
      documents.forEach((document,index) => {
        const box = node('div',null,'source-editor');
        const title = node('label','Document file name');
        const name = node('input'); name.type = 'text'; name.value = document.name || '';
        name.setAttribute('aria-label','Document ' + (index+1) + ' file name');
        name.oninput = () => { document.name = name.value; commit(); };
        const contentLabel = node('label','Source content');
        const content = node('textarea'); content.value = document.content || '';
        content.setAttribute('aria-label','Document ' + (index+1) + ' source content');
        content.oninput = () => { document.content = content.value; commit(); };
        title.append(name); contentLabel.append(content); box.append(title,contentLabel);
        appendFieldHelp(title,name,field.getAttribute('data-document-name-help'),tool+'-document-'+index+'-name-help');
        appendFieldHelp(contentLabel,content,field.getAttribute('data-document-content-help'),tool+'-document-'+index+'-content-help');
        if (documents.length > 1) {
          const remove = node('button','Remove document'); remove.type='button';
          remove.onclick = () => { documents.splice(index,1); editors.setAttribute('data-content',''); field.value=JSON.stringify(documents); stateFor(selectedId).values[tool+'.documents']=field.value; render(); };
          box.append(remove);
        }
        editors.append(box);
      });
      if (documents.length < 2 && tool !== 'import_tag_tables') {
        const add = node('button','Add resource document (.s7res)'); add.type='button';
        add.onclick = () => {
          documents.push({ name:'',content:'' }); field.value=JSON.stringify(documents);
          stateFor(selectedId).values[tool+'.documents']=field.value; render();
        };
        editors.append(add);
      }
      editors.setAttribute('data-content',raw);
    }
    walk(editors,item => { if (['input','textarea','button'].includes(tagOf(item))) item.disabled = inflight || !projectReady(selected()); });
  }
  function renderWriteHelpers() {
    const state = stateFor(selectedId);
    formElements().forEach(form => {
      const tool = form.getAttribute('data-tool');
      if (!isWrite(tool)) return;
      walk(form,item => {
        const helper = item.getAttribute && item.getAttribute('data-helper');
        if (helper) item.disabled = inflight || !projectReady(selected()) ||
          (helper === 'inventory' && !state.selected.cpu) || (helper === 'entries' && !state.entryTable) || (helper === 'source' && !state.sources[tool]);
        if (item.getAttribute && item.getAttribute('data-entry-hint')) item.textContent = state.entryState || 'Entries are from the selected table. System constants cannot be edited.';
      });
      const documents = fieldBy(tool,'documents');
      if (documents) renderDocuments(documents,tool);
    });
  }
  async function refreshAfterWrite(tab, tool, args, run) {
    const state = stateFor(tab.id);
    const stamp = tabStamp(tab);
    const cpu = args.plcObjectId || state.selected.cpu;
    const entryTable = (tool === 'create_tag' || tool === 'create_user_constant') ? args.objectId :
      (run.body.affectedObjects || []).filter(item => item.kind === 'tag' || item.kind === 'userConstant').map(item => item.parentObjectId).find(Boolean) ||
      (state.entries.some(item => item.id === args.objectId) ? state.entryTable : '');
    let readFailed = false;
    const read = async (name, parameters) => {
      const current = tabs.find(item => item.id === tab.id);
      if (!current || tabStamp(current) !== stamp || stateFor(tab.id).selected.cpu !== cpu) return null;
      const result = await executeTool(tab,name,Object.assign({ processId:tab.processId },parameters));
      if (result.failed || (result.body && result.body.complete === false)) readFailed = true;
      return result;
    };
    try {
      if (entryTable) {
        const result = await read('get_tag_table',{ objectId:entryTable, includeEntries:true, includePath:false });
        if (result && result.failed) clearEntries(tab.id,'');
      } else if (cpu) {
        const inventory = inventoryFor(tool);
        await read(inventory,{ plcObjectId:cpu });
        const deleting = ['delete_block','delete_udt','delete_tag_table'].includes(tool);
        for (const item of run.body.affectedObjects) {
          if (deleting) continue;
          if (!item.objectId) continue;
          const name = item.kind === 'block' ? 'get_block' : item.kind === 'udt' ? 'get_udt' : item.kind === 'tagTable' ? 'get_tag_table' : null;
          if (name) await read(name,name === 'get_tag_table'
            ? { objectId:item.objectId, includeEntries:true, includePath:false }
            : { objectId:item.objectId, includeSource:true, includePath:false, sourceFormat:args.sourceFormat });
        }
      }
      if (readFailed) run.capture.message += ' Refresh/readback failed; inspect the follow-up read in history.';
    } finally {
      results.set(tab.id,run.capture); persistRuns();
    }
  }

  async function boot() {
    await loadForms();
    await refreshDashboard();
    restoreRuns();
    await refreshLogs();
    poll();
  }
  var bootPromise = boot().catch(error => { $('message').className = 'error'; $('message').textContent = error.message; });
