using System.Globalization;

namespace Mass.AddressWindow;

public readonly record struct RectangleMm(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public static RectangleMm FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    public RectangleMm Deflate(double mm) =>
        new(Left + mm, Top + mm, Math.Max(0, Width - 2 * mm), Math.Max(0, Height - 2 * mm));

    public RectangleMm Offset(double dx, double dy) => new(Left + dx, Top + dy, Width, Height);

    public RectangleMm Union(RectangleMm other) => FromEdges(
        Math.Min(Left, other.Left), Math.Min(Top, other.Top),
        Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public bool Contains(RectangleMm other, double toleranceMm = 0.05) =>
        other.Left >= Left - toleranceMm &&
        other.Top >= Top - toleranceMm &&
        other.Right <= Right + toleranceMm &&
        other.Bottom <= Bottom + toleranceMm;

    public double IntersectionArea(RectangleMm other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"[x={Left:0.0}, y={Top:0.0}, {Width:0.0}×{Height:0.0} mm]");
}
