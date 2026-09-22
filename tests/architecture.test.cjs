// Source-boundary checks do not load Siemens or start an HTTP listener.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');
const sourceRoot = 'src/TiaOpennessMcpServer/';
// Discover production code by content so protocol coverage follows the real boundary.
function productionFiles(directory) {
  return fs.readdirSync(path.join(root, directory), { withFileTypes: true }).flatMap(entry => {
    const file = path.posix.join(directory, entry.name);
    if (entry.isDirectory()) return ['bin', 'obj'].includes(entry.name) ? [] : productionFiles(file);
    return entry.name.endsWith('.cs') ? [{ file, code: read(file) }] : [];
  });
}
const production = productionFiles(sourceRoot);
const productionCode = production.map(source => source.code).join('\n');
const codeOnly = code => code.replace(/"(?:\\.|[^"\\])*"|\/\/[^\r\n]*|\/\*[\s\S]*?\*\//g, '');
const names = ['list_tia_processes', 'get_status', 'list_devices', 'get_device', 'list_blocks',
  'get_block', 'list_udts', 'get_udt', 'list_tag_tables', 'get_tag_table', 'get_cross_references', 'write_blocks', 'write_udts', 'create_tag_table', 'create_tag', 'create_user_constant', 'set_tag_entry_attribute', 'delete_tag_entry', 'import_tag_tables'];

test('publication exposes nineteen tools with an explicit read-only profile and no V1 dispatch', () => {
  const program = productionCode;
  assert.deepEqual([...program.matchAll(/McpT\("([^"]+)"/g)].map(match => match[1]), names);
  assert.doesNotMatch(program, /prototype-mode|DISABLED during|V1BridgeService|ConnectV1Async|case "connect_to_tia_portal"|TIA_MCP_CONNECTION_PROTOTYPE/);
  assert.doesNotMatch(program, /\/api\/(?:project|devices|connect|analyze)(?:["/])/);
  const boundarySource = production.find(source => source.code.includes('internal sealed class McpBoundary'));
  assert.ok(boundarySource, 'Production MCP boundary is missing');
  const boundary = boundarySource.code.slice(boundarySource.code.indexOf('internal sealed class McpBoundary'));
  assert.doesNotMatch(boundary, /ConnectAsync|DisconnectAsync|Attach\(|RunAsync|Task.Run/);
  const harness = read('tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj').replaceAll('\\', '/');
  assert.ok(harness.includes('../../' + boundarySource.file), 'Harness must compile the actual production MCP boundary');
  assert.doesNotMatch(harness, /MCP_CONTRACT_TEST|TiaOpennessMcpServer\/Program\.cs/);
  assert.doesNotMatch(program, /MCP_CONTRACT_TEST/);
});

test('engineering operations and shared services remain independent of protocol, dashboard and native adapters', () => {
  const core = production.filter(source => /\/(?:Operations|Services|Diagnostics)\//.test(source.file));
  assert.ok(core.length > 0, 'Managed engineering code is missing');
  for (const source of core) {
    assert.doesNotMatch(codeOnly(source.code), /TiaOpennessMcpServer\.(?:Mcp|Dashboard|Openness)\b|Siemens\.Engineering\b|\b(?:McpBoundary|DashboardHistory|DashboardToolForms|OpennessConnectionBackend)\b/, source.file);
  }
  for (const source of production.filter(source => /\/Operations\//.test(source.file))) {
    assert.doesNotMatch(source.code, /TiaOpennessMcpServer\.(?:Services|Host)\b/, source.file);
  }
  for (const source of production.filter(source => /\/Openness\//.test(source.file))) {
    assert.doesNotMatch(source.code, /TiaOpennessMcpServer\.(?:Mcp|Dashboard|Host)\b|\bDashboardHistory\b/, source.file);
  }
  assert.doesNotMatch(productionCode, /TiaOpennessMcpServer\.Prototype\b/);
});

test('one composed host owns the listener, STA worker and connection registry', () => {
  assert.equal([...productionCode.matchAll(/new HttpListener\s*\(|HttpListener\s+\w+\s*=\s*new\s*\(/g)].length, 1);
  assert.equal([...productionCode.matchAll(/new StaTaskScheduler\s*\(/g)].length, 1);
  assert.equal([...productionCode.matchAll(/new ConnectionRegistry\s*\(/g)].length, 1);
  const program = read(sourceRoot + 'Program.cs');
  assert.doesNotMatch(program, /McpT\(|class McpBoundary|HttpListenerContext|tools\/call|\/api\//);
  const definitions = production.filter(source => /McpT\("/.test(source.code));
  assert.equal(definitions.length, 1, 'Tool definitions must have one authoritative source');
  assert.ok(definitions[0].file.includes('/Mcp/'), 'MCP definitions belong to the MCP boundary');
  const mcp = production.filter(source => source.file.includes('/Mcp/')).map(source => source.code).join('\n');
  assert.doesNotMatch(mcp, /Siemens\.Engineering\b|TiaOpennessMcpServer\.(?:Dashboard|Openness)\b/);
});
test('active projects have no dependency on reference or retired V1 code', () => {
  for (const file of [sourceRoot + 'TiaOpennessMcpServer.csproj',
    'tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj']) {
    assert.doesNotMatch(read(file), /reference[\\/]|V1BridgeService|V1Contracts/);
  }
  assert.doesNotMatch(productionCode, /\b(?:V1BridgeService|V1Contracts|ConnectV1Async)\b/);
  assert.doesNotMatch(read('.vscode/tasks.json'), /\/api\/|Compile Block|Save TIA/);
});

test('native block adapter resolves CPU directly and does not export or visit other inventories', () => {
  const source = read(sourceRoot + 'Openness/OpennessBlockReader.cs');
  assert.match(source, /identifiers\.Find\(plcObjectId\)/);
  assert.match(source, /target is DeviceItem cpu/);
  assert.match(source, /cpu\.GetService<SoftwareContainer>\(\)\?\.Software is PlcSoftware/);
  assert.doesNotMatch(source, /GenerateSource|\.Export\(|ExportAsDocuments|TypeGroup|TagTableGroup|ExternalSourceGroup|OrderBy|\.Sort\(/);
  const backend = read(sourceRoot + 'Openness/OpennessConnectionBackend.cs').replace(/\/\/[^\n]*/g, '');
  assert.match(backend, /void Detach\(\) => _portal\.Dispose\(\)/);
  assert.doesNotMatch(backend, /\.Save\(|\.Close\(|process\.Dispose\(/i);
  assert.match(read(sourceRoot + 'Services/EngineeringService.cs'),
    /ListBlocksAsync[\s\S]*?var ticket = _registry\.Capture\(processId\);[\s\S]*?Enqueue\(\(\) => _registry\.ListBlocks\(ticket, plcObjectId\)/);
});

test('block detail uses direct lookup and one bulk attribute read, with no import/save/compile', () => {
  const source = read(sourceRoot + 'Openness/OpennessBlockDetailReader.cs');
  assert.match(source, /identifiers\.Find\(request.ObjectId\)/);
  assert.match(source, /target is PlcBlock block/);
  assert.equal([...source.matchAll(/block.GetAttributes\(/g)].length, 1);
  assert.doesNotMatch(source, /block\.(Name|ProgrammingLanguage|Number|HeaderAuthor|HeaderName|IsKnowHowProtected)\b/);
  assert.doesNotMatch(source, /\.Import\(|\.Save\(|\.Compile\(|\.CreateFromFile\(|\.GenerateBlocksFromSource\(/);
  const exporter = read(sourceRoot + 'Openness/OpennessSourceExporter.cs');
  assert.match(exporter, /state != DocumentResultState.Success/);
  assert.match(exporter, /GenerateOptions.WithDependencies : GenerateOptions.None/);
});


test('UDT native adapters preserve type-only traversal and use the guarded shared exporter', () => {
  const inventory = read(sourceRoot + 'Openness/OpennessUdtReader.cs');
  assert.match(inventory, /identifiers\.Find\(plcObjectId\)/);
  assert.match(inventory, /target is DeviceItem cpu/);
  assert.doesNotMatch(inventory, /GenerateSource|\.Export\(|ExportAsDocuments|BlockGroup|TagTableGroup|OrderBy|\.Sort\(/);
  const detail = read(sourceRoot + 'Openness/OpennessUdtDetailReader.cs');
  assert.match(detail, /identifiers\.Find\(request.ObjectId\)/);
  assert.match(detail, /target is PlcType type/);
  assert.equal([...detail.matchAll(/type.GetAttributes\(/g)].length, 1);
  assert.doesNotMatch(detail, /type\.(Name|Namespace|IsConsistent|IsKnowHowProtected)\b/);
  assert.match(detail, /OpennessSourceExporter.Export\(type, \".udt\"/);
  const exporter = read(sourceRoot + 'Openness/OpennessSourceExporter.cs');
  assert.doesNotMatch(detail + exporter, /\.Import\(|\.Save\(|\.Compile\(|\.GenerateBlocksFromSource\(/);
  const service = read(sourceRoot + 'Services/EngineeringService.cs');
  assert.match(service, /ListUdtsAsync[\s\S]*?var ticket = _registry\.Capture\(processId\);[\s\S]*?Enqueue\(\(\) => _registry\.ListUdts\(ticket, plcObjectId\)/);
  assert.match(service, /ReadUdtAsync[\s\S]*?var ticket = _registry\.Capture\(request.ProcessId\);[\s\S]*?Enqueue\(\(\) => _registry\.ReadUdt\(ticket, request\)/);
});


test('tag table adapters use native compositions and direct IDs without export or unrelated inventories', () => {
  const inventory = read(sourceRoot + 'Openness/OpennessTagTableReader.cs');
  assert.match(inventory, /identifiers\.Find\(plcObjectId\)/);
  assert.match(inventory, /target is DeviceItem cpu/);
  assert.doesNotMatch(inventory, /BlockGroup|TypeGroup|table\.Tags|UserConstants|SystemConstants|OrderBy|\.Sort\(/);
  const detail = read(sourceRoot + 'Openness/OpennessTagTableDetailReader.cs');
  assert.match(detail, /identifiers\.Find\(request.ObjectId\)/);
  assert.match(detail, /target is PlcTagTable table/);
  assert.equal([...detail.matchAll(/table.GetAttributes\(/g)].length, 1);
  assert.match(detail, /identifiers.GetIdentifier\(entry\)/);
  assert.match(detail, /table.Tags.Select/);
  assert.match(detail, /table.UserConstants.Select/);
  assert.match(detail, /table.SystemConstants.Select/);
  assert.doesNotMatch(detail, /table\.(Name|IsDefault|ModifiedTimeStamp)\b/);
  assert.doesNotMatch(detail + inventory, /GenerateSource|ExportAsDocuments|\.Export\(|\.Import\(|\.Save\(|\.Compile\(|XDocument|XmlDocument/);
  const service = read(sourceRoot + 'Services/EngineeringService.cs');
  assert.match(service, /ListTagTablesAsync[\s\S]*?var ticket = _registry\.Capture\(processId\);[\s\S]*?Enqueue\(\(\) => _registry\.ListTagTables\(ticket, plcObjectId\)/);
  assert.match(service, /ReadTagTableAsync[\s\S]*?var ticket = _registry\.Capture\(request.ProcessId\);[\s\S]*?Enqueue\(\(\) => _registry\.ReadTagTable\(ticket, request\)/);
});


test('cross-references resolve native service directly with no type allowlist, inventory, export or compile', () => {
  const source = read(sourceRoot + 'Openness/OpennessCrossReferenceReader.cs');
  assert.match(source, /identifiers.Find\(request.ObjectId\)/);
  assert.match(source, /IEngineeringServiceProvider\)\?\.GetService<CrossReferenceService>\(\)/);
  assert.match(source, /service.GetCrossReferences\(CrossReferenceFilter.AllObjects\)/);
  assert.match(source, /Identifier\(source.UnderlyingObject\)/);
  assert.match(source, /Identifier\(reference.UnderlyingObject\)/);
  assert.match(source, /Identifier\(location.ReferencedAs\)/);
  assert.match(source, /underlying is IEngineeringObject engineering/);
  assert.doesNotMatch(source, /PlcBlock|PlcTag|PlcType|BlockGroup|TypeGroup|TagTableGroup|\.Compile\(|\.Export\(|GetAttributes|XDocument|XmlDocument|OrderBy/);
  const service = read(sourceRoot + 'Services/EngineeringService.cs');
  assert.match(service, /ReadCrossReferencesAsync[\s\S]*?var ticket = _registry\.Capture\(request.ProcessId\);[\s\S]*?Enqueue\(\(\) => _registry\.ReadCrossReferences\(ticket, request\)/);
});

test('writes share the guarded MCP boundary and never save or compile', () => {
  const program = productionCode;
  const native = read(sourceRoot + 'Openness/OpennessWrites.cs');
  const service = read(sourceRoot + 'Services/EngineeringService.cs');
  assert.doesNotMatch(program + native + service, /WriteProbe|write-probe|notArmed|notProbeObject|typedImportUnavailable/);
  assert.match(program,/ToolDefs\(_operations.WriteToolsAvailable\)/);
  assert.match(program,/WriteAsync\(WriteRequest.Parse\(operation, args\)\)/);
  assert.match(service,/if \(!WriteToolsAvailable\)/);
  assert.match(native,/GenerateBlocksFromSource/);
  assert.match(native,/ImportFromDocuments/);
  assert.match(native,/ImportOptions.Override/);
  assert.match(native,/ImportDocumentOptions.Override/);
  assert.match(native,/GenerateBlockOption.None/);
  assert.match(native,/table.Tags.Create/);
  assert.match(native,/table.UserConstants.Create/);
  assert.match(native,/target is PlcSystemConstant/);
  assert.doesNotMatch(native,/\.Save\(|\.Compile\(|\.Close\(|\.Export\(|Substitute|Replace\(/);
  assert.match(service,/WriteAsync[\s\S]*?var ticket = _registry.Capture\(request.ProcessId\);[\s\S]*?Enqueue\(\(\) => _registry.Write\(ticket, request\)/);
  assert.match(read(sourceRoot + 'Dashboard/wwwroot/index.html'),/id="mode-writes"/);
  assert.doesNotMatch(read(sourceRoot + 'Dashboard/wwwroot/index.html'),/write-probe|probeBag|Arm write/);
  assert.doesNotMatch(read(sourceRoot + 'Openness/OpennessBlockDetailReader.cs') + read(sourceRoot + 'Openness/OpennessSourceExporter.cs'), /\.GenerateBlocksFromSource\(/);
});

test('dashboard history is server-owned and does not add an MCP tool or reconnect by path', () => {
  const program = productionCode;
  const service = read(sourceRoot + 'Dashboard/DashboardService.cs');
  const history = read(sourceRoot + 'Dashboard/DashboardHistory.cs');
  const script = read(sourceRoot + 'Dashboard/wwwroot/dashboard.js');
  assert.deepEqual([...program.matchAll(/McpT\("([^"]+)"/g)].map(match => match[1]).length, 19);
  assert.match(program, /X-Tia-Dashboard"\] == "1" \? "dashboard" : "mcp"/);
  assert.match(read(sourceRoot + 'Services/EngineeringService.cs'), /"sourceExport" or "invalidated" or "cleanupFailed"/);
  assert.match(service, /DiagnosticPublished \+= _history\.ImportDiagnostic/);
  assert.match(history, /Canonical/);
  assert.match(history, /MaxLogEntries = 400/);
  assert.match(history, /MaxHistoricalTabs = 24/);
  assert.match(script, /tools\/call|method: 'tools\/call'|method:'tools\/call'/);
  assert.doesNotMatch(history, /Attach\(|TiaPortalProcess\.Dispose|OpenProject/);
  const backend = read(sourceRoot + 'Openness/OpennessConnectionBackend.cs');
  assert.match(backend, /new TiaPortal\(TiaPortalMode\.WithUserInterface\)/);
  assert.match(backend, /Projects\.Open\(/);
  assert.match(backend, /public void Detach\(\) => _portal\.Dispose\(\)/);
  assert.match(backend, /GetCurrentProcess\(\)\.Dispose\(\)/);
  assert.doesNotMatch(backend, /OpenWithUpgrade|WithoutUserInterface|Project\.Close|Project\.Save/);
});
