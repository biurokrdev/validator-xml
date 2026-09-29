using D2ViewerEditor.Api.Security;
using D2ViewerEditor.Application.Features.DocumentHealth.Commands.AnalyzeDocumentHealth;
using D2ViewerEditor.Application.Features.DocumentHealth.Common;
using D2ViewerEditor.Infrastructure.Services.DocumentHealth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Api.Controllers;

[Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
public class DocumentHealthController : BaseApiController
{
    private const long UploadSizeCeiling = 100 * 1024 * 1024;

    private readonly DocumentHealthOptions _options;

    public DocumentHealthController(IOptions<DocumentHealthOptions> options)
    {
        _options = options.Value;
    }

    [HttpPost("analyze")]
    [RequestSizeLimit(UploadSizeCeiling)]
    [ProducesResponseType(typeof(DocumentHealthReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Analyze(
        IFormFile file,
        [FromQuery] bool includeConversionProbes = true,
        CancellationToken cancellationToken = default)
    {
        if (file is null)
            return BadRequest(new { error = "Nie przesłano pliku." });

        if (file.Length > _options.MaxUploadBytes)
            return BadRequest(new { error = $"Plik przekracza limit {_options.MaxUploadBytes} bajtów." });

        await using var stream = file.OpenReadStream();
        var command = new AnalyzeDocumentHealthCommand(stream, Path.GetFileName(file.FileName), includeConversionProbes);
        var result = await Mediator.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }
}
