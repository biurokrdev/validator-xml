using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EfektRuno.Templating;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace EfektRuno.Demo;

/// <summary>
/// Stand-in for the real "Szczegóły sprawy" template (only photos of it were available).
/// Tokens are deliberately split into several runs with proofErr in between - exactly what Word does.
/// </summary>
internal static class DemoTemplateBuilder
{
    private const string Orange = "E4572E";
    private const string Red = "D93025";
    private const string Blue = "1E63B0";
    private const string Grid = "BFBFBF";

    public static void Build(string path)
    {
        using WordprocessingDocument document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        MainDocumentPart main = document.AddMainDocumentPart();

        AddBulletNumbering(main);
        string sentIcon = AddIcon(main, pointsUp: true);
        string receivedIcon = AddIcon(main, pointsUp: false);
        string headerId = AddHeader(main);

        var body = new Body();

        body.Append(AttachmentsTable());
        body.Append(Para(Runs("Wiadomości w sprawie", bold: true, size: 32)), Para());

        body.Append(
            Para(Runs("<%^wiadomosci%>")),
            Para(Runs("ℹ ", bold: true, color: Blue), Runs("Brak dodatkowych wiadomości w sprawie")),
            Para(Runs("<%/wiadomosci%>")));

        // The empty paragraph belongs to the section: two adjacent tables would be merged by Word into one.
        body.Append(
            Para(Runs("<%#wiadomosci%>")),
            MessageTable(sentIcon, receivedIcon),
            Para(),
            Para(Runs("<%/wiadomosci%>")));

        body.Append(
            Para(Runs("<%#zamkniecie%>")),
            ClosedBox(),
            Para(),
            Para(Runs("<%/zamkniecie%>")));

        body.Append(
            Para(Runs("<%#przekazanie%>")),
            new Paragraph(ForwardedShape()),
            Para(Runs("<%/przekazanie%>")));

        body.Append(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId },
            new PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1417, Right = 1300, Bottom = 1417, Left = 1300, Header = 708, Footer = 708, Gutter = 0 }));

        main.Document = new Document(body);
    }

    private static Table AttachmentsTable()
    {
        int[] widths = [3200, 2200, 2200, 1700];
        var table = NewTable(widths);

        table.Append(new TableRow(
            Cell(widths[0], 1, Para(Runs("Nazwa załącznika", bold: true))),
            Cell(widths[1], 1, Para(Runs("Data dołączenia", bold: true))),
            Cell(widths[2], 1, Para(Runs("Użytkownik", bold: true))),
            Cell(widths[3], 1, Para(Runs("Archiwizuj", bold: true)))));

        // Open marker in the first cell, close marker in the last one => the whole row repeats.
        table.Append(new TableRow(
            Cell(widths[0], 1, Para(Runs("<%#zalaczniki_sprawy%><%nazwa_zalacznika%>"))),
            Cell(widths[1], 1, Para(Runs("<%data_dolaczenia|dd.MM.yyyy%>"))),
            Cell(widths[2], 1, Para(Runs("<%dodajacy%>"))),
            Cell(widths[3], 1, Para(Runs("<%archiwizuj%><%/zalaczniki_sprawy%>")))));

        return table;
    }

    private static Table MessageTable(string sentIcon, string receivedIcon)
    {
        int[] widths = [700, 2400, 3100, 3100];
        var table = NewTable(widths);

        var icons = new Paragraph();
        icons.Append(Runs("<%?kierunek==WYSLANA%>"));
        icons.Append(new Run(InlineImage(sentIcon, "Wysłana")));
        icons.Append(Runs("<%/kierunek%><%?kierunek==ODEBRANA%>"));
        icons.Append(new Run(InlineImage(receivedIcon, "Odebrana")));
        icons.Append(Runs("<%/kierunek%>"));

        table.Append(new TableRow(
            Cell(widths[0], 1, icons).WithVerticalMerge(restart: true),
            Cell(widths[1], 1,
                Para(Runs("Rodzaj wiadomości", bold: true)),
                Para(Runs("<%rodzaj_wiadomosci%>"))),
            Cell(widths[2], 1,
                Para(Runs("<%?kierunek==WYSLANA%>Wysłano<%/kierunek%><%?kierunek==ODEBRANA%>Odebrano<%/kierunek%>", bold: true)),
                Para(Runs("<%data_czas_wiadomosci|dd.MM.yyyy HH:mm%>"))),
            Cell(widths[3], 1,
                Para(Runs("<%?kierunek==WYSLANA%>Przez<%/kierunek%><%?kierunek==ODEBRANA%>Od<%/kierunek%>", bold: true)),
                Para(Runs("<%nadawca_wiadomosci%>")))));

        table.Append(new TableRow(
            Cell(widths[0], 1, Para()).WithVerticalMerge(restart: false),
            Cell(widths[1], 1, Para(Runs("Treść wiadomości"))),
            Cell(widths[2] + widths[3], 2, Para(Runs("<%tresc_wiadomosci%>")))));

        // Outer <%?zalaczniki%> spans two cells => hides the whole row when the list is empty.
        // Inner <%#zalaczniki%> uses marker-only paragraphs => repeats the bullet paragraph.
        table.Append(new TableRow(
            Cell(widths[0], 1, Para()).WithVerticalMerge(restart: false),
            Cell(widths[1], 1, Para(Runs("<%?zalaczniki%>Załączniki"))),
            Cell(widths[2] + widths[3], 2,
                Para(Runs("<%#zalaczniki%>")),
                Bullet(Runs("<%nazwa_pliku%>")),
                Para(Runs("<%/zalaczniki%>")),
                Para(Runs("<%/zalaczniki%>")))));

        return table;
    }

    private static Table ClosedBox()
    {
        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "9300", Type = TableWidthUnitValues.Dxa },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 12, Color = Red },
                    new LeftBorder { Val = BorderValues.Single, Size = 12, Color = Red },
                    new BottomBorder { Val = BorderValues.Single, Size = 12, Color = Red },
                    new RightBorder { Val = BorderValues.Single, Size = 12, Color = Red }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "100", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 160, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "100", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 160, Type = TableWidthValues.Dxa })),
            new TableGrid(new GridColumn { Width = "9300" }));

        table.Append(new TableRow(Cell(9300, 1,
            Para(Runs("⚠ ", bold: true, color: Red), Runs("Zamknęliśmy tę sprawę bez odpowiedzi", bold: true)),
            Para(Runs("Powód: <%powod_zamkniecia%>")),
            Para(Runs("Wyjaśnienie: <%wyjasnienie_zamkniecia%>")))));

        return table;
    }

    /// <summary>
    /// Rounded frame the way Word stores it: a wps text box plus a VML fallback, so every token exists twice.
    /// </summary>
    private static Run ForwardedShape()
    {
        string content = string.Concat(new[]
        {
            Para(Runs("ℹ ", bold: true, color: Blue), Runs("Przesłaliśmy tę sprawę dalej (do innego zespołu)", bold: true)),
            Para(Runs("Zespół: <%zespol%>")),
            Para(Runs("Wyjaśnienie: <%wyjasnienie_przeslania%>"))
        }.Select(p => p.OuterXml));

        string xml = $"""
            <w:r xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                 xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                 xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                 xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                 xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"
                 xmlns:v="urn:schemas-microsoft-com:vml">
              <w:rPr><w:noProof/></w:rPr>
              <mc:AlternateContent>
                <mc:Choice Requires="wps">
                  <w:drawing>
                    <wp:inline distT="0" distB="0" distL="0" distR="0">
                      <wp:extent cx="5900000" cy="900000"/>
                      <wp:effectExtent l="0" t="0" r="0" b="0"/>
                      <wp:docPr id="1" name="Ramka przekazania"/>
                      <wp:cNvGraphicFramePr/>
                      <a:graphic>
                        <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
                          <wps:wsp>
                            <wps:cNvSpPr txBox="1"/>
                            <wps:spPr>
                              <a:xfrm><a:off x="0" y="0"/><a:ext cx="5900000" cy="900000"/></a:xfrm>
                              <a:prstGeom prst="roundRect"><a:avLst><a:gd name="adj" fmla="val 8000"/></a:avLst></a:prstGeom>
                              <a:noFill/>
                              <a:ln w="19050"><a:solidFill><a:srgbClr val="{Blue}"/></a:solidFill></a:ln>
                            </wps:spPr>
                            <wps:txbx><w:txbxContent>{content}</w:txbxContent></wps:txbx>
                            <wps:bodyPr rot="0" vert="horz" wrap="square" lIns="91440" tIns="45720" rIns="91440" bIns="45720" anchor="t" anchorCtr="0"><a:spAutoFit/></wps:bodyPr>
                          </wps:wsp>
                        </a:graphicData>
                      </a:graphic>
                    </wp:inline>
                  </w:drawing>
                </mc:Choice>
                <mc:Fallback>
                  <w:pict>
                    <v:roundrect style="width:464pt;height:71pt" arcsize="5243f" filled="f" strokecolor="#{Blue}" strokeweight="1.5pt">
                      <v:textbox><w:txbxContent>{content}</w:txbxContent></v:textbox>
                    </v:roundrect>
                  </w:pict>
                </mc:Fallback>
              </mc:AlternateContent>
            </w:r>
            """;

        return new Run(xml);
    }

    private static string AddHeader(MainDocumentPart main)
    {
        HeaderPart part = main.AddNewPart<HeaderPart>();
        part.Header = new Header(Para(
            Runs("Szczegóły sprawy ", bold: true),
            Runs("<%application_number%>")));
        return main.GetIdOfPart(part);
    }

    private static void AddBulletNumbering(MainDocumentPart main)
    {
        NumberingDefinitionsPart part = main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new Numbering(
            new AbstractNum(
                new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "▪" },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation { Left = "500", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
    }

    private static Table NewTable(int[] widths)
    {
        var table = new Table(
            new TableProperties(
                new TableWidth { Width = widths.Sum().ToString(), Type = TableWidthUnitValues.Dxa },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = Grid },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = Grid },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = Grid },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = Grid },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = Grid },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = Grid }),
                new TableLayout { Type = TableLayoutValues.Fixed }));

        table.Append(new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString() })));
        return table;
    }

    private static TableCell Cell(int width, int gridSpan, params Paragraph[] paragraphs)
    {
        var properties = new TableCellProperties(
            new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa });
        if (gridSpan > 1)
            properties.Append(new GridSpan { Val = gridSpan });

        var cell = new TableCell(properties);
        cell.Append(paragraphs.Cast<OpenXmlElement>());
        return cell;
    }

    private static TableCell WithVerticalMerge(this TableCell cell, bool restart)
    {
        cell.TableCellProperties!.Append(new VerticalMerge
        {
            Val = restart ? MergedCellValues.Restart : MergedCellValues.Continue
        });
        return cell;
    }

    private static Paragraph Para(params IEnumerable<OpenXmlElement>[] content)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines { Before = "40", After = "40" }));
        foreach (IEnumerable<OpenXmlElement> part in content)
            paragraph.Append(part);
        return paragraph;
    }

    private static Paragraph Bullet(IEnumerable<OpenXmlElement> content)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new NumberingProperties(
                new NumberingLevelReference { Val = 0 },
                new NumberingId { Val = 1 })));
        paragraph.Append(content);
        return paragraph;
    }

    /// <summary>Plain text becomes one run; every token becomes three runs wrapped in proofErr, like in Word.</summary>
    private static IEnumerable<OpenXmlElement> Runs(string text, bool bold = false, string? color = null, int? size = null)
    {
        var result = new List<OpenXmlElement>();
        int position = 0;

        foreach (System.Text.RegularExpressions.Match match in TemplateToken.Pattern.Matches(text))
        {
            if (match.Index > position)
                result.Add(NewRun(text[position..match.Index]));

            result.Add(NewRun("<%"));
            result.Add(new ProofError { Type = ProofingErrorValues.SpellStart });
            result.Add(NewRun(match.Value[2..^2]));
            result.Add(new ProofError { Type = ProofingErrorValues.SpellEnd });
            result.Add(NewRun("%>"));
            position = match.Index + match.Length;
        }

        if (position < text.Length)
            result.Add(NewRun(text[position..]));

        return result;

        Run NewRun(string value)
        {
            var properties = new RunProperties();
            if (bold)
                properties.Append(new Bold());
            if (color != null)
                properties.Append(new Color { Val = color });
            if (size != null)
                properties.Append(new FontSize { Val = size.ToString() });

            return new Run(properties, new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        }
    }

    private static string AddIcon(MainDocumentPart main, bool pointsUp)
    {
        ImagePart part = main.AddImagePart(ImagePartType.Png);
        using var stream = new MemoryStream(PaperPlanePng(pointsUp));
        part.FeedData(stream);
        return main.GetIdOfPart(part);
    }

    private static Drawing InlineImage(string relationshipId, string name)
    {
        const long size = 228600; // 0.25"

        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = size, Cy = size },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = 1U, Name = name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = name + ".png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0, Y = 0 },
                                    new A.Extents { Cx = size, Cy = size }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });
    }

    private static byte[] PaperPlanePng(bool pointsUp)
    {
        const int size = 48;
        (double X, double Y)[] triangle = pointsUp
            ? [(2, 22), (46, 2), (26, 46)]
            : [(2, 26), (46, 46), (26, 2)];

        var raw = new MemoryStream();
        for (int y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < size; x++)
            {
                bool inside = IsInside(x + 0.5, y + 0.5, triangle);
                raw.Write(inside ? [0xE4, 0x57, 0x2E, 0xFF] : [0xFF, 0xFF, 0xFF, 0x00]);
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.ToArray());

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", [.. BigEndian(size), .. BigEndian(size), 8, 6, 0, 0, 0]);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static bool IsInside(double x, double y, (double X, double Y)[] t)
    {
        double d1 = Sign(x, y, t[0], t[1]);
        double d2 = Sign(x, y, t[1], t[2]);
        double d3 = Sign(x, y, t[2], t[0]);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);

        static double Sign(double px, double py, (double X, double Y) a, (double X, double Y) b)
        {
            return (px - b.X) * (a.Y - b.Y) - (a.X - b.X) * (py - b.Y);
        }
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(BigEndian(data.Length));
        output.Write(typeBytes);
        output.Write(data);

        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes.Concat(data))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }

        output.Write(BigEndian((int)~crc));
    }

    private static byte[] BigEndian(int value)
    {
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }
}
