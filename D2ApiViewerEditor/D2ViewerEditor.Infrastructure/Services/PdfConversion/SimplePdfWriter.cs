using System.Globalization;
using System.Text;

namespace D2ViewerEditor.Infrastructure.Services.PdfConversion;

internal static class SimplePdfWriter
{
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double Margin = 56;

    private const double BodyFontSize = 11;
    private const double BodyLeading = 14.5;
    private const double TitleFontSize = 15;
    private const double FooterFontSize = 8;
    private const double FooterBaseline = 32;

    private const double AverageGlyphWidthRatio = 0.5;

    private static readonly Encoding PdfEncoding = Encoding.Latin1;

    public static byte[] Build(string title, IReadOnlyList<string> lines, string footerNote)
    {
        var pages = Paginate(title, lines);

        var objects = new List<byte[]>();
        var pageObjectNumbers = new List<int>();

        objects.Add(Latin1("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Array.Empty<byte>());
        objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));

        for (var i = 0; i < pages.Count; i++)
        {
            var content = Latin1(BuildContentStream(pages[i], footerNote, i + 1, pages.Count));
            var contentObjectNumber = objects.Count + 2;
            var pageObjectNumber = objects.Count + 1;

            objects.Add(Latin1(
                "<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] " +
                "/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> " +
                $"/Contents {contentObjectNumber} 0 R >>"));

            var stream = new List<byte>();
            stream.AddRange(Latin1($"<< /Length {content.Length} >>\nstream\n"));
            stream.AddRange(content);
            stream.AddRange(Latin1("\nendstream"));
            objects.Add(stream.ToArray());

            pageObjectNumbers.Add(pageObjectNumber);
        }

        var kids = string.Join(" ", pageObjectNumbers.Select(n => $"{n} 0 R"));
        objects[1] = Latin1($"<< /Type /Pages /Kids [ {kids} ] /Count {pageObjectNumbers.Count} >>");

        return Serialize(objects, title);
    }

    private sealed record PdfPage(string? Title, List<string> Lines);

    private static List<PdfPage> Paginate(string title, IReadOnlyList<string> lines)
    {
        var usableWidth = PageWidth - (2 * Margin);
        var maxChars = Math.Max(20, (int)(usableWidth / (BodyFontSize * AverageGlyphWidthRatio)));

        var wrapped = new List<string>();
        foreach (var line in lines)
            wrapped.AddRange(WrapLine(line, maxChars));

        var bodyTop = PageHeight - Margin;
        var bodyBottom = FooterBaseline + (2 * BodyLeading);
        var linesPerPage = Math.Max(1, (int)((bodyTop - bodyBottom) / BodyLeading));
        var linesOnFirstPage = Math.Max(1, linesPerPage - 3);

        var pages = new List<PdfPage>();
        var index = 0;
        while (index < wrapped.Count || pages.Count == 0)
        {
            var isFirst = pages.Count == 0;
            var capacity = isFirst ? linesOnFirstPage : linesPerPage;
            var take = Math.Min(capacity, wrapped.Count - index);
            var chunk = take > 0 ? wrapped.GetRange(index, take) : new List<string>();
            pages.Add(new PdfPage(isFirst ? title : null, chunk));
            index += take;
            if (take == 0) break;
        }

        return pages;
    }

    private static IEnumerable<string> WrapLine(string line, int maxChars)
    {
        var text = ToWinAnsiSafe(line).TrimEnd();
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (candidate.Length <= maxChars)
            {
                current.Clear().Append(candidate);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            var rest = word;
            while (rest.Length > maxChars)
            {
                yield return rest[..maxChars];
                rest = rest[maxChars..];
            }
            current.Append(rest);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string BuildContentStream(PdfPage page, string footerNote, int pageNumber, int pageCount)
    {
        var sb = new StringBuilder();
        var y = PageHeight - Margin;

        if (page.Title != null)
        {
            sb.Append("BT /F2 ").Append(Num(TitleFontSize)).Append(" Tf ")
              .Append(Num(Margin)).Append(' ').Append(Num(y)).Append(" Td (")
              .Append(EscapeText(page.Title)).Append(") Tj ET\n");
            y -= TitleFontSize + (BodyLeading * 1.2);
        }

        if (page.Lines.Count > 0)
        {
            sb.Append("BT /F1 ").Append(Num(BodyFontSize)).Append(" Tf ")
              .Append(Num(BodyLeading)).Append(" TL ")
              .Append(Num(Margin)).Append(' ').Append(Num(y)).Append(" Td\n");

            for (var i = 0; i < page.Lines.Count; i++)
            {
                if (i > 0) sb.Append("T*\n");
                sb.Append('(').Append(EscapeText(page.Lines[i])).Append(") Tj\n");
            }

            sb.Append("ET\n");
        }

        var footer = $"{footerNote} — strona {pageNumber} z {pageCount}";
        sb.Append("BT /F1 ").Append(Num(FooterFontSize)).Append(" Tf ")
          .Append(Num(Margin)).Append(' ').Append(Num(FooterBaseline)).Append(" Td (")
          .Append(EscapeText(ToWinAnsiSafe(footer))).Append(") Tj ET\n");

        return sb.ToString();
    }

    private static byte[] Serialize(List<byte[]> objects, string title)
    {
        using var ms = new MemoryStream();

        void WriteRaw(string s)
        {
            var bytes = PdfEncoding.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteRaw("%PDF-1.4\n");
        ms.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

        var offsets = new long[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            WriteRaw($"{i + 1} 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            WriteRaw("\nendobj\n");
        }

        var infoObjectNumber = objects.Count + 1;
        var infoOffset = ms.Position;
        WriteRaw($"{infoObjectNumber} 0 obj\n<< /Title ({EscapeText(ToWinAnsiSafe(title))}) /Producer (D2 ViewerEditor - mock DOCX to PDF) >>\nendobj\n");

        var xrefOffset = ms.Position;
        var size = objects.Count + 2;
        WriteRaw($"xref\n0 {size}\n");
        WriteRaw("0000000000 65535 f\r\n");
        for (var i = 1; i <= objects.Count; i++)
            WriteRaw($"{offsets[i]:D10} 00000 n\r\n");
        WriteRaw($"{infoOffset:D10} 00000 n\r\n");

        WriteRaw($"trailer\n<< /Size {size} /Root 1 0 R /Info {infoObjectNumber} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] Latin1(string s) => PdfEncoding.GetBytes(s);

    private static string Num(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string EscapeText(string s) =>
        s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string ToWinAnsiSafe(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\t': sb.Append("    "); continue;
                case ' ': sb.Append(' '); continue;
                case 'ą': sb.Append('a'); continue;
                case 'Ą': sb.Append('A'); continue;
                case 'ć': sb.Append('c'); continue;
                case 'Ć': sb.Append('C'); continue;
                case 'ę': sb.Append('e'); continue;
                case 'Ę': sb.Append('E'); continue;
                case 'ł': sb.Append('l'); continue;
                case 'Ł': sb.Append('L'); continue;
                case 'ń': sb.Append('n'); continue;
                case 'Ń': sb.Append('N'); continue;
                case 'ś': sb.Append('s'); continue;
                case 'Ś': sb.Append('S'); continue;
                case 'ź': sb.Append('z'); continue;
                case 'Ź': sb.Append('Z'); continue;
                case 'ż': sb.Append('z'); continue;
                case 'Ż': sb.Append('Z'); continue;
                case '‘':
                case '’': sb.Append('\''); continue;
                case '“':
                case '”': sb.Append('"'); continue;
                case '–':
                case '—': sb.Append('-'); continue;
                case '…': sb.Append("..."); continue;
                case '•': sb.Append('-'); continue;
            }

            if (ch < 0x20) { sb.Append(' '); continue; }
            sb.Append(ch <= 0x7E || (ch >= 0xA1 && ch <= 0xFF) ? ch : '?');
        }

        return sb.ToString();
    }
}
