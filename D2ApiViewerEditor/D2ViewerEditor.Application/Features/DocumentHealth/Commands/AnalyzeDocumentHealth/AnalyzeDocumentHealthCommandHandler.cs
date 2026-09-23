using D2ViewerEditor.Application.Features.DocumentHealth.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.DocumentHealth.Commands.AnalyzeDocumentHealth;

public class AnalyzeDocumentHealthCommandHandler
    : IRequestHandler<AnalyzeDocumentHealthCommand, Result<DocumentHealthReportDto>>
{
    private readonly IDocumentHealthInspector _inspector;

    public AnalyzeDocumentHealthCommandHandler(IDocumentHealthInspector inspector)
    {
        _inspector = inspector;
    }

    public async Task<Result<DocumentHealthReportDto>> Handle(
        AnalyzeDocumentHealthCommand request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.FileStream.CopyToAsync(buffer, cancellationToken);
        var documentBytes = buffer.ToArray();

        var report = await _inspector.InspectAsync(
            documentBytes,
            request.FileName,
            request.IncludeConversionProbes,
            cancellationToken);

        return Result<DocumentHealthReportDto>.Success(DocumentHealthMapper.ToDto(report));
    }
}
