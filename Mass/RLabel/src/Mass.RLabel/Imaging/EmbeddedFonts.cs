using SkiaSharp;

namespace Mass.RLabel.Imaging;

/// <summary>Fonty osadzone w bibliotece. Ładowane raz, współdzielone między wątkami.</summary>
internal static class EmbeddedFonts
{
    private static readonly Lazy<SKTypeface> RegularTypeface = new(() => Load("Mass.RLabel.Fonts.LiberationSans-Regular.ttf"));
    private static readonly Lazy<SKTypeface> BoldTypeface = new(() => Load("Mass.RLabel.Fonts.LiberationSans-Bold.ttf"));

    public static SKTypeface Regular => RegularTypeface.Value;

    public static SKTypeface Bold => BoldTypeface.Value;

    private static SKTypeface Load(string resourceName)
    {
        using var stream = typeof(EmbeddedFonts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Brak osadzonego fontu {resourceName}.");
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException($"Nie udało się wczytać fontu {resourceName}.");
    }
}
