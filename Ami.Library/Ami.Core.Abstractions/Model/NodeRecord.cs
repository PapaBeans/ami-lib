namespace Ami.Core.Model;

/// <summary>
/// A transient record representing a node during the parsing phase, before it is persisted.
/// </summary>
public record NodeRecord(
    string NodeType,
    string? Key,
    string? GroupName,
    string? FieldName,
    string? ObjectName,
    string? ValueText,
    string? ValueXml,
    string ValueAttrsJson,
    string AttributesJson,
    string? ValueAnalysisJson,
    int? Line,
    int? Column);