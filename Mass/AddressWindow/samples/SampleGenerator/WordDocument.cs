using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace SampleGenerator;

internal sealed class WordDocument
{
    private const string Namespaces =
        "xmlns:wpc=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
        "xmlns:wp14=\"http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:w10=\"urn:schemas-microsoft-com:office:word\" " +
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" " +
        "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "mc:Ignorable=\"w14 wp14\"";

    private const string Styles =
        "<w:docDefaults>" +
        "<w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/>" +
        "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"pl-PL\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"259\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>" +
        "</w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normalny\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Adres\"><w:name w:val=\"Adres\"/><w:basedOn w:val=\"Normalny\"/><w:qFormat/>" +
        "<w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\"/><w:sz w:val=\"20\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Nagwek\"><w:name w:val=\"header\"/><w:basedOn w:val=\"Normalny\"/>" +
        "<w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:style>";

    private readonly StringBuilder _body = new();
    private readonly StringBuilder _header = new();
    private readonly List<byte[]> _images = [];
    private int _shapeId = 1;

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Emu(double mm) => ((long)Math.Round(mm * 36000)).ToString(CultureInfo.InvariantCulture);

    private static string Twips(double mm) => ((long)Math.Round(mm * 1440 / 25.4)).ToString(CultureInfo.InvariantCulture);

    private static string Esc(string s) => SecurityElement.Escape(s);

    public WordDocument Body(string xml)
    {
        _body.Append(xml);
        return this;
    }

    public WordDocument Header(string xml)
    {
        _header.Append(xml);
        return this;
    }


    public static string Run(string text, double? fontPt = null, bool bold = false, bool italic = false)
    {
        var rPr = (bold ? "<w:b/>" : "") + (italic ? "<w:i/>" : "") + (fontPt is { } pt ? $"<w:sz w:val=\"{F(pt * 2)}\"/>" : "");
        return $"<w:r>{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r>";
    }

    public static string P(string text, string pPr = "", double? fontPt = null, bool bold = false, bool italic = false) =>
        $"<w:p>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : "")}{(text.Length > 0 ? Run(text, fontPt, bold, italic) : "")}</w:p>";

    public static string Spacing(double beforeMm = 0, double afterMm = 0) =>
        $"<w:spacing w:before=\"{Twips(beforeMm)}\" w:after=\"{Twips(afterMm)}\"/>";

    public static string AddressParagraphs(IEnumerable<string> lines, double? fontPt = null, bool italic = false, string extraPPr = "") =>
        string.Concat(lines.Select(l => P(l, "<w:pStyle w:val=\"Adres\"/>" + extraPPr, fontPt, italic: italic)));


    private string AnchorOpen(double x, double y, double w, double h, string name, bool behind) =>
        $"<wp:anchor distT=\"0\" distB=\"0\" distL=\"114300\" distR=\"114300\" simplePos=\"0\" relativeHeight=\"{_shapeId + 251658240}\" " +
        $"behindDoc=\"{(behind ? 1 : 0)}\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
        "<wp:simplePos x=\"0\" y=\"0\"/>" +
        $"<wp:positionH relativeFrom=\"page\"><wp:posOffset>{Emu(x)}</wp:posOffset></wp:positionH>" +
        $"<wp:positionV relativeFrom=\"page\"><wp:posOffset>{Emu(y)}</wp:posOffset></wp:positionV>" +
        $"<wp:extent cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/>" +
        $"<wp:docPr id=\"{_shapeId++}\" name=\"{Esc(name)}\"/><wp:cNvGraphicFramePr/>" +
        "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
        "<a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">";

    private const string AnchorClose = "</a:graphicData></a:graphic></wp:anchor>";

    public string TextBox(double x, double y, double w, double h, string name, string content)
    {
        var drawing =
            AnchorOpen(x, y, w, h, name, behind: false) +
            "<wps:wsp><wps:cNvSpPr txBox=\"1\"/>" +
            $"<wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
            "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln w=\"6350\"><a:noFill/></a:ln></wps:spPr>" +
            $"<wps:txbx><w:txbxContent>{content}</w:txbxContent></wps:txbx>" +
            "<wps:bodyPr rot=\"0\" spcFirstLastPara=\"0\" vertOverflow=\"overflow\" horzOverflow=\"overflow\" vert=\"horz\" wrap=\"square\" " +
            "lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" numCol=\"1\" spcCol=\"0\" rtlCol=\"0\" anchor=\"t\" anchorCtr=\"0\">" +
            "<a:noAutofit/></wps:bodyPr></wps:wsp>" +
            AnchorClose;

        var vml =
            $"<v:shapetype id=\"_x0000_t202\" coordsize=\"21600,21600\" o:spt=\"202\" path=\"m,l,21600r21600,l21600,xe\"><v:stroke joinstyle=\"miter\"/><v:path gradientshapeok=\"t\" o:connecttype=\"rect\"/></v:shapetype>" +
            $"<v:shape id=\"{Esc(name)}_vml\" o:spid=\"_x0000_s{1024 + _shapeId}\" type=\"#_x0000_t202\" " +
            $"style=\"position:absolute;margin-left:{F(x)}mm;margin-top:{F(y)}mm;width:{F(w)}mm;height:{F(h)}mm;z-index:{_shapeId};" +
            "mso-position-horizontal-relative:page;mso-position-vertical-relative:page\" filled=\"f\" stroked=\"f\" strokeweight=\".5pt\">" +
            $"<v:textbox><w:txbxContent>{content}</w:txbxContent></v:textbox></v:shape>";

        return "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing>" + drawing +
               "</w:drawing></mc:Choice><mc:Fallback><w:pict>" + vml + "</w:pict></mc:Fallback></mc:AlternateContent></w:r>";
    }

    /// <summary>Grafika PNG zakotwiczona do strony (tak Word zapisuje obraz z układem „Przed tekstem”).</summary>
    public string Picture(double x, double y, double w, double h, string name, byte[] png)
    {
        _images.Add(png);
        var relId = $"rIdImg{_images.Count}";
        return "<w:r><w:drawing>" +
               AnchorOpen(x, y, w, h, name, behind: false)
                   .Replace("http://schemas.microsoft.com/office/word/2010/wordprocessingShape", "http://schemas.openxmlformats.org/drawingml/2006/picture") +
               "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
               $"<pic:nvPicPr><pic:cNvPr id=\"0\" name=\"{Esc(name)}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
               $"<pic:blipFill><a:blip r:embed=\"{relId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
               $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
               "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>" +
               AnchorClose +
               "</w:drawing></w:r>";
    }

    public string WindowGuide(double x, double y, double w, double h, string name) =>
        "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing>" +
        AnchorOpen(x, y, w, h, name, behind: true) +
        "<wps:wsp><wps:cNvSpPr/>" +
        $"<wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
        "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/>" +
        "<a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"E03C31\"/></a:solidFill><a:prstDash val=\"dash\"/></a:ln></wps:spPr>" +
        "<wps:bodyPr/></wps:wsp>" +
        AnchorClose +
        "</w:drawing></mc:Choice><mc:Fallback/></mc:AlternateContent></w:r>";

    public static string FramePPr(double x, double y, double w, double h) =>
        $"<w:framePr w:w=\"{Twips(w)}\" w:h=\"{Twips(h)}\" w:hRule=\"exact\" w:hSpace=\"142\" w:wrap=\"around\" " +
        $"w:vAnchor=\"page\" w:hAnchor=\"page\" w:x=\"{Twips(x)}\" w:y=\"{Twips(y)}\"/>";


    public void Save(string path)
    {
        var hasHeader = _header.Length > 0;
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        Add(zip, "[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            (hasHeader ? "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>" : "") +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "</Types>");

        Add(zip, "_rels/.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "</Relationships>");

        Add(zip, "docProps/core.xml",
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{Esc(Path.GetFileNameWithoutExtension(path))}</dc:title><dc:creator>Mass.AddressWindow SampleGenerator</dc:creator>" +
            "</cp:coreProperties>");

        Add(zip, "word/_rels/document.xml.rels",
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            (hasHeader ? "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>" : "") +
            string.Concat(_images.Select((_, i) =>
                $"<Relationship Id=\"rIdImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{i + 1}.png\"/>")) +
            "</Relationships>");

        for (var i = 0; i < _images.Count; i++)
        {
            using var media = zip.CreateEntry($"word/media/image{i + 1}.png").Open();
            media.Write(_images[i]);
        }

        Add(zip, "word/styles.xml", $"<w:styles {Namespaces}>{Styles}</w:styles>");

        if (hasHeader)
        {
            Add(zip, "word/header1.xml", $"<w:hdr {Namespaces}>{_header}</w:hdr>");
        }

        var sectPr = "<w:sectPr>" +
                     (hasHeader ? "<w:headerReference w:type=\"default\" r:id=\"rId2\"/>" : "") +
                     "<w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                     $"<w:pgMar w:top=\"{Twips(25)}\" w:right=\"{Twips(20)}\" w:bottom=\"{Twips(25)}\" w:left=\"{Twips(25)}\" " +
                     $"w:header=\"{Twips(10)}\" w:footer=\"{Twips(10)}\" w:gutter=\"0\"/></w:sectPr>";
        Add(zip, "word/document.xml", $"<w:document {Namespaces}><w:body>{_body}{sectPr}</w:body></w:document>");
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
        writer.Write(xml);
    }
}
