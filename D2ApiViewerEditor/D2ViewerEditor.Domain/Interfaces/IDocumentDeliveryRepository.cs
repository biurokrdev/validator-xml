using D2ViewerEditor.Domain.Entities;

namespace D2ViewerEditor.Domain.Interfaces;

/// <summary>
/// Repozytorium zadań wysyłki (finish-and-send).
/// </summary>
public interface IDocumentDeliveryRepository
{
    Task AddAsync(DocumentDelivery delivery, CancellationToken cancellationToken = default);

    /// <summary>Pobiera zadanie po Id (śledzone — do mutacji statusu).</summary>
    Task<DocumentDelivery?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Zwraca aktywne (nieterminalne) zadanie dla dokumentu, jeżeli istnieje.
    /// Wykorzystywane do idempotencji wielokrotnego kliknięcia "zakończ i wyślij".
    /// </summary>
    Task<DocumentDelivery?> GetActiveByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomowo claimuje paczkę gotowych zadań (FOR UPDATE SKIP LOCKED): ustawia status Sending,
    /// lease (locked_until/locked_by), inkrementuje attempt_count i zwraca zaclaimowane zadania.
    /// Bezpieczne dla wielu instancji workera; przejmuje też zawieszone zadania z wygasłym lease.
    /// </summary>
    Task<IReadOnlyList<DocumentDelivery>> ClaimDueBatchAsync(
        int batchSize, TimeSpan lease, string workerId, CancellationToken cancellationToken = default);

    /// <summary>Lista zadań w danym statusie (monitoring / panel admina) — komplet, najnowsze pierwsze, bez limitu.</summary>
    Task<IReadOnlyList<DocumentDelivery>> GetByStatusAsync(
        DeliveryStatus status, CancellationToken cancellationToken = default);

    /// <summary>Lista WSZYSTKICH zadań niezależnie od statusu (panel admina — filtr „wszystkie") — komplet, bez limitu.</summary>
    Task<IReadOnlyList<DocumentDelivery>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
