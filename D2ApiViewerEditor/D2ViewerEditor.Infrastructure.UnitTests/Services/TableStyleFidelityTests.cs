using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TableStyleFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private delegate void DocxCustomizer(MainDocumentPart mainPart, Body body);

    private static MemoryStream BuildDocx(Style? tableStyle, TableProperties tblPr, int rows = 3, int cols = 2,
        DocxCustomizer? customize = null, Action<TableRow, int>? rowCustomizer = null)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            if (tableStyle != null)
            {
                var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
                stylesPart.Styles = new Styles(tableStyle);
                stylesPart.Styles.Save();
            }

            var table = new Table();
            table.Append(tblPr);
            var grid = new TableGrid();
            for (var c = 0; c < cols; c++)
                grid.Append(new GridColumn { Width = "3000" });
            table.Append(grid);

            for (var r = 0; r < rows; r++)
            {
                var row = new TableRow();
                rowCustomizer?.Invoke(row, r);
                for (var c = 0; c < cols; c++)
                {
                    row.Append(new TableCell(
                        new TableCellProperties(new TableCellWidth { Width = "3000", Type = TableWidthUnitValues.Dxa }),
                        new Paragraph(new Run(new Text($"R{r}C{c}")))));
                }
                table.Append(row);
            }

            body.Append(table);
            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 }));
            customize?.Invoke(mainPart, body);
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private static Style GridTableStyle(string styleId = "TableGrid") => new(
        new StyleTableProperties(new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 },
            new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })))
    {
        Type = StyleValues.Table,
        StyleId = styleId,
        StyleName = new StyleName { Val = styleId }
    };

    private static string FirstTableTag(string html)
    {
        var start = html.IndexOf("<table", StringComparison.Ordinal);
        var end = html.IndexOf('>', start);
        return html.Substring(start, end - start + 1);
    }

    private static List<string> CellStyles(string html)
    {
        var result = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(html, "<td[^>]*style=\"([^\"]*)\""))
            result.Add(m.Groups[1].Value);
        return result;
    }

    [Test]
    public void Read_TableGridStyle_BordersComeFromStyle()
    {
        var tblPr = new TableProperties(new TableStyle { Val = "TableGrid" });
        var html = _reader.Convert(BuildDocx(GridTableStyle(), tblPr)).Html;

        var cells = CellStyles(html);
        cells.Should().NotBeEmpty();
        cells.Should().OnlyContain(s => s.Contains("border-top:0.7px solid #000000"));
        FirstTableTag(html).Should().NotContain("data-no-borders");
        FirstTableTag(html).Should().Contain("data-tbl-style=\"TableGrid\"");
        FirstTableTag(html).Should().Contain("data-tbl-look=");
    }

    [Test]
    public void Read_StyleFromBasedOnChain_IsInherited()
    {
        var derived = new Style { Type = StyleValues.Table, StyleId = "Derived", BasedOn = new BasedOn { Val = "TableGrid" } };
        var tblPr = new TableProperties(new TableStyle { Val = "Derived" });

        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(GridTableStyle(), derived);
            stylesPart.Styles.Save();
            var body = mainPart.Document.Body!;
            var table = new Table(tblPr,
                new TableGrid(new GridColumn { Width = "3000" }),
                new TableRow(new TableCell(new Paragraph(new Run(new Text("A"))))));
            body.Append(table);
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;
        CellStyles(html).Should().OnlyContain(s => s.Contains("border-top:0.7px solid"));
    }

    [Test]
    public void Read_OuterVsInsideBorders_AreResolvedByCellPosition()
    {
        var tblPr = new TableProperties(new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 8, Color = "FF0000" },
            new LeftBorder { Val = BorderValues.Single, Size = 8, Color = "FF0000" },
            new BottomBorder { Val = BorderValues.Single, Size = 8, Color = "FF0000" },
            new RightBorder { Val = BorderValues.Single, Size = 8, Color = "FF0000" },
            new InsideHorizontalBorder { Val = BorderValues.Dashed, Size = 4, Color = "0000FF" },
            new InsideVerticalBorder { Val = BorderValues.Dashed, Size = 4, Color = "0000FF" }));

        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 2, cols: 2)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(4);

        cells[0].Should().Contain("border-top:1.3px solid #FF0000")
            .And.Contain("border-left:1.3px solid #FF0000")
            .And.Contain("border-bottom:0.7px dashed #0000FF")
            .And.Contain("border-right:0.7px dashed #0000FF");
        cells[3].Should().Contain("border-top:0.7px dashed #0000FF")
            .And.Contain("border-left:0.7px dashed #0000FF")
            .And.Contain("border-bottom:1.3px solid #FF0000")
            .And.Contain("border-right:1.3px solid #FF0000");
    }

    [Test]
    public void Read_FirstRowConditionalShading_AppliesOnlyToHeaderRow()
    {
        var style = GridTableStyle("Accent");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "4472C4" }))
        { Type = TableStyleOverrideValues.FirstRow });

        var tblPr = new TableProperties(
            new TableStyle { Val = "Accent" },
            new TableLook { Val = "04A0", FirstRow = true, NoVerticalBand = true });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 3, cols: 2)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(6);
        cells[0].Should().Contain("background-color:#4472C4");
        cells[1].Should().Contain("background-color:#4472C4");
        cells[2].Should().NotContain("background-color");
        cells[4].Should().NotContain("background-color");
    }

    [Test]
    public void Read_FirstRowConditionalBorders_ResolvePositionallyWithinRegion()
    {
        var style = GridTableStyle("HeadBorders");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Double, Size = 12, Color = "000000" },
                    new LeftBorder { Val = BorderValues.Double, Size = 12, Color = "000000" },
                    new BottomBorder { Val = BorderValues.Double, Size = 12, Color = "000000" },
                    new RightBorder { Val = BorderValues.Double, Size = 12, Color = "000000" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "000000" })))
        { Type = TableStyleOverrideValues.FirstRow });

        var tblPr = new TableProperties(
            new TableStyle { Val = "HeadBorders" },
            new TableLook { FirstRow = true, NoHorizontalBand = true, NoVerticalBand = true });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 2, cols: 3)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(6);

        cells[1].Should().Contain("border-left:0.7px solid")
            .And.Contain("border-right:0.7px solid")
            .And.Contain("border-top:6px double")
            .And.Contain("border-bottom:6px double");
        cells[0].Should().Contain("border-left:6px double").And.Contain("border-right:0.7px solid");
        cells[2].Should().Contain("border-right:6px double").And.Contain("border-left:0.7px solid");
        cells[3].Should().NotContain("double");
        cells[4].Should().NotContain("double");
    }

    [Test]
    public void Read_RowBanding_SkipsHeaderAndAlternates()
    {
        var style = GridTableStyle("Banded");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "D9E2F3" }))
        { Type = TableStyleOverrideValues.Band1Horizontal });

        var tblPr = new TableProperties(
            new TableStyle { Val = "Banded" },
            new TableLook { FirstRow = true, NoHorizontalBand = false, NoVerticalBand = true });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 4, cols: 1)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(4);
        cells[0].Should().NotContain("D9E2F3");
        cells[1].Should().Contain("background-color:#D9E2F3");
        cells[2].Should().NotContain("D9E2F3");
        cells[3].Should().Contain("background-color:#D9E2F3");
    }

    [Test]
    public void Read_LastRowConditionalShading_AppliesOnlyToLastRow()
    {
        var style = GridTableStyle("Totals");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "FFD966" }))
        { Type = TableStyleOverrideValues.LastRow });

        var tblPr = new TableProperties(
            new TableStyle { Val = "Totals" },
            new TableLook { LastRow = true, NoHorizontalBand = true, NoVerticalBand = true });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 3, cols: 2)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(6);
        cells[4].Should().Contain("background-color:#FFD966");
        cells[5].Should().Contain("background-color:#FFD966");
        cells[0].Should().NotContain("FFD966");
        cells[2].Should().NotContain("FFD966");
    }

    [Test]
    public void Read_FirstColumnConditionalShading_AppliesOnlyToFirstColumn()
    {
        var style = GridTableStyle("FirstCol");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "C00000" }))
        { Type = TableStyleOverrideValues.FirstColumn });

        var tblPr = new TableProperties(
            new TableStyle { Val = "FirstCol" },
            new TableLook { FirstColumn = true, NoHorizontalBand = true, NoVerticalBand = true });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 2, cols: 2)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(4);
        cells[0].Should().Contain("background-color:#C00000");
        cells[2].Should().Contain("background-color:#C00000");
        cells[1].Should().NotContain("C00000");
        cells[3].Should().NotContain("C00000");
    }

    [Test]
    public void Read_TblLookLegacyHexMask_EnablesFirstRowRegion()
    {
        var style = GridTableStyle("LegacyLook");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "4472C4" }))
        { Type = TableStyleOverrideValues.FirstRow });

        var tblPr = new TableProperties(
            new TableStyle { Val = "LegacyLook" },
            new TableLook { Val = "0620" });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 2, cols: 1)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(2);
        cells[0].Should().Contain("background-color:#4472C4");
        cells[1].Should().NotContain("4472C4");
    }

    [Test]
    public void Read_NoHorizontalBandFlag_DisablesRowBanding()
    {
        var style = GridTableStyle("BandedOff");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "D9E2F3" }))
        { Type = TableStyleOverrideValues.Band1Horizontal });

        var tblPr = new TableProperties(
            new TableStyle { Val = "BandedOff" },
            new TableLook { FirstRow = false, NoHorizontalBand = true, NoVerticalBand = true });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 4, cols: 1)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(4);
        cells.Should().OnlyContain(s => !s.Contains("D9E2F3"));
    }

    [Test]
    public void Read_ColumnBanding_AlternatesColumns()
    {
        var style = GridTableStyle("ColBanded");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "E2EFD9" }))
        { Type = TableStyleOverrideValues.Band1Vertical });

        var tblPr = new TableProperties(
            new TableStyle { Val = "ColBanded" },
            new TableLook { FirstRow = false, FirstColumn = false, NoHorizontalBand = true, NoVerticalBand = false });

        var html = _reader.Convert(BuildDocx(style, tblPr, rows: 1, cols: 4)).Html;
        var cells = CellStyles(html);
        cells.Should().HaveCount(4);
        cells[0].Should().Contain("background-color:#E2EFD9");
        cells[2].Should().Contain("background-color:#E2EFD9");
        cells[1].Should().NotContain("E2EFD9");
        cells[3].Should().NotContain("E2EFD9");
    }

    [Test]
    public void Read_DirectCellShading_BeatsStyle()
    {
        var style = GridTableStyle("Accent2");
        style.Append(new TableStyleProperties(
            new TableStyleConditionalFormattingTableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "4472C4" }))
        { Type = TableStyleOverrideValues.FirstRow });

        var tblPr = new TableProperties(
            new TableStyle { Val = "Accent2" },
            new TableLook { FirstRow = true, NoVerticalBand = true });

        var ms = BuildDocx(style, tblPr, rows: 1, cols: 1);
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var cell = doc.MainDocumentPart!.Document!.Body!.Descendants<TableCell>().First();
            cell.TableCellProperties!.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "FF0000" });
            doc.MainDocumentPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;
        CellStyles(html)[0].Should().Contain("background-color:#FF0000").And.NotContain("4472C4");
    }

    [Test]
    public void Read_ThemeFillShading_ResolvesThroughTheme()
    {
        var tblPr = new TableProperties();
        var ms = BuildDocx(null, tblPr, rows: 1, cols: 1, customize: (mainPart, body) =>
        {
            var themePart = mainPart.AddNewPart<ThemePart>();
            themePart.Theme = new Drawing.Theme(
                new Drawing.ThemeElements(
                    new Drawing.ColorScheme(
                        new Drawing.Dark1Color(new Drawing.SystemColor { Val = Drawing.SystemColorValues.WindowText, LastColor = "000000" }),
                        new Drawing.Light1Color(new Drawing.SystemColor { Val = Drawing.SystemColorValues.Window, LastColor = "FFFFFF" }),
                        new Drawing.Dark2Color(new Drawing.RgbColorModelHex { Val = "44546A" }),
                        new Drawing.Light2Color(new Drawing.RgbColorModelHex { Val = "E7E6E6" }),
                        new Drawing.Accent1Color(new Drawing.RgbColorModelHex { Val = "4472C4" }),
                        new Drawing.Accent2Color(new Drawing.RgbColorModelHex { Val = "ED7D31" }),
                        new Drawing.Accent3Color(new Drawing.RgbColorModelHex { Val = "A5A5A5" }),
                        new Drawing.Accent4Color(new Drawing.RgbColorModelHex { Val = "FFC000" }),
                        new Drawing.Accent5Color(new Drawing.RgbColorModelHex { Val = "5B9BD5" }),
                        new Drawing.Accent6Color(new Drawing.RgbColorModelHex { Val = "70AD47" }),
                        new Drawing.Hyperlink(new Drawing.RgbColorModelHex { Val = "0563C1" }),
                        new Drawing.FollowedHyperlinkColor(new Drawing.RgbColorModelHex { Val = "954F72" }))
                    { Name = "Office" },
                    new Drawing.FontScheme(
                        new Drawing.MajorFont(new Drawing.LatinFont { Typeface = "Calibri Light" }, new Drawing.EastAsianFont { Typeface = "" }, new Drawing.ComplexScriptFont { Typeface = "" }),
                        new Drawing.MinorFont(new Drawing.LatinFont { Typeface = "Calibri" }, new Drawing.EastAsianFont { Typeface = "" }, new Drawing.ComplexScriptFont { Typeface = "" }))
                    { Name = "Office" },
                    new Drawing.FormatScheme(
                        new Drawing.FillStyleList(
                            new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                            new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                            new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })),
                        new Drawing.LineStyleList(
                            new Drawing.Outline(new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })),
                            new Drawing.Outline(new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })),
                            new Drawing.Outline(new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }))),
                        new Drawing.EffectStyleList(
                            new Drawing.EffectStyle(new Drawing.EffectList()),
                            new Drawing.EffectStyle(new Drawing.EffectList()),
                            new Drawing.EffectStyle(new Drawing.EffectList())),
                        new Drawing.BackgroundFillStyleList(
                            new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                            new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor }),
                            new Drawing.SolidFill(new Drawing.SchemeColor { Val = Drawing.SchemeColorValues.PhColor })))
                    { Name = "Office" }));
            themePart.Theme.Save();

            var cell = body.Descendants<TableCell>().First();
            cell.TableCellProperties!.Append(new Shading
            {
                Val = ShadingPatternValues.Clear,
                Fill = "auto",
                ThemeFill = ThemeColorValues.Accent1
            });
        });

        var html = _reader.Convert(ms).Html;
        CellStyles(html)[0].Should().Contain("background-color:#4472C4");
    }

    [Test]
    public void Read_PercentPatternShading_IsBlendedOverWhite()
    {
        var tblPr = new TableProperties();
        var ms = BuildDocx(null, tblPr, rows: 1, cols: 1, customize: (_, body) =>
        {
            var cell = body.Descendants<TableCell>().First();
            cell.TableCellProperties!.Append(new Shading
            {
                Val = ShadingPatternValues.Percent50,
                Color = "000000",
                Fill = "FFFFFF"
            });
        });

        var html = _reader.Convert(ms).Html;
        CellStyles(html)[0].Should().MatchRegex("background-color:#(7F|80)(7F|80)(7F|80)");
    }

    [Test]
    public void Read_RowHeights_UseHeightCssAndDataAttributes()
    {
        var tblPr = new TableProperties();
        var ms = BuildDocx(null, tblPr, rows: 2, cols: 1, rowCustomizer: (row, idx) =>
        {
            row.Append(new TableRowProperties(new TableRowHeight
            {
                Val = 567u,
                HeightType = idx == 0 ? HeightRuleValues.Exact : HeightRuleValues.AtLeast
            }));
        });

        var html = _reader.Convert(ms).Html;
        html.Should().NotContain("min-height:");
        var trMatches = System.Text.RegularExpressions.Regex.Matches(html, "<tr[^>]*>");
        trMatches[0].Value.Should().Contain("data-row-height-tw=\"567\"")
            .And.Contain("data-row-hrule=\"exact\"")
            .And.Contain("height:37px");
        trMatches[1].Value.Should().Contain("data-row-height-tw=\"567\"")
            .And.NotContain("data-row-hrule")
            .And.Contain("height:37px");
    }

    [Test]
    public void Read_TableHeaderAndCantSplit_AreExposedAsDataAttributes()
    {
        var tblPr = new TableProperties();
        var ms = BuildDocx(null, tblPr, rows: 2, cols: 1, rowCustomizer: (row, idx) =>
        {
            if (idx == 0)
                row.Append(new TableRowProperties(new CantSplit(), new TableHeader()));
        });

        var html = _reader.Convert(ms).Html;
        var trMatches = System.Text.RegularExpressions.Regex.Matches(html, "<tr[^>]*>");
        trMatches[0].Value.Should().Contain("data-tbl-header=\"1\"").And.Contain("data-cant-split=\"1\"");
        trMatches[1].Value.Should().NotContain("data-tbl-header").And.NotContain("data-cant-split");
    }

    [Test]
    public void Read_AutoCellWidthZero_DoesNotEmitZeroPixelWidth()
    {
        var tblPr = new TableProperties();
        var ms = BuildDocx(null, tblPr, rows: 1, cols: 1, customize: (_, body) =>
        {
            var cell = body.Descendants<TableCell>().First();
            cell.TableCellProperties!.TableCellWidth = new TableCellWidth { Width = "0", Type = TableWidthUnitValues.Auto };
        });

        var html = _reader.Convert(ms).Html;
        CellStyles(html)[0].Should().NotContain("width:0px");
    }

    [Test]
    public void Read_FractionalPercentWidth_IsPreserved()
    {
        var tblPr = new TableProperties(new TableWidth { Width = "3333", Type = TableWidthUnitValues.Pct });
        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 1, cols: 1)).Html;
        FirstTableTag(html).Should().Contain("width:66.66%");
    }

    [Test]
    public void Read_TableCellSpacing_EmitsSeparateBorderModel()
    {
        var tblPr = new TableProperties(new TableCellSpacing { Width = "60", Type = TableWidthUnitValues.Dxa });
        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 1, cols: 2)).Html;
        var tag = FirstTableTag(html);
        tag.Should().Contain("border-collapse:separate")
            .And.Contain("border-spacing:8px")
            .And.Contain("data-cell-spacing-tw=\"60\"");
    }

    private static Table FirstTable(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return (Table)doc.MainDocumentPart!.Document!.Body!.Descendants<Table>().First().CloneNode(true);
    }

    [Test]
    public void Write_TableStyleAndLook_AreReemitted()
    {
        var html =
            "<table data-tbl-style=\"TableGrid\" data-tbl-look=\"04A0\" style=\"border-collapse:collapse;width:400px;\">" +
            "<tr><td>A</td></tr></table>";

        var table = FirstTable(_writer.Convert(html));
        var tblPr = table.GetFirstChild<TableProperties>()!;
        tblPr.TableStyle!.Val!.Value.Should().Be("TableGrid");
        (tblPr.FirstChild is TableStyle).Should().BeTrue("w:tblStyle musi być pierwszym dzieckiem tblPr");
        var look = tblPr.GetFirstChild<TableLook>()!;
        look.Val!.Value.Should().Be("04A0");
        look.FirstRow!.Value.Should().BeTrue();
        look.NoVerticalBand!.Value.Should().BeTrue();
    }

    [Test]
    public void Write_RowHeightFromDataAttributes_KeepsExactRuleAndTwips()
    {
        var html =
            "<table style=\"border-collapse:collapse;\">" +
            "<tr data-row-height-tw=\"567\" data-row-hrule=\"exact\" style=\"height:37px;\"><td>A</td></tr>" +
            "<tr data-row-height-tw=\"850\" style=\"height:56px;\"><td>B</td></tr>" +
            "<tr><td>C</td></tr>" +
            "</table>";

        var rows = FirstTable(_writer.Convert(html)).Elements<TableRow>().ToList();

        var h0 = rows[0].TableRowProperties!.Elements<TableRowHeight>().Single();
        h0.Val!.Value.Should().Be(567u);
        h0.HeightType!.Value.Should().Be(HeightRuleValues.Exact);

        var h1 = rows[1].TableRowProperties!.Elements<TableRowHeight>().Single();
        h1.Val!.Value.Should().Be(850u);
        h1.HeightType!.Value.Should().Be(HeightRuleValues.AtLeast);

        rows[2].TableRowProperties.Should().BeNull();
    }

    [Test]
    public void Write_TableHeaderAndCantSplit_RoundTrip()
    {
        var html =
            "<table style=\"border-collapse:collapse;\">" +
            "<tr data-tbl-header=\"1\" data-cant-split=\"1\"><td>H</td></tr>" +
            "<tr><td>B</td></tr>" +
            "</table>";

        var rows = FirstTable(_writer.Convert(html)).Elements<TableRow>().ToList();
        rows[0].TableRowProperties!.Elements<TableHeader>().Should().HaveCount(1);
        rows[0].TableRowProperties!.Elements<CantSplit>().Should().HaveCount(1);
        rows[1].TableRowProperties.Should().BeNull();
    }

    [Test]
    public void Write_TableIndentAndFractionalPercent_AreExported()
    {
        var html =
            "<table style=\"border-collapse:collapse;width:66.66%;margin-left:38px;\">" +
            "<tr><td>A</td></tr></table>";

        var tblPr = FirstTable(_writer.Convert(html)).GetFirstChild<TableProperties>()!;
        int.Parse(tblPr.TableWidth!.Width!.Value!).Should().Be(3333);
        tblPr.TableWidth.Type!.Value.Should().Be(TableWidthUnitValues.Pct);
        tblPr.TableIndentation!.Width!.Value.Should().Be(570);
    }

    [Test]
    public void Write_CellSpacing_RoundTripsThroughDataAttribute()
    {
        var html =
            "<table data-cell-spacing-tw=\"60\" style=\"border-collapse:separate;border-spacing:4px;\">" +
            "<tr><td>A</td></tr></table>";

        var tblPr = FirstTable(_writer.Convert(html)).GetFirstChild<TableProperties>()!;
        var spacing = tblPr.GetFirstChild<TableCellSpacing>()!;
        spacing.Width!.Value.Should().Be("60");
    }

    [Test]
    public void BorderWidth_RoundTripsWithoutInflation()
    {
        var tblPr = new TableProperties(new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 },
            new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 8 },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 8 }));

        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 2, cols: 2)).Html;
        var table = FirstTable(_writer.Convert(html));

        var firstCellBorders = table.Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        firstCellBorders.TopBorder!.Size!.Value.Should().BeInRange(3u, 5u);
        firstCellBorders.BottomBorder!.Size!.Value.Should().BeInRange(7u, 9u);
    }

    [Test]
    public void Write_CellBorderAsSeparateWidthStyleColorProperties_WithRgb_IsPreserved()
    {
        var html =
            "<table style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border-width: 0.7px; border-style: solid; border-color: rgb(0, 0, 0); padding: 0px 7px;\">" +
            "Lp.</td></tr></table>";

        var cell = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First();
        var borders = cell.TableCellProperties?.TableCellBorders;
        borders.Should().NotBeNull("obramowanie z osobnych właściwości border-* musi zostać zapisane");
        foreach (var b in new BorderType[] { borders!.TopBorder!, borders.LeftBorder!, borders.BottomBorder!, borders.RightBorder! })
        {
            b.Val!.Value.Should().Be(BorderValues.Single);
            b.Size!.Value.Should().BeInRange(3u, 5u);
            b.Color!.Value.Should().Be("000000");
        }
    }

    [Test]
    public void Write_CellBorderShorthandWithRgbColor_IsPreserved()
    {
        var html =
            "<table style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border: 1px solid rgb(255, 0, 0);\">A</td></tr></table>";

        var borders = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        borders.TopBorder!.Val!.Value.Should().Be(BorderValues.Single);
        borders.TopBorder!.Color!.Value.Should().Be("FF0000");
    }

    [Test]
    public void Write_CellBorderStyleNoneSeparateForm_EmitsExplicitNone()
    {
        var html =
            "<table data-tbl-style=\"TableGrid\" style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border-style: none; padding: 0px 7px;\">A</td></tr></table>";

        var cell = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First();
        var borders = cell.TableCellProperties?.TableCellBorders;
        borders.Should().NotBeNull();
        borders!.Elements<BorderType>().Should().HaveCount(4)
            .And.OnlyContain(b => b.Val != null && b.Val.Value == BorderValues.Nil);
    }

    [Test]
    public void Write_NestedTable_DoesNotDuplicateInnerRowsInOuterTable()
    {
        var html =
            "<table style=\"border-collapse:collapse;\">" +
            "<tr><td>" +
            "<table style=\"border-collapse:collapse;\">" +
            "<tr><td>N1</td></tr><tr><td>N2</td></tr>" +
            "</table>" +
            "</td><td>B</td></tr>" +
            "<tr><td>C</td><td>D</td></tr>" +
            "</table>";

        var outer = FirstTable(_writer.Convert(html));
        outer.Elements<TableRow>().Should().HaveCount(2, "wiersze zagnieżdżone nie są wierszami tabeli zewnętrznej");

        var nested = outer.Descendants<Table>().Single();
        nested.Elements<TableRow>().Should().HaveCount(2);
        nested.InnerText.Should().Contain("N1").And.Contain("N2");
    }

    [Test]
    public void FullRoundTrip_StyledTable_KeepsStyleReferenceRowHeightsAndSpans()
    {
        var style = GridTableStyle();
        var tblPr = new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableLook { Val = "04A0", FirstRow = true, NoVerticalBand = true });

        var ms = BuildDocx(style, tblPr, rows: 3, cols: 2, rowCustomizer: (row, idx) =>
        {
            if (idx == 0)
                row.Append(new TableRowProperties(
                    new TableRowHeight { Val = 567u, HeightType = HeightRuleValues.Exact },
                    new TableHeader()));
        });

        var html = _reader.Convert(ms).Html;
        var table = FirstTable(_writer.Convert(html));

        var outPr = table.GetFirstChild<TableProperties>()!;
        outPr.TableStyle!.Val!.Value.Should().Be("TableGrid");
        outPr.GetFirstChild<TableLook>()!.Val!.Value.Should().Be("04A0");

        var rows = table.Elements<TableRow>().ToList();
        var h0 = rows[0].TableRowProperties!.Elements<TableRowHeight>().Single();
        h0.Val!.Value.Should().Be(567u);
        h0.HeightType!.Value.Should().Be(HeightRuleValues.Exact);
        rows[0].TableRowProperties!.Elements<TableHeader>().Should().HaveCount(1);
        rows[1].TableRowProperties.Should().BeNull("wiersze bez trHeight nie dostają sztucznej wysokości");

        var cellBorders = table.Descendants<TableCell>().First().TableCellProperties!.TableCellBorders!;
        cellBorders.TopBorder!.Val!.Value.Should().Be(BorderValues.Single);
    }

    private static TableBorders UniformBorders(BorderValues val, uint size) => new(
        new TopBorder { Val = val, Size = size },
        new LeftBorder { Val = val, Size = size },
        new BottomBorder { Val = val, Size = size },
        new RightBorder { Val = val, Size = size },
        new InsideHorizontalBorder { Val = val, Size = size },
        new InsideVerticalBorder { Val = val, Size = size });

    [Test]
    public void Read_DoubleBorder_EmitsCssDoubleWideEnoughForTwoLines()
    {
        var tblPr = new TableProperties(UniformBorders(BorderValues.Double, 4));
        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 2, cols: 2)).Html;

        var cells = CellStyles(html);
        cells.Should().NotBeEmpty();
        cells.Should().OnlyContain(s => s.Contains("border-top:3px double #000000"));
    }

    [Test]
    public void RoundTrip_DoubleBorder_StaysDoubleWithoutInflation()
    {
        var tblPr = new TableProperties(UniformBorders(BorderValues.Double, 6));
        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 2, cols: 2)).Html;

        var borders = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        borders.TopBorder!.Val!.Value.Should().Be(BorderValues.Double,
            "styl double nie może degradować do single przy zapisie");
        borders.TopBorder!.Size!.Value.Should().BeInRange(5u, 7u,
            "szerokość podwójnej linii nie może puchnąć w round-tripie");
    }

    [Test]
    public void Write_MixedMultiValueLonghands_ResolvePerSide()
    {
        var html =
            "<table data-tbl-style=\"TableGrid\" style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border-width: medium 0.7px 0.7px; border-style: none solid solid; " +
            "border-color: currentcolor rgb(217, 217, 217) rgb(217, 217, 217);\">A</td></tr></table>";

        var borders = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        borders.TopBorder!.Val!.Value.Should().Be(BorderValues.Nil,
            "strona bez linii musi wrócić jako jawny nil, nie zniknąć (czarna siatka ze stylu)");
        borders.RightBorder!.Val!.Value.Should().Be(BorderValues.Single);
        borders.RightBorder!.Color!.Value.Should().Be("D9D9D9");
        borders.BottomBorder!.Val!.Value.Should().Be(BorderValues.Single);
        borders.LeftBorder!.Val!.Value.Should().Be(BorderValues.Single,
            "3 wartości CSS: left dziedziczy z right");
    }

    [Test]
    public void Write_ZeroWidthBorder_BecomesExplicitNone()
    {
        var html =
            "<table style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border-top: 0px solid #000000; border-bottom: 0.7px solid #000000;\">A</td></tr></table>";

        var borders = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        borders.TopBorder!.Val!.Value.Should().Be(BorderValues.Nil,
            "0px jest w edytorze niewidoczne — min. 2/8 pt robiło z tego widoczną linię");
        borders.BottomBorder!.Val!.Value.Should().Be(BorderValues.Single);
    }

    [Test]
    public void Write_ShorthandWithoutColor_KeepsSideWithAutoColor()
    {
        var html =
            "<table data-tbl-style=\"TableGrid\" style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border-top: 0.7px solid; border-bottom: 0.7px solid currentcolor;\">A</td></tr></table>";

        var borders = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        borders.TopBorder!.Val!.Value.Should().Be(BorderValues.Single,
            "brak tokenu koloru nie może wyrzucić strony z tcBorders");
        borders.TopBorder!.Color!.Value.Should().Be("auto");
        borders.BottomBorder!.Val!.Value.Should().Be(BorderValues.Single);
        borders.BottomBorder!.Color!.Value.Should().Be("auto");
    }

    [Test]
    public void Write_TransparentBorder_BecomesExplicitNone()
    {
        var html =
            "<table style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border-top: 0.7px solid transparent; border-bottom: 0.7px solid rgba(0, 0, 0, 0);\">A</td></tr></table>";

        var borders = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        borders.TopBorder!.Val!.Value.Should().Be(BorderValues.Nil);
        borders.BottomBorder!.Val!.Value.Should().Be(BorderValues.Nil);
    }

    [Test]
    public void Write_WhiteBorder_StaysWhite()
    {
        var html =
            "<table style=\"border-collapse:collapse;\"><tr><td style=\"" +
            "border: 0.7px solid rgb(255, 255, 255);\">A</td></tr></table>";

        var borders = FirstTable(_writer.Convert(html)).Descendants<TableCell>().First()
            .TableCellProperties!.TableCellBorders!;
        borders.TopBorder!.Val!.Value.Should().Be(BorderValues.Single,
            "biała linia to jawna linia (niewidoczna na białym tle), nie None i nie czarna");
        borders.TopBorder!.Color!.Value.Should().Be("FFFFFF");
    }

    [Test]
    public void RoundTrip_TableWithoutAnyBorders_ExportsExplicitNoneEverywhere()
    {
        var tblPr = new TableProperties();
        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 2, cols: 2)).Html;
        FirstTableTag(html).Should().Contain("data-no-borders=\"1\"");

        var table = FirstTable(_writer.Convert(html));
        var tblBorders = table.GetFirstChild<TableProperties>()!.GetFirstChild<TableBorders>()!;
        tblBorders.TopBorder!.Val!.Value.Should().Be(BorderValues.None);
        tblBorders.InsideHorizontalBorder!.Val!.Value.Should().Be(BorderValues.None);

        var cellBorders = table.Descendants<TableCell>().First().TableCellProperties?.TableCellBorders;
        if (cellBorders != null)
            cellBorders.Elements<BorderType>().Should()
                .OnlyContain(b => b.Val != null && b.Val.Value == BorderValues.Nil);
    }

    [Test]
    public void Read_TableWithoutAnyBorders_MarksCellsForEditorGridlines()
    {
        var html = _reader.Convert(BuildDocx(null, new TableProperties(), rows: 2, cols: 2)).Html;

        var tags = System.Text.RegularExpressions.Regex.Matches(html, "<td[^>]*>")
            .Select(m => m.Value).ToList();
        tags.Should().NotBeEmpty();
        tags.Should().OnlyContain(t => t.Contains("docx-borderless-cell"));
    }

    [Test]
    public void Read_TableGridStyle_DoesNotMarkCellsAsBorderless()
    {
        var tblPr = new TableProperties(new TableStyle { Val = "TableGrid" });
        var html = _reader.Convert(BuildDocx(GridTableStyle(), tblPr)).Html;

        html.Should().NotContain("docx-borderless-cell");
    }

    [Test]
    public void Read_CellWithSingleVisibleEdge_IsNotMarkedAsBorderless()
    {
        var tblPr = new TableProperties(new TableBorders(
            new BottomBorder { Val = BorderValues.Single, Size = 4 }));
        var html = _reader.Convert(BuildDocx(null, tblPr, rows: 2, cols: 1)).Html;

        var tags = System.Text.RegularExpressions.Regex.Matches(html, "<td[^>]*>")
            .Select(m => m.Value).ToList();
        tags.Should().HaveCount(2);
        tags[0].Should().Contain("docx-borderless-cell");
        tags[1].Should().NotContain("docx-borderless-cell");
    }

    [Test]
    public void RoundTrip_BorderlessMarkerClass_DoesNotCreateBordersOnExport()
    {
        var html = _reader.Convert(BuildDocx(null, new TableProperties(), rows: 2, cols: 2)).Html;
        html.Should().Contain("docx-borderless-cell");

        var table = FirstTable(_writer.Convert(html));
        var tblBorders = table.GetFirstChild<TableProperties>()!.GetFirstChild<TableBorders>()!;
        tblBorders.TopBorder!.Val!.Value.Should().Be(BorderValues.None);
        tblBorders.InsideHorizontalBorder!.Val!.Value.Should().Be(BorderValues.None);
    }
}
