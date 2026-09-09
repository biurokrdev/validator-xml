using D2ViewerEditor.Application.Common;
using D2ViewerEditor.Application.Features.Documents.Commands.GeneratePdfPreview;
using D2ViewerEditor.Application.Features.Documents.Commands.SaveDocument;
using D2ViewerEditor.Application.Features.Documents.Commands.SignDocument;
using D2ViewerEditor.Application.Features.Documents.Commands.UploadImage;
using D2ViewerEditor.Application.Features.Documents.Queries.GetNewDocument;
using D2ViewerEditor.Application.Features.Documents.Queries.GetTemplate;
using D2ViewerEditor.Application.Features.Documents.Queries.GetTemplates;
using D2ViewerEditor.Application.Features.Documents.Queries.OpenDocument;
using D2ViewerEditor.Api.Security;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2ViewerEditor.Api.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.RequireAppOperator)]
public class DocumentController : BaseApiController
{
    [HttpPost("open")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> OpenDocument(IFormFile file, [FromForm] string? password = null)
    {
        if (file == null)
            return BadRequest(new { error = "Nie przesłano pliku" });

        if (file.Length == 0)
            return BadRequest(new { code = ErrorCodes.DocumentContentEmpty, error = "Zawartość dokumentu nie może być pusta." });

        var extension = Path.GetExtension(file.FileName).ToLower();
        if (extension != ".docx" && extension != ".doc")
            return BadRequest(new { error = "Obsługiwane są tylko pliki DOCX i PDF." });

        var query = new OpenDocumentQuery(file.OpenReadStream(), file.FileName, password);
        var result = await Mediator.Send(query);

        if (result.IsSuccess)
            return Ok(result.Value);

        return result.Error switch
        {
            ErrorCodes.DocumentProtected => StatusCode(StatusCodes.Status422UnprocessableEntity,
                new { code = ErrorCodes.DocumentProtected, error = "Dokument jest zabezpieczony hasłem. Podaj hasło, aby go otworzyć." }),
            ErrorCodes.DocumentUnlockFailed => StatusCode(StatusCodes.Status422UnprocessableEntity,
                new { code = ErrorCodes.DocumentUnlockFailed, error = "Nieprawidłowe hasło do dokumentu." }),
            ErrorCodes.UnsupportedLegacyDoc => BadRequest(
                new { code = ErrorCodes.UnsupportedLegacyDoc, error = "Plik .doc (starszy, binarny format Worda) wymaga konwersji do .docx. Otwórz go w Wordzie i zapisz jako .docx (Plik → Zapisz jako → Dokument programu Word *.docx), a następnie wczytaj ponownie." }),
            ErrorCodes.DocumentFormatInvalid => BadRequest(
                new { code = ErrorCodes.DocumentFormatInvalid, error = "Nieprawidłowy format dokumentu." }),
            ErrorCodes.DocumentCorrupted => BadRequest(
                new { code = ErrorCodes.DocumentCorrupted, error = "Dokument jest uszkodzony." }),
            _ => HandleResult(result)
        };
    }

    [HttpPost("save")]
    public async Task<IActionResult> SaveDocument([FromBody] SaveDocumentRequest request)
    {
        var command = new SaveDocumentCommand(
            request.Html,
            request.OriginalFileName,
            request.Metadata,
            request.Header,
            request.Footer,
            request.Margins,
            request.PageSize,
            request.SectionHeadersFooters,
            request.Footnotes,
            request.Endnotes,
            request.MasterId,
            request.FootnoteNumberFormat,
            request.EndnoteNumberFormat
        );

        var result = await Mediator.Send(command);

        return result.Match<IActionResult>(
            onSuccess: saveResult => File(
                saveResult.DocxBytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                saveResult.FileName),
            onFailure: error => BadRequest(new { error })
        );
    }

    [HttpGet("new")]
    public async Task<IActionResult> NewDocument()
    {
        var result = await Mediator.Send(new GetNewDocumentQuery());
        return HandleResult(result);
    }

    [HttpPost("generate-pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GeneratePdf([FromBody] SaveDocumentRequest request, CancellationToken cancellationToken)
    {
        var command = new GeneratePdfPreviewCommand(
            request?.Html ?? string.Empty,
            request?.OriginalFileName,
            request?.Metadata,
            request?.Header,
            request?.Footer,
            request?.Margins,
            request?.PageSize,
            request?.SectionHeadersFooters,
            request?.Footnotes,
            request?.Endnotes,
            request?.MasterId,
            request?.FootnoteNumberFormat,
            request?.EndnoteNumberFormat
        );

        var result = await Mediator.Send(command, cancellationToken);

        return result.Match<IActionResult>(
            onSuccess: pdf => File(pdf.PdfBytes, "application/pdf", pdf.FileName),
            onFailure: error => BadRequest(new { error })
        );
    }

    [HttpPost("export-pdf")]
    public IActionResult ExportToPdf([FromBody] SaveDocumentRequest request)
    {
        return StatusCode(501, new { error = "Eksport do PDF nie jest jeszcze zaimplementowany. Użyj funkcji Zapisz jako DOCX." });
    }

    [HttpPost("upload-image")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Nie przesłano pliku" });

        var command = new UploadImageCommand(
            file.OpenReadStream(),
            file.FileName,
            file.ContentType,
            file.Length
        );

        var result = await Mediator.Send(command);
        return HandleResult(result);
    }

    [HttpGet("templates")]
    public async Task<IActionResult> GetTemplates()
    {
        var result = await Mediator.Send(new GetTemplatesQuery());
        return HandleResult(result);
    }

    [HttpGet("templates/{templateId}")]
    public async Task<IActionResult> GetTemplate(string templateId)
    {
        var result = await Mediator.Send(new GetTemplateQuery(templateId));
        return HandleResult(result);
    }

    [HttpPost("sign")]
    public async Task<IActionResult> SignDocument([FromBody] SignDocumentRequest request)
    {
        var command = new SignDocumentCommand(
            request.Html,
            request.OriginalFileName,
            request.Metadata,
            request.Header,
            request.Footer,
            request.CertificateBase64,
            request.CertificatePassword,
            request.SignerName,
            request.SignerTitle,
            request.SignerEmail,
            request.SignatureReason,
            request.Margins,
            request.PageSize
        );

        var result = await Mediator.Send(command);

        return result.Match<IActionResult>(
            onSuccess: signResult => File(
                signResult.DocxBytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                signResult.FileName),
            onFailure: error => BadRequest(new { error })
        );
    }

    [HttpPost("verify-signatures")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public IActionResult VerifySignatures(IFormFile file, [FromServices] IDigitalSignatureService signatureService)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Nie przesłano pliku" });

        try
        {
            using var stream = file.OpenReadStream();
            var signatures = signatureService.VerifySignatures(stream);
            return Ok(signatures);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Błąd weryfikacji podpisów: {ex.Message}" });
        }
    }
}
