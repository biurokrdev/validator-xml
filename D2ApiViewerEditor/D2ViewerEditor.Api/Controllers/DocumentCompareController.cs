using D2ViewerEditor.Api.Security;
using D2ViewerEditor.Application.Features.DocumentCompare.Commands.CompareDocuments;
using D2ViewerEditor.Application.Features.DocumentCompare.Common;
using D2ViewerEditor.Infrastructure.Services.DocumentCompare;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Api.Controllers;

/// <summary>
/// Narzędzie administracyjne „Porównanie dokumentów": dwa pliki DOCX porównane literalnie —
/// wpisy pakietu, części binarne bajt po bajcie, części XML element po elemencie, atrybut po atrybucie
/// i tekst po tekście — z wycinkiem XML obu stron przy każdej różnicy. Bezstanowe.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
public class DocumentCompareController : BaseApiController
{
    /// <summary>Sufit Kestrela dla atrybutu (dwa pliki); realny limit per plik to <c>DocumentCompare:MaxUploadBytes</c>.</summary>
    private const long UploadSizeCeiling = 200 * 1024 * 1024;

    private readonly DocumentCompareOptions _options;

    public DocumentCompareController(IOptions<DocumentCompareOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>Porównuje dwa pliki przesłane w polach multipart <c>left</c> i <c>right</c>.</summary>
    /// <param name="left">Plik „lewy" (np. oryginał / v1).</param>
    /// <param name="right">Plik „prawy" (np. wersja edytowana / eksport).</param>
    /// <param name="ignoreRevisionIds">Pomiń atrybuty <c>w:rsid*</c> i blok <c>w:rsids</c> (szum Worda); domyślnie tak.</param>
    /// <param name="ignoreDocumentProperties">Pomiń <c>docProps/*</c> (daty zapisu, liczniki); domyślnie tak.</param>
    /// <param name="cancellationToken">Token anulowania.</param>
    [HttpPost("analyze")]
    [RequestSizeLimit(UploadSizeCeiling)]
    [ProducesResponseType(typeof(DocumentComparisonReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Analyze(
        IFormFile left,
        IFormFile right,
        [FromQuery] bool ignoreRevisionIds = true,
        [FromQuery] bool ignoreDocumentProperties = true,
        CancellationToken cancellationToken = default)
    {
        if (left is null || right is null)
            return BadRequest(new { error = "Wymagane są dwa pliki: pola 'left' i 'right'." });

        if (left.Length > _options.MaxUploadBytes || right.Length > _options.MaxUploadBytes)
            return BadRequest(new { error = $"Plik przekracza limit {_options.MaxUploadBytes} bajtów." });

        await using var leftStream = left.OpenReadStream();
        await using var rightStream = right.OpenReadStream();

        var command = new CompareDocumentsCommand(
            leftStream, Path.GetFileName(left.FileName),
            rightStream, Path.GetFileName(right.FileName),
            ignoreRevisionIds, ignoreDocumentProperties);

        var result = await Mediator.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }
}
