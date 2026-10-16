using Mass.RLabel;
using SkiaSharp;

namespace Mass.AddressWindow.Api.External;

// Draws the mock letter straight into a PDF with SkiaSharp: real text with positions and the label as an image.
internal static class LetterPdfWriter
{
    private const float PtPerMm = 72f / 25.4f;
    private const float PageWidthMm = 210;
    private const float PageHeightMm = 297;

    private static readonly Lazy<SKTypeface> Typeface = new(LoadLiberationSans);

    // Skia objects are not thread-safe and the typeface is shared, so letters are drawn one at a time.
    private static readonly object DrawLock = new();

    public static byte[] Write(LetterOrder order)
    {
        lock (DrawLock)
        {
            return Draw(order);
        }
    }

    private static byte[] Draw(LetterOrder order)
    {
        using var stream = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(stream))
        {
            using var canvas = pdf.BeginPage(PageWidthMm * PtPerMm, PageHeightMm * PtPerMm);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            Text(canvas, paint, LetterContent.Letterhead, 25, 15, 12);
            Text(canvas, paint, LetterContent.LetterheadDetails, 25, 20, 8);
            Text(canvas, paint, LetterContent.Date, 120, 32, 10);

            var lines = order.Recipient.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                Text(canvas, paint, lines[i], LetterContent.RecipientLeftMm, LetterContent.RecipientTopMm + i * LetterContent.RecipientLineStepMm, LetterContent.RecipientFontPt);
            }

            if (LetterContent.LabelPng(order) is { } label)
            {
                using var image = SKImage.FromEncodedData(label);
                canvas.DrawImage(
                    image,
                    Rect(LetterContent.LabelLeftMm, LetterContent.LabelTopMm, LetterContent.LabelWidthMm, LetterContent.LabelHeightMm),
                    new SKSamplingOptions(SKFilterMode.Linear));
            }

            Text(canvas, paint, LetterContent.Subject, 25, 100, 11);
            for (var i = 0; i < LetterContent.Body.Length; i++)
            {
                Text(canvas, paint, LetterContent.Body[i], 25, 110 + i * 6, 10);
            }

            pdf.EndPage();
            pdf.Close();
        }

        return stream.ToArray();
    }

    private static void Text(SKCanvas canvas, SKPaint paint, string text, double leftMm, double baselineTopMm, double sizePt)
    {
        using var font = new SKFont(Typeface.Value, (float)sizePt) { Edging = SKFontEdging.Antialias, Subpixel = true };
        var baselineMm = baselineTopMm + sizePt * 0.8 / PtPerMm;
        canvas.DrawText(text, (float)(leftMm * PtPerMm), (float)(baselineMm * PtPerMm), SKTextAlign.Left, font, paint);
    }

    private static SKRect Rect(double leftMm, double topMm, double widthMm, double heightMm) =>
        new((float)(leftMm * PtPerMm), (float)(topMm * PtPerMm), (float)((leftMm + widthMm) * PtPerMm), (float)((topMm + heightMm) * PtPerMm));

    // The label library ships Liberation Sans (OFL); reusing it keeps the PDF identical on every system.
    private static SKTypeface LoadLiberationSans()
    {
        var assembly = typeof(RegisteredLabelRenderer).Assembly;
        var name = assembly.GetManifestResourceNames().First(n => n.EndsWith("LiberationSans-Regular.ttf", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException("Nie udało się wczytać fontu Liberation Sans.");
    }
}
