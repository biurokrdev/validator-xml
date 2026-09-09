using D2ViewerEditor.Application.Features.Documents.Commands.SaveDocument;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace D2ViewerEditor.Application.Features.Documents.Commands.GeneratePdfPreview;

public class GeneratePdfPreviewCommandHandler
    : IRequestHandler<GeneratePdfPreviewCommand, Result<GeneratePdfPreviewResult>>
{
    private readonly IMediator _mediator;
    private readonly IDocxToPdfConversionService _pdfConverter;
    private readonly ILogger<GeneratePdfPreviewCommandHandler>? _logger;

    public GeneratePdfPreviewCommandHandler(
        IMediator mediator,
        IDocxToPdfConversionService pdfConverter,
        ILogger<GeneratePdfPreviewCommandHandler>? logger = null)
    {
        _mediator = mediator;
        _pdfConverter = pdfConverter;
        _logger = logger;
    }

    public async Task<Result<GeneratePdfPreviewResult>> Handle(
        GeneratePdfPreviewCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Html))
            return Result<GeneratePdfPreviewResult>.Failure("HTML edytora nie może być pusty.");

        var docxResult = await _mediator.Send(
            new SaveDocumentCommand(
                request.Html,
                request.OriginalFileName,
                request.Metadata,
                request.Header,
                request.Footer,
                request.Margins,
                request.PageSize,
                request.SectionHeadersFooters,
                request.Footnotes,
                request.Endnotes,
                request.MasterId,
                request.FootnoteNumberFormat,
                request.EndnoteNumberFormat),
            cancellationToken);

        if (!docxResult.IsSuccess || docxResult.Value == null)
            return Result<GeneratePdfPreviewResult>.Failure(
                docxResult.Error ?? "Nie udało się złożyć dokumentu DOCX do konwersji.");

        try
        {
            var pdf = await _pdfConverter.ConvertAsync(
                docxResult.Value.DocxBytes, docxResult.Value.FileName, cancellationToken);

            return Result<GeneratePdfPreviewResult>.Success(
                new GeneratePdfPreviewResult(pdf.PdfBytes, pdf.FileName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Konwersja DOCX→PDF nie powiodła się dla pliku {FileName}.",
                docxResult.Value.FileName);
            return Result<GeneratePdfPreviewResult>.Failure(
                "Usługa konwersji do PDF jest niedostępna. Spróbuj ponownie później.");
        }
    }
}
