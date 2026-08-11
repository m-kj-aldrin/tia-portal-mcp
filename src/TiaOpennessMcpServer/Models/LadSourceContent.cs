namespace TiaOpennessMcpServer.Models;

public sealed record SimaticSdDocumentContent
{
    public required string FileName { get; init; }
    public required string Content  { get; init; }
}

public sealed record LadSourceContent
{
    public required string BlockName { get; init; }
    public required BlockType BlockType { get; init; }
    public required int BlockNumber { get; init; }
    public required ProgrammingLanguage Language { get; init; }
    public required string SourceFormat { get; init; }
    public required string ExportState { get; init; }
    public required IReadOnlyList<string> GeneratedFileNames { get; init; }
    public required SimaticSdDocumentContent SourceDocument { get; init; }
    public required IReadOnlyList<SimaticSdDocumentContent> ResourceDocuments { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
