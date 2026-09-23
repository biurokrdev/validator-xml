using D2ViewerEditor.Application.Features.DocumentCompare.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.DocumentCompare.Commands.CompareDocuments;

public record CompareDocumentsCommand(
    Stream LeftStream,
    string LeftFileName,
    Stream RightStream,
    string RightFileName,
    bool IgnoreRevisionIds,
    bool IgnoreDocumentProperties)
    : IRequest<Result<DocumentComparisonReportDto>>;
