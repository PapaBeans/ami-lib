using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using Ami.Core.Abstractions;
using Ami.Core.Model;

namespace Ami.Core.Parsing;

public static class ManuscriptParser
{
    public static async Task<(Manuscript, IAsyncEnumerable<NodeRecord>)> ParseAsync(
        string path,
        IReadOnlyList<IValueAnalyzer> analyzers,
        IFieldNormalizer normalizer,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists) throw new FileNotFoundException("Manuscript file not found.", path);

        var (header, sha256) = await ReadHeaderAndComputeHashAsync(path, ct);

        if (string.IsNullOrEmpty(header.Id))
        {
            throw new ManuscriptConfigurationException($"Manuscript is missing required 'manuscriptId' attribute.", path);
        }

        var manuscript = new Manuscript(
            header.Id,
            header.Name ?? Path.GetFileNameWithoutExtension(path),
            path,
            header.ParentId,
            Depth: 0, // Depth is calculated later by the IndexService
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            sha256,
            header.Version,
            header.Properties
        );

        var nodes = ParseNodesAsync(path, analyzers, normalizer, ct);
        return (manuscript, nodes);
    }

    private static async Task<(ManuscriptHeader Header, string Sha256)> ReadHeaderAndComputeHashAsync(string path, CancellationToken ct)
    {
        var fileStreamOptions = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        await using var stream = new FileStream(path, fileStreamOptions);
        using var sha256 = SHA256.Create();
        using var cryptoStream = new CryptoStream(stream, sha256, CryptoStreamMode.Read);

        var settings = new XmlReaderSettings { Async = true, ConformanceLevel = ConformanceLevel.Fragment };
        using var reader = XmlReader.Create(cryptoStream, settings);

        // Initialize with default values
        ManuscriptHeader header = new(null, null, null, null, new Dictionary<string, string>());

        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element && (reader.Name is "manuscript" or "properties"))
            {
                header = ReadHeaderAttributes(reader);
                break;
            }
        }

        await cryptoStream.CopyToAsync(Stream.Null, ct);
        var hash = Convert.ToHexString(sha256.Hash ?? []);

        return (header, hash);
    }

    private static async IAsyncEnumerable<NodeRecord> ParseNodesAsync(
        string path,
        IReadOnlyList<IValueAnalyzer> analyzers,
        IFieldNormalizer normalizer,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var settings = new XmlReaderSettings { Async = true, ConformanceLevel = ConformanceLevel.Fragment, IgnoreWhitespace = true };
        var fileStreamOptions = new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan };
        await using var stream = new FileStream(path, fileStreamOptions);
        using var reader = XmlReader.Create(stream, settings);

        string? currentObjectId = null;
        var lineInfo = reader as IXmlLineInfo;

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element) continue;

            var elementName = reader.Name;
            if (elementName.Equals("object", StringComparison.OrdinalIgnoreCase))
            {
                currentObjectId = reader.GetAttribute("id");
                var attrs = ReadAllAttributes(reader);
                yield return new NodeRecord(
                    NodeType: "object", Key: null, GroupName: null, FieldName: null, ObjectName: currentObjectId,
                    ValueText: null, ValueXml: null, ValueAttrsJson: "{}", AttributesJson: JsonSerializer.Serialize(attrs),
                    ValueAnalysisJson: null, Line: lineInfo?.LineNumber, Column: lineInfo?.LinePosition
                );
            }
            else if (elementName is "public" or "private")
            {
                var fieldAttrs = ReadAllAttributes(reader);
                var (group, field, key) = normalizer.DeriveKey(elementName, fieldAttrs, currentObjectId);

                string? valueXml = null;
                string? valueText = null;
                string valueAttrsJson = "{}";

                if (!reader.IsEmptyElement && await reader.ReadAsync() && reader.NodeType == XmlNodeType.Element && reader.Name.Equals("value", StringComparison.OrdinalIgnoreCase))
                {
                    valueXml = await reader.ReadOuterXmlAsync();
                    if (!string.IsNullOrEmpty(valueXml))
                    {
                        (valueText, valueAttrsJson) = ExtractValueTextAndAttrs(valueXml);
                    }
                }

                string? analysisJson = null;
                if (!string.IsNullOrEmpty(valueXml))
                {
                    foreach (var analyzer in analyzers)
                    {
                        if (analyzer.CanAnalyze(valueXml))
                        {
                            var result = analyzer.Analyze(valueXml);
                            analysisJson = JsonSerializer.Serialize(result);
                            break;
                        }
                    }
                }

                yield return new NodeRecord(
                    NodeType: elementName, Key: key, GroupName: group, FieldName: field, ObjectName: currentObjectId,
                    ValueText: valueText, ValueXml: valueXml, ValueAttrsJson: valueAttrsJson,
                    AttributesJson: JsonSerializer.Serialize(fieldAttrs), ValueAnalysisJson: analysisJson,
                    Line: lineInfo?.LineNumber, Column: lineInfo?.LinePosition
                );
            }
        }
    }

    private static (string? ValueText, string ValueAttrsJson) ExtractValueTextAndAttrs(string valueXml)
    {
        using var reader = new StringReader(valueXml);
        using var xml = XmlReader.Create(reader, new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });

        xml.Read();

        var valueAttrs = ReadAllAttributes(xml);
        var valueAttrsJson = JsonSerializer.Serialize(valueAttrs);

        if (valueAttrs.TryGetValue("value", out var textValue) && xml.IsEmptyElement)
        {
            return (textValue, valueAttrsJson);
        }

        if (xml.Read() && xml.NodeType == XmlNodeType.Text)
        {
            return (xml.Value, valueAttrsJson);
        }

        return (null, valueAttrsJson);
    }

    private static Dictionary<string, string> ReadAllAttributes(XmlReader reader)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reader.MoveToFirstAttribute())
        {
            do { dict[reader.Name] = reader.Value; }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
        return dict;
    }

    private static ManuscriptHeader ReadHeaderAttributes(XmlReader reader)
    {
        string? id = reader.GetAttribute("manuscriptId");
        string? name = reader.GetAttribute("name");
        string? parentId = reader.GetAttribute("inherited");
        string? version = reader.GetAttribute("version");
        var props = ReadAllAttributes(reader);
        return new ManuscriptHeader(id, name, parentId, version, props);
    }

    private record ManuscriptHeader(string? Id, string? Name, string? ParentId, string? Version, IReadOnlyDictionary<string, string> Properties);
}