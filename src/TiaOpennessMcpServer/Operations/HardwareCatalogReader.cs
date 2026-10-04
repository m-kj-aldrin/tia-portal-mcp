namespace TiaOpennessMcpServer.Operations;

internal static class HardwareCatalogReader
{
    public static HardwareCatalogRead Read(HardwareCatalogRequest request, Action validate,
        Func<IEnumerable<HardwareCatalogNode>> source)
    {
        var result = new HardwareCatalogRead { Offset = request.Offset, Limit = request.Limit };
        var read = new DiscoveryReadContext(result.Errors, validate);
        var matches = 0;
        validate();
        try
        {
            var entries = source();
            validate(); // The native Find call is eager; validate its completed collection acquisition.
            using var iterator = entries.GetEnumerator();
            while (iterator.MoveNext())
            {
                try
                {
                    var entry = iterator.Current;
                    var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                    string? Field(string name)
                    {
                        if (!values.TryGetValue(name, out var value))
                        {
                            value = read.Read(() => entry.ReadField(name), "catalogue:" + name, null);
                            values.Add(name, value);
                        }
                        return value;
                    }
                    if (request.Filters.Any(filter => !HardwareCatalogRequest.Matches(filter.Key, Field(filter.Key), filter.Value))) continue;
                    var index = matches++;
                    if (index < request.Offset || result.Items.Count >= request.Limit) continue;
                    foreach (var field in HardwareCatalogRequest.Fields) Field(field);
                    result.Items.Add(values);
                }
                catch (ConnectionFault) { throw; }
                catch (Exception ex) { read.Failure(ex, "catalogue:entry", null); }
            }
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex) { read.Failure(ex, "catalogue:enumerate", null); }
        validate();
        // A failed field or enumeration can hide matches. Never report an exact total in that case.
        if (result.Complete)
        {
            result.TotalMatches = matches;
            result.HasMore = (long)request.Offset + result.Items.Count < matches;
        }
        return result;
    }
}
