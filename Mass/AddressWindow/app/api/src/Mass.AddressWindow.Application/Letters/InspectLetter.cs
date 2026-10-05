using Mass.AddressWindow.Domain.Inspection;
using Mass.AddressWindow.Domain.Letters;

namespace Mass.AddressWindow.Application.Letters;

public sealed record InspectLetterCommand(string? FileName, byte[] Content, EnvelopeType Envelope, bool IncludePreview = true);

/// <summary>Przypadek użycia: przyjmij plik, zbuduj z niego pismo i sprawdź okna koperty.</summary>
public sealed class InspectLetterHandler(IAddressWindowInspector inspector)
{
    /// <exception cref="InvalidLetterException">Plik nie jest pismem PDF nadającym się do sprawdzenia.</exception>
    public LetterInspection Handle(InspectLetterCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.Envelope))
        {
            throw new ArgumentException($"Nieznany rodzaj koperty: {command.Envelope}.", nameof(command));
        }

        var letter = PdfLetter.Create(command.FileName, command.Content);
        return inspector.Inspect(letter, command.Envelope, command.IncludePreview);
    }
}
