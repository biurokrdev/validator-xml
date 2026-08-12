using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

public interface IDocumentStructureInspector
{
    DocumentStructureAnalysis Analyze(byte[] documentBytes, string fileName, CancellationToken cancellationToken);

    OoxmlFragment? ReadElementXml(byte[] documentBytes, string partPath, IReadOnlyList<int> nodePath, CancellationToken cancellationToken);

    int? FindElementLine(byte[] documentBytes, string partPath, IReadOnlyList<int> nodePath, CancellationToken cancellationToken);

    string? ReadPartXml(byte[] documentBytes, string partPath, CancellationToken cancellationToken);

    IReadOnlyList<SchemaValidationIssue> ValidateSchema(
        byte[] documentBytes,
        string targetVersion,
        IReadOnlyList<InspectedElement> elements,
        CancellationToken cancellationToken);

    IReadOnlyList<string> GetSupportedSchemaTargets();
}

public sealed record OoxmlFragment(string Xml, int? SourceLine);
