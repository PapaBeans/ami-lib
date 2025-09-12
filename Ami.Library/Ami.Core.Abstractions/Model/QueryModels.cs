namespace Ami.Core.Model;

public record NodeQuery(
    string? Key = null,
    string? ObjectName = null,
    string? Contains = null,
    string? NodeType = null,
    bool? HasComparison = null,
    int Limit = 500);

public record ResolvedValue(string? ValueText, string? ValueXml, string SourceManuscriptId, long NodeId);

public record LineageHit(string ManuscriptId, int Depth, string? ValueText, string? ValueXml, long NodeId);