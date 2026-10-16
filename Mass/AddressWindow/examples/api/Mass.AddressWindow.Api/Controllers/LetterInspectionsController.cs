using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Documents;
using Mass.AddressWindow.Api.Inspection;
using Microsoft.AspNetCore.Mvc;

namespace Mass.AddressWindow.Api.Controllers;

// 200 carries the inspection result, including a negative or inconclusive verdict. 4xx/5xx mean the request
// could not be processed at all (bad input, conversion failure, unreadable file, external source failure).
[ApiController]
[Route("api/letter-inspections")]
[Produces("application/json")]
public sealed class LetterInspectionsController(LetterInspectionService service) : ControllerBase
{
    private const long RequestLimitBytes = LetterDocument.MaxSizeBytes + 1024 * 1024;

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(RequestLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = RequestLimitBytes)]
    [ProducesResponseType(typeof(LetterInspectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LetterInspectionResponse>> Upload([FromForm] UploadLetterRequest request, CancellationToken ct)
    {
        var file = request.File!;
        if (file.Length > LetterDocument.MaxSizeBytes)
        {
            throw new DocumentRejectedException($"Plik jest za duży. Dopuszczalny rozmiar to {LetterDocument.MaxSizeBytes / (1024 * 1024)} MB.");
        }

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);

        return Ok(await service.InspectUploadedAsync(file.FileName, buffer.ToArray(), request.Envelope!.Value, request.IncludePreview, ct));
    }

    [HttpPost("from-recipient")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(LetterInspectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LetterInspectionResponse>> FromRecipient([FromBody] OrderedLetterRequest request, CancellationToken ct)
    {
        return Ok(await service.InspectOrderedAsync(request.Envelope!.Value, request.RecipientLines, request.RegisteredNumber, request.Source, request.IncludePreview, ct));
    }

    [HttpPost("from-form")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(LetterInspectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<LetterInspectionResponse>> FromForm([FromBody] FormLetterRequest request, CancellationToken ct)
    {
        return Ok(await service.InspectFormAsync(request.Envelope!.Value, request.FormId, request.IncludePreview, ct));
    }
}
