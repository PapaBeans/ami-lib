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
        string? field = fieldAttrs.GetValueOrDefault("id")?.Split('.').Last(); //@id is already {Group}.{Field} so we only want the last element

        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(field))
        {
            return (group, field, null);
        }

        return (group, field, PathId.Canonical(group, field));
    }
}