namespace Ami.Core.Abstractions;

public interface IValueAnalyzer
{
    bool CanAnalyze(string valueXml);
    ValueAnalysisResult Analyze(string valueXml);
}

public record ValueAnalysisResult(string Kind, string Summary, string DetailsJson);