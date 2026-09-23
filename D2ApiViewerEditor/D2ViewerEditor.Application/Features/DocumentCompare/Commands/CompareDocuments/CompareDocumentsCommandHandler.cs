using D2ViewerEditor.Application.Features.DocumentCompare.Common;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;

namespace D2ViewerEditor.Application.Features.DocumentCompare.Commands.CompareDocuments;

public class CompareDocumentsCommandHandler
    : IRequestHandler<CompareDocumentsCommand, Result<DocumentComparisonReportDto>>
{
    private readonly IDocumentComparer _comparer;

    public CompareDocumentsCommandHandler(IDocumentComparer comparer)
    {
        _comparer = comparer;
    }

    public async Task<Result<DocumentComparisonReportDto>> Handle(
        CompareDocumentsCommand request,
        CancellationToken cancellationToken)
    {
        var left = await ReadAsync(request.LeftStream, cancellationToken);
        var right = await ReadAsync(request.RightStream, cancellationToken);

        var report = _comparer.Compare(
            new DocumentCompareRequest(
                left, request.LeftFileName,
                right, request.RightFileName,
                request.IgnoreRevisionIds,
                request.IgnoreDocumentProperties),
            cancellationToken);

        return Result<DocumentComparisonReportDto>.Success(DocumentCompareMapper.ToDto(report));
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
