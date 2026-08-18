using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TableColumnClampFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private const string WideTable = @"<w:tbl>
  <w:tblPr><w:tblW w:w=""8000"" w:type=""dxa""/><w:tblLayout w:type=""fixed""/></w:tblPr>
  <w:tblGrid><w:gridCol w:w=""4000""/><w:gridCol w:w=""4000""/></w:tblGrid>
  <w:tr><w:tc><w:tcPr><w:tcW w:w=""4000"" w:type=""dxa""/></w:tcPr><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc>
        <w:tc><w:tcPr><w:tcW w:w=""4000"" w:type=""dxa""/></w:tcPr><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr>
</w:tbl>";

    private static MemoryStream DocxWithBody(string bodyInnerXml, string sectPrXml)
    {
        var doc = $@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
  <w:body>{bodyInnerXml}{sectPrXml}</w:body>
</w:document>";
        var ms = new MemoryStream();
        using (var docx = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = docx.AddMainDocumentPart();
            using var w = new StreamWriter(mainPart.GetStream(FileMode.Create), Encoding.UTF8);
            w.Write(doc);
        }
        ms.Position = 0;
        return ms;
    }

    private const string TwoColumnSectPr = @"<w:sectPr>
  <w:pgSz w:w=""11906"" w:h=""16838""/>
  <w:pgMar w:top=""1417"" w:right=""1417"" w:bottom=""1417"" w:left=""1417"" w:header=""708"" w:footer=""708""/>
  <w:cols w:num=""2"" w:space=""708""/>
</w:sectPr>";

    private const string SingleColumnSectPr = @"<w:sectPr>
  <w:pgSz w:w=""11906"" w:h=""16838""/>
  <w:pgMar w:top=""1417"" w:right=""1417"" w:bottom=""1417"" w:left=""1417"" w:header=""708"" w:footer=""708""/>
</w:sectPr>";

    [Test]
    public void Read_WideTableInTwoColumnSection_ScalesPreviewToColumnWidth()
    {
        var html = _reader.Convert(DocxWithBody(WideTable, TwoColumnSectPr)).Html;

        var colPx = System.Text.RegularExpressions.Regex.Matches(html, "<col style=\"width:(\\d+)px;\"")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        colPx.Should().HaveCount(2);
        colPx.Sum().Should().BeLessThanOrEqualTo(280, "tabela szersza niż szpalta musi być doskalowana");
        colPx.Sum().Should().BeGreaterThan(240, "skala proporcjonalna, nie zerowanie");
        html.Should().Contain("data-w-tw=\"4000\"");
        html.Should().NotContain("width:533px");
    }

    [Test]
    public void Read_WideTableInSingleColumnSection_KeepsOriginalWidths()
    {
        var html = _reader.Convert(DocxWithBody(WideTable, SingleColumnSectPr)).Html;

        var colPx = System.Text.RegularExpressions.Regex.Matches(html, "<col style=\"width:(\\d+)px;\"")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        colPx.Should().HaveCount(2);
        colPx.Sum().Should().BeInRange(530, 536);
    }

    [Test]
    public void Read_TableWiderThanPage_SingleColumnSection_KeepsTruePxExtendingIntoMargins()
    {
        var hugeTable = WideTable.Replace("4000", "6000").Replace("8000", "12000");
        var html = _reader.Convert(DocxWithBody(hugeTable, SingleColumnSectPr)).Html;

        var colPx = System.Text.RegularExpressions.Regex.Matches(html, "<col style=\"width:(\\d+)px;\"")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        colPx.Should().HaveCount(2);
        colPx.Sum().Should().BeInRange(798, 802, "tabela wystaje w marginesy jak w Wordzie");
        html.Should().Contain("data-w-tw=\"6000\"");
        html.Should().NotContain("data-tbl-w-tw", "bez clampu nie ma potrzeby markera szerokości");
    }

    [Test]
    public void RoundTrip_ClampedTableInTwoColumnSection_ExportsOriginalTblW()
    {
        var html = _reader.Convert(DocxWithBody(WideTable, TwoColumnSectPr)).Html;
        html.Should().Contain("data-tbl-w-tw=\"8000\"");

        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var tblW = doc.MainDocumentPart!.Document!.Body!
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.TableWidth>().First();
        tblW.Type!.Value.Should().Be(DocumentFormat.OpenXml.Wordprocessing.TableWidthUnitValues.Dxa);
        tblW.Width!.Value.Should().Be("8000");
    }

    [Test]
    public void Read_CenteredTableWithTblInd_KeepsAutoMargins_NoPxMarginLeft()
    {
        var centeredTable = WideTable.Replace(
            "<w:tblPr><w:tblW w:w=\"8000\" w:type=\"dxa\"/>",
            "<w:tblPr><w:tblW w:w=\"8000\" w:type=\"dxa\"/><w:jc w:val=\"center\"/><w:tblInd w:w=\"500\" w:type=\"dxa\"/>");
        var html = _reader.Convert(DocxWithBody(centeredTable, SingleColumnSectPr)).Html;

        var tableTag = System.Text.RegularExpressions.Regex.Match(html, "<table[^>]*>").Value;
        tableTag.Should().Contain("margin-left:auto;margin-right:auto;");
        tableTag.Should().NotMatchRegex(@"margin-left:\d+px", "px z tblInd nie może nadpisać wyśrodkowania");
    }
}
