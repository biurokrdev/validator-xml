using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

/// <summary>Parametry porównania — co pomijać jako szum.</summary>
public sealed record DocumentCompareRequest(
    byte[] LeftBytes,
    string LeftFileName,
    byte[] RightBytes,
    string RightFileName,
    bool IgnoreRevisionIds = true,
    bool IgnoreDocumentProperties = true);

/// <summary>
/// Literalne porównanie dwóch pakietów DOCX: wpisy archiwum, typy zawartości, części binarne
/// bajt po bajcie, części XML element po elemencie, atrybut po atrybucie i tekst po tekście.
/// Nie interpretuje semantyki Worda — pokazuje, CO różni się w plikach, z wycinkiem obu stron.
/// Uszkodzone wejście nie rzuca: część nieczytelna jest raportowana jako <c>Unreadable</c>.
/// </summary>
public interface IDocumentComparer
{
    DocumentComparisonReport Compare(DocumentCompareRequest request, CancellationToken cancellationToken);
}
