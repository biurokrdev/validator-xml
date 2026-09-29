using Mass.AddressWindow.Rules;

namespace Mass.AddressWindow.Docx;

internal enum VerticalAnchor
{
    Top,
    Center,
    Bottom,
}

internal sealed class TextBlockCandidate : IAddressCandidate
{
    public required AddressSourceKind Kind { get; init; }

    public required DocumentPartKind Part { get; init; }

    public string? Name { get; init; }

    public required IReadOnlyCollection<string> Identifiers { get; init; }

    public required RectangleMm Bounds { get; init; }

    public required RectangleMm TextBounds { get; init; }

    public double OverflowMm { get; init; }

    public bool Estimated { get; init; }

    public double RotationDeg { get; init; }

    public bool VerticalText { get; init; }

    public required IReadOnlyList<ParagraphContent> Paragraphs { get; init; }

    public IReadOnlyList<AddressLine> Lines => Paragraphs
        .SelectMany(p => p.Lines)
        .Select(l => new AddressLine(l.Text, l.FontSizes, l.Italic, l.Underline, l.VisualLines))
        .ToList();

    public bool NonLeftAligned => Paragraphs
        .Where(p => p.HasVisibleText)
        .Any(p => p.Props.Alignment is Alignment.Center or Alignment.Right);

    public bool IsRotated => VerticalText || Math.Abs(NormalizedRotation) > 0.5;

    private double NormalizedRotation
    {
        get
        {
            var r = RotationDeg % 360;
            if (r > 180)
            {
                r -= 360;
            }
            else if (r < -180)
            {
                r += 360;
            }

            return r;
        }
    }

    public bool Matches(string hint) =>
        Identifiers.Any(id => id.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
