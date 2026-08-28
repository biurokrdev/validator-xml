using D2ViewerEditor.Api.Controllers;
using D2ViewerEditor.Application.Features.Documents.Commands.DownloadEditedDocument;
using D2ViewerEditor.Application.Features.Documents.Commands.FinishAndSendDocument;
using D2ViewerEditor.Application.Features.Documents.Commands.AbortSend;
using D2ViewerEditor.Application.Features.Documents.Commands.ContinueDelivery;
using D2ViewerEditor.Application.Features.Documents.Commands.CancelDelivery;
using D2ViewerEditor.Application.Features.Documents.Commands.RequeueDelivery;
using D2ViewerEditor.Application.Features.Documents.Commands.UpdateDeliveryRecipientUrl;
using D2ViewerEditor.Application.Features.Documents.Commands.DeleteDocument;
using D2ViewerEditor.Application.Features.Documents.Commands.RestoreDocumentVersion;
using D2ViewerEditor.Application.Features.Documents.Commands.SaveDocumentVersion;
using D2ViewerEditor.Application.Features.Documents.Commands.UploadDocument;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDeliveriesByStatus;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDeliverySnapshotContent;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDeliveryStatus;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDocument;
using D2ViewerEditor.Application.Features.Documents.Commands.UpdateDocumentVersion;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDocumentBaseContent;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDocumentMetadata;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDocumentVersionContent;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDocumentVersions;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDocuments;
using D2ViewerEditor.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace D2ViewerEditor.Api.Controllers;

[Authorize(Policy = AuthorizationPolicies.RequireAppOperator)]
public class DocumentStorageController : BaseApiController
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
    [ProducesResponseType(typeof(List<DocumentListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDocuments([FromQuery] int skip = 0, [FromQuery] int take = 200)
    {
        var query = new GetDocumentsQuery(skip, take);
        var result = await Mediator.Send(query);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(result.Error);
    }

    [HttpDelete("{masterId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteDocument(Guid masterId)
    {
        var result = await Mediator.Send(new DeleteDocumentCommand(masterId));

        if (result.IsSuccess)
            return NoContent();
        if (result.IsNotFound)
            return NotFound(new { error = "Dokument nie istnieje." });
        return Conflict(new { error = result.Error });
    }

    [HttpPost("upload")]
    [ProducesResponseType(typeof(UploadDocumentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadDocument([FromBody] UploadDocumentRequest request)
    {
        var command = new UploadDocumentCommand(
            Content: request.Content,
            FileName: request.Name,
            MimeType: request.MimeType,
            CreatedBy: request.CreatedBy ?? "System"
        );

        var result = await Mediator.Send(command);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(result.Error);
    }

    [HttpPost("{masterId:guid}/save")]
    [ProducesResponseType(typeof(SaveDocumentVersionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveDocumentVersion(Guid masterId, [FromBody] SaveDocumentVersionRequest request)
    {
        var command = new SaveDocumentVersionCommand(
            MasterId: masterId,
            Content: request.Content,
            CreatedBy: request.CreatedBy ?? "System"
        );

        var result = await Mediator.Send(command);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(result.Error);
    }

    [HttpPut("{masterId:guid}/versions/{versionId:guid}")]
    [ProducesResponseType(typeof(UpdateDocumentVersionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateDocumentVersion(
        Guid masterId, Guid versionId, [FromBody] SaveDocumentVersionRequest request)
    {
        var command = new UpdateDocumentVersionCommand(
            MasterId: masterId,
            VersionId: versionId,
            Content: request.Content
        );

        var result = await Mediator.Send(command);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
    }

    [HttpGet("{masterId:guid}/metadata")]
    [ProducesResponseType(typeof(DocumentMetadataDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocumentMetadata(Guid masterId)
    {
        var query = new GetDocumentMetadataQuery(masterId);
        var result = await Mediator.Send(query);

        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error });

        return result.IsSuccess
            ? Ok(result.Value)
            : NotFound(new { error = result.Error });
    }

    [HttpGet("{masterId:guid}")]
    [ProducesResponseType(typeof(DocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocument(Guid masterId)
    {
        var query = new GetDocumentQuery(masterId);
        var result = await Mediator.Send(query);

        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error });

        return result.IsSuccess
            ? Ok(result.Value)
            : NotFound(new { error = result.Error });
    }

    [HttpGet("{masterId:guid}/versions")]
    [ProducesResponseType(typeof(List<DocumentVersionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocumentVersions(Guid masterId)
    {
        var query = new GetDocumentVersionsQuery(masterId);
        var result = await Mediator.Send(query);

        return result.IsSuccess
            ? Ok(result.Value)
            : NotFound(new { error = result.Error });
    }

    [HttpGet("{masterId:guid}/download")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadBaseDocument(Guid masterId)
    {
        var query = new GetDocumentBaseContentQuery(masterId);
        var result = await Mediator.Send(query);

        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error });
        if (!result.IsSuccess)
            return NotFound(new { error = result.Error });

        var dto = result.Value!;
        return File(dto.Content, dto.MimeType, dto.FileName);
    }

    [HttpGet("{masterId:guid}/versions/{versionId:guid}/download")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadDocumentVersion(Guid masterId, Guid versionId)
    {
        var query = new GetDocumentVersionContentQuery(masterId, versionId);
        var result = await Mediator.Send(query);

        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error });
        if (!result.IsSuccess)
            return NotFound(new { error = result.Error });

        var dto = result.Value!;
        return File(dto.Content, dto.MimeType, dto.FileName);
    }

    [HttpPost("{masterId:guid}/user-download")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadEditedDocument(
        [FromRoute] Guid masterId,
        [FromBody] Domain.Models.SaveDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var command = new DownloadEditedDocumentCommand(
            MasterId: masterId,
            Html: request?.Html ?? string.Empty,
            OriginalFileName: request?.OriginalFileName,
            Metadata: request?.Metadata,
            Header: request?.Header,
            Footer: request?.Footer,
            Margins: request?.Margins,
            PageSize: request?.PageSize,
            SectionHeadersFooters: request?.SectionHeadersFooters,
            Footnotes: request?.Footnotes,
            Endnotes: request?.Endnotes,
            FootnoteNumberFormat: request?.FootnoteNumberFormat,
            EndnoteNumberFormat: request?.EndnoteNumberFormat);

        var result = await Mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            return File(result.Value!.DocxBytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                result.Value.FileName);

        if (result.IsNotFound)
            return NotFound(new { error = result.Error });

        if (result.Error != null
            && result.Error.StartsWith(DownloadEditedDocumentCommandHandler.ForbiddenErrorPrefix, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = result.Error[(DownloadEditedDocumentCommandHandler.ForbiddenErrorPrefix.Length)..].Trim()
            });
        }

        return BadRequest(new { error = result.Error });
    }

    [HttpPost("{masterId:guid}/restore/{versionId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RestoreDocumentVersion(Guid masterId, Guid versionId)
    {
        var command = new RestoreDocumentVersionCommand(
            MasterId: masterId,
            VersionId: versionId
        );

        var result = await Mediator.Send(command);

        return result.IsSuccess
            ? Ok(new { Message = "Wersja została przywrócona", VersionId = versionId })
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(result.Error);
    }

    [HttpPost("{masterId:guid}/versions/{versionId:guid}/finish")]
    [ProducesResponseType(typeof(FinishAndSendResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FinishAndSend(
        Guid masterId, Guid versionId, [FromBody] SaveDocumentVersionRequest request)
    {
        var command = new FinishAndSendDocumentCommand(
            MasterId: masterId,
            VersionId: versionId,
            Content: request.Content,
            CreatedBy: request.CreatedBy);

        var result = await Mediator.Send(command);

        if (result.IsNotFound)
            return NotFound(new { error = result.Error });
        if (result.IsFailure)
            return BadRequest(new { error = result.Error });

        return Ok(result.Value);
    }

    [HttpPost("{masterId:guid}/abort-send")]
    [ProducesResponseType(typeof(AbortSendResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AbortSend(Guid masterId)
    {
        var result = await Mediator.Send(new AbortSendCommand(masterId));

        return result.IsSuccess
            ? Ok(result.Value)
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
    }

    [HttpPost("{masterId:guid}/continue-delivery")]
    [ProducesResponseType(typeof(ContinueDeliveryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ContinueDelivery(Guid masterId)
    {
        var result = await Mediator.Send(new ContinueDeliveryCommand(masterId));

        return result.IsSuccess
            ? Ok(result.Value)
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
    }

    [HttpGet("deliveries/{deliveryId:guid}")]
    [ProducesResponseType(typeof(DeliveryStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeliveryStatus(Guid deliveryId)
    {
        var result = await Mediator.Send(new GetDeliveryStatusQuery(deliveryId));

        return result.IsSuccess
            ? Ok(result.Value)
            : NotFound(new { error = result.Error });
    }

    [HttpGet("deliveries/{deliveryId:guid}/download")]
    [Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DownloadDeliverySnapshot(Guid deliveryId)
    {
        var result = await Mediator.Send(new GetDeliverySnapshotContentQuery(deliveryId));

        if (result.IsNotFound)
            return NotFound(new { error = result.Error });
        if (!result.IsSuccess)
            return Conflict(new { error = result.Error });

        var dto = result.Value!;
        Response.Headers["X-Snapshot-Sha256"] = dto.Sha256;
        return File(dto.Content, dto.MimeType, dto.FileName);
    }

    [HttpGet("deliveries")]
    [Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
    [ProducesResponseType(typeof(IReadOnlyList<DeliveryListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDeliveries(
        [FromQuery] string status = "", [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        var result = await Mediator.Send(new GetDeliveriesByStatusQuery(status, skip, take));

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("deliveries/{deliveryId:guid}/retry")]
    [Authorize(Policy = AuthorizationPolicies.RequireAppAdmin)]
    [ProducesResponseType(typeof(RequeueDeliveryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RetryDelivery(Guid deliveryId)
    {
        var result = await Mediator.Send(new RequeueDeliveryCommand(deliveryId));

        return result.IsSuccess
            ? Ok(result.Value)
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
    }

    [HttpPost("deliveries/{deliveryId:guid}/cancel")]
    [ProducesResponseType(typeof(CancelDeliveryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelDelivery(Guid deliveryId)
    {
        var result = await Mediator.Send(new CancelDeliveryCommand(deliveryId));

        return result.IsSuccess
            ? Ok(result.Value)
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
    }

    [HttpPut("deliveries/{deliveryId:guid}/recipient-url")]
    [ProducesResponseType(typeof(UpdateDeliveryRecipientUrlResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateDeliveryRecipientUrl(
        Guid deliveryId, [FromBody] UpdateDeliveryRecipientUrlRequest request)
    {
        var result = await Mediator.Send(
            new UpdateDeliveryRecipientUrlCommand(deliveryId, request.RecipientUrl));

        return result.IsSuccess
            ? Ok(result.Value)
            : result.IsNotFound
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
    }
}

public record UploadDocumentRequest(string Name, string MimeType, byte[] Content, string? CreatedBy);
public record SaveDocumentVersionRequest(byte[] Content, string? CreatedBy);
public record UpdateDeliveryRecipientUrlRequest(string RecipientUrl);
