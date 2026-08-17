using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Commands.DeleteDocument;

public record DeleteDocumentCommand(Guid MasterId) : IRequest<Result<bool>>;
