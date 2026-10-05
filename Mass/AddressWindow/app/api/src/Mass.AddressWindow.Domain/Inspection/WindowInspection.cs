namespace Mass.AddressWindow.Domain.Inspection;

/// <summary>Blok adresowy wykryty w oknie.</summary>
public sealed record AddressBlock(IReadOnlyList<string> Lines, AreaMm TextBounds, double? MinFontSizePt, double? MaxFontSizePt);

/// <summary>Wynik sprawdzenia jednego okna koperty.</summary>
public sealed class WindowInspection
{
    public WindowInspection(
        WindowKind kind,
        string name,
        AreaMm area,
        double clearanceMm,
        AddressBlock? address,
        OverflowMm overflow,
        IReadOnlyList<Finding> findings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(findings);

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

    public AddressBlock? Address { get; }

    public OverflowMm Overflow { get; }

    public IReadOnlyList<Finding> Findings { get; }

    public bool Found => Address is not null;

    public bool IsValid => Found && !Findings.Any(f => f.IsError);
}
