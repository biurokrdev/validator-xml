using D2ViewerEditor.Api.Security;
using D2ViewerEditor.Application.Common;
using D2ViewerEditor.Application.Features.StructureInspection.Commands.AnalyzeDocumentStructure;
using D2ViewerEditor.Application.Features.StructureInspection.Commands.DeleteDocumentStructureInspection;
using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Application.Features.StructureInspection.Queries.GetPackageDiagnostics;
using D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElementDetails;
using D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElements;
using D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElementXml;
using D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructurePartXml;
using D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureSchemaIssues;
using D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureSections;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Infrastructure.Services.StructureInspection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Api.Controllers;

[Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
public class DocumentStructureController : BaseApiController
{
    private const long UploadSizeCeiling = 100 * 1024 * 1024;

    private readonly StructureInspectionOptions _options;

    public DocumentStructureController(IOptions<StructureInspectionOptions> options)
    {
        _options = options.Value;
    }

    [HttpPost("analyze")]
    [RequestSizeLimit(UploadSizeCeiling)]
    [ProducesResponseType(typeof(StructureInspectionSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Analyze(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null)
            return BadRequest(new { error = "Nie przesłano pliku." });

        if (file.Length == 0)
            return BadRequest(new { code = ErrorCodes.DocumentContentEmpty, error = "Zawartość dokumentu nie może być pusta." });

        if (!Path.GetExtension(file.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Walidator struktury obsługuje wyłącznie pliki DOCX." });

        if (file.Length > _options.MaxUploadBytes)
            return BadRequest(new { error = $"Plik przekracza limit {_options.MaxUploadBytes} bajtów." });

        await using var stream = file.OpenReadStream();
        var command = new AnalyzeDocumentStructureCommand(stream, Path.GetFileName(file.FileName));

        return MapResult(await Mediator.Send(command, cancellationToken));
    }

    [HttpGet("{inspectionId:guid}/elements")]
    [ProducesResponseType(typeof(List<StructureElementDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetElements(
        Guid inspectionId,
        [FromQuery] string? part,
        [FromQuery] string? category,
        [FromQuery] string? severity,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var query = new GetStructureElementsQuery(inspectionId, part, category, severity, search);

        return MapResult(await Mediator.Send(query, cancellationToken));
    }

    [HttpGet("{inspectionId:guid}/elements/{elementId}")]
    [ProducesResponseType(typeof(StructureElementDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetElementDetails(
        Guid inspectionId,
        string elementId,
        CancellationToken cancellationToken)
    {
        var query = new GetStructureElementDetailsQuery(inspectionId, elementId);

        return MapResult(await Mediator.Send(query, cancellationToken));
    }

    [HttpGet("{inspectionId:guid}/elements/{elementId}/xml")]
    [ProducesResponseType(typeof(StructureElementXmlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetElementXml(
        Guid inspectionId,
        string elementId,
        CancellationToken cancellationToken)
    {
        var query = new GetStructureElementXmlQuery(inspectionId, elementId);

        return MapResult(await Mediator.Send(query, cancellationToken));
    }

    [HttpGet("{inspectionId:guid}/parts/xml")]
    [ProducesResponseType(typeof(StructurePartXmlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPartXml(
        Guid inspectionId,
        [FromQuery] string path,
        [FromQuery] string? highlightElementId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Parametr 'path' jest wymagany." });

        var query = new GetStructurePartXmlQuery(inspectionId, path, highlightElementId);

        return MapResult(await Mediator.Send(query, cancellationToken));
    }

    [HttpGet("{inspectionId:guid}/schema-issues")]
    [ProducesResponseType(typeof(SchemaIssuesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSchemaIssues(
        Guid inspectionId,
        [FromQuery] string? targetVersion,
        CancellationToken cancellationToken)
    {
        var query = new GetStructureSchemaIssuesQuery(inspectionId, targetVersion);

        return MapResult(await Mediator.Send(query, cancellationToken));
    }

    [HttpGet("{inspectionId:guid}/package-diagnostics")]
    [ProducesResponseType(typeof(PackageDiagnosticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPackageDiagnostics(Guid inspectionId, CancellationToken cancellationToken)
    {
        var query = new GetPackageDiagnosticsQuery(inspectionId);

        return MapResult(await Mediator.Send(query, cancellationToken));
    }

    [HttpGet("{inspectionId:guid}/sections")]
    [ProducesResponseType(typeof(List<DocumentSectionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSections(Guid inspectionId, CancellationToken cancellationToken)
    {
        var query = new GetStructureSectionsQuery(inspectionId);

        return MapResult(await Mediator.Send(query, cancellationToken));
    }

    [HttpDelete("{inspectionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid inspectionId, CancellationToken cancellationToken)
    {
        var result = await Mediator.Send(new DeleteDocumentStructureInspectionCommand(inspectionId), cancellationToken);

        if (result.IsSuccess)
            return NoContent();

        return result.IsNotFound
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    private IActionResult MapResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(result.Value);

        return result.IsNotFound
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }
}
