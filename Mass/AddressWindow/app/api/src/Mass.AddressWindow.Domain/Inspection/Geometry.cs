namespace Mass.AddressWindow.Domain.Inspection;

/// <summary>Prostokąt na stronie pisma, w milimetrach od lewego górnego rogu.</summary>
public readonly record struct AreaMm(double Left, double Top, double Width, double Height);

/// <summary>O ile milimetrów adres wystaje poza okno z każdej strony (0 = mieści się).</summary>
public readonly record struct OverflowMm(double Left, double Top, double Right, double Bottom, string Description)
{
    public static OverflowMm None => new(0, 0, 0, 0, string.Empty);

    public bool Any => Left > 0 || Top > 0 || Right > 0 || Bottom > 0;
}
