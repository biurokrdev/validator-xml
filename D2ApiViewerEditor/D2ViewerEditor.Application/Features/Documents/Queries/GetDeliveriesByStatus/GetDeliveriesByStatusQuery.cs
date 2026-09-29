using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Queries.GetDeliveriesByStatus;

/// <summary>
/// Lista zadań wysyłki (monitoring / panel admina). `Status` puste/null lub "all"
/// (case-insensitive) = wszystkie statusy; w przeciwnym razie filtr po konkretnym statusie.
/// Zwraca KOMPLET pasujących zadań (bez limitu) — grid stronicuje lokalnie.
/// </summary>
public record GetDeliveriesByStatusQuery(string? Status = null)
    : IRequest<Result<IReadOnlyList<DeliveryListItemDto>>>;

public record DeliveryListItemDto(
    Guid DeliveryId,
    Guid DocumentId,
    string Status,
    int AttemptCount,
    DateTime CreatedAt,
    DateTime? LastAttemptAt,
    DateTime? NextAttemptAt,
    DateTime DeadlineAt,
    string? LastError,
    DateTime? LockedUntil,
    string? LockedBy,
    Guid SourceVersionId,
    string RecipientUrl,
    string? CorporateKey);

public class GetDeliveriesByStatusQueryHandler
    : IRequestHandler<GetDeliveriesByStatusQuery, Result<IReadOnlyList<DeliveryListItemDto>>>
{
    private readonly IDocumentDeliveryRepository _deliveryRepository;

    public GetDeliveriesByStatusQueryHandler(IDocumentDeliveryRepository deliveryRepository)
    {
        _deliveryRepository = deliveryRepository;
    }

    public async Task<Result<IReadOnlyList<DeliveryListItemDto>>> Handle(
        GetDeliveriesByStatusQuery request, CancellationToken cancellationToken)
    {
        var raw = request.Status?.Trim();
        var allStatuses = string.IsNullOrEmpty(raw)
            || raw.Equals("all", StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<DocumentDelivery> items;
        if (allStatuses)
        {
            items = await _deliveryRepository.GetAllAsync(cancellationToken);
        }
        else if (Enum.TryParse<DeliveryStatus>(raw, ignoreCase: true, out var status))
        {
            items = await _deliveryRepository.GetByStatusAsync(status, cancellationToken);
        }
        else
        {
            return Result<IReadOnlyList<DeliveryListItemDto>>.Failure(
                $"Nieznany status: {request.Status}");
        }

        var dtos = items
            .Select(d => new DeliveryListItemDto(
                d.Id, d.DocumentId, d.Status.ToString(), d.AttemptCount,
                d.CreatedAt, d.LastAttemptAt, d.NextAttemptAt, d.DeadlineAt, d.LastError,
                d.LockedUntil, d.LockedBy, d.SourceVersionId, d.RecipientUrl, d.CorporateKey))
            .ToList();

        return Result<IReadOnlyList<DeliveryListItemDto>>.Success(dtos);
    }
}
