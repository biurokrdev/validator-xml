using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Queries.GetDocuments;

public class GetDocumentsQueryHandler : IRequestHandler<GetDocumentsQuery, Result<List<DocumentListItemDto>>>
{
    private readonly IDocumentRepository _documentRepository;

    public GetDocumentsQueryHandler(IDocumentRepository documentRepository)
    {
        _documentRepository = documentRepository;
    }

    public async Task<Result<List<DocumentListItemDto>>> Handle(GetDocumentsQuery request, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await _documentRepository.ListAsync(cancellationToken);

            var dtos = entries.Select(e => new DocumentListItemDto(
                MasterId: e.Id,
                Name: e.Name,
                MimeType: e.MimeType,
                CreatedAt: e.CreatedAt,
                ActiveVersionId: e.ActiveVersionId ?? Guid.Empty,
                VersionNumber: e.ActiveVersionNumber ?? 0,
                Status: e.Status.ToString(),
                LastModifiedBy: e.LastModifiedBy
            )).ToList();

            return Result<List<DocumentListItemDto>>.Success(dtos);
        }
        catch (Exception ex)
        {
            return Result<List<DocumentListItemDto>>.Failure($"Błąd podczas pobierania dokumentów: {ex.Message}");
        }
    }
}
