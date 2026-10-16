using System.Globalization;

using Mass.AddressWindow.Pdf.Text;

namespace Mass.AddressWindow.Rules.Labels;

/// <summary>Wynik odczytu treści nalepki R z jej obrazu.</summary>
internal sealed record LabelReading(
    int WidthPx,
    int HeightPx,
    Code128Reading? Barcode,
    RegisteredNumberInfo? Number,
    string? Problem)
{
    public string Symbology => Barcode is { IsGs1: true } ? "GS1-128" : "Code 128";
}

/// <summary>Czyta kod kreskowy z obrazu nalepki i rozpoznaje w nim numer nadawczy.</summary>
internal static class LabelReader
{
    public static LabelReading Read(ImageBlock image)
    {
        ImagePixels pixels;
        try
        {
            pixels = image.LoadPixels();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            pixels = ImagePixels.Failed(ex.Message);
        }

        if (pixels.Image is null)
        {
            return new LabelReading(0, 0, null, null, pixels.Problem ?? "nie udało się wczytać obrazu");
        }

        var gray = pixels.Image;
        var barcode = Code128Decoder.Decode(gray);
        if (barcode is null)
        {
            var pxPerMm = image.Bounds.Width > 0 ? gray.Width / image.Bounds.Width : 0;
            return new LabelReading(gray.Width, gray.Height, null, null,
                $"w obrazie {gray.Width}×{gray.Height} px ({pxPerMm.ToString("0.0", CultureInfo.GetCultureInfo("pl-PL"))} px/mm) nie ma czytelnego kodu Code 128");
        }

        return new LabelReading(gray.Width, gray.Height, barcode, RegisteredNumberFormat.Parse(barcode.Text), null);
    }
}
