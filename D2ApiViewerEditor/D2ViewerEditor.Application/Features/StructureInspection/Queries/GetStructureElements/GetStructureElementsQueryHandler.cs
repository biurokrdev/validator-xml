using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using D2ViewerEditor.Domain.Models;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElements;

public class GetStructureElementsQueryHandler
    : IRequestHandler<GetStructureElementsQuery, Result<List<StructureElementDto>>>
{
    private const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    private readonly IDocumentStructureInspectionStore _store;

    public GetStructureElementsQueryHandler(IDocumentStructureInspectionStore store)
    {
        _store = store;
    }

    public Task<Result<List<StructureElementDto>>> Handle(
        GetStructureElementsQuery request,
        CancellationToken cancellationToken)
    {
        var inspection = _store.Get(request.InspectionId);

        if (inspection is null)
        {
            return Task.FromResult(Result<List<StructureElementDto>>.NotFound(StructureInspectionErrors.InspectionNotFound));
        }

        IEnumerable<InspectedElement> elements = inspection.Analysis.Elements;

        if (!string.IsNullOrWhiteSpace(request.PartPath))
        {
            elements = elements.Where(element => element.PartPath.Equals(request.PartPath, Comparison));
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            elements = elements.Where(element => element.Category.Equals(request.Category, Comparison));
        }

        if (!string.IsNullOrWhiteSpace(request.Severity))
        {
            elements = elements.Where(element =>
                StructureInspectionMapper.ToSeverityName(element.HighestSeverity).Equals(request.Severity, Comparison));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            elements = elements.Where(element => Matches(element, request.Search));
        }

        var result = elements.Select(StructureInspectionMapper.ToListItem).ToList();

        return Task.FromResult(Result<List<StructureElementDto>>.Success(result));
    }

    private static bool Matches(InspectedElement element, string search) =>
        element.SearchText.Contains(search, Comparison);
}
