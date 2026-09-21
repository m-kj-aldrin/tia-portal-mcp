// Source-boundary checks do not load Siemens or start an HTTP listener.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const sourceRoot = 'src/TiaOpennessMcpServer/';
const names = ['list_tia_processes', 'get_status', 'list_devices', 'get_device', 'list_blocks',
  'get_block', 'list_udts', 'get_udt', 'list_tag_tables', 'get_tag_table', 'get_cross_references'];

test('publication exposes exactly eleven read-only tools and no V1 dispatch', () => {
  const program = read(sourceRoot + 'Program.cs');
  assert.deepEqual([...program.matchAll(/McpT\("([^"]+)"/g)].map(match => match[1]), names);
  assert.doesNotMatch(program, /prototype-mode|DISABLED during|V1BridgeService|TiaPortalService|ConnectV1Async|case "connect_to_tia_portal"|TIA_MCP_ACCESS|TIA_MCP_CONNECTION_PROTOTYPE/);
  assert.doesNotMatch(program, /\/api\/(?:project|devices|connect|analyze)(?:["/])/);
  const boundary = program.slice(program.indexOf('internal sealed class McpBoundary'));
  assert.doesNotMatch(boundary, /ConnectAsync|DisconnectAsync|Attach\(|RunAsync|Task.Run/);
  assert.match(read('tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj'), /Program.cs/);
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


test('tag table adapters use native compositions and direct IDs without export or unrelated inventories', () => {
  const inventory = read(sourceRoot + 'Prototype/OpennessTagTableReader.cs');
  assert.match(inventory, /identifiers\.Find\(plcObjectId\)/);
  assert.match(inventory, /target is DeviceItem cpu/);
  assert.doesNotMatch(inventory, /BlockGroup|TypeGroup|table\.Tags|UserConstants|SystemConstants|OrderBy|\.Sort\(/);
  const detail = read(sourceRoot + 'Prototype/OpennessTagTableDetailReader.cs');
  assert.match(detail, /identifiers\.Find\(request.ObjectId\)/);
  assert.match(detail, /target is PlcTagTable table/);
  assert.equal([...detail.matchAll(/table.GetAttributes\(/g)].length, 1);
  assert.match(detail, /identifiers.GetIdentifier\(entry\)/);
  assert.match(detail, /table.Tags.Select/);
  assert.match(detail, /table.UserConstants.Select/);
  assert.match(detail, /table.SystemConstants.Select/);
  assert.doesNotMatch(detail, /table\.(Name|IsDefault|ModifiedTimeStamp)\b/);
  assert.doesNotMatch(detail + inventory, /GenerateSource|ExportAsDocuments|\.Export\(|\.Import\(|\.Save\(|\.Compile\(|XDocument|XmlDocument/);
  const service = read(sourceRoot + 'Prototype/ConnectionPrototypeService.cs');
  assert.match(service, /ListTagTablesAsync[\s\S]*?var ticket = _registry\.Capture\(processId\);[\s\S]*?Enqueue\(\(\) => _registry\.ListTagTables\(ticket, plcObjectId\)/);
  assert.match(service, /ReadTagTableAsync[\s\S]*?var ticket = _registry\.Capture\(request.ProcessId\);[\s\S]*?Enqueue\(\(\) => _registry\.ReadTagTable\(ticket, request\)/);
});


test('cross-references resolve native service directly with no type allowlist, inventory, export or compile', () => {
  const source = read(sourceRoot + 'Prototype/OpennessCrossReferenceReader.cs');
  assert.match(source, /identifiers.Find\(request.ObjectId\)/);
  assert.match(source, /IEngineeringServiceProvider\)\?\.GetService<CrossReferenceService>\(\)/);
  assert.match(source, /service.GetCrossReferences\(CrossReferenceFilter.AllObjects\)/);
  assert.match(source, /Identifier\(source.UnderlyingObject\)/);
  assert.match(source, /Identifier\(reference.UnderlyingObject\)/);
  assert.match(source, /Identifier\(location.ReferencedAs\)/);
  assert.match(source, /underlying is IEngineeringObject engineering/);
  assert.doesNotMatch(source, /PlcBlock|PlcTag|PlcType|BlockGroup|TypeGroup|TagTableGroup|\.Compile\(|\.Export\(|GetAttributes|XDocument|XmlDocument|OrderBy/);
  const service = read(sourceRoot + 'Prototype/ConnectionPrototypeService.cs');
  assert.match(service, /ReadCrossReferencesAsync[\s\S]*?var ticket = _registry\.Capture\(request.ProcessId\);[\s\S]*?Enqueue\(\(\) => _registry\.ReadCrossReferences\(ticket, request\)/);
});
