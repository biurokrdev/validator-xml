namespace Mass.AddressWindow;

public enum AddressSourceKind
{
    TextBox,

    VmlTextBox,

    Frame,

    TableCell,

    Paragraphs,

    PdfText,
}

public enum DocumentPartKind
{
    Body,

    Header,
}

public sealed class DetectedAddressBlock
{
    internal DetectedAddressBlock(
        AddressSourceKind kind,
        DocumentPartKind part,
        string? name,
        RectangleMm containerBounds,
        RectangleMm textBounds,
        IReadOnlyList<string> lines,
        bool positionEstimated,
        double? minFontSizePt,
        double? maxFontSizePt)
    {
        Kind = kind;
        Part = part;
        Name = name;
        ContainerBounds = containerBounds;
        TextBounds = textBounds;
        Lines = lines;
        PositionEstimated = positionEstimated;
        MinFontSizePt = minFontSizePt;
        MaxFontSizePt = maxFontSizePt;
    }

    public AddressSourceKind Kind { get; }

    public DocumentPartKind Part { get; }

    public string? Name { get; }

    public RectangleMm ContainerBounds { get; }

    public RectangleMm TextBounds { get; }

    public IReadOnlyList<string> Lines { get; }

    public bool PositionEstimated { get; }

    public double? MinFontSizePt { get; }

    public double? MaxFontSizePt { get; }
}

public sealed class WindowCheckResult
{
    internal WindowCheckResult(
        WindowRole role, AddressWindowSpec window, DetectedAddressBlock? block, IReadOnlyList<ValidationIssue> issues, WindowOverflow overflow)
    {
        Role = role;
        WindowName = window.Name;
        WindowArea = window.Area;
        ClearanceMm = window.ClearanceMm;
        Block = block;
        Issues = issues;
        Overflow = overflow;
    }

    public WindowRole Role { get; }

    public string WindowName { get; }

    public RectangleMm WindowArea { get; }

    public double ClearanceMm { get; }

    public WindowOverflow Overflow { get; }

    public bool Found => Block is not null;

    public DetectedAddressBlock? Block { get; }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool IsValid => Found && Issues.All(i => i.Severity != IssueSeverity.Error);
}

public sealed class AddressWindowValidationResult
{
    internal AddressWindowValidationResult(
        WindowMode mode,
        bool isDocumentReadable,
        IReadOnlyList<WindowCheckResult> windows,
        IReadOnlyList<ValidationIssue> documentIssues)
    {
        Mode = mode;
        IsDocumentReadable = isDocumentReadable;
        Windows = windows;
        Issues = documentIssues.Concat(windows.SelectMany(w => w.Issues)).ToArray();
    }

    internal static AddressWindowValidationResult Unreadable(WindowMode mode, string reason) => new(
        mode,
        isDocumentReadable: false,
        windows: [],
        documentIssues: [new ValidationIssue(IssueCodes.InvalidDocument, IssueSeverity.Error, reason)]);

    public WindowMode Mode { get; }

    public bool IsDocumentReadable { get; }

    public bool IsValid => IsDocumentReadable && Windows.Count > 0 && Windows.All(w => w.IsValid)
        && Issues.All(i => i.Severity != IssueSeverity.Error);

    public IReadOnlyList<WindowCheckResult> Windows { get; }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);

    public WindowCheckResult? For(WindowRole role) => Windows.FirstOrDefault(w => w.Role == role);
}
