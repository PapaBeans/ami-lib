using Ami.Core.Model;

namespace Ami.Core.Parsing;

public class DefaultFieldNormalizer : IFieldNormalizer
{
    public (string? Group, string? Field, string? Key) DeriveKey(
        string elementName, IReadOnlyDictionary<string, string> fieldAttrs, string? currentObjectId = null)
    {
        if (elementName is not "public" and not "private")
        {
            return (null, null, null);
        }

        string? group = currentObjectId;
        string? field = fieldAttrs.GetValueOrDefault("id");

        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(field))
        {
            return (group, field, null);
        }

        return (group, field, PathId.Canonical(group, field));
    }
}