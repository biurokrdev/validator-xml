using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Mass.AddressWindow.Pdf.Text;

internal sealed class PdfPageGeometry
{
    private const double MmPerPt = 25.4 / 72.0;

    private readonly double _originX;
    private readonly double _originY;
    private readonly double _heightPt;

    public PdfPageGeometry(Page page)
    {
        var crop = page.CropBox.Bounds;
        var rotation = ((page.Rotation.Value % 360) + 360) % 360;

        (_originX, _originY) = rotation switch
        {
            90 => (crop.Bottom, 0),
            180 => (0, 0),
            270 => (0, 0),
            _ => (crop.Left, crop.Bottom),
        };
        _heightPt = page.Height;
        WidthMm = page.Width * MmPerPt;
        HeightMm = page.Height * MmPerPt;
    }

    public double WidthMm { get; }

    public double HeightMm { get; }

    public bool IsA4Portrait => Math.Abs(WidthMm - 210) < 2 && Math.Abs(HeightMm - 297) < 2;

    public (double X, double Y) ToPage(PdfPoint p) =>
        ((p.X - _originX) * MmPerPt, (_heightPt - (p.Y - _originY)) * MmPerPt);

    public RectangleMm ToPage(PdfRectangle r)
    {
        var corners = new[] { ToPage(r.BottomLeft), ToPage(r.BottomRight), ToPage(r.TopLeft), ToPage(r.TopRight) };
        return RectangleMm.FromEdges(
            corners.Min(c => c.X), corners.Min(c => c.Y),
            corners.Max(c => c.X), corners.Max(c => c.Y));
    }
}
