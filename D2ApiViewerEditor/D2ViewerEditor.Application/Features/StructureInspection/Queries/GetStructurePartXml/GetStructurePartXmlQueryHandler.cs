using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructurePartXml;

public class GetStructurePartXmlQueryHandler
    : IRequestHandler<GetStructurePartXmlQuery, Result<StructurePartXmlDto>>
{
    private readonly IDocumentStructureInspectionStore _store;
    private readonly IDocumentStructureInspector _inspector;

    public GetStructurePartXmlQueryHandler(
        IDocumentStructureInspectionStore store,
        IDocumentStructureInspector inspector)
    {
        _store = store;
        _inspector = inspector;
    }

    public Task<Result<StructurePartXmlDto>> Handle(
        GetStructurePartXmlQuery request,
        CancellationToken cancellationToken)
    {
        var inspection = _store.Get(request.InspectionId);

        if (inspection is null)
        {
            return Task.FromResult(Result<StructurePartXmlDto>.NotFound(StructureInspectionErrors.InspectionNotFound));
        }

        var part = inspection.Analysis.Parts.FirstOrDefault(candidate =>
            candidate.Path.Equals(request.PartPath, StringComparison.OrdinalIgnoreCase));

        if (part is null)
        {
            return Task.FromResult(Result<StructurePartXmlDto>.NotFound(StructureInspectionErrors.PartNotFound));
        }

        try
        {
            var xml = _inspector.ReadPartXml(inspection.DocumentBytes, part.Path, cancellationToken);

            if (xml is null)
            {
                return Task.FromResult(Result<StructurePartXmlDto>.NotFound(StructureInspectionErrors.PartNotFound));
            }

            var highlightLine = FindHighlightLine(inspection, part.Path, request.HighlightElementId, cancellationToken);

            return Task.FromResult(Result<StructurePartXmlDto>.Success(new StructurePartXmlDto(
                part.Path,
                xml,
                highlightLine is null ? null : request.HighlightElementId,
                highlightLine)));
        }
        catch (InvalidOoxmlPackageException exception)
        {
            return Task.FromResult(Result<StructurePartXmlDto>.Failure(exception.Message));
        }
    }

    private int? FindHighlightLine(
        DocumentStructureInspection inspection,
        string partPath,
        string? elementId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(elementId) ||
            !inspection.Analysis.ElementsById.TryGetValue(elementId, out var element) ||
            !element.PartPath.Equals(partPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _inspector.FindElementLine(inspection.DocumentBytes, partPath, element.NodePath, cancellationToken);
    }
}
