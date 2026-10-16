namespace Mass.AddressWindow.Rules.Labels;

/// <summary>Obraz w skali szarości (0 = czarny, 255 = biały), wiersz po wierszu, bez wyrównania.</summary>
internal sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height)
        {
            throw new ArgumentException("Rozmiar obrazu nie zgadza się z liczbą pikseli.");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public ReadOnlySpan<byte> Row(int y) => Pixels.AsSpan(y * Width, Width);

    public static byte ToGray(byte r, byte g, byte b) => (byte)((r * 299 + g * 587 + b * 114) / 1000);
}

/// <summary>Wynik wczytania pikseli grafiki: obraz albo powód, dla którego się nie udało.</summary>
internal sealed record ImagePixels(GrayImage? Image, string? Problem)
{
    public static ImagePixels Of(GrayImage image) => new(image, null);

    public static ImagePixels Failed(string problem) => new(null, problem);
}
