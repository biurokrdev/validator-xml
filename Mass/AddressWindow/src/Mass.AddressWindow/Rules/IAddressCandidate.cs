namespace Mass.AddressWindow.Rules;

internal sealed record AddressLine(string Text, IReadOnlyList<double> FontSizes, bool Italic, bool Underline, int VisualLines = 1);

internal interface IAddressCandidate
{
    AddressSourceKind Kind { get; }

    DocumentPartKind Part { get; }

    string? Name { get; }

    RectangleMm Bounds { get; }

    RectangleMm TextBounds { get; }

    double OverflowMm { get; }

    bool Estimated { get; }

    bool IsRotated { get; }

    bool NonLeftAligned { get; }

    IReadOnlyList<AddressLine> Lines { get; }

    bool Matches(string hint);
}
