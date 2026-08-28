using System.Security.Cryptography;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Queries.GetDeliverySnapshotContent;

public class GetDeliverySnapshotContentQueryHandler
    : IRequestHandler<GetDeliverySnapshotContentQuery, Result<DeliverySnapshotContentDto>>
{
    private readonly IDocumentDeliveryRepository _deliveryRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentStorageService _storage;

    public GetDeliverySnapshotContentQueryHandler(
        IDocumentDeliveryRepository deliveryRepository,
        IDocumentRepository documentRepository,
        IDocumentStorageService storage)
    {
        _deliveryRepository = deliveryRepository;
        _documentRepository = documentRepository;
        _storage = storage;
    }

    public async Task<Result<DeliverySnapshotContentDto>> Handle(
        GetDeliverySnapshotContentQuery request, CancellationToken cancellationToken)
    {
        var delivery = await _deliveryRepository.GetByIdAsync(request.DeliveryId, cancellationToken);
        if (delivery == null)
            return Result<DeliverySnapshotContentDto>.NotFound("Nie znaleziono zadania wysyłki");

        var document = await _documentRepository.GetByIdAsync(delivery.DocumentId, cancellationToken);
        if (document == null)
            return Result<DeliverySnapshotContentDto>.NotFound("Nie znaleziono dokumentu zadania wysyłki");

        byte[] content;
        try
        {
            content = await _storage.DownloadAsync(delivery.SnapshotObjectName, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<DeliverySnapshotContentDto>.Failure(
                $"Nie udało się pobrać snapshotu wysyłki z magazynu: {ex.Message}");
        }

        var actualSha256 = Convert.ToHexString(SHA256.HashData(content));
        if (!string.Equals(actualSha256, delivery.SnapshotSha256, StringComparison.OrdinalIgnoreCase))
            return Result<DeliverySnapshotContentDto>.Failure(
                "Snapshot wysyłki w magazynie nie zgadza się ze skrótem zapisanym na zadaniu (SHA-256) — plik jest niespójny.");

        var baseName = Path.GetFileNameWithoutExtension(document.Name);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "dokument";
        var fileName = $"{baseName}_wyslany_{delivery.Id:N}"[..Math.Min(120, baseName.Length + 9 + 32)]
                       + GetExtension(document.MimeType);

        return Result<DeliverySnapshotContentDto>.Success(new DeliverySnapshotContentDto(
            FileName: fileName,
            MimeType: document.MimeType,
            Content: content,
            Sha256: delivery.SnapshotSha256));
    }

    private static string GetExtension(string mimeType) => mimeType switch
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/msword" => ".doc",
        "application/pdf" => ".pdf",
        _ => string.Empty
    };
}
