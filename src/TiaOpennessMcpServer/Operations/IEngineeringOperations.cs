namespace TiaOpennessMcpServer.Operations;

// Managed engineering operations. Protocol adapters consume this interface.
internal interface IEngineeringOperations
{
    bool WriteToolsAvailable { get; }
    Task<WriteResult> WriteAsync(WriteRequest request);
    object BridgeStatus();
    Task<ProcessDiscovery> DiscoverAsync();
    Task<ProcessStatus> ReadStatusAsync(int processId);
    Task<DeviceInventory> ListDevicesAsync(int processId);
    Task<DeviceRead> ReadDeviceAsync(int processId, string objectId, bool includePath);
    Task<BlockInventory> ListBlocksAsync(int processId, string plcObjectId);
    Task<BlockRead> ReadBlockAsync(BlockReadRequest request);
    Task<BlockInventory> ListUdtsAsync(int processId, string plcObjectId);
    Task<BlockRead> ReadUdtAsync(BlockReadRequest request);
    Task<BlockInventory> ListTagTablesAsync(int processId, string plcObjectId);
    Task<TagTableRead> ReadTagTableAsync(TagTableReadRequest request);
    Task<BlockInventory> ListTechnologyObjectsAsync(int processId, string plcObjectId);
    Task<TechnologyObjectRead> ReadTechnologyObjectAsync(TechnologyObjectReadRequest request);
    Task<CrossReferenceRead> ReadCrossReferencesAsync(CrossReferenceRequest request);
    Task<TagTableExportResult> ExportTagTableAsync(ExportTagTableRequest request);
    Task<CompileResult> CompileAsync(CompileRequest request);
}
