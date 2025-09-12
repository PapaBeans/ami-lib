using System.Text;
using System.Text.Json;
using System.Xml;
using Ami.Core.Abstractions;

namespace Ami.Core.Parsing;

public class ComparisonValueAnalyzer : IValueAnalyzer
{
    public bool CanAnalyze(string valueXml) =>
        valueXml.Contains("<comparison", StringComparison.OrdinalIgnoreCase);

    public ValueAnalysisResult Analyze(string valueXml)
    {
        string? type = null;
        var operands = new List<string>(2);

        try
        {
            using var reader = new StringReader(valueXml);
            using var xml = XmlReader.Create(reader, new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element)
                {
                    if (xml.Name.Equals("comparison", StringComparison.OrdinalIgnoreCase))
                    {
                        type = xml.GetAttribute("type");
                    }
                    else if (xml.Name.Equals("compare", StringComparison.OrdinalIgnoreCase) && operands.Count < 2)
                    {
                        string? idref = xml.GetAttribute("idref");
                        string? value = xml.GetAttribute("value");
                        operands.Add(idref ?? value ?? "?");
                    }
                }
            }
        }
        catch (XmlException)
        {
            return new ValueAnalysisResult("Comparison", "Malformed XML", "{}");
        }

        var summary = new StringBuilder(type ?? "comparison");
        if (operands.Count > 0) summary.Append($" {operands[0]}");
        if (operands.Count > 1) summary.Append($" {operands[1]}");

        var details = new { type, left = operands.ElementAtOrDefault(0), right = operands.ElementAtOrDefault(1) };
        return new ValueAnalysisResult("Comparison", summary.ToString(), JsonSerializer.Serialize(details));
    }
}