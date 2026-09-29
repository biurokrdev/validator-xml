using D2ViewerEditor.Application.Features.DocumentHealth.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.DocumentHealth.Commands.AnalyzeDocumentHealth;

public record AnalyzeDocumentHealthCommand(Stream FileStream, string FileName, bool IncludeConversionProbes)
    : IRequest<Result<DocumentHealthReportDto>>;
