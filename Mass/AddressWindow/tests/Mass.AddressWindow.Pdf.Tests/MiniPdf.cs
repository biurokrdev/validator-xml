using System.Globalization;
using System.Text;

namespace Mass.AddressWindow.Pdf.Tests;

internal sealed class MiniPdf
{
    private const double PtPerMm = 72 / 25.4;
    private readonly StringBuilder _content = new();
    private readonly double _widthPt;
    private readonly double _heightPt;
    private readonly int _rotate;
    private bool _hasImage;

    public MiniPdf(double widthMm = 210, double heightMm = 297, int rotate = 0)
    {
        _widthPt = widthMm * PtPerMm;
        _heightPt = heightMm * PtPerMm;
        _rotate = rotate;
    }

    public MiniPdf Text(double leftMm, double topMm, double fontPt, params string[] lines)
    {
        var lineHeightMm = fontPt * 1.2 / PtPerMm;
        for (var i = 0; i < lines.Length; i++)
        {
            var baselineTopMm = topMm + i * lineHeightMm + fontPt * 0.72 / PtPerMm;
            var x = leftMm * PtPerMm;
            var y = _heightPt - baselineTopMm * PtPerMm;
            _content.Append(CultureInfo.InvariantCulture, $"BT /F1 {fontPt} Tf 1 0 0 1 {x:0.##} {y:0.##} Tm ({Escape(lines[i])}) Tj ET\n");
        }

        return this;
    }

    /// <summary>Grafika rastrowa (szary piksel rozciągnięty na prostokąt), tak jak nalepka R wstawiona jako obraz.</summary>
    public MiniPdf Image(double leftMm, double topMm, double widthMm, double heightMm)
    {
        _hasImage = true;
        var x = leftMm * PtPerMm;
        var y = _heightPt - (topMm + heightMm) * PtPerMm;
        _content.Append(CultureInfo.InvariantCulture, $"q {widthMm * PtPerMm:0.###} 0 0 {heightMm * PtPerMm:0.###} {x:0.###} {y:0.###} cm /Im1 Do Q\n");
        return this;
    }

    public MiniPdf RotatedText(double xPt, double yPt, double fontPt, string text)
    {
        _content.Append(CultureInfo.InvariantCulture, $"BT /F1 {fontPt} Tf 0 1 -1 0 {xPt:0.##} {yPt:0.##} Tm ({Escape(text)}) Tj ET\n");
        return this;
    }

    public byte[] Build()
    {
        var content = _content.ToString();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {_widthPt:0.##} {_heightPt:0.##}]{(_rotate != 0 ? $" /Rotate {_rotate}" : "")} /Resources << /Font << /F1 5 0 R >>{(_hasImage ? " /XObject << /Im1 6 0 R >>" : "")} >> /Contents 4 0 R >>"),
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (_hasImage)
        {
            objects.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\nP\nendstream");
        }

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
