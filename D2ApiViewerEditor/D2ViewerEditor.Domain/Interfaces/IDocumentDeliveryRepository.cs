using D2ViewerEditor.Domain.Entities;

namespace D2ViewerEditor.Domain.Interfaces;

public interface IDocumentDeliveryRepository
{
    Task AddAsync(DocumentDelivery delivery, CancellationToken cancellationToken = default);

    Task<DocumentDelivery?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DocumentDelivery?> GetActiveByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentDelivery>> ClaimDueBatchAsync(
        int batchSize, TimeSpan lease, string workerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentDelivery>> GetByStatusAsync(
        DeliveryStatus status, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentDelivery>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
