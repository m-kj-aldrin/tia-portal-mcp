// Source-boundary checks do not load Siemens or start an HTTP listener.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const sourceRoot = 'src/TiaOpennessMcpServer/';
const names = ['connect_to_tia_portal', 'get_status', 'list_devices', 'list_plc_objects',
  'find_plc_objects', 'read_plc_object', 'get_tag_table_entries', 'get_cross_references'];

test('publication hold retains only eight explicitly disabled descriptors and no V1 dispatch', () => {
  const program = read(sourceRoot + 'Program.cs');
  const definitions = [...program.matchAll(/McpT\("([^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(definitions, names);
  assert.match(program, /Description = "DISABLED during rehaul transition/);
  assert.match(program, /known \? "prototype-mode" : "unknownTool"/);
  assert.doesNotMatch(program, /V1BridgeService|TiaPortalService|ConnectV1Async|case "connect_to_tia_portal"|TIA_MCP_ACCESS|TIA_MCP_CONNECTION_PROTOTYPE/);
  assert.doesNotMatch(program, /\/api\/(?:project|devices|connect|analyze)(?:["/])/);
});

test('active projects have no dependency on reference or retired V1 code', () => {
  for (const file of [sourceRoot + 'TiaOpennessMcpServer.csproj',
    'tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj']) {
    assert.doesNotMatch(read(file), /reference[\\/]|V1|Services[\\/]|Models[\\/]/);
  }
  for (const folder of ['Services', 'Models']) {
    const dir = path.join(root, sourceRoot, folder);
    assert.ok(!fs.existsSync(dir) || fs.readdirSync(dir).length === 0, 'Legacy source remains active: ' + folder);
  }
  assert.doesNotMatch(read('.vscode/tasks.json'), /\/api\/|Compile Block|Save TIA/);
});

test('native block adapter resolves CPU directly and does not export or visit other inventories', () => {
  const source = read(sourceRoot + 'Prototype/OpennessBlockReader.cs');
  assert.match(source, /identifiers\.Find\(plcObjectId\)/);
  assert.match(source, /target is DeviceItem cpu/);
  assert.match(source, /cpu\.GetService<SoftwareContainer>\(\)\?\.Software is PlcSoftware/);
  assert.doesNotMatch(source, /GenerateSource|\.Export\(|ExportAsDocuments|TypeGroup|TagTableGroup|ExternalSourceGroup|OrderBy|\.Sort\(/);
  const backend = read(sourceRoot + 'Prototype/OpennessConnectionBackend.cs').replace(/\/\/[^\n]*/g, '');
  assert.match(backend, /void Detach\(\) => _portal\.Dispose\(\)/);
  assert.doesNotMatch(backend, /\.Save\(|\.Close\(|process\.Dispose\(/i);
  assert.match(read(sourceRoot + 'Prototype/ConnectionPrototypeService.cs'),
    /ListBlocksAsync[\s\S]*?var ticket = _registry\.Capture\(processId\);[\s\S]*?Enqueue\(\(\) => _registry\.ListBlocks\(ticket, plcObjectId\)/);
});

test('block detail uses direct lookup and one bulk attribute read, with no import/save/compile', () => {
  const source = read(sourceRoot + 'Prototype/OpennessBlockDetailReader.cs');
  assert.match(source, /identifiers\.Find\(request.ObjectId\)/);
  assert.match(source, /target is PlcBlock block/);
  assert.equal([...source.matchAll(/block.GetAttributes\(/g)].length, 1);
  assert.doesNotMatch(source, /block\.(Name|ProgrammingLanguage|Number|HeaderAuthor|HeaderName|IsKnowHowProtected)\b/);
  assert.doesNotMatch(source, /\.Import\(|\.Save\(|\.Compile\(|\.CreateFromFile\(|\.GenerateBlocksFromSource\(/);
  const exporter = read(sourceRoot + 'Prototype/OpennessSourceExporter.cs');
  assert.match(exporter, /state != DocumentResultState.Success/);
  assert.match(exporter, /GenerateOptions.WithDependencies : GenerateOptions.None/);
});


test('UDT native adapters preserve type-only traversal and use the guarded shared exporter', () => {
  const inventory = read(sourceRoot + 'Prototype/OpennessUdtReader.cs');
  assert.match(inventory, /identifiers\.Find\(plcObjectId\)/);
  assert.match(inventory, /target is DeviceItem cpu/);
  assert.doesNotMatch(inventory, /GenerateSource|\.Export\(|ExportAsDocuments|BlockGroup|TagTableGroup|OrderBy|\.Sort\(/);
  const detail = read(sourceRoot + 'Prototype/OpennessUdtDetailReader.cs');
  assert.match(detail, /identifiers\.Find\(request.ObjectId\)/);
  assert.match(detail, /target is PlcType type/);
  assert.equal([...detail.matchAll(/type.GetAttributes\(/g)].length, 1);
  assert.doesNotMatch(detail, /type\.(Name|Namespace|IsConsistent|IsKnowHowProtected)\b/);
  assert.match(detail, /OpennessSourceExporter.Export\(type, \".udt\"/);
  const exporter = read(sourceRoot + 'Prototype/OpennessSourceExporter.cs');
  assert.doesNotMatch(detail + exporter, /\.Import\(|\.Save\(|\.Compile\(|\.GenerateBlocksFromSource\(/);
  const service = read(sourceRoot + 'Prototype/ConnectionPrototypeService.cs');
  assert.match(service, /ListUdtsAsync[\s\S]*?var ticket = _registry\.Capture\(processId\);[\s\S]*?Enqueue\(\(\) => _registry\.ListUdts\(ticket, plcObjectId\)/);
  assert.match(service, /ReadUdtAsync[\s\S]*?var ticket = _registry\.Capture\(request.ProcessId\);[\s\S]*?Enqueue\(\(\) => _registry\.ReadUdt\(ticket, request\)/);
});
