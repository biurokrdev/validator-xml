using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class TabStopFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }

    private static MemoryStream BuildDocxWithTabbedHeader(int centerPos = 4536, int rightPos = 9072)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            var headerPart = mainPart.AddNewPart<HeaderPart>();
            headerPart.Header = new Header(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Center, Position = centerPos },
                    new TabStop { Val = TabStopValues.Right, Position = rightPos })),
                new Run(new Text("Lewa")),
                new Run(new TabChar()),
                new Run(new Text("Środek")),
                new Run(new TabChar()),
                new Run(new Text("Prawa"))));
            headerPart.Header.Save();

            body.Append(new Paragraph(new Run(new Text("Treść"))));
            var sectPr = new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) },
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417, Header = 708, Footer = 708 });
            body.Append(sectPr);

            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void Read_HeaderWithTabStops_EmitsDataTabStopsAndPositionedSegments()
    {
        using var stream = BuildDocxWithTabbedHeader();

        var content = _reader.Convert(stream);

        content.Header.Should().NotBeNull();
        var html = content.Header!.Html;
        html.Should().Contain("data-tab-stops=\"4536:center;9072:right\"");
        html.Should().Contain("docx-tab-seg");
        html.Should().Contain("data-tab-align=\"center\"");
        html.Should().Contain("translateX(-50%)");
        html.Should().Contain("data-tab-align=\"right\"");
        html.Should().Contain("translateX(-100%)");
        html.Should().Contain("left:302px");
        html.Should().Contain("Środek");
    }

    [Test]
    public void Read_BodyParagraphWithTabStops_RendersPositionedSegmentsAtRealStops()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Right, Position = 9000 })),
                new Run(new Text("Podpis")),
                new Run(new TabChar()),
                new Run(new Text("Data")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var content = _reader.Convert(ms);

        content.Html.Should().Contain("data-tab-stops=\"9000:right\"");
        content.Html.Should().NotContain("display:flex", "body używa teraz pozycjonowania na realnych stopach");
        content.Html.Should().Contain("docx-tab-seg");
        content.Html.Should().Contain("data-tab-align=\"right\"");
        content.Html.Should().Contain("left:600px");
        content.Html.Should().Contain("translateX(-100%)");
    }

    [Test]
    public void Read_TabbedParagraphWithComplexDateField_IsStillPositioned()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Left, Position = 5245 })),
                new Run(new TabChar()),
                new Run(new Text("Warszawa, ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                new Run(new FieldCode(" DATE ")),
                new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                new Run(new Text("3.07.2026")),
                new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
                new Run(new Text(" r.") { Space = SpaceProcessingModeValues.Preserve }))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-tab-seg", "pole złożone nie może wyłączać pozycjonowania tabów");
        html.Should().Contain("data-tab-align=\"left\"");
        html.Should().Contain("left:349px");
        html.Should().Contain("Warszawa, ")
            .And.Contain(System.DateTime.Now.ToString("dd.MM.yyyy"))
            .And.Contain(" r.");
        html.Should().NotContain("min-width:2em", "tab nie może degradować do wcięcia przy marginesie");
    }

    [Test]
    public void Read_MoreTabsThanStops_FallsBackToDefaultTabStops()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Left, Position = 5387 })),
                new Run(new TabChar()),
                new Run(new TabChar(), new Text("Warszawa, 3.07.2026 r.")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;

        System.Text.RegularExpressions.Regex.Matches(html, "docx-tab-seg").Count.Should().Be(2);
        html.Should().Contain("left:359px");
        html.Should().Contain("left:377px");
        html.Should().Contain("Warszawa, 3.07.2026 r.");
    }

    [Test]
    public void Read_MoreTabsThanStops_UsesDefaultTabStopFromSettings()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            var settingsPart = mainPart.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new Settings(new DefaultTabStop { Val = 1440 });
            settingsPart.Settings.Save();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Left, Position = 5387 })),
                new Run(new TabChar()),
                new Run(new TabChar(), new Text("Za drugim tabem")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("left:384px");
        html.Should().Contain("Za drugim tabem");
    }

    [Test]
    public void Read_PositionalTab_RendersCenteredSegment()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(
                    new PositionalTab
                    {
                        Alignment = AbsolutePositionTabAlignmentValues.Center,
                        RelativeTo = AbsolutePositionTabPositioningBaseValues.Margin,
                        Leader = AbsolutePositionTabLeaderCharValues.None,
                    },
                    new Text("Warszawa, 3.07.2026 r."))),
                new SectionProperties(
                    new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 1417, Right = 1417, Bottom = 1417, Left = 1417, Header = 708, Footer = 708 })));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-tab-seg");
        html.Should().Contain("data-tab-align=\"center\"");
        html.Should().Contain("translateX(-50%)");
        html.Should().Contain("Warszawa, 3.07.2026 r.");
    }

    [Test]
    public void Write_DataTabStops_RecreatesTabsInParagraphProperties()
    {
        var html = "<p data-tab-stops=\"4536:center;9072:right:dot\">Lewa\tŚrodek\tPrawa</p>";

        var bytes = _writer.Convert(html);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var para = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
        var tabs = para.ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().ToList();

        tabs.Should().HaveCount(2);
        ((int)tabs[0].Position!.Value).Should().Be(4536);
        tabs[0].Val!.Value.Should().Be(TabStopValues.Center);
        ((int)tabs[1].Position!.Value).Should().Be(9072);
        tabs[1].Val!.Value.Should().Be(TabStopValues.Right);
        tabs[1].Leader!.Value.Should().Be(TabStopLeaderCharValues.Dot);
    }

    [Test]
    public void Write_LiteralTabInText_BecomesTabCharElement_NotText()
    {
        var bytes = _writer.Convert("<p>Lewa\tPrawa</p>");

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var para = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();

        para.Descendants<TabChar>().Should().HaveCount(1, "Word nie renderuje \\t w w:t — tab musi być elementem w:tab");
        para.InnerText.Should().NotContain("\t");
    }

    [Test]
    public void RoundTrip_TabbedHeader_PreservesStopsAndTabs()
    {
        using var stream = BuildDocxWithTabbedHeader();
        var content = _reader.Convert(stream);

        var bytes = _writer.Convert("<p>Treść</p>",
            header: content.Header, footer: content.Footer,
            margins: content.Margins, pageSize: content.PageSize);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var headerPart = doc.MainDocumentPart!.HeaderParts.First();
        var para = headerPart.Header!.Elements<Paragraph>().First();

        var tabs = para.ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().ToList();
        tabs.Should().HaveCount(2);
        ((int)tabs[0].Position!.Value).Should().Be(4536);
        ((int)tabs[1].Position!.Value).Should().Be(9072);

        para.Descendants<TabChar>().Should().HaveCount(2);
        para.InnerText.Should().Contain("Lewa").And.Contain("Środek").And.Contain("Prawa");
    }

    [Test]
    public void RoundTrip_TabbedHeader_SecondPass_IsStable()
    {
        using var stream = BuildDocxWithTabbedHeader();
        var first = _reader.Convert(stream);
        var bytes = _writer.Convert("<p>Treść</p>", header: first.Header);
        var second = _reader.Convert(new MemoryStream(bytes));

        second.Header!.Html.Should().Contain("data-tab-stops=\"4536:center;9072:right\"");
        var segCount = System.Text.RegularExpressions.Regex.Matches(second.Header.Html, "docx-tab-seg").Count;
        segCount.Should().Be(2);
    }

    private static MemoryStream BuildDocxWithBodyParagraph(ParagraphProperties paraProps, Style? paragraphStyle = null)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            if (paragraphStyle != null)
            {
                var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
                stylesPart.Styles = new Styles(paragraphStyle);
                stylesPart.Styles.Save();
            }
            mainPart.Document = new Document(new Body(new Paragraph(
                paraProps,
                new Run(new Text("Lewa")),
                new Run(new TabChar()),
                new Run(new Text("Prawa")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private static Style ParagraphStyleWithCenterTab(int position = 4536) => new(
        new StyleParagraphProperties(new Tabs(
            new TabStop { Val = TabStopValues.Center, Position = position })))
    {
        Type = StyleValues.Paragraph,
        StyleId = "TabbedStyle",
        StyleName = new StyleName { Val = "TabbedStyle" }
    };

    [Test]
    public void Read_TabLeader_IsSerializedInDataTabStops()
    {
        using var ms = BuildDocxWithBodyParagraph(new ParagraphProperties(new Tabs(
            new TabStop { Val = TabStopValues.Right, Position = 9072, Leader = TabStopLeaderCharValues.Dot })));

        var content = _reader.Convert(ms);

        content.Html.Should().Contain("data-tab-stops=\"9072:right:dot\"",
            "leader musi przetrwać round-trip w trzecim segmencie atrybutu");
    }

    [Test]
    public void Read_ClearTab_RemovesStopInheritedFromParagraphStyle()
    {
        using var ms = BuildDocxWithBodyParagraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "TabbedStyle" },
                new Tabs(
                    new TabStop { Val = TabStopValues.Clear, Position = 4536 },
                    new TabStop { Val = TabStopValues.Right, Position = 9000 })),
            ParagraphStyleWithCenterTab());

        var content = _reader.Convert(ms);

        content.Html.Should().Contain("data-tab-stops=\"9000:right\"");
        content.Html.Should().NotContain("4536:center");
    }

    [Test]
    public void Read_DirectTab_OverridesStyleStopAtSamePosition()
    {
        using var ms = BuildDocxWithBodyParagraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "TabbedStyle" },
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = 4536 })),
            ParagraphStyleWithCenterTab());

        var content = _reader.Convert(ms);

        content.Html.Should().Contain("data-tab-stops=\"4536:right\"");
        content.Html.Should().NotContain("4536:center");
    }

    [Test]
    public void Read_BarTab_IsSkipped_RealStopsRemain()
    {
        using var ms = BuildDocxWithBodyParagraph(new ParagraphProperties(new Tabs(
            new TabStop { Val = TabStopValues.Bar, Position = 3000 },
            new TabStop { Val = TabStopValues.Right, Position = 9000 })));

        var content = _reader.Convert(ms);

        content.Html.Should().Contain("data-tab-stops=\"9000:right\"");
        content.Html.Should().NotContain("3000:");
    }

    private static MemoryStream BuildDocxWithSingleRightStopBands()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            var headerPart = mainPart.AddNewPart<HeaderPart>();
            headerPart.Header = new Header(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Right, Position = 9360 })),
                new Run(new Text("Logo")),
                new Run(new TabChar()),
                new Run(new Text("Katowice, data"))));
            headerPart.Header.Save();

            var footerPart = mainPart.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(
                new Paragraph(new Run(new Text("Qutas  — linia bez tabulatora"))),
                new Paragraph(
                    new ParagraphProperties(new Tabs(
                        new TabStop { Val = TabStopValues.Right, Position = 9360 })),
                    new Run(new Text("Miejscowość: Katowice")),
                    new Run(new TabChar()),
                    new Run(new Text("Data"))));
            footerPart.Footer.Save();

            body.Append(new Paragraph(new Run(new Text("Treść"))));
            body.Append(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(headerPart) },
                new FooterReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(footerPart) },
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417, Header = 708, Footer = 708 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void RoundTrip_BandParagraphWithOwnStop_StyleMustNotInjectWordDefaultStops()
    {
        using var stream = BuildDocxWithSingleRightStopBands();
        var first = _reader.Convert(stream);

        var bytes = _writer.Convert("<p>Treść</p>", header: first.Header, footer: first.Footer);
        var second = new DocxToHtmlConverter().Convert(new MemoryStream(bytes));

        second.Footer!.Html.Should().Contain("data-tab-stops=\"9360:right\"",
            "efektywne stopy akapitu stopki to wyłącznie jego własny right 9360");
        second.Footer.Html.Should().NotContain("4536").And.NotContain("9072");
        second.Footer.Html.Should().Contain("data-tab-align=\"right\"",
            "segment po tabulatorze musi zostać na prawym stopie, nie przeskoczyć na center");
        second.Footer.Html.Should().NotContain("data-tab-align=\"center\"");
        second.Header!.Html.Should().Contain("data-tab-stops=\"9360:right\"");
        second.Header.Html.Should().NotContain("4536").And.NotContain("9072");
    }

    [Test]
    public void RoundTrip_BandParagraphs_ThirdPass_NoFlexAndStable()
    {
        using var stream = BuildDocxWithSingleRightStopBands();
        var first = _reader.Convert(stream);
        var bytes2 = _writer.Convert("<p>Treść</p>", header: first.Header, footer: first.Footer);
        var second = new DocxToHtmlConverter().Convert(new MemoryStream(bytes2));
        var bytes3 = new HtmlToDocxConverter().Convert("<p>Treść</p>", header: second.Header, footer: second.Footer);
        var third = new DocxToHtmlConverter().Convert(new MemoryStream(bytes3));

        third.Footer!.Html.Should().NotContain("display:flex");
        third.Header!.Html.Should().NotContain("display:flex");
        third.Footer.Html.Should().Contain("data-tab-stops=\"9360:right\"");
        third.Footer.Html.Should().Contain("data-tab-align=\"right\"");
        third.Footer.Html.Should().NotContain("4536").And.NotContain("9072");
    }

    [Test]
    public void PreservedPackage_OriginalFooterStyleWithWordStops_DoesNotHijackBandTab()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(new Style(
                new StyleName { Val = "footer" },
                new StyleParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Center, Position = 4536 },
                    new TabStop { Val = TabStopValues.Right, Position = 9072 })))
            { Type = StyleValues.Paragraph, StyleId = "Footer" });
            stylesPart.Styles.Save();

            var footerPart = mainPart.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Right, Position = 9360 })),
                new Run(new Text("Miejscowość: Katowice")),
                new Run(new TabChar()),
                new Run(new Text("Data"))));
            footerPart.Footer.Save();

            mainPart.Document.Body!.Append(new Paragraph(new Run(new Text("Treść"))));
            mainPart.Document.Body!.Append(new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(footerPart) },
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417, Header = 708, Footer = 708 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        var original = ms.ToArray();

        var first = _reader.Convert(new MemoryStream(original));
        var saved = _writer.ConvertPreservingPackage(
            "<p>Treść</p>", new MemoryStream(original), footer: first.Footer);
        var second = new DocxToHtmlConverter().Convert(new MemoryStream(saved));

        second.Footer!.Html.Should().Contain("data-tab-stops=\"9360:right\"");
        second.Footer.Html.Should().NotContain("4536:center",
            "stopy wordowego stylu Footer z oryginalnego styles.xml nie mogą dokleić się do akapitu");
        second.Footer.Html.Should().Contain("data-tab-align=\"right\"");
        second.Footer.Html.Should().NotContain("data-tab-align=\"center\"");
    }

    [Test]
    public void Read_ParagraphDeclaringAlignedStops_WithoutTabChar_DoesNotUseFlexLayout()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Center, Position = 4536 },
                    new TabStop { Val = TabStopValues.Right, Position = 9072 })),
                new Run(new Text("Linia bez tabulatora")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var content = _reader.Convert(ms);

        content.Html.Should().NotContain("display:flex");
        content.Html.Should().Contain("data-tab-stops=\"4536:center;9072:right\"",
            "stopy nadal round-tripują — zmienia się tylko renderowanie");
    }

    [Test]
    public void Read_CenteredParagraphWithTab_FlowsInlineInsteadOfAbsoluteSegment()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(
                    new Justification { Val = JustificationValues.Center },
                    new Tabs(new TabStop { Val = TabStopValues.Left, Position = 6013 })),
                new Run(new Text("Umowa w sprawie obsługi wpłat i wypłat gotówkowych w formie zamkniętej")),
                new Run(new TabChar()),
                new Run(new Text("i świadczenia usługi inkasa samochodowego")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var content = _reader.Convert(ms);

        content.Html.Should().NotContain("docx-tab-seg",
            "segment absolutny nie modeluje linii wycentrowanej (nachodzący tekst, brak zawijania)");
        content.Html.Should().Contain("data-tab-stops=\"6013:left\"");
        content.Html.Should().Contain("min-width:2em", "tab renderuje się jako płynący nośnik");
        content.Html.Should().Contain("text-align:center");
    }

    [Test]
    public void Read_LeftAlignedParagraphWithTab_StaysOnPositionedPath()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Left, Position = 6013 })),
                new Run(new Text("Pole:")),
                new Run(new TabChar()),
                new Run(new Text("wartość")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var content = _reader.Convert(ms);

        content.Html.Should().Contain("docx-tab-seg");
        content.Html.Should().Contain("data-tab-stops=\"6013:left\"");
    }

    [Test]
    public void Read_LongTextBeforeTab_FallsBackToInlineCarrier()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new Run(
                    new RunProperties(new FontSize { Val = "32" }),
                    new Text("To jest test długiego tytułu gdzie przechodzę do kolejnej linii")),
                new Run(
                    new RunProperties(new FontSize { Val = "32" }),
                    new TabChar(),
                    new Text("poprzez tab")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;

        html.Should().NotContain("docx-tab-seg",
            "segment absolutny nie łamie wiersza — tekst szerszy niż stop nachodziłby na pierwszą linię");
        html.Should().Contain("min-width:2em", "tab renderuje się jako płynący nośnik");
        html.Should().Contain("poprzez tab");
    }

    [Test]
    public void Read_ModerateTextBeforeFarStop_StaysOnPositionedPath()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(new Tabs(
                    new TabStop { Val = TabStopValues.Left, Position = 6013 })),
                new Run(new Text("Nazwa pola formularza dłuższa")),
                new Run(new TabChar()),
                new Run(new Text("wartość")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;

        html.Should().Contain("docx-tab-seg");
        html.Should().Contain("left:400px");
    }

    [Test]
    public void RoundTrip_CenteredParagraphWithTab_PreservesStopsAndTabChar()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(
                new ParagraphProperties(
                    new Justification { Val = JustificationValues.Center },
                    new Tabs(new TabStop { Val = TabStopValues.Left, Position = 6013 })),
                new Run(new Text("Tytuł część pierwsza")),
                new Run(new TabChar()),
                new Run(new Text("i część druga")))));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html = _reader.Convert(ms).Html;
        var bytes = _writer.Convert(html);

        using var doc2 = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var para = doc2.MainDocumentPart!.Document.Body!.Elements<Paragraph>()
            .First(p => p.InnerText.Contains("Tytuł część pierwsza"));
        var stops = para.ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().ToList();
        stops.Should().ContainSingle(s => s.Position!.Value == 6013 && s.Val!.Value == TabStopValues.Left);
        para.Descendants<TabChar>().Should().HaveCount(1, "znak tabulatora round-tripuje z nośnika 2em");
        para.InnerText.Should().Contain("i część druga");
    }
}
