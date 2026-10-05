using Mass.AddressWindow.Domain.Letters;

namespace Mass.AddressWindow.Domain.Inspection;

/// <summary>Port domenowy: sprawdza, czy adresy na pierwszej stronie pisma trafiają w okna koperty.</summary>
public interface IAddressWindowInspector
{
    LetterInspection Inspect(PdfLetter letter, EnvelopeType envelope, bool includePreview);
}
