using System.Xml;
using System.Xml.Linq;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.StructureInspection;

public sealed record OpcPackageAnalysis(
    string MainDocumentPartPath,
    OoxmlRelationshipIndex Relationships,
    IReadOnlyDictionary<string, string> ContentTypes,
    IReadOnlyList<StructureIssue> Issues);

public sealed class OpcPackageAnalyzer
{
    private const string ContentTypesPath = "[Content_Types].xml";
    private static readonly XNamespace ContentTypesNamespace = OoxmlNamespaces.ContentTypes;
    private static readonly XNamespace RelationshipsNamespace = OoxmlNamespaces.PackageRelationships;

    private readonly SafeOoxmlXmlLoader _xmlLoader;

    public OpcPackageAnalyzer(SafeOoxmlXmlLoader xmlLoader)
    {
        _xmlLoader = xmlLoader;
    }

    public OpcPackageAnalysis Analyze(RawPackageContents package, CancellationToken cancellationToken)
    {
        var issues = new List<StructureIssue>();
        var contentTypes = ParseContentTypes(package, issues);
        ApplyContentTypes(package, contentTypes, issues);

        var relationships = ParseRelationships(package, issues, cancellationToken);
        ValidateRelationshipTargets(package, relationships, issues);
        ValidateReachability(package, relationships, issues);

        var mainDocumentPart = ResolveMainDocumentPart(package, relationships, contentTypes, issues);
        DetectStrictOoxml(package, mainDocumentPart, issues);

        return new OpcPackageAnalysis(mainDocumentPart, relationships, contentTypes, issues);
    }

    private Dictionary<string, string> ParseContentTypes(
        RawPackageContents package,
        ICollection<StructureIssue> issues)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = LoadRoot(package.XmlParts[ContentTypesPath], $"{ContentTypesPath} zawiera niepoprawny XML");

        if (root is null ||
            root.Name.NamespaceName != OoxmlNamespaces.ContentTypes ||
            root.Name.LocalName != "Types")
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.ContentTypesRootInvalid,
                StructureIssueSeverity.Error,
                "Niepoprawny korzeń [Content_Types].xml",
                "[Content_Types].xml nie zawiera elementu Types w oczekiwanym namespace OPC."));

            return resolved;
        }

        foreach (var element in root.Elements().Where(child => child.Name.Namespace == ContentTypesNamespace))
        {
            switch (element.Name.LocalName)
            {
                case "Default":
                    ReadDefault(element, defaults, issues);
                    break;
                case "Override":
                    ReadOverride(element, overrides, issues);
                    break;
            }
        }

        ValidateOverrideTargets(package, overrides, issues);

        foreach (var entry in package.Entries.Values.Where(entry => !IsContentTypesPart(entry.Path)))
        {
            if (overrides.TryGetValue(entry.Path, out var overrideType))
            {
                resolved[entry.Path] = overrideType;
                continue;
            }

            var extension = Path.GetExtension(entry.Path).TrimStart('.');

            if (extension.Length > 0 && defaults.TryGetValue(extension, out var defaultType))
            {
                resolved[entry.Path] = defaultType;
            }
        }

        return resolved;
    }

    private static void ReadDefault(
        XElement element,
        IDictionary<string, string> defaults,
        ICollection<StructureIssue> issues)
    {
        var extension = ((string?)element.Attribute("Extension"))?.TrimStart('.');
        var contentType = (string?)element.Attribute("ContentType");

        if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(contentType))
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.ContentTypeDefaultInvalid,
                StructureIssueSeverity.Error,
                "Niepoprawny wpis Default",
                "Wpis Default w [Content_Types].xml nie ma atrybutu Extension albo ContentType."));
            return;
        }

        if (!defaults.TryAdd(extension, contentType))
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.ContentTypeDefaultDuplicate,
                StructureIssueSeverity.Error,
                "Zduplikowany wpis Default",
                $"Rozszerzenie '{extension}' ma więcej niż jedną deklarację Default."));
        }
    }

    private static void ReadOverride(
        XElement element,
        IDictionary<string, string> overrides,
        ICollection<StructureIssue> issues)
    {
        var rawPartName = (string?)element.Attribute("PartName") ?? string.Empty;
        var partName = PackagePathResolver.Normalize(rawPartName);
        var contentType = (string?)element.Attribute("ContentType");

        if (string.IsNullOrWhiteSpace(partName) ||
            string.IsNullOrWhiteSpace(contentType) ||
            !rawPartName.StartsWith('/'))
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.ContentTypeOverrideInvalid,
                StructureIssueSeverity.Error,
                "Niepoprawny wpis Override",
                $"Override PartName='{rawPartName}' musi być bezwzględną nazwą części zaczynającą się od '/' i mieć ContentType."));

            if (string.IsNullOrWhiteSpace(partName) || string.IsNullOrWhiteSpace(contentType))
            {
                return;
            }
        }

        if (!overrides.TryAdd(partName, contentType))
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.ContentTypeOverrideDuplicate,
                StructureIssueSeverity.Error,
                "Zduplikowany wpis Override",
                $"Część '{partName}' ma więcej niż jedną deklarację Override."));
        }
    }

    private static void ValidateOverrideTargets(
        RawPackageContents package,
        IReadOnlyDictionary<string, string> overrides,
        ICollection<StructureIssue> issues)
    {
        foreach (var partName in overrides.Keys.Where(path => !package.Entries.ContainsKey(path)))
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.ContentTypeOverrideTargetNotFound,
                StructureIssueSeverity.Warning,
                "Override wskazuje nieistniejącą część",
                $"[Content_Types].xml deklaruje Override dla '{partName}', ale takiej części nie ma w pakiecie."));
        }
    }

    private static void ApplyContentTypes(
        RawPackageContents package,
        IReadOnlyDictionary<string, string> contentTypes,
        ICollection<StructureIssue> issues)
    {
        foreach (var entry in package.Entries.Values.Where(entry => !IsContentTypesPart(entry.Path)))
        {
            if (!contentTypes.TryGetValue(entry.Path, out var contentType))
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.ContentTypeMissing,
                    StructureIssueSeverity.Error,
                    "Brak typu zawartości",
                    $"Wpis pakietu '{entry.Path}' nie ma pasującego wpisu Default ani Override."));
                continue;
            }

            if (package.XmlParts.TryGetValue(entry.Path, out var part))
            {
                part.ContentType = contentType;
            }
        }
    }

    private OoxmlRelationshipIndex ParseRelationships(
        RawPackageContents package,
        ICollection<StructureIssue> issues,
        CancellationToken cancellationToken)
    {
        var relationships = new Dictionary<string, StructureRelationship>(StringComparer.Ordinal);

        foreach (var part in package.XmlParts.Values
                     .Where(part => part.Path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = TryLoadRelationshipRoot(part, issues);

            if (root is null)
            {
                continue;
            }

            var sourcePart = PackagePathResolver.GetSourcePartPath(part.Path);

            if (sourcePart.Length > 0 && !package.Entries.ContainsKey(sourcePart))
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.RelationshipSourceNotFound,
                    StructureIssueSeverity.Error,
                    "Brak części źródłowej relationshipów",
                    $"Część '{part.Path}' opisuje relationshipy nieistniejącej części '{sourcePart}'."));
            }

            ReadRelationshipEntries(root, part, sourcePart, relationships, issues);
        }

        return new OoxmlRelationshipIndex(relationships);
    }

    private XElement? TryLoadRelationshipRoot(RawPackagePart part, ICollection<StructureIssue> issues)
    {
        XElement? root;

        try
        {
            root = _xmlLoader.Load(part.Content).Root;
        }
        catch (XmlException exception)
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.RelationshipsXmlInvalid,
                StructureIssueSeverity.Error,
                "Niepoprawny XML relationshipów",
                $"Część '{part.Path}' zawiera niepoprawny XML: {exception.Message}"));
            return null;
        }

        if (root?.Name.Namespace == RelationshipsNamespace && root.Name.LocalName == "Relationships")
        {
            return root;
        }

        issues.Add(new StructureIssue(
            StructureIssueCodes.RelationshipsRootInvalid,
            StructureIssueSeverity.Error,
            "Niepoprawny korzeń części relationshipów",
            $"Część '{part.Path}' nie zawiera elementu Relationships w namespace OPC."));

        return null;
    }

    private static void ReadRelationshipEntries(
        XElement root,
        RawPackagePart relationshipPart,
        string sourcePart,
        IDictionary<string, StructureRelationship> relationships,
        ICollection<StructureIssue> issues)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in root.Elements()
                     .Where(child => child.Name.Namespace == RelationshipsNamespace && child.Name.LocalName == "Relationship"))
        {
            var id = (string?)node.Attribute("Id");
            var type = (string?)node.Attribute("Type");
            var target = (string?)node.Attribute("Target");
            var targetMode = (string?)node.Attribute("TargetMode") ?? "Internal";

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(target))
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.RelationshipInvalid,
                    StructureIssueSeverity.Error,
                    "Niepoprawny relationship",
                    $"Część '{relationshipPart.Path}' zawiera relationship bez Id, Type albo Target."));
                continue;
            }

            if (!seenIds.Add(id))
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.RelationshipIdDuplicate,
                    StructureIssueSeverity.Error,
                    "Zduplikowany identyfikator relationshipu",
                    $"Część '{relationshipPart.Path}' zawiera zduplikowane Id '{id}'."));
                continue;
            }

            var isExternal = targetMode.Equals("External", StringComparison.OrdinalIgnoreCase);

            if (!isExternal && !targetMode.Equals("Internal", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.RelationshipTargetModeInvalid,
                    StructureIssueSeverity.Error,
                    "Niepoprawny TargetMode",
                    $"Relationship '{id}' w '{relationshipPart.Path}' używa TargetMode='{targetMode}'."));
            }

            if (!isExternal && PackagePathResolver.EscapesPackageRoot(sourcePart, target))
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.RelationshipTargetEscapesPackage,
                    StructureIssueSeverity.Error,
                    "Cel relationshipu wychodzi poza pakiet",
                    $"Relationship '{id}' w '{relationshipPart.Path}' wskazuje '{target}', co prowadzi ponad korzeń pakietu."));
            }

            var resolvedTarget = isExternal ? null : PackagePathResolver.ResolveInternalTarget(sourcePart, target);

            relationships.TryAdd(
                OoxmlRelationshipIndex.CreateKey(sourcePart, id),
                new StructureRelationship(
                    sourcePart,
                    relationshipPart.Path,
                    id,
                    type,
                    target,
                    targetMode,
                    resolvedTarget,
                    isExternal ? StructureRelationshipStatus.External : StructureRelationshipStatus.Resolved));
        }
    }

    private static void ValidateRelationshipTargets(
        RawPackageContents package,
        OoxmlRelationshipIndex relationships,
        ICollection<StructureIssue> issues)
    {
        foreach (var relationship in relationships.All)
        {
            if (relationship.Status == StructureRelationshipStatus.External)
            {
                issues.Add(new StructureIssue(
                    StructureIssueCodes.RelationshipExternal,
                    StructureIssueSeverity.Info,
                    "Relationship zewnętrzny",
                    $"Relationship '{relationship.Id}' z '{DisplaySource(relationship.SourcePart)}' wskazuje zasób poza pakietem: {relationship.Target}."));
                continue;
            }

            if (relationship.ResolvedTarget is null || !package.Entries.ContainsKey(relationship.ResolvedTarget))
            {
                relationships.MarkTargetMissing(relationship);
                issues.Add(new StructureIssue(
                    StructureIssueCodes.RelationshipTargetMissing,
                    StructureIssueSeverity.Error,
                    "Cel relationshipu nie istnieje",
                    $"Relationship '{relationship.Id}' z '{DisplaySource(relationship.SourcePart)}' wskazuje '{relationship.ResolvedTarget ?? relationship.Target}', ale takiego wpisu nie ma w pakiecie."));
            }
        }
    }

    private static void ValidateReachability(
        RawPackageContents package,
        OoxmlRelationshipIndex relationships,
        ICollection<StructureIssue> issues)
    {
        var bySource = relationships.All
            .Where(relationship => relationship.Status != StructureRelationshipStatus.External && relationship.ResolvedTarget is not null)
            .GroupBy(relationship => relationship.SourcePart, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        Enqueue(string.Empty);

        while (pending.Count > 0)
        {
            Enqueue(pending.Dequeue());
        }

        foreach (var entry in package.Entries.Values)
        {
            if (IsContentTypesPart(entry.Path) ||
                entry.Path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ||
                reachable.Contains(entry.Path))
            {
                continue;
            }

            issues.Add(new StructureIssue(
                StructureIssueCodes.OrphanedPart,
                StructureIssueSeverity.Warning,
                "Część osierocona",
                $"Część '{entry.Path}' nie jest osiągalna z korzenia pakietu przez relationshipy."));
        }

        void Enqueue(string sourcePart)
        {
            if (!bySource.TryGetValue(sourcePart, out var outgoing))
            {
                return;
            }

            foreach (var relationship in outgoing)
            {
                if (reachable.Add(relationship.ResolvedTarget!))
                {
                    pending.Enqueue(relationship.ResolvedTarget!);
                }
            }
        }
    }

    private static string ResolveMainDocumentPart(
        RawPackageContents package,
        OoxmlRelationshipIndex relationships,
        IReadOnlyDictionary<string, string> contentTypes,
        ICollection<StructureIssue> issues)
    {
        var candidates = relationships.All
            .Where(relationship =>
                relationship.SourcePart.Length == 0 &&
                OoxmlNamespaces.IsOfficeDocumentRelationshipType(relationship.Type) &&
                relationship.ResolvedTarget is not null)
            .Select(relationship => relationship.ResolvedTarget!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 1 && package.XmlParts.ContainsKey(candidates[0]))
        {
            ValidateMainDocumentContentType(candidates[0], contentTypes, issues);
            return candidates[0];
        }

        issues.Add(candidates.Length > 1
            ? new StructureIssue(
                StructureIssueCodes.MultipleMainDocumentRelationships,
                StructureIssueSeverity.Error,
                "Wiele relationshipów głównej części",
                $"Pakiet deklaruje kilka celów officeDocument: {string.Join(", ", candidates)}.")
            : new StructureIssue(
                StructureIssueCodes.MainDocumentRelationshipMissing,
                StructureIssueSeverity.Error,
                "Brak relationshipu głównej części",
                "Korzeń relationshipów nie zawiera poprawnego relationshipu officeDocument."));

        var byContentType = contentTypes
            .Where(pair => pair.Value.Contains(OoxmlNamespaces.MainDocumentContentTypeFragment, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .FirstOrDefault(package.XmlParts.ContainsKey);

        if (byContentType is null)
        {
            throw new InvalidOoxmlPackageException(
                "Pakiet nie zawiera możliwej do odnalezienia głównej części dokumentu WordprocessingML.");
        }

        ValidateMainDocumentContentType(byContentType, contentTypes, issues);
        issues.Add(new StructureIssue(
            StructureIssueCodes.MainDocumentFallback,
            StructureIssueSeverity.Warning,
            "Główna część ustalona z typu zawartości",
            $"Użyto '{byContentType}' jako głównej części dokumentu, bo relationship z korzenia był nieobecny lub niejednoznaczny."));

        return byContentType;
    }

    private static void ValidateMainDocumentContentType(
        string mainDocumentPart,
        IReadOnlyDictionary<string, string> contentTypes,
        ICollection<StructureIssue> issues)
    {
        if (contentTypes.TryGetValue(mainDocumentPart, out var contentType) &&
            contentType.Contains(OoxmlNamespaces.MainDocumentContentTypeFragment, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        issues.Add(new StructureIssue(
            StructureIssueCodes.MainDocumentContentTypeInvalid,
            StructureIssueSeverity.Error,
            "Nieoczekiwany typ głównej części",
            $"Główna część '{mainDocumentPart}' ma typ zawartości '{contentType ?? "<brak>"}', a nie typ głównej części WordprocessingML."));
    }

    private void DetectStrictOoxml(
        RawPackageContents package,
        string mainDocumentPart,
        ICollection<StructureIssue> issues)
    {
        if (!package.XmlParts.TryGetValue(mainDocumentPart, out var part))
        {
            return;
        }

        XElement? root;

        try
        {
            root = _xmlLoader.Load(part.Content).Root;
        }
        catch (XmlException)
        {
            return;
        }

        if (root?.Name.NamespaceName == OoxmlNamespaces.WordprocessingStrict)
        {
            issues.Add(new StructureIssue(
                StructureIssueCodes.StrictOoxml,
                StructureIssueSeverity.Info,
                "Dokument w wariancie Strict",
                "Główna część używa namespace ISO/IEC 29500 Strict. Walidator obsługuje warianty Strict i Transitional."));
        }
    }

    private XElement? LoadRoot(RawPackagePart part, string errorPrefix)
    {
        try
        {
            return _xmlLoader.Load(part.Content).Root;
        }
        catch (XmlException exception)
        {
            throw new InvalidOoxmlPackageException($"{errorPrefix}: {exception.Message}");
        }
    }

    private static bool IsContentTypesPart(string path) =>
        path.Equals(ContentTypesPath, StringComparison.OrdinalIgnoreCase);

    private static string DisplaySource(string sourcePart) =>
        sourcePart.Length == 0 ? "korzenia pakietu" : sourcePart;
}
