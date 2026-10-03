'use strict';

// The only product acceptance suite. Every request goes to the existing MCP
// server and the one user-connected repository Demo project.
const assert = require('node:assert/strict');
const { LiveClient, optionsFrom } = require('./mcp-client.cjs');
const { TOOLS, PROJECT_PATH, CPU_TYPE, GROUPS, canonical, walk, leaves, id, one,
  affected, checksums, sourceSpec, compilation, crossReference } = require('./mcp-fixture.cjs');

async function workflow(client) {
  const processId = client.options.processId;
  const fixture = client.report.fixture;
  const objects = fixture.objects;
  const prefix = client.prefix;
  objects.device = { kind: 'device', name: prefix + '_Device', deviceItemName: prefix + '_PLC' };
  objects.table = { kind: 'tagTable', name: prefix + '_Tags' };
  objects.tag = { kind: 'tag', name: prefix + '_Signal', logicalAddress: '%M0.0' };
  objects.constant = { kind: 'userConstant', name: prefix + '_Limit', value: '100' };
  objects.technology = { kind: 'technologyObject', name: prefix + '_PID' };
  let plcObjectId;
  const cpuArgs = () => ({ processId, plcObjectId });
  const objectArgs = key => ({ processId, objectId: id(objects[key].objectId) });
  const inventory = async tool => {
    const result = await client.call(tool, cpuArgs());
    assert.equal(result.plcObjectId, plcObjectId);
    assert.ok(Array.isArray(result.roots));
    return result;
  };
  const store = (key, item) => { Object.assign(objects[key], item, { deleted: false }); client.persist(); };
  const deleted = key => { objects[key].deleted = true; client.persist(); };
  const destination = kind => ({ ...cpuArgs(), groupPath: fixture.groups[kind].child.path });
  const tableRead = async () => {
    const result = await client.call('get_tag_table', objectArgs('table'));
    assert.equal(result.metadata.objectId, objects.table.objectId);
    assert.equal(result.metadata.name, objects.table.name);
    assert.ok(result.metadata.path.startsWith(fixture.groups.tagTable.child.path + '/'));
    for (const collection of ['tags', 'userConstants', 'systemConstants']) assert.ok(Array.isArray(result.entries?.[collection]));
    return result.entries;
  };
  const tagEntry = entries => {
    const item = one(entries.tags, objects.tag.name);
    id(item.objectId); assert.equal(item.dataType, 'Bool'); assert.equal(item.logicalAddress, objects.tag.logicalAddress);
    return item;
  };
  const constantEntry = entries => {
    const item = one(entries.userConstants, objects.constant.name);
    id(item.objectId); assert.equal(item.dataType, 'Int'); assert.equal(item.value, objects.constant.value);
    return item;
  };
  const populatedTable = async () => {
    const entries = await tableRead();
    assert.equal(entries.tags.length, 1); assert.equal(entries.userConstants.length, 1);
    const tag = tagEntry(entries), constant = constantEntry(entries);
    assert.notEqual(tag.objectId, constant.objectId); assert.notEqual(tag.objectId, objects.table.objectId);
    assert.notEqual(constant.objectId, objects.table.objectId);
    return { entries, tag, constant };
  };
  const verifyMissing = async (tool, objectId, extra = {}) => {
    const result = await client.call(tool, { processId, objectId, ...extra }, { expectedError: true });
    assert.equal(result.error?.code, 'objectNotFound');
  };

  await client.stage('preflight', 'Verify exact full publication, visible user-connected Demo and its empty device inventory.',
    ['list_tia_processes', 'get_status', 'list_devices'], async () => {
      const initialized = await client.rpc('initialize', { protocolVersion: '2025-03-26', capabilities: {},
        clientInfo: { name: 'tia-mcp-live', version: '1' } });
      assert.equal(initialized.serverInfo?.name, 'tia-portal-openness');
      client.report.serverInfo = initialized.serverInfo;
      const listing = await client.rpc('tools/list', {});
      assert.ok(Array.isArray(listing.tools));
      assert.deepEqual(listing.tools.map(tool => tool.name).sort(), [...TOOLS].sort(), 'All 34 expected tools must be published exactly once.');
      client.report.publication = listing.tools;
      const bridge = await client.call('get_status');
      assert.equal(bridge.accessProfile, 'full'); assert.equal(bridge.writeToolsAvailable, true);
      const processes = await client.call('list_tia_processes');
      assert.ok(Array.isArray(processes.processes));
      const selected = processes.processes.filter(process => process.processId === processId);
      assert.equal(selected.length, 1);
      assert.equal(selected[0].mode, 'with-ui'); assert.equal(selected[0].connectedByMcp, true);
      assert.equal(canonical(selected[0].primaryProjectPath), canonical(PROJECT_PATH));
      const status = await client.call('get_status', { processId });
      client.assertTarget(status, true); client.report.before = status;
      const devices = await client.call('list_devices', { processId });
      assert.equal(leaves(devices, 'device').length, 0, 'Demo must be empty; inspect leftovers before rerunning.');
    });

  await client.stage('device.create', 'Create the exact CPU fixture and discover its Device identity and CPU software selector.',
    ['create_device', 'get_device', 'list_devices'], async () => {
      const created = await client.call('create_device', { processId, typeIdentifier: CPU_TYPE,
        deviceItemName: objects.device.deviceItemName, deviceName: objects.device.name });
      const item = affected(created, 'device', objects.device.name); id(item.objectId); store('device', item);
      const devices = leaves(await client.call('list_devices', { processId }), 'device');
      assert.equal(devices.length, 1); assert.equal(devices[0].objectId, item.objectId); assert.equal(devices[0].name, item.name);
      const read = await client.call('get_device', objectArgs('device'));
      assert.equal(read.metadata.objectId, item.objectId); assert.equal(read.metadata.name, item.name);
      assert.ok(read.metadata.path && Array.isArray(read.deviceItems));
      const cpus = walk(read.deviceItems).filter(node => node.plcObjectId != null);
      assert.equal(cpus.length, 1); const cpu = cpus[0];
      assert.equal(cpu.name, objects.device.deviceItemName); assert.equal(cpu.plcObjectId, cpu.objectId);
      assert.notEqual(cpu.objectId, item.objectId); assert.equal(cpu.typeIdentifier, CPU_TYPE);
      assert.equal(cpu.firmwareVersion, 'V2.0');
      plcObjectId = id(cpu.plcObjectId); fixture.cpu = cpu; client.persist();
      const noPaths = await client.call('get_device', { ...objectArgs('device'), includePath: false });
      assert.equal(noPaths.metadata.path, null);
      for (const node of walk(noPaths.deviceItems)) assert.equal(node.path, null);
    });

  await client.stage('groups.create', 'Create a parent and nested child in each typed composition and verify native kinds and exact paths.',
    ['create_group', ...GROUPS.map(group => group.list)], async () => {
      for (const spec of GROUPS) {
        fixture.groups[spec.kind] = {};
        for (const level of ['parent', 'child']) {
          const name = prefix + '_' + spec.kind + '_' + level;
          const group = fixture.groups[spec.kind]; group[level] = { name, kind: spec.kind }; client.persist();
          const before = walk((await inventory(spec.list)).roots); assert.ok(!before.some(node => node.name === name));
          const created = await client.call('create_group', { ...cpuArgs(), kind: spec.kind, name,
            ...(level === 'child' ? { groupPath: group.parent.path } : {}) });
          const item = affected(created, spec.nodeKind, name);
          const found = one(walk((await inventory(spec.list)).roots), name);
          assert.equal(found.kind, spec.nodeKind); assert.ok(found.path.endsWith('/' + name));
          if (level === 'child') assert.equal(found.path, group.parent.path + '/' + name);
          if (item.objectId != null) assert.equal(found.objectId, item.objectId);
          Object.assign(group[level], { objectId: found.objectId ?? null, path: found.path, deleted: false }); client.persist();
        }
      }
    });

  await client.stage('tags.create', 'Create the nested table, addressed Bool tag and populated Int constant, then verify distinct native identities and values.',
    ['create_tag_table', 'create_tag', 'create_user_constant', 'get_tag_table', 'list_tag_tables'], async () => {
      const created = affected(await client.call('create_tag_table', { ...destination('tagTable'), name: objects.table.name }), 'tagTable', objects.table.name);
      id(created.objectId); store('table', created);
      const found = one(leaves(await inventory('list_tag_tables'), 'tagTable'), objects.table.name);
      assert.equal(found.objectId, created.objectId); assert.equal(found.path, fixture.groups.tagTable.child.path + '/' + objects.table.name);
      const empty = await tableRead(); assert.equal(empty.tags.length, 0); assert.equal(empty.userConstants.length, 0);
      const tag = affected(await client.call('create_tag', { ...objectArgs('table'), name: objects.tag.name,
        dataType: 'Bool', logicalAddress: objects.tag.logicalAddress }), 'tag', objects.tag.name);
      id(tag.objectId); assert.equal(tag.parentObjectId, objects.table.objectId); store('tag', tag);
      assert.equal(tagEntry(await tableRead()).objectId, tag.objectId);
      const constant = affected(await client.call('create_user_constant', { ...objectArgs('table'), name: objects.constant.name,
        dataType: 'Int', value: objects.constant.value }), 'userConstant', objects.constant.name);
      id(constant.objectId); assert.equal(constant.parentObjectId, objects.table.objectId); store('constant', constant);
      const populated = await populatedTable(); assert.equal(populated.constant.objectId, constant.objectId);
      assert.equal(typeof populated.tag.typeSpecific.ExternalVisible, 'boolean');
      objects.tag.externalVisible = populated.tag.typeSpecific.ExternalVisible;
      const metadata = await client.call('get_tag_table', { ...objectArgs('table'), includeEntries: false, includePath: false });
      assert.equal(metadata.metadata.objectId, objects.table.objectId); assert.equal(metadata.metadata.path, null); assert.equal(metadata.entries, null);
    });

  await client.stage('tags.edit', 'Change the owned Bool tag ExternalVisible and independently read back the changed native boolean.',
    ['set_tag_entry_attribute', 'get_tag_table'], async () => {
      const before = tagEntry(await tableRead()); assert.equal(before.objectId, objects.tag.objectId);
      const value = !before.typeSpecific.ExternalVisible;
      affected(await client.call('set_tag_entry_attribute', { ...objectArgs('tag'), attributeName: 'ExternalVisible', attributeValue: value }),
        'tag', objects.tag.name, objects.tag.objectId);
      const after = tagEntry(await tableRead()); assert.equal(after.objectId, before.objectId); assert.equal(after.typeSpecific.ExternalVisible, value);
      objects.tag.externalVisible = value; client.persist();
    });

  for (const kind of ['block', 'udt']) {
    const spec = sourceSpec(kind, prefix, objects.tag.name);
    objects[kind] = { kind, name: spec.name }; client.persist();
    await client.stage(kind + '.source', 'Create and replace the nested ' + kind + ' from complete source; verify native metadata, checksum and changed semantics.',
      [spec.write, spec.read, spec.list], async () => {
        let documents = [{ name: spec.filename, content: spec.content }];
        for (const updated of [false, true]) {
          const before = leaves(await inventory(spec.list), kind).filter(node => node.name === spec.name);
          if (!updated) assert.equal(before.length, 0);
          else { assert.equal(before.length, 1); assert.equal(before[0].objectId, objects[kind].objectId); }
          const written = affected(await client.call(spec.write, { ...destination(kind), sourceFormat: 'external-source', documents }), kind, spec.name);
          const found = one(leaves(await inventory(spec.list), kind), spec.name); id(found.objectId);
          if (written.objectId != null) assert.equal(found.objectId, written.objectId);
          assert.equal(found.path, fixture.groups[kind].child.path + '/' + spec.name); store(kind, { objectId: found.objectId });
          const metadata = await client.call(spec.read, { ...objectArgs(kind), includeSource: false, includePath: false });
          assert.equal(metadata.metadata.objectId, found.objectId); assert.equal(metadata.metadata.name, spec.name);
          assert.equal(metadata.metadata.path, null); assert.equal(metadata.source, null);
          const read = await client.call(spec.read, { ...objectArgs(kind), sourceFormat: 'external-source', includeDependencies: false });
          assert.equal(read.metadata.name, spec.name); assert.equal(read.metadata.path, found.path);
          assert.equal(read.source.dependenciesIncluded, false);
          const exported = checksums(read.source, 'external-source'); spec.verify(exported, updated);
          if (!updated) documents = exported.map(document => ({ name: document.name, content: spec.edit(document.content) }));
        }
      });
  }

  await client.stage('group.rename', 'Rename the table parent group and read back the new paths while preserving table and entry IDs.',
    ['rename', 'list_tag_tables', 'get_tag_table'], async () => {
      const group = fixture.groups.tagTable;
      const oldParent = group.parent.path, oldChild = group.child.path, oldName = group.parent.name;
      const newName = oldName + '_Renamed';
      affected(await client.call('rename', { ...cpuArgs(), kind: 'tagTable', groupPath: oldParent, name: newName }), 'tagTableGroup', newName);
      const nodes = walk((await inventory('list_tag_tables')).roots);
      assert.ok(!nodes.some(node => node.name === oldName || node.path === oldParent || node.path === oldChild));
      const parent = one(nodes, newName), child = one(nodes, group.child.name);
      assert.equal(parent.kind, 'tagTableGroup'); assert.equal(child.path, parent.path + '/' + group.child.name);
      group.parent.name = newName; group.parent.path = parent.path; group.child.path = child.path; client.persist();
      const table = one(nodes.filter(node => node.kind === 'tagTable'), objects.table.name);
      assert.equal(table.objectId, objects.table.objectId); assert.equal(table.path, child.path + '/' + objects.table.name);
      const populated = await populatedTable(); assert.equal(populated.tag.objectId, objects.tag.objectId); assert.equal(populated.constant.objectId, objects.constant.objectId);
    });

  await client.stage('technology', 'Verify CPU catalogue support, create nested PID_Compact 2.3 and change ManualEnable from false to true with independent readback.',
    ['list_available_technology_objects', 'create_technology_object', 'get_technology_object', 'list_technology_objects', 'set_technology_object_parameters'], async () => {
      const available = await client.call('list_available_technology_objects', cpuArgs());
      assert.equal(available.plcObjectId, plcObjectId); assert.equal(available.cpuFamily, 'S7-1500');
      assert.equal(available.technologyCpu, false); assert.equal(available.firmwareVersion, 'V2.0');
      const pid = one(available.technologyObjects, 'PID_Compact'); assert.equal(pid.systemLibElement, 'PID_Compact');
      const created = affected(await client.call('create_technology_object', { ...destination('technologyObject'),
        name: objects.technology.name, systemLibElement: 'PID_Compact', systemLibVersion: '2.3' }), 'technologyObject', objects.technology.name);
      id(created.objectId); store('technology', created);
      const found = one(leaves(await inventory('list_technology_objects'), 'technologyObject'), objects.technology.name);
      assert.equal(found.objectId, created.objectId); assert.equal(found.path, fixture.groups.technologyObject.child.path + '/' + objects.technology.name);
      const read = await client.call('get_technology_object', objectArgs('technology'));
      assert.equal(read.metadata.objectId, created.objectId); assert.equal(read.metadata.name, objects.technology.name);
      assert.equal(read.metadata.ofSystemLibElement, 'PID_Compact'); assert.equal(read.metadata.ofSystemLibVersion, '2.3');
      assert.equal(one(read.parameters, 'ManualEnable').value, false);
      const written = await client.call('set_technology_object_parameters', { ...objectArgs('technology'), parameters: [{ name: 'ManualEnable', value: true }] });
      const parameter = affected(written, 'technologyParameter', 'ManualEnable'); assert.equal(parameter.parentObjectId, created.objectId);
      const after = await client.call('get_technology_object', objectArgs('technology')); assert.equal(one(after.parameters, 'ManualEnable').value, true);
      const metadata = await client.call('get_technology_object', { ...objectArgs('technology'), includeParameters: false, includePath: false });
      assert.equal(metadata.metadata.name, objects.technology.name); assert.equal(metadata.metadata.path, null); assert.equal(metadata.parameters, null);
    });

  await client.stage('table.export-delete-import', 'Export exact native XML, delete its table, import that document and verify restored fixture values through fresh native IDs.',
    ['export_tag_table', 'delete_tag_table', 'import_tag_tables', 'list_tag_tables', 'get_tag_table'], async () => {
      const before = await populatedTable();
      assert.equal(before.tag.typeSpecific.ExternalVisible, objects.tag.externalVisible);
      const exported = await client.call('export_tag_table', objectArgs('table')); assert.equal(exported.objectId, objects.table.objectId);
      const documents = checksums(exported.source, 'simatic-ml'); assert.equal(documents.length, 1);
      const document = documents[0]; assert.match(document.name, /\.xml$/i);
      assert.equal((document.content.match(/<SW\.Tags\.PlcTagTable\b/g) || []).length, 1);
      for (const key of ['table', 'tag', 'constant']) assert.ok(document.content.includes('<Name>' + objects[key].name + '</Name>'));
      const oldId = objects.table.objectId;
      affected(await client.call('delete_tag_table', objectArgs('table')), 'tagTable', objects.table.name, oldId);
      for (const key of ['table', 'tag', 'constant']) deleted(key);
      assert.ok(!leaves(await inventory('list_tag_tables'), 'tagTable').some(node => node.objectId === oldId || node.name === objects.table.name));
      await verifyMissing('get_tag_table', oldId, { includeEntries: false });
      const imported = affected(await client.call('import_tag_tables', { ...destination('tagTable'),
        documents: [{ name: document.name, content: document.content }] }), 'tagTable', objects.table.name);
      const restored = one(leaves(await inventory('list_tag_tables'), 'tagTable'), objects.table.name); id(restored.objectId);
      if (imported.objectId != null) assert.equal(restored.objectId, imported.objectId);
      store('table', { objectId: restored.objectId });
      const after = await populatedTable(); store('tag', { objectId: after.tag.objectId }); store('constant', { objectId: after.constant.objectId });
      assert.equal(after.tag.typeSpecific.ExternalVisible, objects.tag.externalVisible);
    });

  await client.stage('compile.references', 'Compile valid fixtures and verify their native UsedBy/Read tag-to-FC relation.',
    ['compile_plc', 'get_cross_references'], async () => {
      const diagnostics = compilation(await client.call('compile_plc', cpuArgs()), true);
      crossReference(await client.call('get_cross_references', objectArgs('tag')), objects.tag.objectId, objects.block.objectId);
      return diagnostics;
    });

  await client.stage('compile.error-repair', 'Delete only the referenced fixture tag, verify compiler errors identify that fixture, restore it and compile successfully.',
    ['delete_tag_entry', 'compile_plc', 'create_tag', 'get_tag_table'], async () => {
      const oldId = objects.tag.objectId; assert.equal(tagEntry(await tableRead()).objectId, oldId);
      affected(await client.call('delete_tag_entry', objectArgs('tag')), 'tag', objects.tag.name, oldId); deleted('tag');
      const empty = await tableRead(); assert.equal(empty.tags.length, 0); assert.equal(constantEntry(empty).objectId, objects.constant.objectId);
      const failed = compilation(await client.call('compile_plc', cpuArgs(), { expectedError: true }), false, [objects.tag.name, objects.block.name]);
      const created = affected(await client.call('create_tag', { ...objectArgs('table'), name: objects.tag.name, dataType: 'Bool',
        logicalAddress: objects.tag.logicalAddress }), 'tag', objects.tag.name);
      id(created.objectId); store('tag', created);
      const restored = tagEntry(await tableRead()); assert.equal(restored.objectId, created.objectId);
      objects.tag.externalVisible = restored.typeSpecific.ExternalVisible; client.persist();
      const repaired = compilation(await client.call('compile_plc', cpuArgs()), true);
      return { failed, repaired };
    });

  for (const spec of [
    { key: 'technology', tool: 'delete_block', read: 'get_technology_object', list: 'list_technology_objects', kind: 'technologyObject', deletionKind: 'block', extra: { includeParameters: false } },
    { key: 'block', tool: 'delete_block', read: 'get_block', list: 'list_blocks', kind: 'block', extra: { includeSource: false } },
    { key: 'udt', tool: 'delete_udt', read: 'get_udt', list: 'list_udts', kind: 'udt', extra: { includeSource: false } }
  ]) {
    await client.stage('delete.' + spec.key, 'Individually delete the owned ' + spec.key + ' and verify fresh inventory absence and old-ID rejection.',
      [spec.tool, spec.list, spec.read], async () => {
        const item = objects[spec.key], objectId = id(item.objectId);
        const current = one(leaves(await inventory(spec.list), spec.kind), item.name); assert.equal(current.objectId, objectId);
        const read = await client.call(spec.read, { ...objectArgs(spec.key), ...spec.extra }); assert.equal(read.metadata.name, item.name);
        affected(await client.call(spec.tool, objectArgs(spec.key)), spec.deletionKind || spec.kind, item.name, objectId); deleted(spec.key);
        assert.ok(!leaves(await inventory(spec.list), spec.kind).some(node => node.objectId === objectId || node.name === item.name));
        await verifyMissing(spec.read, objectId, spec.extra);
      });
  }

  for (const key of ['tag', 'constant']) {
    await client.stage('delete.' + key, 'Delete the owned ' + key + ' by its entry ID and verify absence from the still-readable table.',
      ['delete_tag_entry', 'get_tag_table'], async () => {
        const item = objects[key], entries = await tableRead();
        const current = key === 'tag' ? tagEntry(entries) : constantEntry(entries); assert.equal(current.objectId, item.objectId);
        affected(await client.call('delete_tag_entry', objectArgs(key)), item.kind, item.name, item.objectId); deleted(key);
        const after = await tableRead(); const collection = key === 'tag' ? after.tags : after.userConstants;
        assert.ok(!collection.some(entry => entry.objectId === item.objectId || entry.name === item.name));
      });
  }
  await client.stage('delete.table', 'Delete the emptied fixture table and verify inventory absence and old-ID rejection.',
    ['delete_tag_table', 'list_tag_tables', 'get_tag_table'], async () => {
      const entries = await tableRead(); assert.equal(entries.tags.length, 0); assert.equal(entries.userConstants.length, 0);
      const objectId = objects.table.objectId;
      affected(await client.call('delete_tag_table', objectArgs('table')), 'tagTable', objects.table.name, objectId); deleted('table');
      assert.ok(!leaves(await inventory('list_tag_tables'), 'tagTable').some(node => node.objectId === objectId || node.name === objects.table.name));
      await verifyMissing('get_tag_table', objectId, { includeEntries: false });
    });

  await client.stage('groups.delete', 'Delete each now-empty typed child and parent group separately and verify absence after every native deletion.',
    ['delete_group', ...GROUPS.map(group => group.list)], async () => {
      for (const spec of GROUPS) for (const level of ['child', 'parent']) {
        const group = fixture.groups[spec.kind][level];
        const before = one(walk((await inventory(spec.list)).roots), group.name);
        assert.equal(before.path, group.path); assert.equal((before.children || []).length, 0);
        affected(await client.call('delete_group', { ...cpuArgs(), kind: spec.kind, groupPath: group.path }), spec.nodeKind, group.name);
        group.deleted = true; client.persist();
        assert.ok(!walk((await inventory(spec.list)).roots).some(node => node.name === group.name || node.path === group.path));
      }
    });

  await client.stage('device.delete', 'Compile the remaining CPU, delete only the owned Device last and verify Demo has no devices and the old Device ID fails.',
    ['compile_plc', 'delete_device', 'list_devices', 'get_device', 'get_status'], async () => {
      compilation(await client.call('compile_plc', cpuArgs()), true);
      const objectId = objects.device.objectId;
      const current = leaves(await client.call('list_devices', { processId }), 'device');
      assert.equal(current.length, 1); assert.equal(current[0].objectId, objectId); assert.equal(current[0].name, objects.device.name);
      affected(await client.call('delete_device', objectArgs('device')), 'device', objects.device.name, objectId); deleted('device');
      assert.equal(leaves(await client.call('list_devices', { processId }), 'device').length, 0);
      await verifyMissing('get_device', objectId);
      const status = await client.call('get_status', { processId }); client.assertTarget(status); client.report.after = status;
    });
}

async function main() {
  const options = optionsFrom(process.argv.slice(2));
  if (options.help) {
    console.log('node tests/mcp-live.cjs --process-id <user-connected Demo PID> [--endpoint http://127.0.0.1:5000/mcp] [--timeout-ms 120000]');
    console.log('Fixed target: ' + PROJECT_PATH);
    console.log('WRITES and compiles through the existing full-access MCP server. Demo must start empty.');
    console.log('Successful runs delete their fixtures and Device. Failed runs stop and retain evidence/leftovers. No save, attach, transfer, retry or resume.');
    return;
  }
  const client = new LiveClient(options);
  let failure;
  try { await workflow(client); }
  catch (error) { failure = error; }
  const report = client.finish(failure);
  console.log(report.status.toUpperCase() + ': ' + (TOOLS.length - report.unverifiedTools.length) + '/' + TOOLS.length + ' tools verified.');
  console.log('Report: ' + client.output + '/report.json');
  if (failure) console.error(failure.message);
  if (client.persistenceFailed) console.error('Evidence persistence failed; inspect the retained report.json before further native actions.');
  process.exitCode = report.status === 'passed' ? 0 : 1;
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
