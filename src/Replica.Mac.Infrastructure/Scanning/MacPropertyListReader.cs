using System.Xml;
using System.Xml.Linq;

namespace Replica.Mac.Infrastructure.Scanning;

internal static class MacPropertyListReader
{
    private const long MaximumCharacters = 2 * 1024 * 1024;

    public static IReadOnlyDictionary<string, string> ReadStringDictionary(string path)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumCharacters,
            XmlResolver = null,
        };

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement? dictionary = document.Root?.Element("dict");
        if (dictionary is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        List<XElement> elements = dictionary.Elements().ToList();
        for (int index = 0; index + 1 < elements.Count; index += 2)
        {
            XElement key = elements[index];
            XElement value = elements[index + 1];
            if (key.Name.LocalName == "key" &&
                value.Name.LocalName is "string" or "integer" &&
                !string.IsNullOrWhiteSpace(key.Value))
            {
                values[key.Value] = value.Value;
            }
        }

        return values;
    }
}
