using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Queries.GetDocuments;

/// <summary>
/// Lista WSZYSTKICH dokumentów dla panelu administratora (bez limitu, bez paginacji po stronie serwera —
/// grid stronicuje lokalnie). Wiersz niesie tylko kolumny gridu; szczegóły (wersje) doładowuje się osobno.
/// </summary>
public record GetDocumentsQuery : IRequest<Result<List<DocumentListItemDto>>>;

public record DocumentListItemDto(
    Guid MasterId,
    string Name,
    string MimeType,
    DateTime CreatedAt,
    Guid ActiveVersionId,
    int VersionNumber,
    string Status,
    string? LastModifiedBy
);
