using D2ViewerEditor.Application.Features.StructureInspection.Common;
using D2ViewerEditor.Domain.Common;
using MediatR;

namespace D2ViewerEditor.Application.Features.StructureInspection.Commands.AnalyzeDocumentStructure;

public record AnalyzeDocumentStructureCommand(Stream FileStream, string FileName)
    : IRequest<Result<StructureInspectionSummaryDto>>;
