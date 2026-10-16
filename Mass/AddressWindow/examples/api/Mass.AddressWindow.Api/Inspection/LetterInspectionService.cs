using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Conversion;
using Mass.AddressWindow.Api.Documents;
using Mass.AddressWindow.Api.External;
using Mass.AddressWindow.Pdf;
using Microsoft.Extensions.Options;

namespace Mass.AddressWindow.Api.Inspection;

// Both variants end in the same place: a PDF goes to the validation library with whatever is known about the
// letter. A DOCX is first sent to the external service for rendering; a letter ordered from the external
// application carries the data it must contain.
public sealed class LetterInspectionService(
    IPdfAddressWindowValidator validator,
    IDocxToPdfConverter converter,
    IExternalLetterSource externalSource,
    IOptions<EnvelopeProfileOptions> profileOptions)
{
    private readonly EnvelopeProfileOptions _profile = profileOptions.Value;

    // Variant 1: the user uploads a PDF or DOCX. Recipient data is unknown, so the address is only recognised, not matched.
    public Task<LetterInspectionResponse> InspectUploadedAsync(
        string? fileName, byte[] content, EnvelopeType envelope, bool includePreview, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(envelope))
        {
            throw new DocumentRejectedException($"Nieznany rodzaj koperty: {envelope}.");
        }

        var document = LetterDocument.Create(fileName, content);
        return InspectAsync(document, DocumentSource.Upload, envelope, expectedRecipient: null, expectedNumber: null, includePreview, cancellationToken);
    }

    // Variant 2: the document comes from the external application; the submitted data is what the letter must contain.
    public async Task<LetterInspectionResponse> InspectOrderedAsync(
        EnvelopeType envelope, IEnumerable<string>? recipientLines, string? registeredNumber, string? source, bool includePreview, CancellationToken cancellationToken)
    {
        var order = LetterOrder.Create(envelope, Recipient.Create(recipientLines), registeredNumber, source);
        var document = await externalSource.FetchAsync(order, cancellationToken);
        return await InspectAsync(document, DocumentSource.ExternalMock, envelope, order.Recipient.Lines, order.RegisteredNumber, includePreview, cancellationToken);
    }

    // Variant 3: only the form identifier is known, so the address is recognised, not matched; the label number
    // is read from the barcode and checked for validity, not against an expected value.
    public async Task<LetterInspectionResponse> InspectFormAsync(
        EnvelopeType envelope, string? formId, bool includePreview, CancellationToken cancellationToken)
    {
        var form = FormOrder.Create(envelope, formId);
        var document = await externalSource.FetchFormAsync(form, cancellationToken);
        return await InspectAsync(document, DocumentSource.FormMock, envelope, expectedRecipient: null, expectedNumber: null, includePreview, cancellationToken);
    }

    private async Task<LetterInspectionResponse> InspectAsync(
        LetterDocument document,
        DocumentSource source,
        EnvelopeType envelope,
        IReadOnlyList<string>? expectedRecipient,
        string? expectedNumber,
        bool includePreview,
        CancellationToken cancellationToken)
    {
        var pdf = document.Format == DocumentFormat.Pdf
            ? document
            : document.AsConvertedPdf(await converter.ConvertAsync(document, cancellationToken));

        var layout = _profile.Layout;
        var profile = new ValidationProfile
        {
            Layout = layout,
            ExpectedRecipient = expectedRecipient,
            LabelRules = new RegisteredLabelRules { ExpectedNumber = expectedNumber },
        };
        var mode = envelope == EnvelopeType.DoubleWindow ? WindowMode.Double : WindowMode.Single;

        var result = validator.Validate(pdf.Content, mode, profile, new PdfValidationOptions { IncludePreview = includePreview });
        if (!result.IsDocumentReadable)
        {
            throw new UnreadableDocumentException(result.Summary);
        }

        return LetterInspectionResponse.From(
            result,
            envelope,
            layout,
            new ProfileDto(layout.Name, _profile.Confirmed, _profile.Confirmed ? "" : _profile.Note),
            new DocumentDto(document.FileName, document.Format, ConvertedToPdf: document.Format == DocumentFormat.Docx, source));
    }
}
