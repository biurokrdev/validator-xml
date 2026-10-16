using Mass.RLabel.Barcode;
using Mass.RLabel.Imaging;
using Mass.RLabel.Numbers;
using SkiaSharp;

namespace Mass.RLabel;

/// <summary>Domyślna implementacja <see cref="IRegisteredLabelRenderer"/> oparta na SkiaSharp.</summary>
public sealed class RegisteredLabelRenderer : IRegisteredLabelRenderer
{
    /// <summary>Czerwień litery R i jej ramki (jak na nalepkach R Poczty Polskiej).</summary>
    public static readonly SKColor RegisteredRed = new(0xD5, 0x00, 0x00);

    /// <inheritdoc/>
    public string RenderBase64(
        string number,
        MailType type,
        double widthMm = RegisteredLabelRequest.DefaultWidthMm,
        double heightMm = RegisteredLabelRequest.DefaultHeightMm,
        LabelImageFormat format = LabelImageFormat.Png) =>
        Render(new RegisteredLabelRequest
        {
            Number = number,
            Type = type,
            WidthMm = widthMm,
            HeightMm = heightMm,
            Format = format,
        }).Base64;

    /// <inheritdoc/>
    public RegisteredLabelImage Render(RegisteredLabelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var number = RegisteredNumber.Parse(request.Number, request.Type, request.ValidateCheckDigit);
        var modules = number.Type == MailType.Domestic
            ? Code128.EncodeGs1(number.BarcodeContent)
            : Code128.Encode(number.BarcodeContent);

        var pxPerMm = request.Dpi / 25.4f;
        var width = (int)Math.Round(request.WidthMm * pxPerMm);
        var height = (int)Math.Round(request.HeightMm * pxPerMm);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        var moduleMm = Draw(canvas, width, height, pxPerMm, modules, number.HumanReadable, request.DrawBorder);

        using var image = surface.Snapshot();
        using var encoded = request.Format == LabelImageFormat.Png
            ? image.Encode(SKEncodedImageFormat.Png, 100)
            : image.Encode(SKEncodedImageFormat.Jpeg, request.JpegQuality);
        var bytes = ImageDpi.Apply(encoded.ToArray(), request.Format, request.Dpi);

        return new RegisteredLabelImage(bytes, request.Format, width, height, request.Dpi, number.Normalized, number.HumanReadable, moduleMm);
    }

    private static void Validate(RegisteredLabelRequest request)
    {
        if (request.WidthMm is <= 0 or > 500 || request.HeightMm is <= 0 or > 500)
        {
            throw new ArgumentException($"Wymiary nalepki muszą być w zakresie 0–500 mm, podano {request.WidthMm}×{request.HeightMm} mm.", nameof(request));
        }

        if (request.Dpi is < 72 or > 2400)
        {
            throw new ArgumentException($"Rozdzielczość musi być w zakresie 72–2400 DPI, podano {request.Dpi}.", nameof(request));
        }

        if (request.JpegQuality is < 1 or > 100)
        {
            throw new ArgumentException($"Jakość JPEG musi być w zakresie 1–100, podano {request.JpegQuality}.", nameof(request));
        }

        if (request.WidthMm * request.Dpi / 25.4 < 120)
        {
            throw new ArgumentException(
                $"Nalepka {request.WidthMm} mm przy {request.Dpi} DPI ma za mało pikseli na kod kreskowy. Zwiększ szerokość albo DPI.",
                nameof(request));
        }
    }

    /// <summary>
    /// Układ wzorowany na nalepce R Poczty Polskiej: bez obramowania, po lewej duża czerwona litera R na wysokość
    /// kodu kreskowego, po prawej kod, pod nimi numer na całą szerokość.
    /// Zwraca szerokość modułu kodu w mm.
    /// </summary>
    private static double Draw(
        SKCanvas canvas, int width, int height, float pxPerMm, bool[] modules, string humanReadable, bool drawBorder)
    {
        var pad = Math.Max(2f, height * 0.04f);
        var stroke = Math.Max(1f, pxPerMm * 0.25f);

        using var black = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var red = new SKPaint { Color = RegisteredRed, IsAntialias = true, Style = SKPaintStyle.Fill };

        if (drawBorder)
        {
            using var line = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = stroke };
            canvas.DrawRect(stroke / 2, stroke / 2, width - stroke, height - stroke, line);
        }

        var inner = new SKRect(pad, pad, width - pad, height - pad);
        var textH = inner.Height * 0.2f;
        var gap = inner.Height * 0.03f;
        var barTop = (float)Math.Round(inner.Top);
        var barBottom = (float)Math.Round(inner.Bottom - textH - gap);
        var barHeight = barBottom - barTop;

        // Litera R: wysokość glifu równa wysokości kodu, podstawa na dolnej krawędzi kodu.
        var rColumnWidth = inner.Width * 0.25f;
        var rRight = DrawCapitalR(canvas, red, inner.Left, barBottom, barHeight, rColumnWidth);

        var right = new SKRect(rRight + pad * 1.5f, inner.Top, inner.Right, inner.Bottom);

        // Kod kreskowy: moduł to całkowita liczba pikseli, żeby kreski były ostre; wyśrodkowany w dostępnej szerokości.
        var totalModules = modules.Length + 2 * Code128.QuietZoneModules;
        var module = Math.Max(1, (int)Math.Floor(right.Width / totalModules));
        var barcodeWidth = modules.Length * module;
        var x0 = (float)Math.Round(right.MidX - barcodeWidth / 2f);
        using var bar = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
        for (var i = 0; i < modules.Length;)
        {
            if (!modules[i])
            {
                i++;
                continue;
            }

            var run = 0;
            while (i + run < modules.Length && modules[i + run])
            {
                run++;
            }

            canvas.DrawRect(x0 + i * module, barTop, run * module, barHeight, bar);
            i += run;
        }

        // Numer pod kodem, na całą szerokość nalepki (jak na oryginale zaczyna się pod literą R).
        var textRect = new SKRect(inner.Left, inner.Bottom - textH, inner.Right, inner.Bottom);
        DrawTextFitted(canvas, humanReadable, EmbeddedFonts.Regular, black, textRect, textH * 0.9f, SKTextAlign.Center);

        return module / pxPerMm;
    }

    /// <summary>Rysuje „R” tak, by glif miał dokładnie zadaną wysokość (i nie przekroczył szerokości kolumny). Zwraca prawą krawędź glifu.</summary>
    private static float DrawCapitalR(SKCanvas canvas, SKPaint paint, float left, float baseline, float glyphHeight, float maxWidth)
    {
        using var font = new SKFont(EmbeddedFonts.Bold, 100) { Subpixel = true, Edging = SKFontEdging.Antialias };
        font.MeasureText("R", out var bounds, paint);
        var scale = Math.Min(glyphHeight / bounds.Height, maxWidth / bounds.Width);
        font.Size = 100 * scale;
        font.MeasureText("R", out bounds, paint);

        // bounds jest względem punktu (0, baseline); przesuwamy tak, by glif zaczynał się na left.
        var x = left - bounds.Left;
        canvas.DrawText("R", x, baseline, SKTextAlign.Left, font, paint);
        return x + bounds.Right;
    }

    /// <summary>Rysuje tekst o zadanej wielkości, pomniejszając go, gdy nie mieści się w prostokącie; wyśrodkowany w pionie.</summary>
    private static void DrawTextFitted(SKCanvas canvas, string text, SKTypeface typeface, SKPaint paint, SKRect rect, float size, SKTextAlign align)
    {
        using var font = new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var textWidth = font.MeasureText(text, paint);
        if (textWidth > rect.Width && textWidth > 0)
        {
            font.Size = size * rect.Width / textWidth;
        }

        var metrics = font.Metrics;
        var textHeight = metrics.Descent - metrics.Ascent;
        var baseline = rect.MidY - textHeight / 2 - metrics.Ascent;
        var x = align switch
        {
            SKTextAlign.Center => rect.MidX,
            SKTextAlign.Right => rect.Right,
            _ => rect.Left,
        };
        canvas.DrawText(text, x, baseline, align, font, paint);
    }
}
