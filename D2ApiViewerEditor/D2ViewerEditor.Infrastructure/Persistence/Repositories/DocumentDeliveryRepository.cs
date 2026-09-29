using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace D2ViewerEditor.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repozytorium zadań wysyłki. Claim batcha realizowany surowym SQL-em
/// (FOR UPDATE SKIP LOCKED + lease), bezpiecznym dla wielu instancji workera.
/// </summary>
public class DocumentDeliveryRepository : IDocumentDeliveryRepository
{
    private static readonly DeliveryStatus[] ActiveStatuses =
        { DeliveryStatus.Pending, DeliveryStatus.Sending, DeliveryStatus.RetryScheduled };

    private readonly DocumentDbContext _context;

    public DocumentDeliveryRepository(DocumentDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task AddAsync(DocumentDelivery delivery, CancellationToken cancellationToken = default)
    {
        await _context.DocumentDeliveries.AddAsync(delivery, cancellationToken);
    }

    public async Task<DocumentDelivery?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.DocumentDeliveries
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<DocumentDelivery?> GetActiveByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        return await _context.DocumentDeliveries
            .AsNoTracking()
            .Where(d => d.DocumentId == documentId && ActiveStatuses.Contains(d.Status))
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentDelivery>> ClaimDueBatchAsync(
        int batchSize, TimeSpan lease, string workerId, CancellationToken cancellationToken = default)
    {
        // CTE wybiera gotowe (next_attempt_at <= now) ORAZ zawieszone (Sending z wygasłym lease),
        // blokuje wiersze (FOR UPDATE SKIP LOCKED — inne instancje pomijają zajęte) i atomowo
        // przestawia je w Sending z nowym lease + inkrementacją attempt_count.
        const string sql = @"
            WITH due AS (
                SELECT id FROM document_deliveries
                WHERE (status IN ('Pending','RetryScheduled') AND next_attempt_at <= now())
                   OR (status = 'Sending' AND locked_until IS NOT NULL AND locked_until < now())
                ORDER BY next_attempt_at
                LIMIT {0}
                FOR UPDATE SKIP LOCKED
            )
            UPDATE document_deliveries d
            SET status = 'Sending',
                locked_until = now() + make_interval(secs => {1}),
                locked_by = {2},
                attempt_count = attempt_count + 1,
                first_attempt_at = COALESCE(d.first_attempt_at, now()),
                last_attempt_at = now(),
                updated_at = now()
            FROM due
            WHERE d.id = due.id
            RETURNING d.*;";

        return await _context.DocumentDeliveries
            .FromSqlRaw(sql, batchSize, (int)lease.TotalSeconds, workerId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    // Listy admina: bez OFFSET/LIMIT — grid stronicuje lokalnie. Sortowanie pokryte indeksami
    // ix_document_deliveries_created_at / ix_document_deliveries_status_created_at (SQL 012).
    public async Task<IReadOnlyList<DocumentDelivery>> GetByStatusAsync(
        DeliveryStatus status, CancellationToken cancellationToken = default)
    {
        return await BuildListQuery(_context, status).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentDelivery>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await BuildListQuery(_context, status: null).ToListAsync(cancellationToken);
    }

    /// <summary>Zapytanie listy admina (wydzielone, aby test mógł sprawdzić wygenerowany SQL bez bazy).</summary>
    internal static IQueryable<DocumentDelivery> BuildListQuery(DocumentDbContext context, DeliveryStatus? status)
    {
        IQueryable<DocumentDelivery> query = context.DocumentDeliveries.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(d => d.Status == status.Value);
        }
        return query.OrderByDescending(d => d.CreatedAt);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }
}
