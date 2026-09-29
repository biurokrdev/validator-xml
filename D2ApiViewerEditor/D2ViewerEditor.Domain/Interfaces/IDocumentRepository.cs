using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

/// <summary>
/// Repository dla zarządzania dokumentami
/// </summary>
public interface IDocumentRepository
{
    /// <summary>
    /// Pobiera dokument po ID (guid_master)
    /// </summary>
    Task<Document?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pobiera dokument po ID z załadowanymi wersjami
    /// </summary>
    Task<Document?> GetByIdWithVersionsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista WSZYSTKICH nieusuniętych dokumentów (najnowsze pierwsze) jako lekkie wiersze gridu —
    /// bez limitu liczności, bez metadanych i bez kolekcji wersji (projekcja SQL).
    /// </summary>
    Task<IReadOnlyList<DocumentListEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Dodaje nowy dokument
    /// </summary>
    Task AddAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aktualizuje dokument
    /// </summary>
    Task UpdateAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>
    /// TRWALE usuwa dokument (wpis mastera; wersje i zadania wysyłki schodzą kaskadą
    /// ON DELETE CASCADE ze schematu infra/sql). Wymaga SaveChangesAsync.
    /// </summary>
    Task DeleteAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Zapisuje zmiany w bazie danych
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sprawdza czy dokument istnieje
    /// </summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);
}
