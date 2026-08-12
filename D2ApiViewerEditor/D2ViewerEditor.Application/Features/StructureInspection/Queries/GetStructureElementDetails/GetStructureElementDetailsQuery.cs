using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElementDetails;

public record GetStructureElementDetailsQuery(Guid InspectionId, string ElementId)
    : IRequest<Result<StructureElementDetailsDto>>;
