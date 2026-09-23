using System.Globalization;

namespace TiaOpennessMcpServer.Operations;

// Native delegates are consumed on the shared STA and never appear in a response.
internal class CompileResultNode
{
    public Func<string?> State { get; set; } = () => null;
    public Func<int?> ErrorCount { get; set; } = () => null;
    public Func<int?> WarningCount { get; set; } = () => null;
    public Func<IEnumerable<CompileMessageNode>> Messages { get; set; } = () => Array.Empty<CompileMessageNode>();
}

internal sealed class CompileMessageNode : CompileResultNode
{
    public Func<string?> Path { get; set; } = () => null;
    public Func<DateTime?> DateTime { get; set; } = () => null;
    public Func<string?> Description { get; set; } = () => null;
}

internal sealed class CompileResultReader
{
    private readonly DiscoveryReadContext _read;
    private readonly Action _validate;

    public CompileResultReader(CompileResult result, Action validate)
    {
        _read = new DiscoveryReadContext(result.Errors, validate);
        _validate = validate;
    }

    public void Read(CompileResult result, CompileResultNode native)
    {
        result.State = Field(native.State, "state", null);
        result.ErrorCount = Field(native.ErrorCount, "errorCount", null);
        result.WarningCount = Field(native.WarningCount, "warningCount", null);
        result.Messages = Messages(native.Messages, "messages");
    }

    private T? Field<T>(Func<T> source, string operation, string? path) => _read.Read(() =>
    {
        _validate();
        var value = source();
        _validate();
        return value;
    }, operation, path);

    private List<CompileMessage>? Messages(Func<IEnumerable<CompileMessageNode>> source, string path)
    {
        var messages = _read.Collect(source, message => Message(message, path), path);
        _validate();
        return messages;
    }

    private CompileMessage Message(CompileMessageNode native, string location)
    {
        var result = new CompileMessage { Path = Field(native.Path, "path", location) };
        var path = result.Path ?? location;
        var timestamp = Field(native.DateTime, "dateTime", path);
        // Siemens documents UTC timestamps; Unspecified must not be shifted by the host time zone.
        result.DateTime = timestamp.HasValue ?
            (timestamp.Value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(timestamp.Value, DateTimeKind.Utc) : timestamp.Value.ToUniversalTime())
            .ToString("O", CultureInfo.InvariantCulture) : null;
        result.State = Field(native.State, "state", path);
        result.Description = Field(native.Description, "description", path);
        result.ErrorCount = Field(native.ErrorCount, "errorCount", path);
        result.WarningCount = Field(native.WarningCount, "warningCount", path);
        result.Messages = Messages(native.Messages, path + "/messages");
        return result;
    }
}
