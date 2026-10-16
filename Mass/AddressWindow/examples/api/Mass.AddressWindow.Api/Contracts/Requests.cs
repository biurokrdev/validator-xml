using System.ComponentModel.DataAnnotations;

namespace Mass.AddressWindow.Api.Contracts;

public enum EnvelopeType
{
    SingleWindow = 1,

    DoubleWindow = 2,
}

// Variant 1: multipart/form-data with the document chosen by the user.
public sealed class UploadLetterRequest
{
    [Required(ErrorMessage = "Nie przesłano pliku.")]
    public IFormFile? File { get; set; }

    [Required(ErrorMessage = "Wybierz rodzaj koperty.")]
    public EnvelopeType? Envelope { get; set; }

    public bool IncludePreview { get; set; } = true;
}

// Variant 3: JSON with the identifier of a form whose document the external application already holds.
public sealed class FormLetterRequest
{
    [Required(ErrorMessage = "Wybierz rodzaj koperty.")]
    public EnvelopeType? Envelope { get; set; }

    [Required(ErrorMessage = "Podaj identyfikator formularza.")]
    public string? FormId { get; set; }

    public bool IncludePreview { get; set; } = true;
}

// Variant 2: JSON with the data the external application puts into the letter.
public sealed class OrderedLetterRequest
{
    [Required(ErrorMessage = "Wybierz rodzaj koperty.")]
    public EnvelopeType? Envelope { get; set; }

    // Free-form source identifier handed to the external application; optional.
    public string? Source { get; set; }

    [Required(ErrorMessage = "Podaj adres odbiorcy.")]
    public List<string>? RecipientLines { get; set; }

    public string? RegisteredNumber { get; set; }

    public bool IncludePreview { get; set; } = true;
}
