using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElementDetails;

public class GetStructureElementDetailsQueryHandler
    : IRequestHandler<GetStructureElementDetailsQuery, Result<StructureElementDetailsDto>>
{
    private readonly IDocumentStructureInspectionStore _store;

    public GetStructureElementDetailsQueryHandler(IDocumentStructureInspectionStore store)
    {
        _store = store;
    }

    public Task<Result<StructureElementDetailsDto>> Handle(
        GetStructureElementDetailsQuery request,
        CancellationToken cancellationToken)
    {
        var inspection = _store.Get(request.InspectionId);

        if (inspection is null)
        {
            return Task.FromResult(Result<StructureElementDetailsDto>.NotFound(StructureInspectionErrors.InspectionNotFound));
        }

        if (!inspection.Analysis.ElementsById.TryGetValue(request.ElementId, out var element))
        {
            return Task.FromResult(Result<StructureElementDetailsDto>.NotFound(StructureInspectionErrors.ElementNotFound));
        }

        return Task.FromResult(Result<StructureElementDetailsDto>.Success(StructureInspectionMapper.ToDetails(element)));
    }
}
