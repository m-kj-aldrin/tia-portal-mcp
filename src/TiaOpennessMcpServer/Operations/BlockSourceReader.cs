namespace TiaOpennessMcpServer.Operations;

// Shared single-format source export and metadata-only reads.
internal static class BlockSourceReader
{
    public static void Read(BlockRead result, BlockReadRequest request,
        Func<string, BlockSource> export, Action validate, Func<Exception, string> origin)
    {
        if (!request.IncludeSource) return;
        var format = request.SourceFormat!; // Required by BlockReadRequest whenever source is requested.
        validate();
        try
        {
            var source = export(format);
            validate();
            if (source.Documents.Count == 0 || source.Documents.Any(document => string.IsNullOrWhiteSpace(document.Content)))
                throw new InvalidOperationException("The export returned no usable source content.");
            result.Source = source;
            result.Attempts.Add(new SourceAttempt { Format = format, State = "success" });
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            validate(); // Ordinary export failure must never conceal context loss.
            var errors = ex is SourceExportFault fault ? fault.Errors : new List<DiscoveryError>
            {
                new() { Origin = origin(ex), Operation = "sourceExport", Format = format, Message = ex.Message }
            };
            result.Errors.AddRange(errors);
            result.Attempts.Add(new SourceAttempt { Format = format, State = "failed", Errors = errors });
        }
    }
}
