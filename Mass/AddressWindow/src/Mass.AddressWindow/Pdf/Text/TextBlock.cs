namespace Mass.AddressWindow.Pdf.Text;

internal sealed record TextLine(string Text, RectangleMm Bounds, IReadOnlyList<double> FontSizes, bool Italic, bool Rotated);

/// <summary>Blok tekstu z pierwszej strony: wiersze leżące blisko siebie w jednej kolumnie. Kandydat na adres.</summary>
internal sealed class TextBlock
{
    public TextBlock(IReadOnlyList<TextLine> lines)
    {
        Lines = lines;
        TextBounds = lines.Select(l => l.Bounds).Aggregate((a, b) => a.Union(b));
        IsRotated = lines.Count(l => l.Rotated) * 2 > lines.Count;
        NonLeftAligned = DetectNonLeftAlignment(lines);
    }

    public IReadOnlyList<TextLine> Lines { get; }

    public RectangleMm TextBounds { get; }

    public bool IsRotated { get; }

    public bool NonLeftAligned { get; }

    private static bool DetectNonLeftAlignment(IReadOnlyList<TextLine> lines)
    {
        if (lines.Count < 2)
        {
            return false;
        }

        var lefts = lines.Select(l => l.Bounds.Left).ToList();
        var rights = lines.Select(l => l.Bounds.Right).ToList();
        var centers = lines.Select(l => l.Bounds.Left + l.Bounds.Width / 2).ToList();
        var leftSpread = lefts.Max() - lefts.Min();
        var rightSpread = rights.Max() - rights.Min();
        var centerSpread = centers.Max() - centers.Min();

        return leftSpread > 2 && (rightSpread < 1 || centerSpread < 1);
    }
}

/// <summary>Grafika rastrowa na stronie: kandydat na nalepkę R. Piksele są wczytywane dopiero przy odczycie kodu.</summary>
internal sealed record ImageBlock(RectangleMm Bounds, Func<Rules.Labels.ImagePixels> LoadPixels);
