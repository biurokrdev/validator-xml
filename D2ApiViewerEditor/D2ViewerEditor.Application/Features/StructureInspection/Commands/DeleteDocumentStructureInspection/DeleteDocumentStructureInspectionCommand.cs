using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Commands.DeleteDocumentStructureInspection;

public record DeleteDocumentStructureInspectionCommand(Guid InspectionId) : IRequest<Result>;
