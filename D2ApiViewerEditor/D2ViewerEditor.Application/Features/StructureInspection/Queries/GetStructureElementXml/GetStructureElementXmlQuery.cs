using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureElementXml;

public record GetStructureElementXmlQuery(Guid InspectionId, string ElementId)
    : IRequest<Result<StructureElementXmlDto>>;
