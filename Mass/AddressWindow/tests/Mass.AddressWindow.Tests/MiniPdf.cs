using System.Globalization;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Mass.AddressWindow.Pdf.Tests;

internal sealed class MiniPdf
{
    private const double PtPerMm = 72 / 25.4;
    private readonly StringBuilder _content = new();
    private readonly double _widthPt;
    private readonly double _heightPt;
    private readonly int _rotate;
    private readonly List<string> _imageObjects = [];

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

    /// <summary>
    /// Grafika rastrowa jako obraz RGB z filtrem Flate, tak jak Word zapisuje PNG w PDF. Bez <paramref name="png"/>
    /// wstawiany jest jeden szary piksel rozciągnięty na prostokąt (obraz bez kodu kreskowego).
    /// </summary>
    public MiniPdf Image(double leftMm, double topMm, double widthMm, double heightMm, byte[]? png = null)
    {
        int width = 1, height = 1;
        byte[] rgb = [0x50, 0x50, 0x50];
        if (png is not null)
        {
            using var bitmap = SKBitmap.Decode(png);
            width = bitmap.Width;
            height = bitmap.Height;
            rgb = new byte[width * height * 3];
            var i = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    rgb[i++] = c.Red;
                    rgb[i++] = c.Green;
                    rgb[i++] = c.Blue;
                }
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rgb);
        }

        return Place(leftMm, topMm, widthMm, heightMm, width, height, "/FlateDecode", compressed.ToArray());
    }

    /// <summary>Obraz JPEG wstawiony wprost z filtrem DCTDecode.</summary>
    public MiniPdf Jpeg(double leftMm, double topMm, double widthMm, double heightMm, byte[] jpeg)
    {
        var info = SKBitmap.DecodeBounds(jpeg);
        return Place(leftMm, topMm, widthMm, heightMm, info.Width, info.Height, "/DCTDecode", jpeg);
    }

    private MiniPdf Place(double leftMm, double topMm, double widthMm, double heightMm, int width, int height, string filter, byte[] data)
    {
        var name = $"/Im{_imageObjects.Count + 1}";
        _imageObjects.Add(
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 "
            + $"/Filter {filter} /Length {data.Length} >>\nstream\n" + Encoding.Latin1.GetString(data) + "\nendstream");
        var x = leftMm * PtPerMm;
        var y = _heightPt - (topMm + heightMm) * PtPerMm;
        _content.Append(CultureInfo.InvariantCulture, $"q {widthMm * PtPerMm:0.###} 0 0 {heightMm * PtPerMm:0.###} {x:0.###} {y:0.###} cm {name} Do Q\n");
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
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {_widthPt:0.##} {_heightPt:0.##}]{(_rotate != 0 ? $" /Rotate {_rotate}" : "")} /Resources << /Font << /F1 5 0 R >>{XObjects()} >> /Contents 4 0 R >>"),
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        objects.AddRange(_imageObjects);

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

    private string XObjects() =>
        _imageObjects.Count == 0
            ? ""
            : " /XObject << " + string.Join(" ", _imageObjects.Select((_, i) => $"/Im{i + 1} {6 + i} 0 R")) + " >>";

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
