using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

public sealed record DocumentCompareRequest(
    byte[] LeftBytes,
    string LeftFileName,
    byte[] RightBytes,
    string RightFileName,
    bool IgnoreRevisionIds = true,
    bool IgnoreDocumentProperties = true);

public interface IDocumentComparer
{
    DocumentComparisonReport Compare(DocumentCompareRequest request, CancellationToken cancellationToken);
}
