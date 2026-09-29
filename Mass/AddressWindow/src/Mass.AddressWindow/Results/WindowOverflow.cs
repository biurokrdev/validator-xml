using System.Globalization;

namespace Mass.AddressWindow;

public readonly record struct WindowOverflow(double LeftMm, double TopMm, double RightMm, double BottomMm)
{
    private const double Tolerance = 0.05;

    public static WindowOverflow None => default;

    public bool Any => LeftMm > 0 || TopMm > 0 || RightMm > 0 || BottomMm > 0;

    public double MaxMm => Math.Max(Math.Max(LeftMm, TopMm), Math.Max(RightMm, BottomMm));

    public static WindowOverflow Of(RectangleMm allowed, RectangleMm text) => new(
        Excess(allowed.Left - text.Left),
        Excess(allowed.Top - text.Top),
        Excess(text.Right - allowed.Right),
        Excess(text.Bottom - allowed.Bottom));

    private static double Excess(double value) => value > Tolerance ? Math.Round(value, 1) : 0;

    public string Describe()
    {
        var parts = new List<string>(4);
        if (LeftMm > 0)
        {
            parts.Add($"z lewej o {Mm(LeftMm)}");
        }

        if (RightMm > 0)
        {
            parts.Add($"z prawej o {Mm(RightMm)}");
        }

        if (TopMm > 0)
        {
            parts.Add($"u góry o {Mm(TopMm)}");
        }

        if (BottomMm > 0)
        {
            parts.Add($"u dołu o {Mm(BottomMm)}");
        }

        return string.Join(", ", parts);
    }

    public override string ToString() => Any ? Describe() : "mieści się";

    private static string Mm(double value) => value.ToString("0.0", CultureInfo.GetCultureInfo("pl-PL")) + " mm";
}
