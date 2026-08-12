using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElementXml;

public class GetStructureElementXmlQueryHandler
    : IRequestHandler<GetStructureElementXmlQuery, Result<StructureElementXmlDto>>
{
    private readonly IDocumentStructureInspectionStore _store;
    private readonly IDocumentStructureInspector _inspector;

    public GetStructureElementXmlQueryHandler(
        IDocumentStructureInspectionStore store,
        IDocumentStructureInspector inspector)
    {
        _store = store;
        _inspector = inspector;
    }

    public Task<Result<StructureElementXmlDto>> Handle(
        GetStructureElementXmlQuery request,
        CancellationToken cancellationToken)
    {
        var inspection = _store.Get(request.InspectionId);

        if (inspection is null)
        {
            return Task.FromResult(Result<StructureElementXmlDto>.NotFound(StructureInspectionErrors.InspectionNotFound));
        }

        if (!inspection.Analysis.ElementsById.TryGetValue(request.ElementId, out var element))
        {
            return Task.FromResult(Result<StructureElementXmlDto>.NotFound(StructureInspectionErrors.ElementNotFound));
        }

        try
        {
            var fragment = _inspector.ReadElementXml(
                inspection.DocumentBytes,
                element.PartPath,
                element.NodePath,
                cancellationToken);

            if (fragment is null)
            {
                return Task.FromResult(Result<StructureElementXmlDto>.NotFound(StructureInspectionErrors.ElementNotFound));
            }

            var dto = new StructureElementXmlDto(
                element.Id,
                element.PartPath,
                element.DisplayPath,
                fragment.Xml,
                fragment.SourceLine);

            return Task.FromResult(Result<StructureElementXmlDto>.Success(dto));
        }
        catch (InvalidOoxmlPackageException exception)
        {
            return Task.FromResult(Result<StructureElementXmlDto>.Failure(exception.Message));
        }
    }
}
