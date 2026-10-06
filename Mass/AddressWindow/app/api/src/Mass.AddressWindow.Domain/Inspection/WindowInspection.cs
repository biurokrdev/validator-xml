namespace Mass.AddressWindow.Domain.Inspection;

/// <summary>Blok adresowy wykryty w oknie.</summary>
public sealed record AddressBlock(IReadOnlyList<string> Lines, AreaMm TextBounds, double? MinFontSizePt, double? MaxFontSizePt);

/// <summary>Grafika uznana za nalepkę R w oknie.</summary>
public sealed record RegisteredLabel(AreaMm Bounds, bool PositionEstimated);

/// <summary>Wynik sprawdzenia jednego okna koperty.</summary>
public sealed class WindowInspection
{
    public WindowInspection(
        WindowKind kind,
        string name,
        AreaMm area,
        double clearanceMm,
        WindowContentKind content,
        AddressBlock? address,
        RegisteredLabel? label,
        OverflowMm overflow,
        IReadOnlyList<Finding> findings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(findings);
        if (content == WindowContentKind.Address ? label is not null : address is not null)
        {
            throw new ArgumentException($"Okno {content} nie może mieć wyniku innego rodzaju.", nameof(content));
        }

        Content = content;
        Label = label;

        Kind = kind;
        Name = name;
        Area = area;
        ClearanceMm = clearanceMm;
        Address = address;
        Overflow = overflow;
        Findings = findings;
    }

    public WindowKind Kind { get; }

    public string Name { get; }

    /// <summary>Obszar strony widoczny w oknie przy każdym położeniu kartki w kopercie.</summary>
    public AreaMm Area { get; }

    public double ClearanceMm { get; }

    public WindowContentKind Content { get; }

    /// <summary>Wykryty adres; null w oknie nalepki R.</summary>
    public AddressBlock? Address { get; }

    /// <summary>Wykryta nalepka R; null w oknie adresowym.</summary>
    public RegisteredLabel? Label { get; }

    public OverflowMm Overflow { get; }

    public IReadOnlyList<Finding> Findings { get; }

    public bool Found => Address is not null || Label is not null;

    public bool IsValid => Found && !Findings.Any(f => f.IsError);
}
