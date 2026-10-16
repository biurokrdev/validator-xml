using Mass.AddressWindow.Samples;

namespace Mass.AddressWindow.Api.External;

// Builds the mock letter as DOCX the way the sample letters are built: address in a page-anchored text box,
// label as a page-anchored picture, both in their envelope windows.
internal static class LetterDocxWriter
{
    public static byte[] Write(LetterOrder order)
    {
        var doc = new WordDocument();
        doc.Header(WordDocument.P(LetterContent.Letterhead, "<w:pStyle w:val=\"Nagwek\"/>", fontPt: 12, bold: true)
                   + WordDocument.P(LetterContent.LetterheadDetails, "<w:pStyle w:val=\"Nagwek\"/>", fontPt: 8));

        var anchors = doc.TextBox(
            LetterContent.RecipientLeftMm - 2.5, LetterContent.RecipientTopMm - 1.3, 72, 30, "AdresOdbiorcy",
            WordDocument.AddressParagraphs(order.Recipient.Lines, fontPt: LetterContent.RecipientFontPt));
        if (LetterContent.LabelPng(order) is { } label)
        {
            anchors += doc.Picture(LetterContent.LabelLeftMm, LetterContent.LabelTopMm, LetterContent.LabelWidthMm, LetterContent.LabelHeightMm, "NalepkaR", label);
        }

        var body = $"<w:p><w:pPr><w:jc w:val=\"right\"/></w:pPr>{anchors}{WordDocument.Run(LetterContent.Date)}</w:p>"
                   + WordDocument.P(LetterContent.Subject, WordDocument.Spacing(beforeMm: 62, afterMm: 4), bold: true)
                   + string.Concat(LetterContent.Body.Select(line => WordDocument.P(line)));
        doc.Body(body);
        return doc.ToBytes("Pismo z aplikacji zewnętrznej (mock)");
    }
}
