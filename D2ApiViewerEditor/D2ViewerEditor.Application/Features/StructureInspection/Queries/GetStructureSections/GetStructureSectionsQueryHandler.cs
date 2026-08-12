using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureSections;

public class GetStructureSectionsQueryHandler
    : IRequestHandler<GetStructureSectionsQuery, Result<List<DocumentSectionDto>>>
{
    private readonly IDocumentStructureInspectionStore _store;

    public GetStructureSectionsQueryHandler(IDocumentStructureInspectionStore store)
    {
        _store = store;
    }

    public Task<Result<List<DocumentSectionDto>>> Handle(
        GetStructureSectionsQuery request,
        CancellationToken cancellationToken)
    {
        var inspection = _store.Get(request.InspectionId);

        if (inspection is null)
        {
            return Task.FromResult(
                Result<List<DocumentSectionDto>>.NotFound(StructureInspectionErrors.InspectionNotFound));
        }

        var sections = inspection.Analysis.Sections.Select(StructureInspectionMapper.ToDto).ToList();

        return Task.FromResult(Result<List<DocumentSectionDto>>.Success(sections));
    }
}
