using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>Kontekst pakietu po etapie OPC: surowe części, wynik analizatora OPC i odczytane wpisy.</summary>
public sealed class HealthPackageContext
{
    public required LenientPackage Package { get; init; }
    public required RawPackageContents Raw { get; init; }
    public required OpcPackageAnalysis Opc { get; init; }

    public string MainDocumentPartPath => Opc.MainDocumentPartPath;

    public string? ContentTypeOf(string path) => Opc.ContentTypes.GetValueOrDefault(path);

    public bool IsMainPart(string path) =>
        path.Equals(MainDocumentPartPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Części o content type zawierającym dany fragment (np. <c>header+xml</c>).</summary>
    public IEnumerable<string> PartsOfType(string contentTypeFragment) =>
        Opc.ContentTypes
            .Where(pair => pair.Value.Contains(contentTypeFragment, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key);
}

/// <summary>
/// Etap „Pakiet OPC": ten sam analizator, którego używa Walidator struktury (typy zawartości,
/// relationshipy, osiągalność części, główna część przez relationship), z dopisaną oceną wpływu
/// każdego kodu na Worda i konwersję. Do tego rozpoznanie odmiany pakietu (docx/docm/dotx),
/// makr, podpisów i brakujących części opcjonalnych.
/// </summary>
public sealed class PackageHealthCheck
{
    private const string ContentTypesPath = "[Content_Types].xml";
    private const string RootRelationshipsPath = "_rels/.rels";

    private readonly OpcPackageAnalyzer _opcAnalyzer;

    public PackageHealthCheck(OpcPackageAnalyzer opcAnalyzer)
    {
        _opcAnalyzer = opcAnalyzer;
    }

    public HealthPackageContext? Run(
        LenientPackage package,
        HealthFindingCollector findings,
        CancellationToken cancellationToken,
        out string detectedFormat)
    {
        detectedFormat = package.DetectedFormat;

        if (package.DetectedFormat == "odt")
        {
            return null;
        }

        if (!package.Contains(ContentTypesPath) || package.Find(ContentTypesPath)?.Bytes is null)
        {
            findings.Add(DocumentHealthCodes.ContentTypesPartMissing, StructureIssueSeverity.Error, HealthStage.Package,
                "Brak [Content_Types].xml",
                "Pakiet OPC musi zawierać czytelną część [Content_Types].xml. Bez niej ani Word, ani Open XML SDK, ani żaden konwerter nie rozpoznają części dokumentu.",
                ContentTypesPath, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                "To nie jest pakiet DOCX albo został spakowany bez tabeli typów zawartości — wyeksportuj dokument ponownie.");
            return null;
        }

        if (!package.Contains(RootRelationshipsPath) || package.Find(RootRelationshipsPath)?.Bytes is null)
        {
            findings.Add(DocumentHealthCodes.RootRelationshipsPartMissing, StructureIssueSeverity.Error, HealthStage.Package,
                "Brak korzenia relationshipów _rels/.rels",
                "Bez _rels/.rels nie ma relationshipu officeDocument wskazującego główną część — Word nie wie, gdzie jest treść.",
                RootRelationshipsPath, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                "Wyeksportuj dokument ponownie z aplikacji źródłowej.");
        }

        var raw = BuildRawContents(package);
        OpcPackageAnalysis opc;

        try
        {
            opc = _opcAnalyzer.Analyze(raw, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            findings.Add(DocumentHealthCodes.PackageAnalysisFailed, StructureIssueSeverity.Error, HealthStage.Package,
                "Analiza pakietu OPC nie powiodła się",
                $"Analizator OPC przerwał pracę: {exception.Message}. Pakiet jest na tyle niestandardowy, że nie da się ustalić jego głównej części.",
                wordImpact: WordOpenImpact.CannotOpen, pdfImpact: PdfConversionImpact.Blocking);
            return null;
        }

        foreach (var issue in opc.Issues)
        {
            findings.Add(MapOpcIssue(issue));
        }

        var context = new HealthPackageContext { Package = package, Raw = raw, Opc = opc };

        detectedFormat = DescribeVariant(context, findings);
        CheckPackageExtras(context, findings);

        return context;
    }

    private static RawPackageContents BuildRawContents(LenientPackage package)
    {
        var xmlParts = new Dictionary<string, RawPackagePart>(StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, InspectedPackageEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in package.Entries)
        {
            if (entries.ContainsKey(entry.Path))
            {
                continue;
            }

            entries[entry.Path] = new InspectedPackageEntry(entry.Path, entry.Length, entry.CompressedLength, null, entry.IsXml);

            if (entry.IsXml && entry.Bytes is not null)
            {
                xmlParts[entry.Path] = new RawPackagePart
                {
                    Path = entry.Path,
                    Content = package.ReadText(entry.Path) ?? string.Empty,
                    UncompressedSize = entry.Length,
                    CompressedSize = entry.CompressedLength
                };
            }
        }

        return new RawPackageContents(xmlParts, entries);
    }

    private static HealthFinding MapOpcIssue(StructureIssue issue)
    {
        var (wordImpact, pdfImpact, remedy) = issue.Code switch
        {
            StructureIssueCodes.ContentTypesRootInvalid or
            StructureIssueCodes.RelationshipsXmlInvalid or
            StructureIssueCodes.RelationshipsRootInvalid or
            StructureIssueCodes.MainDocumentRelationshipMissing or
            StructureIssueCodes.MainDocumentContentTypeInvalid
                => (WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                    "Uszkodzenie warstwy pakietu — wyeksportuj dokument ponownie; naprawa w Wordzie nie jest możliwa."),

            StructureIssueCodes.ContentTypeMissing or
            StructureIssueCodes.RelationshipInvalid or
            StructureIssueCodes.RelationshipIdDuplicate or
            StructureIssueCodes.RelationshipTargetEscapesPackage or
            StructureIssueCodes.RelationshipTargetMissing or
            StructureIssueCodes.MultipleMainDocumentRelationships
                => (WordOpenImpact.Repair, PdfConversionImpact.Likely,
                    "Otwórz i zapisz w Wordzie (naprawa) albo popraw relationship w aplikacji źródłowej."),

            StructureIssueCodes.ContentTypeDefaultInvalid or
            StructureIssueCodes.ContentTypeDefaultDuplicate or
            StructureIssueCodes.ContentTypeOverrideInvalid or
            StructureIssueCodes.ContentTypeOverrideDuplicate or
            StructureIssueCodes.RelationshipTargetModeInvalid
                => (WordOpenImpact.Repair, PdfConversionImpact.Possible, null),

            StructureIssueCodes.RelationshipSourceNotFound or
            StructureIssueCodes.StrictOoxml
                => (WordOpenImpact.None, PdfConversionImpact.Possible, null),

            _ => (WordOpenImpact.None, PdfConversionImpact.None, null)
        };

        return new HealthFinding(issue.Code, issue.Severity, HealthStage.Package, issue.Title, issue.Description,
            null, wordImpact, pdfImpact, remedy);
    }

    private static string DescribeVariant(HealthPackageContext context, HealthFindingCollector findings)
    {
        var mainType = context.ContentTypeOf(context.MainDocumentPartPath) ?? string.Empty;

        if (mainType.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase) ||
            mainType.Contains("presentationml", StringComparison.OrdinalIgnoreCase))
        {
            var variant = mainType.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase) ? "xlsx" : "pptx";
            findings.Add(DocumentHealthCodes.FileNotWordPackage, StructureIssueSeverity.Error, HealthStage.Package,
                $"To pakiet {variant.ToUpperInvariant()}, nie dokument Word",
                $"Główna część pakietu ma content type '{mainType}'. Word nie otworzy tego pliku jako dokumentu; konwerter DOCX→PDF go odrzuci.",
                context.MainDocumentPartPath, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                "Sprawdź u nadawcy, jaki plik został wysłany — rozszerzenie .docx nie zgadza się z zawartością.");
            return variant;
        }

        if (!mainType.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase))
        {
            return "ooxml";
        }

        var macroEnabled = mainType.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase);
        var template = mainType.Contains("template", StringComparison.OrdinalIgnoreCase);

        if (macroEnabled)
        {
            findings.Add(DocumentHealthCodes.MacroEnabledDocument, StructureIssueSeverity.Warning, HealthStage.Package,
                "Dokument z obsługą makr (DOCM/DOTM)",
                $"Główna część ma content type '{mainType}'. Word otwiera taki plik tylko z rozszerzeniem .docm/.dotm (jako .docx zgłasza błąd); usługi konwertujące często odrzucają dokumenty z makrami.",
                context.MainDocumentPartPath, WordOpenImpact.Repair, PdfConversionImpact.Likely,
                "Zapisz w Wordzie jako „Dokument programu Word (.docx)” — makra zostaną usunięte.");
        }

        if (template)
        {
            findings.Add(DocumentHealthCodes.TemplateContentType, StructureIssueSeverity.Info, HealthStage.Package,
                "Pakiet jest szablonem (DOTX)",
                $"Główna część ma content type szablonu '{mainType}'. Word otwiera go jako nowy dokument; niektóre konwertery wymagają typu dokumentu.",
                context.MainDocumentPartPath, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Zapisz jako .docx, jeśli konwerter odrzuca szablony.");
        }

        return (template, macroEnabled) switch
        {
            (true, true) => "dotm",
            (true, false) => "dotx",
            (false, true) => "docm",
            _ => "docx"
        };
    }

    private static void CheckPackageExtras(HealthPackageContext context, HealthFindingCollector findings)
    {
        var macroPart = context.Package.Entries.FirstOrDefault(entry =>
            entry.Path.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));

        if (macroPart is not null)
        {
            findings.Add(DocumentHealthCodes.MacrosPresent, StructureIssueSeverity.Warning, HealthStage.Package,
                "Pakiet zawiera projekt VBA (makra)",
                $"Część '{macroPart.Path}' to projekt VBA. Bramka uploadu aplikacji odrzuca dokumenty z makrami (UploadSecurity:RejectDocxMacros); część usług konwertujących również.",
                macroPart.Path, WordOpenImpact.None, PdfConversionImpact.Possible,
                "Zapisz w Wordzie jako .docx (bez makr).");
        }

        if (context.Package.Entries.Any(entry => entry.Path.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(DocumentHealthCodes.SignedPackage, StructureIssueSeverity.Info, HealthStage.Package,
                "Pakiet ma podpis cyfrowy XML-DSig",
                "W pakiecie jest katalog _xmlsignatures — każda modyfikacja (także naprawa w Wordzie i zapis z edytora) unieważni podpis pakietu. Nie wpływa na konwersję.",
                "_xmlsignatures/");
        }

        foreach (var (suffix, label, pdfImpact) in new[]
                 {
                     ("styles", "styles.xml (style)", PdfConversionImpact.Possible),
                     ("settings", "settings.xml (ustawienia)", PdfConversionImpact.None),
                     ("fontTable", "fontTable.xml (tabela fontów)", PdfConversionImpact.None)
                 })
        {
            if (context.Opc.Relationships.FindTargetByType(context.MainDocumentPartPath, suffix) is null)
            {
                findings.Add(DocumentHealthCodes.OptionalPartMissing, StructureIssueSeverity.Info, HealthStage.Package,
                    $"Brak części {label}",
                    $"Główna część nie ma relationshipu '{suffix}'. Word otwiera taki dokument z ustawieniami domyślnymi; dokumenty generowane programowo często pomijają tę część, ale niektóre konwertery zakładają jej obecność.",
                    context.MainDocumentPartPath, WordOpenImpact.None, pdfImpact);
            }
        }
    }
}
