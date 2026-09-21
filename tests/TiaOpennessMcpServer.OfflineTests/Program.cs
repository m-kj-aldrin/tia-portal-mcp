var tests = ConnectionPrototypeTests.Cases().Concat(DiscoveryTests.Cases()).Concat(BlockInventoryTests.Cases()).Concat(BlockReadTests.Cases()).Concat(UdtTests.Cases()).Concat(TagTableTests.Cases()).ToArray();
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
