using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using D2ViewerEditor.Infrastructure.Services.StructureInspection.Analyzers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed class DocumentStructureInspector : IDocumentStructureInspector
{
    private readonly OoxmlPackageReader _packageReader;
    private readonly OpcPackageAnalyzer _opcAnalyzer;
    private readonly OoxmlElementIndexer _elementIndexer;
    private readonly IReadOnlyList<IStructureAnalyzer> _analyzers;
    private readonly SectionHeaderFooterAnalyzer _sectionAnalyzer;
    private readonly OpenXmlSchemaValidatorRunner _schemaValidator;
    private readonly SchemaIssueMapper _schemaIssueMapper;
    private readonly OoxmlFragmentReader _fragmentReader;
    private readonly SafeOoxmlXmlLoader _xmlLoader;
    private readonly StructureInspectionOptions _options;
    private readonly ILogger<DocumentStructureInspector> _logger;

    private readonly ConditionalWeakTable<byte[], ConcurrentDictionary<string, CachedPart>> _partCache = new();

    public DocumentStructureInspector(
        OoxmlPackageReader packageReader,
        OpcPackageAnalyzer opcAnalyzer,
        OoxmlElementIndexer elementIndexer,
        IEnumerable<IStructureAnalyzer> analyzers,
        SectionHeaderFooterAnalyzer sectionAnalyzer,
        OpenXmlSchemaValidatorRunner schemaValidator,
        SchemaIssueMapper schemaIssueMapper,
        OoxmlFragmentReader fragmentReader,
        SafeOoxmlXmlLoader xmlLoader,
        IOptions<StructureInspectionOptions> options,
        ILogger<DocumentStructureInspector> logger)
    {
        _packageReader = packageReader;
        _opcAnalyzer = opcAnalyzer;
        _elementIndexer = elementIndexer;
        _analyzers = analyzers.ToArray();
        _sectionAnalyzer = sectionAnalyzer;
        _schemaValidator = schemaValidator;
        _schemaIssueMapper = schemaIssueMapper;
        _fragmentReader = fragmentReader;
        _xmlLoader = xmlLoader;
        _options = options.Value;
        _logger = logger;
    }

    public DocumentStructureAnalysis Analyze(byte[] documentBytes, string fileName, CancellationToken cancellationToken)
    {
        var package = _packageReader.Read(documentBytes, cancellationToken);
        var opc = _opcAnalyzer.Analyze(package, cancellationToken);
        var elementIndex = _elementIndexer.Build(package, opc, cancellationToken);
        var context = new StructureAnalysisContext(elementIndex.Nodes, package, opc, _xmlLoader, cancellationToken);

        RunAnalyzers(context);

        var sections = _sectionAnalyzer.Analyze(context);
        var elements = elementIndex.Nodes.Select(node => node.Element).ToArray();
        var schemaValidation = ValidateSchemaDuringAnalysis(documentBytes, elements, cancellationToken);

        PopulateSearchText(elements);

        return new DocumentStructureAnalysis
        {
            FileName = fileName,
            FileSizeInBytes = documentBytes.LongLength,
            MainDocumentPartPath = opc.MainDocumentPartPath,
            Elements = elements,
            ElementsById = elements.ToDictionary(element => element.Id, StringComparer.Ordinal),
            Parts = elementIndex.Parts,
            Entries = package.Entries.Values.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            Sections = sections,
            PackageIssues = opc.Issues,
            SchemaIssues = schemaValidation.Issues,
            SchemaIssueCount = schemaValidation.TotalCount,
            ElementsTruncated = elementIndex.Truncated
        };
    }

    public OoxmlFragment? ReadElementXml(
        byte[] documentBytes,
        string partPath,
        IReadOnlyList<int> nodePath,
        CancellationToken cancellationToken)
    {
        var part = GetCachedPart(documentBytes, partPath, cancellationToken);

        return part is null ? null : _fragmentReader.Read(part.Document, nodePath);
    }

    public int? FindElementLine(
        byte[] documentBytes,
        string partPath,
        IReadOnlyList<int> nodePath,
        CancellationToken cancellationToken)
    {
        var part = GetCachedPart(documentBytes, partPath, cancellationToken);

        return part is null ? null : _fragmentReader.FindLine(part.Document, nodePath);
    }

    public string? ReadPartXml(byte[] documentBytes, string partPath, CancellationToken cancellationToken) =>
        GetCachedPart(documentBytes, partPath, cancellationToken)?.Content;

    private CachedPart? GetCachedPart(byte[] documentBytes, string partPath, CancellationToken cancellationToken)
    {
        var packageParts = _partCache.GetValue(
            documentBytes,
            _ => new ConcurrentDictionary<string, CachedPart>(StringComparer.OrdinalIgnoreCase));

        if (packageParts.TryGetValue(partPath, out var cached))
        {
            return cached;
        }

        var part = _packageReader.ReadXmlPart(documentBytes, partPath, cancellationToken);

        if (part is null)
        {
            return null;
        }

        XDocument document;

        try
        {
            document = _xmlLoader.Load(part.Content);
        }
        catch (XmlException exception)
        {
            throw new InvalidOoxmlPackageException(
                $"Część '{part.Path}' zawiera niepoprawny XML: {exception.Message}");
        }

        return packageParts.GetOrAdd(part.Path, new CachedPart(part.Content, document));
    }

    private sealed record CachedPart(string Content, XDocument Document);

    public IReadOnlyList<SchemaValidationIssue> ValidateSchema(
        byte[] documentBytes,
        string targetVersion,
        IReadOnlyList<InspectedElement> elements,
        CancellationToken cancellationToken)
    {
        var result = _schemaValidator.Validate(documentBytes, targetVersion, cancellationToken);

        return _schemaIssueMapper.Map(result.Issues, elements, annotateElements: false);
    }

    public IReadOnlyList<string> GetSupportedSchemaTargets() => OpenXmlSchemaValidatorRunner.SupportedTargetVersions();

    private void RunAnalyzers(StructureAnalysisContext context)
    {
        foreach (var analyzer in _analyzers)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            try
            {
                analyzer.Analyze(context);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Analizator {Analyzer} nie ukończył pracy.", analyzer.GetType().Name);
                context.FindPartRoot(context.MainDocumentPartPath)?.Issues.Add(new StructureIssue(
                    StructureIssueCodes.AnalyzerFailed,
                    StructureIssueSeverity.Warning,
                    "Analizator nie ukończył pracy",
                    $"{analyzer.GetType().Name}: {exception.Message}. Wyniki tej warstwy semantycznej są niekompletne; surowy XML pozostaje dostępny."));
            }
        }
    }

    private static void PopulateSearchText(IReadOnlyList<InspectedElement> elements)
    {
        foreach (var element in elements)
        {
            element.SearchText = string.Join(" | ",
            [
                element.XmlName,
                element.DisplayName,
                element.Category,
                element.PartPath,
                element.Preview ?? string.Empty,
                string.Join(" ", element.Attributes.Select(attribute => $"{attribute.Name}={attribute.RawValue}")),
                string.Join(" ", element.Properties.Select(property => $"{property.Name}={property.Value}")),
                string.Join(" ", element.Relationships.Select(relationship =>
                    $"{relationship.Id} {relationship.Target} {relationship.ResolvedTarget}")),
                string.Join(" ", element.EditorCompatibility.Select(compatibility =>
                    $"{compatibility.Feature} {compatibility.Level}")),
                string.Join(" ", element.Issues.Select(issue => $"{issue.Code} {issue.Title}"))
            ]).ToLowerInvariant();
        }
    }

    private SchemaValidationResult ValidateSchemaDuringAnalysis(
        byte[] documentBytes,
        IReadOnlyList<InspectedElement> elements,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = _schemaValidator.Validate(documentBytes, _options.DefaultSchemaTarget, cancellationToken);

            return result with { Issues = _schemaIssueMapper.Map(result.Issues, elements, annotateElements: true) };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Open XML SDK nie otworzył pakietu do walidacji schematu.");

            return new SchemaValidationResult(
            [
                new SchemaValidationIssue(
                    StructureIssueCodes.SchemaValidationFailed,
                    "Error",
                    $"Open XML SDK nie zwalidował pakietu: {exception.Message}",
                    null,
                    null,
                    null,
                    null,
                    _options.DefaultSchemaTarget)
            ], 1);
        }
    }
}
