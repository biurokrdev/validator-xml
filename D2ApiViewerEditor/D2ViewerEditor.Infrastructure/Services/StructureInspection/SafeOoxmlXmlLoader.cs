using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

/// <summary>
/// Ładowanie XML z niezaufanego pakietu: DTD odrzucone, brak resolvera zewnętrznego, brak encji
/// zewnętrznych, twardy limit znaków (XXE / entity expansion / zip bomb po dekompresji).
/// Whitespace jest zachowywany — podgląd ma pokazywać XML taki, jaki jest w pakiecie —
/// a informacja o linii pozwala przejść z elementu do jego miejsca w pełnym XML części.
/// </summary>
public sealed class SafeOoxmlXmlLoader
{
    private readonly StructureInspectionOptions _options;

    public SafeOoxmlXmlLoader(IOptions<StructureInspectionOptions> options)
    {
        _options = options.Value;
    }

    public XDocument Load(string xml) => Load(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

    /// <summary>Ten sam bezpieczny czytnik z jawnymi opcjami (np. bez zachowania whitespace — do porównań i formatowania wycinków).</summary>
    public XDocument Load(string xml, LoadOptions options)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = _options.MaxXmlCharacters,
            MaxCharactersFromEntities = 0
        };

        using var textReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(textReader, settings);

        return XDocument.Load(xmlReader, options);
    }
}
