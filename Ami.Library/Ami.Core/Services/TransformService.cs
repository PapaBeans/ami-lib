using System.Xml;
using Ami.Core.Abstractions;
using Ami.Core.Parsing;
using Microsoft.Extensions.Logging;

namespace Ami.Core.Services;

public class TransformService : IAmiTransformService
{
    private readonly IFieldNormalizer _normalizer;
    private readonly ILogger<TransformService> _logger;

    public TransformService(IFieldNormalizer normalizer, ILogger<TransformService> logger)
    {
        _normalizer = normalizer;
        _logger = logger;
    }

    public Task<int> SetValueXmlAsync(string manuscriptPath, string key, string newValueXml, CancellationToken ct = default)
    {
        return RewriteXmlAsync(manuscriptPath, key, (writer, reader) =>
        {
            writer.WriteRaw(newValueXml);
            reader.Skip();
        }, ct);
    }

    public Task<int> SetValueAttributesAsync(string manuscriptPath, string key, IReadOnlyDictionary<string, string> newAttributes, CancellationToken ct = default)
    {
        return RewriteXmlAsync(manuscriptPath, key, (writer, reader) =>
        {
            writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
            foreach (var attr in newAttributes)
            {
                writer.WriteAttributeString(attr.Key, attr.Value);
            }

            if (reader.Read())
            {
                while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)
                {
                    writer.WriteNode(reader, true);
                }
            }
            writer.WriteEndElement();
            reader.ReadEndElement();
        }, ct);
    }

    private async Task<int> RewriteXmlAsync(string path, string key, Action<XmlWriter, XmlReader> valueRewriteAction, CancellationToken ct)
    {
        var tempPath = Path.GetTempFileName();
        var updates = 0;

        try
        {
            var readerSettings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Ignore };
            var writerSettings = new XmlWriterSettings { Async = true, ConformanceLevel = ConformanceLevel.Fragment, Indent = true, OmitXmlDeclaration = true };

            await using var readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var writeStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var reader = XmlReader.Create(readStream, readerSettings);
            await using var writer = XmlWriter.Create(writeStream, writerSettings);

            string? currentObjectId = null;

            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                
                if (reader.NodeType == XmlNodeType.Element && reader.Name.Equals("object", StringComparison.OrdinalIgnoreCase))
                {
                    currentObjectId = reader.GetAttribute("id");
                }

                if (reader.NodeType == XmlNodeType.Element && reader.Name is "public" or "private")
                {
                    var fieldAttrs = ReadAllAttributes(reader);
                    var (_, _, derivedKey) = _normalizer.DeriveKey(reader.Name, fieldAttrs, currentObjectId);
                    
                    if (derivedKey == key)
                    {
                        await writer.WriteNodeAsync(reader, false);
                        
                        if (await reader.ReadAsync() && reader.NodeType == XmlNodeType.Element && reader.Name.Equals("value", StringComparison.OrdinalIgnoreCase))
                        {
                            valueRewriteAction(writer, reader);
                            updates++;
                            continue;
                        }
                    }
                }
                
                await writer.WriteNodeAsync(reader, true);
            }
            
            await writer.FlushAsync();
            
            if (updates > 0)
            {
                _logger.LogInformation("Applying {UpdateCount} update(s) for key '{Key}' to file {Path}.", updates, key, path);
                File.Replace(tempPath, path, null);
            }
            else
            {
                _logger.LogWarning("No matching node found for key '{Key}' in file {Path}. No changes were made.", key, path);
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rewrite XML file {Path}. The temporary file can be found at {TempPath}", path, tempPath);
            File.Delete(tempPath); // Clean up on failure
            throw;
        }

        return updates;
    }

    private static IReadOnlyDictionary<string, string> ReadAllAttributes(XmlReader reader)
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
}