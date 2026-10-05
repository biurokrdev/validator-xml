namespace Mass.AddressWindow.Domain.Inspection;

public sealed record PageInfo(int Number, int Count, double WidthMm, double HeightMm);

/// <summary>Podgląd strony w PNG z naniesionymi oknami i wykrytym adresem.</summary>
public sealed record PagePreview(byte[] Png, int WidthPx, int HeightPx, bool RenderedFromPdf);

/// <summary>
/// Wynik sprawdzenia pisma: werdykt, okna koperty i zgłoszenia. Werdykt wynika wyłącznie
/// z zawartości wyniku, nie da się go ustawić z zewnątrz.
/// </summary>
public sealed class LetterInspection
{
    private LetterInspection(
        string fileName,
        EnvelopeType envelope,
        bool isReadable,
        string summary,
        PageInfo? page,
        IReadOnlyList<WindowInspection> windows,
        IReadOnlyList<Finding> documentFindings,
        PagePreview? preview)
    {
        FileName = fileName;
        Envelope = envelope;
        IsReadable = isReadable;
        Summary = summary;
        Page = page;
        Windows = windows;
        DocumentFindings = documentFindings;
        Preview = preview;
    }

    /// <summary>Plik udało się odczytać i sprawdzono okna wymagane dla danej koperty.</summary>
    public static LetterInspection Completed(
        string fileName,
        EnvelopeType envelope,
        string summary,
        PageInfo page,
        IReadOnlyList<WindowInspection> windows,
        IReadOnlyList<Finding> documentFindings,
        PagePreview? preview)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(documentFindings);

        var required = RequiredWindows(envelope);
        if (!windows.Select(w => w.Kind).SequenceEqual(required))
        {
            throw new ArgumentException(
                $"Koperta {envelope} wymaga sprawdzenia okien: {string.Join(", ", required)}.", nameof(windows));
        }

        return new LetterInspection(fileName, envelope, isReadable: true, summary, page, windows, documentFindings, preview);
    }

    /// <summary>Pliku nie udało się odczytać (uszkodzony, zaszyfrowany); powód jest w zgłoszeniach.</summary>
    public static LetterInspection Unreadable(string fileName, EnvelopeType envelope, string summary, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (!findings.Any(f => f.IsError))
        {
            throw new ArgumentException("Nieodczytany dokument musi mieć zgłoszenie z powodem.", nameof(findings));
        }

        return new LetterInspection(fileName, envelope, isReadable: false, summary, page: null, windows: [], findings, preview: null);
    }

    public static IReadOnlyList<WindowKind> RequiredWindows(EnvelopeType envelope) => envelope switch
    {
        EnvelopeType.SingleWindow => [WindowKind.Recipient],
        EnvelopeType.DoubleWindow => [WindowKind.Recipient, WindowKind.Sender],
        _ => throw new ArgumentOutOfRangeException(nameof(envelope), envelope, "Nieznany rodzaj koperty."),
    };

    public string FileName { get; }

    public EnvelopeType Envelope { get; }

    public bool IsReadable { get; }

    public string Summary { get; }

    public PageInfo? Page { get; }

    public IReadOnlyList<WindowInspection> Windows { get; }

    /// <summary>Zgłoszenia dotyczące całego dokumentu, a nie konkretnego okna.</summary>
    public IReadOnlyList<Finding> DocumentFindings { get; }

    public PagePreview? Preview { get; }

    public bool IsValid => IsReadable && Windows.All(w => w.IsValid) && !DocumentFindings.Any(f => f.IsError);
}
