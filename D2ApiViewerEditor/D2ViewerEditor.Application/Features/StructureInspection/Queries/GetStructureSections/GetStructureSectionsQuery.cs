using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureSections;

public record GetStructureSectionsQuery(Guid InspectionId) : IRequest<Result<List<DocumentSectionDto>>>;
