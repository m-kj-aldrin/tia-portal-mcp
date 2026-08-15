namespace TiaOpennessMcpServer.Models;

public sealed record SclSourceContent
{
    public required string BlockName { get; init; }
    public required BlockType BlockType { get; init; }
    public required int BlockNumber { get; init; }
    public required ProgrammingLanguage Language { get; init; }
    public required string SourceFormat { get; init; }
    public required string GeneratedFileName { get; init; }
    public required string SourceCode { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
