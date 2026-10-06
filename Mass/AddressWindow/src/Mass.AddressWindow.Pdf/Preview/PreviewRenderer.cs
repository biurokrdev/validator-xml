using Mass.AddressWindow.Pdf.Text;
using PDFtoImage;
using SkiaSharp;

namespace Mass.AddressWindow.Pdf.Preview;

internal static class PreviewRenderer
{
    private static readonly SKColor Ok = new(0x1B, 0x8A, 0x3A);
    private static readonly SKColor Error = new(0xD5, 0x00, 0x00);
    private static readonly SKColor Address = new(0x1E, 0x5A, 0xA8);

    private static readonly Lazy<SKTypeface> Typeface = new(() =>
    {
        using var stream = typeof(PreviewRenderer).Assembly.GetManifestResourceStream("Mass.AddressWindow.Pdf.Fonts.LiberationSans-Regular.ttf")
            ?? throw new InvalidOperationException("Brak osadzonego fontu.");
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException("Nie udało się wczytać fontu.");
    });

    public static ValidationPreview Render(byte[] pdf, int pageIndex, PdfPageText text, IReadOnlyList<WindowCheckResult> windows, int dpi)
    {
        SKBitmap? bitmap = null;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                bitmap = Conversion.ToImage(pdf, page: pageIndex, options: new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true));
            }
            catch (Exception)
            {
                bitmap = null;
            }
        }

        var rendered = bitmap is not null;
        bitmap ??= Schematic(text, dpi);

        using (bitmap)
        {
            var scaleX = bitmap.Width / text.Geometry.WidthMm;
            var scaleY = bitmap.Height / text.Geometry.HeightMm;
            using (var canvas = new SKCanvas(bitmap))
            {
                DrawOverlays(canvas, (float)scaleX, (float)scaleY, windows, dpi);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return new ValidationPreview(encoded.ToArray(), bitmap.Width, bitmap.Height, dpi, rendered);
        }
    }

    private static SKBitmap Schematic(PdfPageText text, int dpi)
    {
        var scale = dpi / 25.4f;
        var bitmap = new SKBitmap((int)Math.Round(text.Geometry.WidthMm * scale), (int)Math.Round(text.Geometry.HeightMm * scale));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var glyph = new SKPaint { Color = new SKColor(0x60, 0x60, 0x60), Style = SKPaintStyle.Fill, IsAntialias = true };
        foreach (var box in text.LetterBoxes)
        {
            canvas.DrawRect(Rect(box, scale, scale), glyph);
        }

        return bitmap;
    }

    private static void DrawOverlays(SKCanvas canvas, float scaleX, float scaleY, IReadOnlyList<WindowCheckResult> windows, int dpi)
    {
        var stroke = Math.Max(1.5f, dpi / 48f);
        var fontPx = Math.Max(9f, 3.2f * dpi / 25.4f);
        using var font = new SKFont(Typeface.Value, fontPx) { Edging = SKFontEdging.Antialias, Subpixel = true };

        foreach (var window in windows)
        {
            var color = window.IsValid ? Ok : Error;
            var area = Rect(window.WindowArea, scaleX, scaleY);

            using var fill = new SKPaint { Color = color.WithAlpha(28), Style = SKPaintStyle.Fill };
            using var line = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true };
            canvas.DrawRect(area, fill);
            canvas.DrawRect(area, line);

            var isLabel = window.Content == WindowContent.RegisteredLabel;
            var status = window.Found ? (window.IsValid ? "OK" : "BŁĄD") : isLabel ? "BRAK NALEPKI R" : "BRAK ADRESU";
            Label(canvas, font, color, $"{window.WindowName}: {status}", area.Left, area.Top - stroke * 2, above: true);

            if (window.ContentBounds is not { } contentBounds)
            {
                continue;
            }

            var what = isLabel ? "nalepka R" : "adres";
            var textRect = Rect(contentBounds, scaleX, scaleY);
            using var addressLine = new SKPaint { Color = Address, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true };
            canvas.DrawRect(textRect, addressLine);

            if (window.Overflow.Any)
            {
                using var outside = new SKPaint { Color = Error.WithAlpha(110), Style = SKPaintStyle.Fill };
                canvas.Save();
                canvas.ClipRect(area, SKClipOperation.Difference);
                canvas.DrawRect(textRect, outside);
                canvas.Restore();
            }

            var caption = window.Overflow.Any ? $"{what} wystaje: {window.Overflow.Describe()}" : what;
            Label(canvas, font, window.Overflow.Any ? Error : Address, caption, textRect.Left, Math.Max(area.Bottom, textRect.Bottom) + stroke * 2, above: false);
        }
    }

    private static void Label(SKCanvas canvas, SKFont font, SKColor color, string text, float x, float y, bool above)
    {
        var width = font.MeasureText(text);
        var metrics = font.Metrics;
        var height = metrics.Descent - metrics.Ascent;
        var top = above ? y - height : y;
        if (top < 0)
        {
            top = y + 2;
        }

        using var background = new SKPaint { Color = SKColors.White.WithAlpha(215), Style = SKPaintStyle.Fill };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var box = new SKRect(x, top, x + width + 6, top + height);
        canvas.DrawRect(box, background);
        canvas.DrawText(text, x + 3, top - metrics.Ascent, SKTextAlign.Left, font, paint);
    }

    private static SKRect Rect(RectangleMm r, float scaleX, float scaleY) =>
        new((float)(r.Left * scaleX), (float)(r.Top * scaleY), (float)(r.Right * scaleX), (float)(r.Bottom * scaleY));
}
