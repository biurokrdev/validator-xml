using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Golden;

[TestFixture]
public class GoldenSnapshotTests
{
    private DocxToHtmlConverter _converter = null!;

    [SetUp]
    public void Setup() => _converter = new DocxToHtmlConverter();

    [Test]
    public void SimpleParagraphs_MatchesSnapshot()
    {
        var content = _converter.Convert(GoldenDocuments.SimpleParagraphs());
        content.Html.Should().Contain("text-align:center");
        HtmlSnapshot.Verify(content.Html, "simple-paragraphs");
    }

    [Test]
    public void StyledRuns_MatchesSnapshot()
    {
        var content = _converter.Convert(GoldenDocuments.StyledRuns());

        content.Html.Should().Contain("<strong>pogrubiony");
        content.Html.Should().Contain("<em>kursywa");
        content.Html.Should().Contain("<u>podkreślony");
        content.Html.Should().Contain("color:#FF0000");
        content.Html.Should().Contain("font-size:16pt");
        HtmlSnapshot.Verify(content.Html, "styled-runs");
    }

    [Test]
    public void CharacterStyleRun_AppliesNamedRunStyle()
    {
        var content = _converter.Convert(GoldenDocuments.CharacterStyleRun());

        content.Html.Should().Contain("font-weight:bold");
        content.Html.Should().Contain("color:#C00000");
        content.Html.Should().Contain("font-size:14pt");
        HtmlSnapshot.Verify(content.Html, "character-style-run");
    }

    [Test]
    public void ParagraphSpacingAndIndent_MatchesSnapshot()
    {
        var content = _converter.Convert(GoldenDocuments.ParagraphSpacingAndIndent());

        content.Html.Should().Contain("margin-top:12pt");
        content.Html.Should().Contain("margin-bottom:6pt");
        content.Html.Should().Contain("margin-left:48px");
        content.Html.Should().Contain("text-indent:32px");
        HtmlSnapshot.Verify(content.Html, "paragraph-spacing-indent");
    }

    [Test]
    public void TabStopLeftCenterRight_RendersPositionedSegments()
    {
        var content = _converter.Convert(GoldenDocuments.TabStopLeftCenterRight());

        content.Html.Should().Contain("docx-tab-seg");
        content.Html.Should().Contain("data-tab-align=\"center\"").And.Contain("translateX(-50%)");
        content.Html.Should().Contain("data-tab-align=\"right\"").And.Contain("translateX(-100%)");
        content.Html.Should().NotContain("display:flex");
        content.Html.Should().Contain("Lewy");
        content.Html.Should().Contain("Środek");
        content.Html.Should().Contain("Prawy");
        HtmlSnapshot.Verify(content.Html, "tab-stops-lcr");
    }

    [Test]
    public void SimpleTable_MatchesSnapshot()
    {
        var content = _converter.Convert(GoldenDocuments.SimpleTableWithBordersAndWidths());

        content.Html.Should().Contain("<table");
        content.Html.Should().Contain("table-layout:fixed");
        content.Html.Should().Contain("<colgroup>");
        content.Html.Should().Contain("<col style=\"width:200px;\"");
        content.Html.Should().Contain("<col style=\"width:133px;\"");
        HtmlSnapshot.Verify(content.Html, "simple-table");
    }

    [Test]
    public void MergedCellsTable_MatchesSnapshot()
    {
        var content = _converter.Convert(GoldenDocuments.MergedCellsTable());

        content.Html.Should().Contain("table-layout:fixed");
        content.Html.Should().Contain("width:399px");
        content.Html.Should().Contain("colspan=\"2\"");
        content.Html.Should().Contain("rowspan=\"2\"");
        HtmlSnapshot.Verify(content.Html, "merged-cells-table");
    }

    [Test]
    public void HeaderFooterWithImage_MatchesSnapshot()
    {
        var content = _converter.Convert(GoldenDocuments.HeaderFooterWithImage());

        content.Header.Should().NotBeNull();
        content.Footer.Should().NotBeNull();
        content.Header!.Html.Should().Contain("data-width-emu=\"1270000\"");
        content.Header.Html.Should().Contain("data-height-emu=\"317500\"");

        HtmlSnapshot.Verify(content.Header.Html, "headerfooter-header");
        HtmlSnapshot.Verify(content.Footer!.Html, "headerfooter-footer");
    }
}
