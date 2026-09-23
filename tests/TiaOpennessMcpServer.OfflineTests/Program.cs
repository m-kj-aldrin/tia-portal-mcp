var tests = ConnectionRegistryTests.Cases().Concat(DiscoveryTests.Cases()).Concat(BlockInventoryTests.Cases()).Concat(BlockReadTests.Cases()).Concat(UdtTests.Cases()).Concat(TagTableTests.Cases()).Concat(TagTableExportTests.Cases()).Concat(CrossReferenceTests.Cases()).Concat(CompileTests.Cases()).Concat(McpContractTests.Cases()).Concat(RpcEnvelopeTests.Cases()).Concat(DashboardHistoryTests.Cases()).Concat(WriteTests.Cases()).Concat(ServiceIntegrationTests.Cases()).Concat(OriginPolicyTests.Cases()).ToArray();
var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} offline test groups passed.");
return failed == 0 ? 0 : 1;
