namespace Ami.Core.Model;

public record Manuscript(
    string Id,
    string Name,
    string Path,
    string? ParentId,
    int Depth,
    long SizeBytes,
    DateTime MtimeUtc,
    string Sha256,
    string? Version = null,
    IReadOnlyDictionary<string, string>? Properties = null);

public record Node(
    long Id,
    string ManuscriptId,
    string NodeType,
    string? Key,
    string? GroupName,
    string? FieldName,
    string? ObjectName,
    string? ValueText,
    string? ValueXml,
    string? ValueAttrs,
    string? Attributes,
    string? ValueAnalysis,
    string? XPath,
    int? Line,
    int? Column);