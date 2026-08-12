using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Queries.GetStructureSchemaIssues;

public record GetStructureSchemaIssuesQuery(Guid InspectionId, string? TargetVersion)
    : IRequest<Result<SchemaIssuesDto>>;
