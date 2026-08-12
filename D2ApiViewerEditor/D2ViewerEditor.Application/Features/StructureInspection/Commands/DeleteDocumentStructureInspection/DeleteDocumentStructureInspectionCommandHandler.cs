using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Commands.DeleteDocumentStructureInspection;

public class DeleteDocumentStructureInspectionCommandHandler
    : IRequestHandler<DeleteDocumentStructureInspectionCommand, Result>
{
    private readonly IDocumentStructureInspectionStore _store;

    public DeleteDocumentStructureInspectionCommandHandler(IDocumentStructureInspectionStore store)
    {
        _store = store;
    }

    public Task<Result> Handle(DeleteDocumentStructureInspectionCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(_store.Delete(request.InspectionId)
            ? Result.Success()
            : Result.NotFound(StructureInspectionErrors.InspectionNotFound));
}
