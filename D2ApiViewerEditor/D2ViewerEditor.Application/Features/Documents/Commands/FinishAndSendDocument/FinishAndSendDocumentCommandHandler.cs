using System.Security.Cryptography;
using System.Text.Json;
using D2ViewerEditor.Application.Common.Security;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Commands.FinishAndSendDocument;

public class FinishAndSendDocumentCommandHandler
    : IRequestHandler<FinishAndSendDocumentCommand, Result<FinishAndSendResult>>
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentDeliveryRepository _deliveryRepository;
    private readonly IDocumentStorageService _storage;
    private readonly IDeliverySender _sender;
    private readonly ICurrentUserProvider _currentUser;
    private readonly IReturnUrlValidator _returnUrlValidator;

    public FinishAndSendDocumentCommandHandler(
        IDocumentRepository documentRepository,
        IDocumentDeliveryRepository deliveryRepository,
        IDocumentStorageService storage,
        IDeliverySender sender,
        ICurrentUserProvider currentUser,
        IReturnUrlValidator returnUrlValidator)
    {
        _documentRepository = documentRepository;
        _deliveryRepository = deliveryRepository;
        _storage = storage;
        _sender = sender;
        _currentUser = currentUser;
        _returnUrlValidator = returnUrlValidator;
    }

    public async Task<Result<FinishAndSendResult>> Handle(
        FinishAndSendDocumentCommand request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Content == null || request.Content.Length == 0)
                return Result<FinishAndSendResult>.Failure("Zawartość dokumentu nie może być pusta");

            var corporateKey = _currentUser.CorporateKey;
            if (string.IsNullOrWhiteSpace(corporateKey))
                return Result<FinishAndSendResult>.Failure(
                    "Nie można ustalić użytkownika kończącego dokument (brak CorporateKey).");

            var document = await _documentRepository.GetByIdWithVersionsAsync(request.MasterId, cancellationToken);
            if (document == null)
                return Result<FinishAndSendResult>.NotFound();

            var version = document.Versions.FirstOrDefault(v => v.Id == request.VersionId);
            if (version == null)
                return Result<FinishAndSendResult>.NotFound();

            var recipientUrl = ReadReturnUrl(document.Metadata);
            var returnUrlValidation = _returnUrlValidator.Validate(recipientUrl);
            if (!returnUrlValidation.IsValid)
                return Result<FinishAndSendResult>.Failure(
                    "Brak poprawnego adresu odbiorcy (returnUrl) w metadanych dokumentu");

            var active = await _deliveryRepository.GetActiveByDocumentIdAsync(document.Id, cancellationToken);
            if (active is { Status: DeliveryStatus.Sending })
                return Result<FinishAndSendResult>.Success(new FinishAndSendResult(
                    active.Id, active.Status.ToString(), document.Status.ToString(), Delivered: false));

            await _storage.UploadAsync(version.Id, request.Content, document.MimeType, cancellationToken);
            document.UpdateVersion(version.Id, request.Content.Length);

            document.SetLastModifiedBy(corporateKey);

            var delivery = active is null
                ? await CreateQueuedDeliveryAsync(document, version, request, returnUrlValidation.NormalizedUrl!, corporateKey, cancellationToken)
                : await _deliveryRepository.GetByIdAsync(active.Id, cancellationToken);
            if (delivery is null)
                return Result<FinishAndSendResult>.Failure("Nie znaleziono zadania wysyłki do ponowienia");

            if (active is not null)
            {
                await _storage.UploadRawAsync(delivery.SnapshotObjectName, request.Content, document.MimeType, cancellationToken);
                delivery.RefreshSnapshot(request.Content.Length, Convert.ToHexString(SHA256.HashData(request.Content)));
            }

            return await AttemptInlineDeliveryAsync(document, delivery, request.Content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return Result<FinishAndSendResult>.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            return Result<FinishAndSendResult>.Failure($"Błąd podczas kończenia i wysyłki dokumentu: {Flatten(ex)}");
        }
    }

    private async Task<DocumentDelivery> CreateQueuedDeliveryAsync(
        Document document, DocumentVersion version, FinishAndSendDocumentCommand request,
        string recipientUrl, string? corporateKey, CancellationToken cancellationToken)
    {
        var deliveryId = Guid.NewGuid();
        var snapshotObjectName = $"deliveries/{deliveryId}";
        var sha256 = Convert.ToHexString(SHA256.HashData(request.Content));
        await _storage.UploadRawAsync(snapshotObjectName, request.Content, document.MimeType, cancellationToken);

        var delivery = DocumentDelivery.Create(
            id: deliveryId,
            documentId: document.Id,
            sourceVersionId: version.Id,
            snapshotObjectName: snapshotObjectName,
            snapshotSizeBytes: request.Content.Length,
            snapshotSha256: sha256,
            recipientUrl: recipientUrl,
            createdBy: request.CreatedBy ?? document.CreatedBy,
            correlationId: Guid.NewGuid(),
            retentionWindow: RetentionWindow,
            corporateKey: corporateKey);

        await _deliveryRepository.AddAsync(delivery, cancellationToken);
        return delivery;
    }

    private async Task<Result<FinishAndSendResult>> AttemptInlineDeliveryAsync(
        Document document, DocumentDelivery delivery, byte[] content, CancellationToken cancellationToken)
    {
        delivery.BeginInlineAttempt();
        document.MarkSending();
        await _deliveryRepository.SaveChangesAsync(cancellationToken);

        var dispatch = new DeliveryDispatch(
            delivery.Id, delivery.RecipientUrl, content, delivery.SnapshotSha256,
            delivery.DocumentId, delivery.SourceVersionId, delivery.CorporateKey);

        DeliveryResult result;
        try
        {
            result = await _sender.SendAsync(dispatch, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = DeliveryResult.Retryable(Flatten(ex));
        }

        if (result.Outcome == DeliveryOutcome.Succeeded)
        {
            delivery.MarkSent();
            document.MarkSent();
            await _deliveryRepository.SaveChangesAsync(cancellationToken);
            return Result<FinishAndSendResult>.Success(new FinishAndSendResult(
                delivery.Id, delivery.Status.ToString(), document.Status.ToString(), Delivered: true));
        }

        var error = result.Error ?? "Nie udało się dostarczyć dokumentu";
        delivery.HoldAfterFailedInlineAttempt(error);
        document.MarkDeliveryFailed();
        await _deliveryRepository.SaveChangesAsync(cancellationToken);
        return Result<FinishAndSendResult>.Success(new FinishAndSendResult(
            delivery.Id, delivery.Status.ToString(), document.Status.ToString(), Delivered: false, Error: error));
    }

    private static string Flatten(Exception ex)
    {
        var details = ex.Message;
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            details += $" -> {inner.Message}";
        return details;
    }

    private static string? ReadReturnUrl(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<ExternalMetadata>(metadata, JsonOptions);
            return parsed?.ReturnUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ExternalMetadata(string? ReturnUrl, string? Classification);
}
