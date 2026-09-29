using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

public interface IDocumentRepository
{
    Task<Document?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Document?> GetByIdWithVersionsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentListEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(Document document, CancellationToken cancellationToken = default);

    Task UpdateAsync(Document document, CancellationToken cancellationToken = default);

    Task DeleteAsync(Document document, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);
}
