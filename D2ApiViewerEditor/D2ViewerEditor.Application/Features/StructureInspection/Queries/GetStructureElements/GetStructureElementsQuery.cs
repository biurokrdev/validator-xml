using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElements;

public record GetStructureElementsQuery(
    Guid InspectionId,
    string? PartPath = null,
    string? Category = null,
    string? Severity = null,
    string? Search = null) : IRequest<Result<List<StructureElementDto>>>;
