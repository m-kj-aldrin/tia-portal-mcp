namespace TiaOpennessMcpServer.Operations;

// Independent of Siemens so strict attempts, metadata-only reads and fallback can be exercised offline.
internal static class BlockSourceReader
{
    public static string[] Formats(string requested, string? language, bool dataBlock, bool udt = false) => requested != "best" ? new[] { requested } :
        udt ? new[] { "external-source", "simatic-sd", "simatic-ml" } :
        dataBlock || language is "SCL" or "STL" ? new[] { "external-source", "simatic-ml" } :
        language == "LAD" ? new[] { "simatic-sd", "simatic-ml" } : new[] { "simatic-ml" };

    public static void Read(BlockRead result, BlockReadRequest request, string? language, bool dataBlock,
        Func<string, BlockSource> export, Action validate, Func<Exception, string> origin, bool udt = false)
    {
        if (!request.IncludeSource) return;
        var failures = new List<DiscoveryError>();
        foreach (var format in Formats(request.SourceFormat, language, dataBlock, udt))
        {
            validate();
            try
            {
                var source = export(format);
                validate();
                if (source.Documents.Count == 0 || source.Documents.Any(document => string.IsNullOrWhiteSpace(document.Content)))
                    throw new InvalidOperationException("The export returned no usable source content.");
                result.Source = source;
                result.Attempts.Add(new SourceAttempt { Format = format, State = "success" });
                return; // Earlier attempts remain in the dashboard log, not successful result errors.
            }
            catch (ConnectionFault) { throw; }
            catch (Exception ex)
            {
                validate(); // Ordinary export failure must never conceal context loss.
                var errors = ex is SourceExportFault fault ? fault.Errors : new List<DiscoveryError>
                {
                    new() { Origin = origin(ex), Operation = "sourceExport", Format = format, Message = ex.Message }
                };
                failures.AddRange(errors);
                result.Attempts.Add(new SourceAttempt { Format = format, State = "failed", Errors = errors });
            }
        }
        result.Errors.AddRange(failures);
    }
}
