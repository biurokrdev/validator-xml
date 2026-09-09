using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Models;
using MediatR;

namespace D2ViewerEditor.Application.Features.Documents.Commands.GeneratePdfPreview;

public record GeneratePdfPreviewCommand(
    string Html,
    string? OriginalFileName,
    DocumentMetadata? Metadata,
    HeaderFooterContent? Header,
    HeaderFooterContent? Footer,
    PageMargins? Margins = null,
    PageSize? PageSize = null,
    List<SectionHeaderFooter>? SectionHeadersFooters = null,
    List<Footnote>? Footnotes = null,
    List<Endnote>? Endnotes = null,
    Guid? MasterId = null,
    string? FootnoteNumberFormat = null,
    string? EndnoteNumberFormat = null
) : IRequest<Result<GeneratePdfPreviewResult>>;

public record GeneratePdfPreviewResult(byte[] PdfBytes, string FileName);
