using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Application.Letters;
using Mass.AddressWindow.Domain.Letters;
using Microsoft.AspNetCore.Mvc;

namespace Mass.AddressWindow.Api.Controllers;

/// <summary>Sprawdzanie, czy pismo PDF nadaje się do koperty z okienkiem.</summary>
[ApiController]
[Route("api/letter-inspections")]
[Produces("application/json")]
public sealed class LetterInspectionsController(InspectLetterHandler handler) : ControllerBase
{
    // Zapas na nagłówki i pola formularza multipart ponad sam plik.
    private const long RequestLimitBytes = PdfLetter.MaxSizeBytes + 1024 * 1024;

    /// <summary>
    /// Sprawdza pierwszą stronę pisma. 200 także wtedy, gdy pismo jest niepoprawne (isValid = false);
    /// 400 tylko wtedy, gdy pliku nie da się przyjąć: brak pliku, plik pusty, za duży albo nie PDF.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(RequestLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = RequestLimitBytes)]
    [ProducesResponseType(typeof(LetterInspectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LetterInspectionResponse>> Inspect([FromForm] InspectLetterRequest request, CancellationToken ct)
    {
        var file = request.File!;   // [Required] -> 400 z [ApiController], zanim tu dojdziemy
        if (file.Length > PdfLetter.MaxSizeBytes)
        {
            // Nie wczytujemy do pamięci pliku, który domena i tak odrzuci.
            throw new InvalidLetterException($"Plik jest za duży. Dopuszczalny rozmiar to {PdfLetter.MaxSizeBytes / (1024 * 1024)} MB.");
        }

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);

        var inspection = handler.Handle(new InspectLetterCommand(file.FileName, buffer.ToArray(), request.Envelope, request.IncludePreview));
        return Ok(LetterInspectionResponse.From(inspection));
    }
}
