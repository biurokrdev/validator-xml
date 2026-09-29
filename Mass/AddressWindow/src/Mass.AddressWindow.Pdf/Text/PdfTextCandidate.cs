using Mass.AddressWindow.Rules;

namespace Mass.AddressWindow.Pdf.Text;

internal sealed record PdfTextLine(string Text, RectangleMm Bounds, IReadOnlyList<double> FontSizes, bool Italic, bool Rotated);

internal sealed class PdfTextCandidate : IAddressCandidate
{
    public PdfTextCandidate(IReadOnlyList<PdfTextLine> lines)
    {
        TextLines = lines;
        TextBounds = lines.Select(l => l.Bounds).Aggregate((a, b) => a.Union(b));
        Lines = lines.Select(l => new AddressLine(l.Text, l.FontSizes, l.Italic, Underline: false)).ToList();
        IsRotated = lines.Count(l => l.Rotated) * 2 > lines.Count;
        NonLeftAligned = DetectNonLeftAlignment(lines);
    }

    public IReadOnlyList<PdfTextLine> TextLines { get; }

    public AddressSourceKind Kind => AddressSourceKind.PdfText;

    public DocumentPartKind Part => DocumentPartKind.Body;

    public string? Name => null;

    public RectangleMm Bounds => TextBounds;

    public RectangleMm TextBounds { get; }

    public double OverflowMm => 0;

    public bool Estimated => false;

    public bool IsRotated { get; }

    public bool NonLeftAligned { get; }

    public IReadOnlyList<AddressLine> Lines { get; }

    public bool Matches(string hint) => false;

    private static bool DetectNonLeftAlignment(IReadOnlyList<PdfTextLine> lines)
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
