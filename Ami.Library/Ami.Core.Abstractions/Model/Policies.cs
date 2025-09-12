using System.Text;

namespace Ami.Core.Model;

public enum Visibility { Public, Private }

public static class VisibilityPolicy
{
public static bool CanReference(Visibility from, Visibility to) =>
from == Visibility.Public && to == Visibility.Public;
}

public static class PathId
{
public static string Canonical(string objectId, string fieldId) => $"{objectId}.{fieldId}";
}

public sealed class DiagnosticsBag
{
private readonly List<string> _messages = new();
public IReadOnlyList<string> Messages => _messages;
public bool HasMessages => _messages.Count > 0;
    
public void Add(string message) => _messages.Add(message);

public override string ToString()
{
    if (!HasMessages) return "No diagnostics.";
    var sb = new StringBuilder();
    foreach (var msg in _messages)
    {
        sb.AppendLine(msg);
    }
    return sb.ToString();
}

  

}