using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

public interface IDocumentHealthInspector
{
    Task<DocumentHealthReport> InspectAsync(
        byte[] documentBytes,
        string fileName,
        bool includeConversionProbes,
        CancellationToken cancellationToken);
}
