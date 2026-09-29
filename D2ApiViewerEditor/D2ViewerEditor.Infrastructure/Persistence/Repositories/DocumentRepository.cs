using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace D2ViewerEditor.Infrastructure.Persistence.Repositories;

/// <summary>
/// Implementacja repozytorium dokumentów z użyciem Entity Framework Core
/// </summary>
public class DocumentRepository : IDocumentRepository
{
    private readonly DocumentDbContext _context;

    public DocumentRepository(DocumentDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, cancellationToken);
    }

    public async Task<Document?> GetByIdWithVersionsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted, cancellationToken);
    }

    /// <summary>
    /// Projekcja do kolumn gridu: jedno zapytanie SQL z podzapytaniem o aktywną wersję
    /// (pokryte unikalnym indeksem częściowym <c>idx_document_versions_unique_active</c>).
    /// Kolumna <c>metadata</c> nie jest czytana; bez LIMIT — grid pokazuje komplet.
    /// </summary>
    public async Task<IReadOnlyList<DocumentListEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await BuildListQuery(_context).ToListAsync(cancellationToken);
    }

    /// <summary>Zapytanie listy admina (wydzielone, aby test mógł sprawdzić wygenerowany SQL bez bazy).</summary>
    internal static IQueryable<DocumentListEntry> BuildListQuery(DocumentDbContext context)
    {
        return context.Documents
            .AsNoTracking()
            .Where(d => !d.IsDeleted)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DocumentListEntry(
                d.Id,
                d.Name,
                d.MimeType,
                d.CreatedAt,
                d.Status,
                d.LastModifiedBy,
                d.Versions.Where(v => v.IsActive).Select(v => (Guid?)v.Id).FirstOrDefault(),
                d.Versions.Where(v => v.IsActive).Select(v => (int?)v.VersionNumber).FirstOrDefault()));
    }

    public async Task AddAsync(Document document, CancellationToken cancellationToken = default)
    {
        await _context.Documents.AddAsync(document, cancellationToken);
    }

    public Task UpdateAsync(Document document, CancellationToken cancellationToken = default)
    {
        _context.Documents.Update(document);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Document document, CancellationToken cancellationToken = default)
    {
        _context.Documents.Remove(document);
        return Task.CompletedTask;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .AnyAsync(d => d.Id == id && !d.IsDeleted, cancellationToken);
    }
}
