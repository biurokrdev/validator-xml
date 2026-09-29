using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Mass.AddressWindow.Tests;

internal static class TestDocx
{
    private const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
        "xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "mc:Ignorable=\"wps wpg\"";

    public static readonly string[] Recipient = ["Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"];

    public static readonly string[] Sender = ["Urząd Gminy Wólka", "ul. Polna 1", "21-100 Lubartów"];

    public static byte[] Create(string body, string? header = null, string? styles = null, bool titlePage = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            var rels = new StringBuilder(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            var headerRef = "";
            if (header is not null)
            {
                rels.Append("<Relationship Id=\"rIdH1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>");
                headerRef = $"<w:headerReference w:type=\"{(titlePage ? "first" : "default")}\" r:id=\"rIdH1\"/>";
                Add(zip, "word/header1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr {Namespaces}>{header}</w:hdr>");
            }

            if (styles is not null)
            {
                rels.Append("<Relationship Id=\"rIdS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
                Add(zip, "word/styles.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles {Namespaces}>{styles}</w:styles>");
            }

            rels.Append("</Relationships>");
            Add(zip, "word/_rels/document.xml.rels", rels.ToString());

            var sectPr = $"<w:sectPr>{headerRef}<w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                         "<w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1417\" w:left=\"1417\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/>" +
                         (titlePage ? "<w:titlePg/>" : "") + "</w:sectPr>";
            Add(zip, "word/document.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Namespaces}><w:body>{body}{sectPr}</w:body></w:document>");
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public static string Emu(double mm) => ((long)Math.Round(mm * 36000)).ToString(CultureInfo.InvariantCulture);

    public static string Twips(double mm) => ((long)Math.Round(mm * 1440 / 25.4)).ToString(CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Runs(IEnumerable<string> lines, double fontPt = 10, string extraRPr = "") =>
        string.Join("<w:r><w:br/></w:r>", lines.Select(l =>
            $"<w:r><w:rPr>{extraRPr}<w:sz w:val=\"{F(fontPt * 2)}\"/></w:rPr><w:t xml:space=\"preserve\">{SecurityElement.Escape(l)}</w:t></w:r>"));

    public static string Paragraphs(IEnumerable<string> lines, double fontPt = 10, string pPr = "") =>
        string.Concat(lines.Select(l =>
            $"<w:p><w:pPr>{(pPr.Contains("<w:spacing") ? "" : "<w:spacing w:before=\"0\" w:after=\"0\"/>")}{pPr}</w:pPr>{Runs([l], fontPt)}</w:p>"));

    public static string TextBox(
        double x, double y, double w, double h, IEnumerable<string> lines,
        double fontPt = 10, string name = "Pole tekstowe 1", string bodyPrExtra = "", string xfrmAttrs = "",
        string relativeFrom = "page", string? content = null)
    {
        return "<w:p><w:r><w:drawing>" + Anchor(x, y, w, h, lines, fontPt, name, bodyPrExtra, xfrmAttrs, relativeFrom, content) +
               "</w:drawing></w:r></w:p>";
    }

    public static string Anchor(
        double x, double y, double w, double h, IEnumerable<string> lines,
        double fontPt = 10, string name = "Pole tekstowe 1", string bodyPrExtra = "", string xfrmAttrs = "",
        string relativeFrom = "page", string? content = null)
    {
        content ??= Paragraphs(lines, fontPt);
        return
            $"<wp:anchor distT=\"0\" distB=\"0\" distL=\"114300\" distR=\"114300\" simplePos=\"0\" relativeHeight=\"1\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
            "<wp:simplePos x=\"0\" y=\"0\"/>" +
            $"<wp:positionH relativeFrom=\"{relativeFrom}\"><wp:posOffset>{Emu(x)}</wp:posOffset></wp:positionH>" +
            $"<wp:positionV relativeFrom=\"{relativeFrom}\"><wp:posOffset>{Emu(y)}</wp:posOffset></wp:positionV>" +
            $"<wp:extent cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/>" +
            $"<wp:docPr id=\"1\" name=\"{name}\"/><wp:cNvGraphicFramePr/>" +
            "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">" +
            "<wps:wsp><wps:cNvSpPr txBox=\"1\"/>" +
            $"<wps:spPr><a:xfrm {xfrmAttrs}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr>" +
            $"<wps:txbx><w:txbxContent>{content}</w:txbxContent></wps:txbx>" +
            $"<wps:bodyPr rot=\"0\" vert=\"horz\" wrap=\"square\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" anchor=\"t\" {bodyPrExtra}><a:noAutofit/></wps:bodyPr>" +
            "</wps:wsp></a:graphicData></a:graphic></wp:anchor>";
    }

    public static string VmlShape(double x, double y, double w, double h, IEnumerable<string> lines, double fontPt = 10) =>
        $"<v:shape id=\"AdresVml\" type=\"#_x0000_t202\" style=\"position:absolute;margin-left:{F(x)}mm;margin-top:{F(y)}mm;width:{F(w)}mm;height:{F(h)}mm;" +
        "mso-position-horizontal-relative:page;mso-position-vertical-relative:page\" stroked=\"f\">" +
        $"<v:textbox inset=\"2.5mm,1.3mm,2.5mm,1.3mm\"><w:txbxContent>{Paragraphs(lines, fontPt)}</w:txbxContent></v:textbox></v:shape>";

    public static string VmlTextBox(double x, double y, double w, double h, IEnumerable<string> lines, double fontPt = 10) =>
        $"<w:p><w:r><w:pict>{VmlShape(x, y, w, h, lines, fontPt)}</w:pict></w:r></w:p>";

    public static string AlternateContentTextBox(double x, double y, double w, double h, IEnumerable<string> lines) =>
        "<w:p><w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing>" +
        Anchor(x, y, w, h, lines) +
        "</w:drawing></mc:Choice><mc:Fallback><w:pict>" +
        VmlShape(x, y, w, h, lines) +
        "</w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p>";

    public static string Frame(double x, double y, double w, double h, IEnumerable<string> lines, double fontPt = 10) =>
        Paragraphs(lines, fontPt,
            $"<w:framePr w:w=\"{Twips(w)}\" w:h=\"{Twips(h)}\" w:hRule=\"exact\" w:wrap=\"around\" w:vAnchor=\"page\" w:hAnchor=\"page\" w:x=\"{Twips(x)}\" w:y=\"{Twips(y)}\"/>");

    public static string PositionedTable(double x, double y, double firstColumn, double secondColumn, double rowHeight, IEnumerable<string> lines) =>
        "<w:tbl><w:tblPr>" +
        $"<w:tblpPr w:leftFromText=\"0\" w:rightFromText=\"0\" w:vertAnchor=\"page\" w:horzAnchor=\"page\" w:tblpX=\"{Twips(x)}\" w:tblpY=\"{Twips(y)}\"/>" +
        "<w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblCellMar><w:left w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"0\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>" +
        $"<w:tblGrid><w:gridCol w:w=\"{Twips(firstColumn)}\"/><w:gridCol w:w=\"{Twips(secondColumn)}\"/></w:tblGrid>" +
        $"<w:tr><w:trPr><w:trHeight w:val=\"{Twips(rowHeight)}\" w:hRule=\"exact\"/></w:trPr>" +
        "<w:tc><w:p/></w:tc>" +
        $"<w:tc>{Paragraphs(lines)}</w:tc></w:tr></w:tbl>";
}
