using Mass.AddressWindow.Rules.Labels;

namespace Mass.AddressWindow;

public sealed class DetectedAddressBlock
{
    internal DetectedAddressBlock(RectangleMm textBounds, IReadOnlyList<string> lines, double? minFontSizePt, double? maxFontSizePt)
    {
        TextBounds = textBounds;
        Lines = lines;
        MinFontSizePt = minFontSizePt;
        MaxFontSizePt = maxFontSizePt;
    }

    public RectangleMm TextBounds { get; }

    public IReadOnlyList<string> Lines { get; }

    public double? MinFontSizePt { get; }

    public double? MaxFontSizePt { get; }
}

/// <summary>Grafika uznana za nalepkę R w oknie, razem z tym, co udało się odczytać z jej kodu kreskowego.</summary>
public sealed class DetectedLabel
{
    internal DetectedLabel(RectangleMm bounds, LabelReading? reading)
    {
        Bounds = bounds;
        BarcodeRead = reading is not null;
        WidthPx = reading?.WidthPx ?? 0;
        HeightPx = reading?.HeightPx ?? 0;
        if (reading is { Barcode: { } barcode, Number: { } number })
        {
            Symbology = reading.Symbology;
            BarcodeText = barcode.Text;
            BarcodeModulePx = barcode.ModulePx;
            Number = number.Normalized;
            HumanReadableNumber = number.HumanReadable;
            Type = number.Type;
            NumberIsValid = number.IsValid;
        }
    }

    /// <summary>Położenie i rozmiar grafiki na stronie, w mm.</summary>
    public RectangleMm Bounds { get; }

    /// <summary>Czy podjęto próbę odczytu kodu kreskowego (<see cref="RegisteredLabelRules.ReadBarcode"/>).</summary>
    public bool BarcodeRead { get; }

    /// <summary>Rozmiar obrazu w pikselach; 0, gdy obrazu nie udało się wczytać.</summary>
    public int WidthPx { get; }

    public int HeightPx { get; }

    /// <summary>„GS1-128” albo „Code 128”; <c>null</c>, gdy kodu nie udało się odczytać.</summary>
    public string? Symbology { get; }

    public string? BarcodeText { get; }

    /// <summary>Zmierzona szerokość najwęższej kreski kodu w pikselach obrazu.</summary>
    public double? BarcodeModulePx { get; }

    /// <summary>Numer nadawczy po normalizacji (same wielkie litery i cyfry); <c>null</c>, gdy kodu nie udało się odczytać.</summary>
    public string? Number { get; }

    /// <summary>Numer w zapisie z nalepki, np. <c>(00)75900773 1 51200062 1</c>; tylko dla numeru o poprawnej budowie.</summary>
    public string? HumanReadableNumber { get; }

    /// <summary>Krajowy albo zagraniczny; <c>null</c>, gdy tekst z kodu nie ma budowy numeru nadawczego.</summary>
    public RegisteredMailType? Type { get; }

    /// <summary>Numer ma poprawną budowę i cyfrę kontrolną.</summary>
    public bool NumberIsValid { get; }
}

public sealed class WindowCheckResult
{
    internal WindowCheckResult(
        WindowRole role,
        AddressWindowSpec window,
        WindowContent content,
        DetectedAddressBlock? block,
        DetectedLabel? label,
        IReadOnlyList<ValidationIssue> issues,
        WindowOverflow overflow,
        RecipientMatch recipientMatch = RecipientMatch.NotChecked)
    {
        Role = role;
        Content = content;
        WindowName = window.Name;
        WindowArea = window.Area;
        ClearanceMm = window.ClearanceMm;
        Block = block;
        Label = label;
        Issues = issues;
        Overflow = overflow;
        RecipientMatch = recipientMatch;
    }

    public WindowRole Role { get; }

    /// <summary>Czego szukano w oknie: adresu czy nalepki R.</summary>
    public WindowContent Content { get; }

    public string WindowName { get; }

    public RectangleMm WindowArea { get; }

    public double ClearanceMm { get; }

    public WindowOverflow Overflow { get; }

    public bool Found => Block is not null || Label is not null;

    /// <summary>Wykryty adres; null w oknie nalepki R.</summary>
    public DetectedAddressBlock? Block { get; }

    /// <summary>Wykryta nalepka R; null w oknie adresowym.</summary>
    public DetectedLabel? Label { get; }

    public RectangleMm? ContentBounds => Block?.TextBounds ?? Label?.Bounds;

    /// <summary>Czy adres porównano z danymi odbiorcy (tylko z podanym <see cref="ValidationProfile.ExpectedRecipient"/>).</summary>
    public RecipientMatch RecipientMatch { get; }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Błąd przeważa nad brakiem rozstrzygnięcia.</summary>
    public ValidationOutcome Outcome =>
        !Found || Issues.Any(i => i.Severity == IssueSeverity.Error) ? ValidationOutcome.Invalid
        : Issues.Any(i => i.Severity == IssueSeverity.Inconclusive) ? ValidationOutcome.Inconclusive
        : ValidationOutcome.Valid;

    public bool IsValid => Outcome == ValidationOutcome.Valid;
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
        DocumentIssues = documentIssues;
        Issues = documentIssues.Concat(windows.SelectMany(w => w.Issues)).ToArray();
    }

    internal static AddressWindowValidationResult Unreadable(WindowMode mode, string reason) => new(
        mode,
        isDocumentReadable: false,
        windows: [],
        documentIssues: [new ValidationIssue(IssueCodes.InvalidDocument, IssueSeverity.Inconclusive, reason)]);

    public WindowMode Mode { get; }

    public bool IsDocumentReadable { get; }

    /// <summary>
    /// Niepoprawny, gdy którekolwiek okno jest niepoprawne albo dokument ma błąd; nierozstrzygnięty, gdy pliku nie
    /// da się odczytać, strona nie ma tekstu albo któreś okno nie dało się rozstrzygnąć; inaczej poprawny.
    /// </summary>
    public ValidationOutcome Outcome
    {
        get
        {
            if (Windows.Any(w => w.Outcome == ValidationOutcome.Invalid) || Issues.Any(i => i.Severity == IssueSeverity.Error))
            {
                return ValidationOutcome.Invalid;
            }

            if (!IsDocumentReadable || Windows.Count == 0 || Issues.Any(i => i.Severity == IssueSeverity.Inconclusive))
            {
                return ValidationOutcome.Inconclusive;
            }

            return ValidationOutcome.Valid;
        }
    }

    public bool IsValid => Outcome == ValidationOutcome.Valid;

    public IReadOnlyList<WindowCheckResult> Windows { get; }

    /// <summary>Zgłoszenia dotyczące całego dokumentu, a nie konkretnego okna.</summary>
    public IReadOnlyList<ValidationIssue> DocumentIssues { get; }

    /// <summary>Wszystkie zgłoszenia: dokumentu i okien.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);

    public WindowCheckResult? For(WindowRole role) => Windows.FirstOrDefault(w => w.Role == role);
}
