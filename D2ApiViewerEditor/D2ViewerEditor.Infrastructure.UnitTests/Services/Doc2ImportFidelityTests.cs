using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class Doc2ImportFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static MemoryStream Docx(params OpenXmlElement[] bodyChildren)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var c in bodyChildren) body.Append(c);
            mainPart.Document = new Document(body);
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void BodyLeftTab_PlacesFollowingSegmentAtStopStart()
    {
        
        using var ms = Docx(new Paragraph(
            new ParagraphProperties(new Tabs(new TabStop { Val = TabStopValues.Left, Position = 1440 })),
            new Run(new Text("Before")), new Run(new TabChar()), new Run(new Text("After"))));

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("data-tab-align=\"left\"");
        html.Should().Contain("left:96px");
        html.Should().NotContain("translateX");                 
        html.Should().Contain("Before").And.Contain("After");   
    }

    [Test]
    public void BodyRightTab_AlignsFollowingSegmentEndToStop()
    {
        using var ms = Docx(new Paragraph(
            new ParagraphProperties(new Tabs(new TabStop { Val = TabStopValues.Right, Position = 9000 })),
            new Run(new Text("Podpis")), new Run(new TabChar()), new Run(new Text("Data"))));

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("data-tab-align=\"right\"");
        html.Should().Contain("left:600px");            
        html.Should().Contain("translateX(-100%)");     
    }

    [Test]
    public void BodyMultipleTabs_ProducesOneSegmentPerStop()
    {
        using var ms = Docx(new Paragraph(
            new ParagraphProperties(new Tabs(
                new TabStop { Val = TabStopValues.Left, Position = 2000 },
                new TabStop { Val = TabStopValues.Center, Position = 5000 },
                new TabStop { Val = TabStopValues.Right, Position = 9000 })),
            new Run(new Text("A")), new Run(new TabChar()),
            new Run(new Text("B")), new Run(new TabChar()),
            new Run(new Text("C")), new Run(new TabChar()),
            new Run(new Text("D"))));

        var html = _reader.Convert(ms).Html;

        System.Text.RegularExpressions.Regex.Matches(html, "docx-tab-seg").Count.Should().Be(3);
        html.Should().Contain("data-tab-align=\"left\"")
            .And.Contain("data-tab-align=\"center\"")
            .And.Contain("data-tab-align=\"right\"");
        html.Should().Contain("A").And.Contain("B").And.Contain("C").And.Contain("D");
    }

    [Test]
    public void BodyTabs_RoundTripPreservesStopsAndTabCount()
    {
        var html = "<p data-tab-stops=\"1440:left;9000:right\">Before"
                   + "<span class=\"docx-tab-seg\" data-tab-align=\"left\">Mid</span>"
                   + "<span class=\"docx-tab-seg\" data-tab-align=\"right\">End</span></p>";

        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var para = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
        var tabs = para.ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().ToList();
        tabs.Should().HaveCount(2);
        para.Descendants<TabChar>().Should().HaveCount(2);
        para.InnerText.Should().Contain("Before").And.Contain("Mid").And.Contain("End");
    }

    [Test]
    public void CellVerticalCenter_EmitsSingleMiddle()
        => AssertVerticalAlignment(TableVerticalAlignmentValues.Center, "middle");

    [Test]
    public void CellVerticalBottom_EmitsSingleBottom()
        => AssertVerticalAlignment(TableVerticalAlignmentValues.Bottom, "bottom");

    private void AssertVerticalAlignment(TableVerticalAlignmentValues val, string css)
    {
        var table = SingleCellTable(new TableCellProperties(new TableCellVerticalAlignment { Val = val }));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain($"vertical-align:{css};");
        
        System.Text.RegularExpressions.Regex.Matches(html, "vertical-align:").Count.Should().Be(1);
    }

    [Test]
    public void CellWithoutVerticalAlignment_DefaultsToTopOnce()
    {
        var table = SingleCellTable(new TableCellProperties());

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        System.Text.RegularExpressions.Regex.Matches(html, "vertical-align:").Count.Should().Be(1);
        html.Should().Contain("vertical-align:top;");
    }

    private static Table SingleCellTable(TableCellProperties cellProps) => new(
        new TableProperties(new TableStyle { Val = "TableGrid" }),
        new TableGrid(new GridColumn { Width = "3000" }),
        new TableRow(new TableCell(cellProps, new Paragraph(new Run(new Text("x"))))));

    [Test]
    public void ShortSingleCellRow_SpansFullGrid()
    {
        
        var table = ThreeColTable(
            Row(Cell("H1"), Cell("H2"), Cell("H3")),
            Row(Cell("MERGED-ALL")));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("<td colspan=\"3\"");
        html.Should().Contain("MERGED-ALL");
    }

    [Test]
    public void ShortRow_LeadingCellThenContent_ExtendsLastCellOnly()
    {
        var table = ThreeColTable(
            Row(Cell("H1"), Cell("H2"), Cell("H3")),
            Row(Cell("Lp"), Cell("WIDE")));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("<td class=\"docx-borderless-cell\" style=").And.Contain("Lp");
        html.Should().Contain("<td colspan=\"2\"");
        html.Should().Contain("WIDE");
        html.Should().NotContain("colspan=\"3\"");
    }

    [Test]
    public void ExplicitGridSpanRow_IsUnchanged_AndContentNotDuplicated()
    {
        var table = ThreeColTable(
            Row(Cell("H1"), Cell("H2"), Cell("H3")),
            Row(Cell("SPANNED", 3)));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("<td colspan=\"3\"");
        System.Text.RegularExpressions.Regex.Matches(html, "SPANNED").Count.Should().Be(1);
    }

    [Test]
    public void FullRows_AreNotAlteredByShortRowLogic()
    {
        var table = ThreeColTable(
            Row(Cell("A"), Cell("B"), Cell("C")),
            Row(Cell("D"), Cell("E"), Cell("F")));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().NotContain("colspan");
    }

    [Test]
    public void HMergeRow_FoldsContinueCellsIntoRestartColspan()
    {
        
        var table = ThreeColTable(
            Row(Cell("H1"), Cell("H2"), Cell("H3")),
            Row(HMergeCell("MERGED", MergedCellValues.Restart),
                HMergeCell("", MergedCellValues.Continue),
                HMergeCell("", MergedCellValues.Continue)));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("<td colspan=\"3\"");
        html.Should().Contain("MERGED");
        
        System.Text.RegularExpressions.Regex.Matches(html, "<td").Count.Should().Be(4);
    }

    [Test]
    public void HMergeContinue_WithOmittedVal_IsTreatedAsContinue()
    {
        
        var table = ThreeColTable(
            Row(Cell("H1"), Cell("H2"), Cell("H3")),
            Row(HMergeCell("AB", MergedCellValues.Restart), HMergeCell("", null), Cell("C")));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("<td colspan=\"2\"");
        html.Should().Contain("AB").And.Contain("C");
        System.Text.RegularExpressions.Regex.Matches(html, "<td").Count.Should().Be(5);
    }

    [Test]
    public void HMergeContinue_WithoutExplicitRestart_FoldsIntoPreviousCell()
    {
        var table = ThreeColTable(
            Row(Cell("Umowa"), HMergeCell("", MergedCellValues.Continue), HMergeCell("", null)));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        System.Text.RegularExpressions.Regex.Matches(html, "<td").Count.Should().Be(1);
        html.Should().Contain("colspan=\"3\"");
        html.Should().Contain("Umowa");
    }

    [Test]
    public void HMergeContinue_AsFirstCellOfRow_RendersNormally_NoContentLoss()
    {
        var table = ThreeColTable(
            Row(HMergeCell("ORPHAN", MergedCellValues.Continue), Cell("B"), Cell("C")));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("ORPHAN");
        System.Text.RegularExpressions.Regex.Matches(html, "<td").Count.Should().Be(3);
    }

    [Test]
    public void HMerge_InSecondTcPrAfterContent_StillMergesRow()
    {
        var table = ThreeColTable(
            Row(
                DoubleTcPrCell("Misio", MergedCellValues.Restart),
                DoubleTcPrCell("", MergedCellValues.Continue),
                DoubleTcPrCell("", MergedCellValues.Continue)));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        System.Text.RegularExpressions.Regex.Matches(html, "<td").Count.Should().Be(1);
        html.Should().Contain("colspan=\"3\"");
        html.Should().Contain("Misio");
    }

    private static TableCell HMergeCell(string text, MergedCellValues? val)
    {
        var hMerge = val == null ? new HorizontalMerge() : new HorizontalMerge { Val = val };
        return new TableCell(
            new TableCellProperties(hMerge),
            new Paragraph(new Run(new Text(text))));
    }

    private static TableCell DoubleTcPrCell(string text, MergedCellValues val)
    {
        return new TableCell(
            new TableCellProperties(new TableCellWidth { Width = "1000", Type = TableWidthUnitValues.Dxa }),
            new Paragraph(new Run(new Text(text))),
            new TableCellProperties(new HorizontalMerge { Val = val }));
    }

    [Test]
    public void CellVerticalAlign_InSecondTcPrAfterContent_IsApplied()
    {
        var cell = new TableCell(
            new TableCellProperties(new TableCellWidth { Width = "3000", Type = TableWidthUnitValues.Dxa }),
            new Paragraph(new Run(new Text("x"))),
            new TableCellProperties(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }));
        var table = new Table(
            new TableProperties(new TableStyle { Val = "TableGrid" }),
            new TableGrid(new GridColumn { Width = "3000" }),
            new TableRow(cell));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("vertical-align:middle;");
        System.Text.RegularExpressions.Regex.Matches(html, "vertical-align:").Count.Should().Be(1);
    }

    [Test]
    public void CellVerticalAlign_FromTableStyleWholeTable_AppliesMiddle()
    {
        var style = new Style(
            new StyleTableCellProperties(
                new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }))
        {
            Type = StyleValues.Table,
            StyleId = "CenteredContentTable",
        };
        var table = new Table(
            new TableProperties(new TableStyle { Val = "CenteredContentTable" }),
            new TableGrid(new GridColumn { Width = "3000" }),
            new TableRow(new TableCell(
                new TableCellProperties(),
                new Paragraph(new Run(new Text("x"))))));

        using var ms = DocxWithStyles(style, table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().Contain("vertical-align:middle;");
        System.Text.RegularExpressions.Regex.Matches(html, "vertical-align:").Count.Should().Be(1);
    }

    [Test]
    public void Write_BorderNoneNormalizedByBrowser_EmitsExplicitNoneBorders()
    {
        var html = "<table><tr><td style=\"border: medium none;\"><p>x</p></td></tr></table>";

        var bytes = _writer.Convert(html);

        var borders = FirstCellBorders(bytes);
        borders.Should().NotBeNull("jawny brak linii musi nadpisać obramowanie ze stylu tabeli");
        
        borders!.Elements<BorderType>().Should().HaveCount(4)
            .And.OnlyContain(b => b.Val != null && b.Val.Value == BorderValues.Nil);
    }

    [Test]
    public void Write_UniformRgbBorder_KeepsColorAndStyle()
    {
        var html = "<table><tr><td style=\"border: 0.5px solid rgb(217, 217, 217);\"><p>x</p></td></tr></table>";

        var bytes = _writer.Convert(html);

        var borders = FirstCellBorders(bytes);
        borders.Should().NotBeNull();
        borders!.Elements<BorderType>().Should().HaveCount(4)
            .And.OnlyContain(b => b.Val!.Value == BorderValues.Single && b.Color!.Value == "D9D9D9");
    }

    [Test]
    public void Write_MixedSides_ColoredAndMediumNone_EmitsBoth()
    {
        var html = "<table><tr><td style=\"border-top: 0.5px solid #D9D9D9; border-bottom: medium none;\">" +
                   "<p>x</p></td></tr></table>";

        var bytes = _writer.Convert(html);

        var borders = FirstCellBorders(bytes);
        borders.Should().NotBeNull();
        borders!.GetFirstChild<TopBorder>()!.Val!.Value.Should().Be(BorderValues.Single);
        borders.GetFirstChild<TopBorder>()!.Color!.Value.Should().Be("D9D9D9");
        borders.GetFirstChild<BottomBorder>()!.Val!.Value.Should().Be(BorderValues.Nil);
    }

    private static TableCellBorders? FirstCellBorders(byte[] docxBytes)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docxBytes), false);
        return doc.MainDocumentPart!.Document!.Body!
            .Descendants<TableCell>().First()
            .TableCellProperties?.GetFirstChild<TableCellBorders>();
    }

    private static MemoryStream DocxWithStyles(Style style, params OpenXmlElement[] bodyChildren)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(style);
            var body = new Body();
            foreach (var c in bodyChildren) body.Append(c);
            mainPart.Document = new Document(body);
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void TabInsideTableCell_DoesNotUsePositionedSegments()
    {
        
        var cellPara = new Paragraph(
            new ParagraphProperties(new Tabs(new TabStop { Val = TabStopValues.Right, Position = 9000 })),
            new Run(new Text("Waluta")), new Run(new TabChar()), new Run(new Text("PLN")));
        var table = ThreeColTable(
            Row(new TableCell(new TableCellProperties(), cellPara), Cell("B"), Cell("C")));

        using var ms = Docx(table, new Paragraph());
        var html = _reader.Convert(ms).Html;

        html.Should().NotContain("docx-tab-seg");
        html.Should().NotContain("position:absolute");
        html.Should().Contain("data-tab-stops=\"9000:right\"");
        html.Should().Contain("Waluta").And.Contain("PLN");
    }

    private static Table ThreeColTable(params TableRow[] rows)
    {
        var t = new Table(
            new TableProperties(new TableStyle { Val = "TableGrid" }),
            new TableGrid(
                new GridColumn { Width = "3000" },
                new GridColumn { Width = "3000" },
                new GridColumn { Width = "3000" }));
        foreach (var r in rows) t.Append(r);
        return t;
    }

    private static TableRow Row(params TableCell[] cells) => new(cells.Cast<OpenXmlElement>().ToArray());

    private static TableCell Cell(string text, int gridSpan = 1)
    {
        var props = gridSpan > 1
            ? new TableCellProperties(new GridSpan { Val = gridSpan })
            : new TableCellProperties();
        return new TableCell(props, new Paragraph(new Run(new Text(text))));
    }

    [Test]
    public void ParagraphSpacing_MapsToDistinctCssProperties()
    {
        using var ms = Docx(new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines
            { Before = "240", After = "120", Line = "360", LineRule = LineSpacingRuleValues.Auto }),
            new Run(new Text("l1")), new Run(new Break()), new Run(new Text("l2"))));

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("margin-top:12pt;");     
        html.Should().Contain("padding-bottom:6pt;");  
        html.Should().Contain("line-height:1.8;");     
        html.Should().Contain("--w-line-tw:360;");     
        html.Should().Contain("<br/>");                
    }

    [Test]
    public void ModernTextBox_InAlternateContent_RendersTextOnce()
    {
        
        const string body = @"<w:p><w:r>
  <mc:AlternateContent>
    <mc:Choice Requires=""wps"">
      <w:drawing><wp:inline><wp:extent cx=""1000000"" cy=""500000""/>
        <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
          <wps:wsp><wps:txbx><w:txbxContent>
            <w:p><w:r><w:t>Tresc pola tekstowego</w:t></w:r></w:p>
          </w:txbxContent></wps:txbx></wps:wsp>
        </a:graphicData></a:graphic>
      </wp:inline></w:drawing>
    </mc:Choice>
    <mc:Fallback>
      <w:pict><v:shape><v:textbox><w:txbxContent>
        <w:p><w:r><w:t>Tresc pola tekstowego</w:t></w:r></w:p>
      </w:txbxContent></v:textbox></v:shape></w:pict>
    </mc:Fallback>
  </mc:AlternateContent>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-textbox");
        html.Should().Contain("Tresc pola tekstowego");
        System.Text.RegularExpressions.Regex.Matches(html, "Tresc pola tekstowego").Count
            .Should().Be(1, "Choice i Fallback niosą tę samą treść — renderujemy raz");
    }

    [Test]
    public void AnchoredTextBox_IsAbsolutelyPositionedFromWordOffsets()
    {
        
        const string body = @"<w:p><w:r>
  <w:drawing>
    <wp:anchor behindDoc=""0"" relativeHeight=""1"" allowOverlap=""1"" simplePos=""0""
      locked=""0"" layoutInCell=""1"">
      <wp:simplePos x=""0"" y=""0""/>
      <wp:positionH relativeFrom=""page""><wp:posOffset>914400</wp:posOffset></wp:positionH>
      <wp:positionV relativeFrom=""page""><wp:posOffset>1828800</wp:posOffset></wp:positionV>
      <wp:extent cx=""1828800"" cy=""457200""/>
      <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
        <wps:wsp><wps:txbx><w:txbxContent>
          <w:p><w:r><w:t>Zakotwiczone pole</w:t></w:r></w:p>
        </w:txbxContent></wps:txbx></wps:wsp>
      </a:graphicData></a:graphic>
    </wp:anchor>
  </w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-textbox").And.Contain("Zakotwiczone pole");
        html.Should().Contain("position:absolute;");
        html.Should().Contain("left:96px;").And.Contain("top:96px;");
        html.Should().Contain("width:192px;");   
    }

    [Test]
    public void LineShape_WithoutImage_RendersVisibleLine()
    {
        
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""5486400"" cy=""0""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp><wps:spPr>
        <a:prstGeom prst=""straightConnector1""><a:avLst/></a:prstGeom>
        <a:ln w=""19050""><a:solidFill><a:srgbClr val=""FF6600""/></a:solidFill></a:ln>
      </wps:spPr></wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-line");
        html.Should().Contain("background:#FF6600;");
        html.Should().Contain("width:576px;");   
        html.Should().Contain("height:2px;");     
    }

    [Test]
    public void CustomGeometryShape_WithoutImage_RendersAsInlineSvgPath()
    {
        
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""817245"" cy=""276860""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp><wps:spPr>
        <a:custGeom><a:pathLst>
          <a:path w=""100"" h=""50"">
            <a:moveTo><a:pt x=""0"" y=""0""/></a:moveTo>
            <a:lnTo><a:pt x=""100"" y=""0""/></a:lnTo>
            <a:lnTo><a:pt x=""100"" y=""50""/></a:lnTo>
            <a:close/>
          </a:path>
        </a:pathLst></a:custGeom>
        <a:solidFill><a:srgbClr val=""000066""/></a:solidFill>
        <a:ln><a:noFill/></a:ln>
      </wps:spPr></wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-custgeom");
        html.Should().Contain("<svg").And.Contain("viewBox=\"0 0 100 50\"");
        html.Should().Contain("<path d=\"M0 0 L100 0 L100 50 Z\"");
        html.Should().Contain("fill=\"#000066\"");
        
        html.Should().NotContain("stroke=");
    }

    [Test]
    public void CustomGeometryShape_ThemeFill_ResolvesSchemeColorFromTheme()
    {
        
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""817245"" cy=""276860""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp><wps:spPr>
        <a:custGeom><a:pathLst>
          <a:path w=""100"" h=""50""><a:moveTo><a:pt x=""0"" y=""0""/></a:moveTo>
            <a:lnTo><a:pt x=""100"" y=""0""/></a:lnTo><a:lnTo><a:pt x=""100"" y=""50""/></a:lnTo><a:close/></a:path>
        </a:pathLst></a:custGeom>
        <a:solidFill><a:schemeClr val=""accent1""/></a:solidFill>
      </wps:spPr></wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBodyWithTheme(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-custgeom");
        html.Should().Contain("fill=\"#FF6200\"");
        html.Should().NotContain("#000000");
    }

    [Test]
    public void CustomGeometryShape_StyleFillRef_ResolvesSchemeColorFromTheme()
    {
        
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""817245"" cy=""276860""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp>
        <wps:spPr><a:custGeom><a:pathLst>
          <a:path w=""100"" h=""50""><a:moveTo><a:pt x=""0"" y=""0""/></a:moveTo>
            <a:lnTo><a:pt x=""100"" y=""0""/></a:lnTo><a:lnTo><a:pt x=""100"" y=""50""/></a:lnTo><a:close/></a:path>
        </a:pathLst></a:custGeom></wps:spPr>
        <wps:style><a:fillRef idx=""1""><a:schemeClr val=""accent1""/></a:fillRef></wps:style>
      </wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBodyWithTheme(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-custgeom").And.Contain("fill=\"#FF6200\"");
        html.Should().NotContain("#000000");
    }

    [Test]
    public void CustomGeometryShape_ExplicitNoFill_RendersWithoutInk()
    {
        
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""714375"" cy=""714375""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp>
        <wps:spPr><a:custGeom><a:pathLst>
          <a:path w=""100"" h=""100""><a:moveTo><a:pt x=""0"" y=""0""/></a:moveTo>
            <a:lnTo><a:pt x=""100"" y=""0""/></a:lnTo><a:lnTo><a:pt x=""100"" y=""100""/></a:lnTo><a:close/></a:path>
        </a:pathLst></a:custGeom><a:noFill/></wps:spPr>
        <wps:style><a:fillRef idx=""1""><a:schemeClr val=""accent1""/></a:fillRef></wps:style>
      </wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBodyWithTheme(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-custgeom");
        html.Should().Contain("fill=\"none\"");
        
        html.Should().NotContain("fill=\"#FF6200\"").And.NotContain("fill=\"currentColor\"");
    }

    [Test]
    public void CustomGeometryShape_NoResolvableFill_DoesNotRenderBlackBlob()
    {
        
        const string body = @"<w:p><w:r>
  <w:drawing><wp:inline><wp:extent cx=""817245"" cy=""276860""/>
    <a:graphic><a:graphicData uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape"">
      <wps:wsp><wps:spPr>
        <a:custGeom><a:pathLst>
          <a:path w=""100"" h=""50""><a:moveTo><a:pt x=""0"" y=""0""/></a:moveTo>
            <a:lnTo><a:pt x=""100"" y=""0""/></a:lnTo><a:lnTo><a:pt x=""100"" y=""50""/></a:lnTo><a:close/></a:path>
        </a:pathLst></a:custGeom>
      </wps:spPr></wps:wsp>
    </a:graphicData></a:graphic>
  </wp:inline></w:drawing>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-custgeom");
        html.Should().Contain("fill=\"currentColor\"");
        html.Should().NotContain("#000000");
    }

    [Test]
    public void VmlTextBox_Standalone_RendersText()
    {
        const string body = @"<w:p><w:r>
  <w:pict><v:shape><v:textbox><w:txbxContent>
    <w:p><w:r><w:t>Legacy textbox</w:t></w:r></w:p>
  </w:txbxContent></v:textbox></v:shape></w:pict>
</w:r></w:p>";
        using var ms = DocxFromRawBody(body);

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-textbox").And.Contain("Legacy textbox");
    }

    private static MemoryStream DocxFromRawBody(string bodyInnerXml)
    {
        const string doc = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""
  xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing""
  xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main""
  xmlns:mc=""http://schemas.openxmlformats.org/markup-compatibility/2006""
  xmlns:wps=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape""
  xmlns:v=""urn:schemas-microsoft-com:vml""
  xmlns:w10=""urn:schemas-microsoft-com:office:word"">
  <w:body>{BODY}</w:body>
</w:document>";

        var ms = new MemoryStream();
        using (var wpd = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = wpd.AddMainDocumentPart();
            using var w = new StreamWriter(mainPart.GetStream(FileMode.Create), Encoding.UTF8);
            w.Write(doc.Replace("{BODY}", bodyInnerXml));
        }
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream DocxFromRawBodyWithTheme(string bodyInnerXml)
    {
        const string doc = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""
  xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing""
  xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main""
  xmlns:mc=""http://schemas.openxmlformats.org/markup-compatibility/2006""
  xmlns:wps=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape""
  xmlns:v=""urn:schemas-microsoft-com:vml""
  xmlns:w10=""urn:schemas-microsoft-com:office:word"">
  <w:body>{BODY}</w:body>
</w:document>";

        const string theme = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<a:theme xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" name=""Test"">
  <a:themeElements>
    <a:clrScheme name=""Test"">
      <a:dk1><a:srgbClr val=""000000""/></a:dk1>
      <a:lt1><a:srgbClr val=""FFFFFF""/></a:lt1>
      <a:dk2><a:srgbClr val=""1F1F1F""/></a:dk2>
      <a:lt2><a:srgbClr val=""EEEEEE""/></a:lt2>
      <a:accent1><a:srgbClr val=""FF6200""/></a:accent1>
      <a:accent2><a:srgbClr val=""112233""/></a:accent2>
      <a:accent3><a:srgbClr val=""112233""/></a:accent3>
      <a:accent4><a:srgbClr val=""112233""/></a:accent4>
      <a:accent5><a:srgbClr val=""112233""/></a:accent5>
      <a:accent6><a:srgbClr val=""112233""/></a:accent6>
      <a:hlink><a:srgbClr val=""0000FF""/></a:hlink>
      <a:folHlink><a:srgbClr val=""800080""/></a:folHlink>
    </a:clrScheme>
    <a:fontScheme name=""Test"">
      <a:majorFont><a:latin typeface=""Calibri""/><a:ea typeface=""""/><a:cs typeface=""""/></a:majorFont>
      <a:minorFont><a:latin typeface=""Calibri""/><a:ea typeface=""""/><a:cs typeface=""""/></a:minorFont>
    </a:fontScheme>
    <a:fmtScheme name=""Test"">
      <a:fillStyleLst><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:fillStyleLst>
      <a:lnStyleLst><a:ln><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:ln><a:ln><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:ln><a:ln><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:ln></a:lnStyleLst>
      <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
      <a:bgFillStyleLst><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:bgFillStyleLst>
    </a:fmtScheme>
  </a:themeElements>
</a:theme>";

        var ms = new MemoryStream();
        using (var wpd = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = wpd.AddMainDocumentPart();
            using (var w = new StreamWriter(mainPart.GetStream(FileMode.Create), Encoding.UTF8))
                w.Write(doc.Replace("{BODY}", bodyInnerXml));

            var themePart = mainPart.AddNewPart<ThemePart>();
            using var tw = new StreamWriter(themePart.GetStream(FileMode.Create), Encoding.UTF8);
            tw.Write(theme);
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void ValidSvg_IsEmittedAsImageSvgXmlDataUri()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>"
                  + "<rect width='10' height='10' fill='red'/></svg>";
        using var ms = DocxWithSvg(svg, "image/svg+xml");

        var result = _reader.Convert(ms);

        result.Html.Should().Contain("src=\"data:image/svg+xml;base64,");
        result.Images.Should().ContainSingle().Which.ContentType.Should().Be("image/svg+xml");
    }

    [Test]
    public void MalformedSvgMimePrefix_IsNormalised()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect/></svg>";
        
        using var ms = DocxWithSvg(svg, "img/svg+xml");

        var result = _reader.Convert(ms);

        result.Html.Should().Contain("data:image/svg+xml;base64,");
        result.Html.Should().NotContain("data:img/svg");
    }

    [Test]
    public void DangerousSvg_ScriptAndHandlersAreStripped()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)' width='10' height='10'>"
                  + "<script>alert(2)</script><rect width='10' height='10' onclick='x()'/></svg>";
        using var ms = DocxWithSvg(svg, "image/svg+xml");

        var result = _reader.Convert(ms);

        var decoded = DecodeSingleImage(result);
        decoded.Should().NotContain("script");
        decoded.Should().NotContain("onload");
        decoded.Should().NotContain("onclick");
    }

    [Test]
    public void SvgLogoBuiltFromDefsAndUse_KeepsVisibleContentInDataUri()
    {
        
        var svg =
            "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 100 40'>"
            + "<defs><path id='lion' d='M10 10 C 20 0, 40 0, 50 10 Z' fill='#ff6200'/></defs>"
            + "<use xlink:href='#lion'/></svg>";
        using var ms = DocxWithSvg(svg, "image/svg+xml");

        var result = _reader.Convert(ms);

        result.Html.Should().Contain("data:image/svg+xml;base64,");
        var decoded = DecodeSingleImage(result);
        decoded.Should().Contain("<use").And.Contain("#lion");
    }

    [Test]
    public void SvgWithUtf8Bom_IsAcceptedAndEmbedded()
    {
        
        var svg = "\uFEFF<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>"
                  + "<rect width='10' height='10' fill='red'/></svg>";
        using var ms = DocxWithSvg(svg, "image/svg+xml");

        var result = _reader.Convert(ms);

        result.Images.Should().ContainSingle();
        result.Html.Should().Contain("data:image/svg+xml;base64,");
    }

    [Test]
    public void NonSvgDataInSvgPart_IsRejected_NotEmbedded()
    {
        using var ms = DocxWithSvg("this is not svg at all", "image/svg+xml");

        var result = _reader.Convert(ms);

        result.Images.Should().BeEmpty("niepoprawny SVG jest odrzucany, nie osadzany surowo");
    }

    private static string DecodeSingleImage(D2ViewerEditor.Domain.Models.DocumentContent content)
    {
        var img = content.Images.Single();
        return Encoding.UTF8.GetString(System.Convert.FromBase64String(img.Base64Data!));
    }

    private static MemoryStream DocxWithSvg(string svg, string contentType)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var body = new Body();
            var imagePart = mainPart.AddImagePart(contentType);
            using (var s = new MemoryStream(Encoding.UTF8.GetBytes(svg))) imagePart.FeedData(s);
            var rid = mainPart.GetIdOfPart(imagePart);
            body.Append(new Paragraph(new Run(BuildInlineDrawing(rid, 100000, 100000))));
            mainPart.Document = new Document(body);
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private static Drawing BuildInlineDrawing(string relationshipId, long cx, long cy)
        => new(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = cx, Cy = cy },
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1U, Name = "svg" },
            new DocumentFormat.OpenXml.Drawing.Graphic(
                new DocumentFormat.OpenXml.Drawing.GraphicData(
                    new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                        new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 0U, Name = "svg" },
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                        new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                            new DocumentFormat.OpenXml.Drawing.Blip { Embed = relationshipId },
                            new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
                        new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
                            new DocumentFormat.OpenXml.Drawing.Transform2D(
                                new DocumentFormat.OpenXml.Drawing.Offset { X = 0, Y = 0 },
                                new DocumentFormat.OpenXml.Drawing.Extents { Cx = cx, Cy = cy }),
                            new DocumentFormat.OpenXml.Drawing.PresetGeometry(new DocumentFormat.OpenXml.Drawing.AdjustValueList())
                            { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
}
