using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructurePartXml;

public record GetStructurePartXmlQuery(Guid InspectionId, string PartPath, string? HighlightElementId)
    : IRequest<Result<StructurePartXmlDto>>;
