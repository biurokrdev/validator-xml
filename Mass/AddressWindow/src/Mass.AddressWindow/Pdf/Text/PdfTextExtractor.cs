using System.Text;
using Mass.AddressWindow.Rules.Labels;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Mass.AddressWindow.Pdf.Text;

internal sealed class PdfPageText
{
    public required PdfPageGeometry Geometry { get; init; }

    public required IReadOnlyList<TextBlock> Candidates { get; init; }

    public required IReadOnlyList<RectangleMm> LetterBoxes { get; init; }

    /// <summary>Grafiki rastrowe na stronie (kandydaci na nalepkę R), w milimetrach strony.</summary>
    public required IReadOnlyList<ImageBlock> Images { get; init; }

    public bool HasText => LetterBoxes.Count > 0;
}

internal static class PdfTextExtractor
{
    private const double AscentEm = 0.8;
    private const double AccentedAscentEm = 0.92;
    private const double DescentEm = 0.22;
    private const double SameLineToleranceEm = 0.35;
    private const double ColumnGapEm = 3.0;
    private const double WordGapEm = 0.25;
    private const double BlockGapFactor = 0.6;
    private const string AccentedCapitals = "ĄĆĘŁŃÓŚŹŻ";

    private sealed record Glyph(string Text, double Along, double Cross, double Size, double AlongEnd, RectangleMm Box, bool Italic, bool Horizontal);

    public static PdfPageText Extract(Page page)
    {
        var geometry = new PdfPageGeometry(page);
        var glyphs = page.Letters
            .Where(l => !string.IsNullOrEmpty(l.Value) && l.PointSize > 0)
            .Select(l => ToGlyph(l, geometry))
            .ToList();

        var lines = new List<TextLine>();
        foreach (var orientation in glyphs.GroupBy(g => g.Horizontal))
        {
            lines.AddRange(BuildLines(orientation.ToList(), orientation.Key));
        }

        var candidates = BuildBlocks(lines).Select(block => new TextBlock(block)).ToList();
        var letterBoxes = glyphs.Where(g => !string.IsNullOrWhiteSpace(g.Text)).Select(g => g.Box).ToList();

        var images = new List<ImageBlock>();
        foreach (var image in page.GetImages())
        {
            var bounds = geometry.ToPage(image.BoundingBox);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            // Bajty bierzemy od razu, bo dokument PDF jest zamykany zanim okna zostaną sprawdzone.
            byte[]? encoded;
            try
            {
                encoded = image.TryGetPng(out var png) ? png : image.RawMemory.ToArray();
            }
            catch (Exception)
            {
                encoded = null;
            }

            images.Add(new ImageBlock(bounds, () => DecodePixels(encoded)));
        }

        return new PdfPageText { Geometry = geometry, Candidates = candidates, LetterBoxes = letterBoxes, Images = images };
    }

    /// <summary>PNG (z PdfPig) albo JPEG (DCTDecode wprost z PDF) do skali szarości przez SkiaSharp.</summary>
    private static ImagePixels DecodePixels(byte[]? encoded)
    {
        if (encoded is null || encoded.Length == 0)
        {
            return ImagePixels.Failed("nie udało się wydobyć obrazu z PDF");
        }

        var info = SKBitmap.DecodeBounds(encoded);
        if (info.IsEmpty)
        {
            return ImagePixels.Failed("obraz w PDF jest w formacie, którego nie da się zdekodować");
        }

        // Skia nie dekoduje wprost do Gray8, więc bierzemy 32-bitowe piksele i liczymy szarość sami,
        // składając przezroczystość na białym tle (nalepka na białym papierze).
        using var decoded = SKBitmap.Decode(encoded);
        if (decoded is null)
        {
            return ImagePixels.Failed("nie udało się zdekodować obrazu z PDF");
        }

        using var bitmap = decoded.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888
            ? decoded
            : decoded.Copy(SKColorType.Bgra8888) ?? throw new InvalidOperationException("nie udało się przekształcić obrazu");
        var (ri, gi, bi) = bitmap.ColorType == SKColorType.Rgba8888 ? (0, 1, 2) : (2, 1, 0);
        var pixels = new byte[bitmap.Width * bitmap.Height];
        var source = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = source.Slice(y * bitmap.RowBytes, bitmap.Width * 4);
            for (var x = 0; x < bitmap.Width; x++)
            {
                var o = x * 4;
                int a = row[o + 3];
                var gray = GrayImage.ToGray(row[o + ri], row[o + gi], row[o + bi]);
                pixels[y * bitmap.Width + x] = (byte)((gray * a + 255 * (255 - a)) / 255);
            }
        }

        return ImagePixels.Of(new GrayImage(bitmap.Width, bitmap.Height, pixels));
    }

    private static Glyph ToGlyph(Letter letter, PdfPageGeometry geometry)
    {
        var start = letter.StartBaseLine;
        var end = letter.EndBaseLine;
        var size = letter.PointSize;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6)
        {
            (dx, dy, length) = letter.TextOrientation switch
            {
                TextOrientation.Rotate90 => (0, 1, 1),
                TextOrientation.Rotate180 => (-1, 0, 1),
                TextOrientation.Rotate270 => (0, -1, 1),
                _ => (1, 0, 1),
            };
            end = new PdfPoint(start.X + dx * size * 0.5, start.Y + dy * size * 0.5);
        }

        var (ux, uy) = (dx / length, dy / length);
        var (nx, ny) = (-uy, ux);
        var ascent = size * (AccentedCapitals.Contains(letter.Value[0]) ? AccentedAscentEm : AscentEm);
        var descent = size * DescentEm;

        var corners = new[]
        {
            geometry.ToPage(new PdfPoint(start.X - nx * descent, start.Y - ny * descent)),
            geometry.ToPage(new PdfPoint(start.X + nx * ascent, start.Y + ny * ascent)),
            geometry.ToPage(new PdfPoint(end.X - nx * descent, end.Y - ny * descent)),
            geometry.ToPage(new PdfPoint(end.X + nx * ascent, end.Y + ny * ascent)),
        };
        var box = RectangleMm.FromEdges(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));

        var pageStart = geometry.ToPage(start);
        var pageEnd = geometry.ToPage(end);
        var horizontal = Math.Abs(pageEnd.X - pageStart.X) >= Math.Abs(pageEnd.Y - pageStart.Y);
        var sizeMm = size * 25.4 / 72;

        return horizontal
            ? new Glyph(letter.Value, pageStart.X, pageStart.Y, sizeMm, pageEnd.X, box, letter.FontDetails.IsItalic, true)
            : new Glyph(letter.Value, pageStart.Y, pageStart.X, sizeMm, pageEnd.Y, box, letter.FontDetails.IsItalic, false);
    }

    private static IEnumerable<TextLine> BuildLines(List<Glyph> glyphs, bool horizontal)
    {
        var rows = new List<List<Glyph>>();
        foreach (var glyph in glyphs.OrderBy(g => g.Cross))
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(glyph.Cross - row[^1].Cross) <= SameLineToleranceEm * Math.Max(glyph.Size, row[^1].Size))
            {
                row.Add(glyph);
            }
            else
            {
                rows.Add([glyph]);
            }
        }

        foreach (var row in rows)
        {
            var ordered = row.OrderBy(g => Math.Min(g.Along, g.AlongEnd)).ToList();
            var segment = new List<Glyph>();
            foreach (var glyph in ordered)
            {
                if (segment.Count > 0)
                {
                    var previous = segment[^1];
                    var gap = Math.Min(glyph.Along, glyph.AlongEnd) - Math.Max(previous.Along, previous.AlongEnd);
                    if (gap > ColumnGapEm * Math.Max(glyph.Size, previous.Size))
                    {
                        if (ToLine(segment, horizontal) is { } line)
                        {
                            yield return line;
                        }

                        segment = [];
                    }
                }

                segment.Add(glyph);
            }

            if (ToLine(segment, horizontal) is { } last)
            {
                yield return last;
            }
        }
    }

    private static TextLine? ToLine(List<Glyph> segment, bool horizontal)
    {
        var visible = segment.Where(g => !string.IsNullOrWhiteSpace(g.Text)).ToList();
        if (visible.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        Glyph? previous = null;
        foreach (var glyph in segment)
        {
            if (previous is not null)
            {
                var gap = Math.Min(glyph.Along, glyph.AlongEnd) - Math.Max(previous.Along, previous.AlongEnd);
                var lastIsSpace = text.Length > 0 && char.IsWhiteSpace(text[^1]);
                if (gap > WordGapEm * glyph.Size && !lastIsSpace && !string.IsNullOrWhiteSpace(glyph.Text))
                {
                    text.Append(' ');
                }
            }

            text.Append(glyph.Text);
            previous = glyph;
        }

        var bounds = visible.Select(g => g.Box).Aggregate((a, b) => a.Union(b));
        var sizes = visible.Select(g => Math.Round(g.Size * 72 / 25.4, 1)).ToList();
        return new TextLine(text.ToString().Trim(), bounds, sizes, visible.Any(g => g.Italic), Rotated: !horizontal);
    }

    private static IEnumerable<List<TextLine>> BuildBlocks(List<TextLine> lines)
    {
        var blocks = new List<List<TextLine>>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Top).ThenBy(l => l.Bounds.Left))
        {
            List<TextLine>? target = null;
            foreach (var block in blocks)
            {
                var last = block[^1];
                if (last.Rotated != line.Rotated)
                {
                    continue;
                }

                var gap = line.Bounds.Top - last.Bounds.Bottom;
                var overlapX = Math.Min(line.Bounds.Right, last.Bounds.Right) - Math.Max(line.Bounds.Left, last.Bounds.Left);
                if (gap <= BlockGapFactor * Math.Max(line.Bounds.Height, last.Bounds.Height) && gap > -last.Bounds.Height * 0.5 && overlapX > 0)
                {
                    target = block;
                    break;
                }
            }

            if (target is null)
            {
                blocks.Add([line]);
            }
            else
            {
                target.Add(line);
            }
        }

        return blocks;
    }
}
