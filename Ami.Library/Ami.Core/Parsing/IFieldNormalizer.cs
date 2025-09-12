namespace Ami.Core.Parsing;

public interface IFieldNormalizer
{
    (string? Group, string? Field, string? Key) DeriveKey(
        string elementName, IReadOnlyDictionary<string, string> fieldAttrs, string? currentObjectId = null);
}