using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class SafeOoxmlXmlLoader
{
    private readonly StructureInspectionOptions _options;

    public SafeOoxmlXmlLoader(IOptions<StructureInspectionOptions> options)
    {
        _options = options.Value;
    }

    public XDocument Load(string xml) => Load(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

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
