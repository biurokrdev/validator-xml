using System.Xml;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed class XmlPartsHealthCheck
{
    private static readonly XNamespace Mc = OoxmlNamespaces.MarkupCompatibility;

    private static readonly (string ContentTypeFragment, string Root, bool Wordprocessing)[] ExpectedRoots =
    [
        ("wordprocessingml.document.main+xml", "document", true),
        ("wordprocessingml.template.main+xml", "document", true),
        ("wordprocessingml.document.macroEnabled.main+xml", "document", true),
        ("wordprocessingml.template.macroEnabled.main+xml", "document", true),
        ("wordprocessingml.styles+xml", "styles", true),
        ("wordprocessingml.numbering+xml", "numbering", true),
        ("wordprocessingml.settings+xml", "settings", true),
        ("wordprocessingml.fontTable+xml", "fonts", true),
        ("wordprocessingml.footnotes+xml", "footnotes", true),
        ("wordprocessingml.endnotes+xml", "endnotes", true),
        ("wordprocessingml.header+xml", "hdr", true),
        ("wordprocessingml.footer+xml", "ftr", true),
        ("wordprocessingml.comments+xml", "comments", true),
        ("wordprocessingml.webSettings+xml", "webSettings", true),
        ("wordprocessingml.document.glossary+xml", "glossaryDocument", true),
        ("theme+xml", "theme", false),
        ("core-properties+xml", "coreProperties", false),
        ("extended-properties+xml", "Properties", false)
    ];

    private readonly SafeOoxmlXmlLoader _xmlLoader;

    public XmlPartsHealthCheck(SafeOoxmlXmlLoader xmlLoader)
    {
        _xmlLoader = xmlLoader;
    }

    public Dictionary<string, XDocument> Run(
        HealthPackageContext context,
        HealthFindingCollector findings,
        CancellationToken cancellationToken)
    {
        var parsed = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in context.Raw.XmlParts.Values.OrderBy(part => context.IsMainPart(part.Path) ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (part.Path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ||
                part.Path.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var document = Parse(context, part, findings);

            if (document?.Root is null)
            {
                continue;
            }

            parsed[part.Path] = document;
            CheckRoot(context, part.Path, document.Root, findings);
            CheckCompatibilityPrefixes(part.Path, document.Root, findings);
        }

        return parsed;
    }

    private XDocument? Parse(HealthPackageContext context, RawPackagePart part, HealthFindingCollector findings)
    {
        var critical = context.IsMainPart(part.Path);

        if (string.IsNullOrWhiteSpace(part.Content))
        {
            findings.Add(DocumentHealthCodes.XmlEmpty, StructureIssueSeverity.Error, HealthStage.Xml,
                "Część XML jest pusta",
                $"Część '{part.Path}' ma {part.UncompressedSize} B i nie zawiera XML. Word traktuje pustą część jako uszkodzoną.",
                part.Path, critical ? WordOpenImpact.CannotOpen : WordOpenImpact.Repair,
                critical ? PdfConversionImpact.Blocking : PdfConversionImpact.Likely,
                "Wyeksportuj dokument ponownie; pusta część powstaje przy przerwanym zapisie strumienia.");
            return null;
        }

        try
        {
            return _xmlLoader.Load(part.Content);
        }
        catch (XmlException exception)
        {
            var isDtd = exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) ||
                        part.Content.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);

            if (isDtd)
            {
                findings.Add(DocumentHealthCodes.XmlDtdPresent, StructureIssueSeverity.Error, HealthStage.Xml,
                    "Część XML zawiera deklarację DTD",
                    $"Część '{part.Path}' ma <!DOCTYPE …>. OOXML zabrania DTD; Word i bezpieczne parsery (ochrona przed XXE) odrzucają taką część.",
                    HealthLocations.Of(part.Path, exception.LineNumber, exception.LinePosition),
                    WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Usuń deklarację DOCTYPE z części XML w aplikacji źródłowej.");
                return null;
            }

            findings.Add(DocumentHealthCodes.XmlNotWellFormed, StructureIssueSeverity.Error, HealthStage.Xml,
                "Niepoprawny XML",
                $"Część '{part.Path}' nie jest poprawnie sformułowanym XML: {exception.Message}",
                HealthLocations.Of(part.Path, exception.LineNumber, exception.LinePosition),
                critical ? WordOpenImpact.CannotOpen : WordOpenImpact.Repair,
                critical ? PdfConversionImpact.Blocking : PdfConversionImpact.Likely,
                "Typowe przyczyny: znak sterujący w tekście, niedomknięty tag, ucięta część, niepoprawne kodowanie. Popraw generator dokumentu; Word nie naprawi błędu składni w głównej części.");
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            findings.Add(DocumentHealthCodes.XmlNotWellFormed, StructureIssueSeverity.Error, HealthStage.Xml,
                "Części XML nie da się wczytać",
                $"Część '{part.Path}': {exception.Message}",
                part.Path, critical ? WordOpenImpact.CannotOpen : WordOpenImpact.Repair,
                critical ? PdfConversionImpact.Blocking : PdfConversionImpact.Likely);
            return null;
        }
    }

    private static void CheckRoot(HealthPackageContext context, string path, XElement root, HealthFindingCollector findings)
    {
        var contentType = context.ContentTypeOf(path);

        if (string.IsNullOrEmpty(contentType))
        {
            return;
        }

        foreach (var (fragment, expectedRoot, wordprocessing) in ExpectedRoots)
        {
            if (!contentType.EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rootOk = root.Name.LocalName == expectedRoot &&
                         (!wordprocessing || OoxmlNamespaces.IsWordprocessing(root.Name.NamespaceName));

            if (!rootOk)
            {
                var critical = context.IsMainPart(path);
                findings.Add(DocumentHealthCodes.XmlRootUnexpected, StructureIssueSeverity.Error, HealthStage.Xml,
                    "Korzeń XML nie pasuje do content type",
                    $"Część '{path}' ma content type '{contentType}', więc powinna zaczynać się od <{expectedRoot}> w namespace WordprocessingML, a zaczyna się od <{HealthLocations.QualifiedName(root)}> ({root.Name.NamespaceName}).",
                    HealthLocations.Of(path, root),
                    critical ? WordOpenImpact.CannotOpen : WordOpenImpact.Repair,
                    critical ? PdfConversionImpact.Blocking : PdfConversionImpact.Likely,
                    "Popraw content type w [Content_Types].xml albo zawartość części — jedno z dwojga jest błędne.");
            }

            return;
        }
    }

    private static void CheckCompatibilityPrefixes(string path, XElement root, HealthFindingCollector findings)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attributeName in new[] { "Ignorable", "MustUnderstand" })
            {
                var attribute = element.Attribute(Mc + attributeName);

                if (attribute is null)
                {
                    continue;
                }

                var prefixes = attribute.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

                foreach (var prefix in prefixes)
                {
                    if (element.GetNamespaceOfPrefix(prefix) is not null)
                    {
                        continue;
                    }

                    findings.Add(DocumentHealthCodes.XmlIgnorablePrefixUndeclared, StructureIssueSeverity.Error, HealthStage.Xml,
                        $"mc:{attributeName} wymienia niezadeklarowany prefiks",
                        $"Element <{HealthLocations.QualifiedName(element)}> w '{path}' ma mc:{attributeName}=\"{attribute.Value}\", ale prefiks '{prefix}' nie ma deklaracji xmlns w zasięgu. Word zgłasza wtedy „znaleziono nieczytelną zawartość” i proponuje naprawę; Open XML SDK i konwertery przerywają wczytywanie.",
                        HealthLocations.Of(path, element), WordOpenImpact.Repair, PdfConversionImpact.Likely,
                        $"Dodaj deklarację xmlns:{prefix}=\"…\" na korzeniu części albo usuń '{prefix}' z mc:{attributeName}. Zwykle wina leży po stronie biblioteki, która kopiowała atrybuty bez namespace'ów.");
                }
            }
        }
    }
}
