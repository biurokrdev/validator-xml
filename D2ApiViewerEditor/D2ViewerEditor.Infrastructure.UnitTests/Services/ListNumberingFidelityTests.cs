using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using D2ViewerEditor.Infrastructure.Services;
using FluentAssertions;
using NUnit.Framework;
using V = DocumentFormat.OpenXml.Vml;

namespace D2ViewerEditor.Infrastructure.UnitTests.Services;

[TestFixture]
public class ListNumberingFidelityTests
{
    private DocxToHtmlConverter _reader = null!;
    private HtmlToDocxConverter _writer = null!;

    [SetUp]
    public void Setup()
    {
        _reader = new DocxToHtmlConverter();
        _writer = new HtmlToDocxConverter();
    }


    private static Level Lvl(int index, NumberFormatValues fmt, string lvlText, int start = 1)
    {
        var level = new Level { LevelIndex = index };
        level.Append(new StartNumberingValue { Val = start });
        level.Append(new NumberingFormat { Val = fmt });
        level.Append(new LevelText { Val = lvlText });
        level.Append(new LevelJustification { Val = LevelJustificationValues.Left });
        return level;
    }

    private static AbstractNum Abstract(int id, params Level[] levels)
    {
        var abs = new AbstractNum { AbstractNumberId = id };
        abs.Append(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel });
        foreach (var l in levels) abs.Append(l);
        return abs;
    }

    private static NumberingInstance Num(int numId, int absId, params (int lvl, int startOverride)[] overrides)
    {
        var num = new NumberingInstance { NumberID = numId };
        num.Append(new AbstractNumId { Val = absId });
        foreach (var (lvl, so) in overrides)
        {
            num.Append(new LevelOverride(
                new StartOverrideNumberingValue { Val = so })
            { LevelIndex = lvl });
        }
        return num;
    }

    private static Paragraph ListItem(string text, int numId, int ilvl) =>
        new(
            new ParagraphProperties(
                new NumberingProperties(
                    new NumberingLevelReference { Val = ilvl },
                    new NumberingId { Val = numId })),
            new Run(new Text(text)));

    private static Paragraph Plain(string text) => new(new Run(new Text(text)));

    private static MemoryStream BuildDocx(
        IEnumerable<AbstractNum> abstracts,
        IEnumerable<NumberingInstance> nums,
        IEnumerable<OpenXmlElement> bodyElements)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());

            var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
            numberingPart.Numbering = new Numbering();
            foreach (var a in abstracts) numberingPart.Numbering.Append(a);
            foreach (var n in nums) numberingPart.Numbering.Append(n);
            numberingPart.Numbering.Save();

            var body = mainPart.Document.Body!;
            foreach (var el in bodyElements) body.Append(el);
            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    private static List<string> ListOpenTags(string html) =>
        Regex.Matches(html, "<(ol|ul)[^>]*>").Select(m => m.Value).ToList();


    [Test]
    public void NestedBullets_MainNumberingContinues_AfterNesting()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."), Lvl(1, NumberFormatValues.Bullet, "•"))],
            [Num(1, 1)],
            [
                ListItem("Pierwszy", 1, 0),
                ListItem("Podpunkt", 1, 1),
                ListItem("Kolejny podpunkt", 1, 1),
                ListItem("Drugi", 1, 0),
                ListItem("Trzeci", 1, 0),
            ]);

        var html = _reader.Convert(docx).Html;

        var tags = ListOpenTags(html);
        tags.Count(t => t.StartsWith("<ol")).Should().Be(1);
        tags.Count(t => t.StartsWith("<ul")).Should().Be(1);
        tags.First(t => t.StartsWith("<ol")).Should().NotContain("start=");
        tags.First(t => t.StartsWith("<ol")).Should().Contain("data-num-id=\"1\"").And.Contain("data-ilvl=\"0\"");
        tags.First(t => t.StartsWith("<ul")).Should().Contain("data-ilvl=\"1\"");
        Regex.Matches(html, "<li").Count.Should().Be(5);
    }

    [Test]
    public void ListInterruptedByParagraph_ContinuesNumbering()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."))],
            [Num(1, 1)],
            [
                ListItem("Jeden", 1, 0),
                ListItem("Dwa", 1, 0),
                Plain("Zwykły akapit w środku"),
                ListItem("Trzy", 1, 0),
                ListItem("Cztery", 1, 0),
            ]);

        var html = _reader.Convert(docx).Html;

        var ols = ListOpenTags(html).Where(t => t.StartsWith("<ol")).ToList();
        ols.Should().HaveCount(2);
        ols[0].Should().NotContain("start=");
        ols[1].Should().Contain("start=\"3\"");
        ols[0].Should().Contain("data-num-id=\"1\"");
        ols[1].Should().Contain("data-num-id=\"1\"");
    }

    [Test]
    public void IndependentLists_SameFormat_AreNotMerged()
    {
        using var docx = BuildDocx(
            [
                Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1.")),
                Abstract(2, Lvl(0, NumberFormatValues.Decimal, "%1.")),
            ],
            [Num(1, 1), Num(2, 2)],
            [
                ListItem("A1", 1, 0),
                ListItem("A2", 1, 0),
                ListItem("B1", 2, 0),
                ListItem("B2", 2, 0),
            ]);

        var html = _reader.Convert(docx).Html;

        var ols = ListOpenTags(html).Where(t => t.StartsWith("<ol")).ToList();
        ols.Should().HaveCount(2);
        ols[0].Should().Contain("data-num-id=\"1\"");
        ols[1].Should().Contain("data-num-id=\"2\"");
        ols[1].Should().NotContain("start=");
    }

    [Test]
    public void StartValueOtherThanOne_IsHonored()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1.", start: 5))],
            [Num(1, 1)],
            [ListItem("Piąty", 1, 0), ListItem("Szósty", 1, 0)]);

        var html = _reader.Convert(docx).Html;

        var ol = ListOpenTags(html).First(t => t.StartsWith("<ol"));
        ol.Should().Contain("start=\"5\"");
        ol.Should().Contain("data-start=\"5\"");
    }

    [Test]
    public void SharedAbstract_DifferentNums_ContinueNumbering()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."))],
            [Num(1, 1), Num(2, 1)],
            [
                ListItem("Jeden", 1, 0),
                ListItem("Dwa", 1, 0),
                Plain("Przerwa"),
                ListItem("Trzy", 2, 0),
            ]);

        var html = _reader.Convert(docx).Html;

        var ols = ListOpenTags(html).Where(t => t.StartsWith("<ol")).ToList();
        ols.Should().HaveCount(2);
        ols[1].Should().Contain("start=\"3\"");
    }

    [Test]
    public void StartOverride_RestartsNumbering_DespiteSharedAbstract()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."))],
            [Num(1, 1), Num(2, 1, (0, 1))],
            [
                ListItem("Jeden", 1, 0),
                ListItem("Dwa", 1, 0),
                Plain("Przerwa"),
                ListItem("Restart-jeden", 2, 0),
            ]);

        var html = _reader.Convert(docx).Html;

        var ols = ListOpenTags(html).Where(t => t.StartsWith("<ol")).ToList();
        ols.Should().HaveCount(2);
        ols[1].Should().NotContain("start=");
    }

    [Test]
    public void DeeperLevel_RestartsAfterReturnToMainLevel()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."), Lvl(1, NumberFormatValues.LowerLetter, "%2."))],
            [Num(1, 1)],
            [
                ListItem("Jeden", 1, 0),
                ListItem("a", 1, 1),
                ListItem("b", 1, 1),
                ListItem("Dwa", 1, 0),
                ListItem("a-znowu", 1, 1),
            ]);

        var html = _reader.Convert(docx).Html;

        var nestedOls = ListOpenTags(html)
            .Where(t => t.StartsWith("<ol") && t.Contains("data-ilvl=\"1\""))
            .ToList();
        nestedOls.Should().HaveCount(2);
        nestedOls[1].Should().NotContain("start=");
    }

    [Test]
    public void MixedFormatsPerLevel_EmitCorrectTagsAndDataAttributes()
    {
        using var docx = BuildDocx(
            [Abstract(1,
                Lvl(0, NumberFormatValues.UpperRoman, "%1)"),
                Lvl(1, NumberFormatValues.Bullet, "•"),
                Lvl(2, NumberFormatValues.LowerLetter, "%3."))],
            [Num(1, 1)],
            [
                ListItem("Rzymski", 1, 0),
                ListItem("Punktor", 1, 1),
                ListItem("Litera", 1, 2),
            ]);

        var html = _reader.Convert(docx).Html;

        var tags = ListOpenTags(html);
        var outer = tags.First(t => t.StartsWith("<ol") && t.Contains("data-ilvl=\"0\""));
        outer.Should().Contain("data-num-fmt=\"upperRoman\"");
        outer.Should().Contain("data-lvl-text=\"%1)\"");
        outer.Should().Contain("upper-roman");
        tags.Should().Contain(t => t.StartsWith("<ul") && t.Contains("data-num-fmt=\"bullet\""));
        var innerOl = tags.First(t => t.StartsWith("<ol") && t.Contains("data-ilvl=\"2\""));
        innerOl.Should().Contain("data-num-fmt=\"lowerLetter\"");
        innerOl.Should().Contain("lower-alpha");
    }


    private static (List<(int numId, int ilvl)> items, Numbering numbering) ReadListParagraphs(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var items = doc.MainDocumentPart!.Document.Body!
            .Descendants<Paragraph>()
            .Select(p => p.ParagraphProperties?.NumberingProperties)
            .Where(n => n?.NumberingId?.Val?.Value is > 0)
            .Select(n => (n!.NumberingId!.Val!.Value, n.NumberingLevelReference?.Val?.Value ?? 0))
            .ToList();
        var numbering = (Numbering)doc.MainDocumentPart.NumberingDefinitionsPart!.Numbering.CloneNode(true);
        return (items, numbering);
    }

    [Test]
    public void Writer_SameDataNumId_SharesOneNumberingInstance()
    {
        var html =
            "<ol data-num-id=\"7\" data-ilvl=\"0\" data-num-fmt=\"decimal\"><li>a</li><li>b</li></ol>" +
            "<p>przerwa</p>" +
            "<ol start=\"3\" data-num-id=\"7\" data-ilvl=\"0\" data-num-fmt=\"decimal\"><li>c</li></ol>";

        var (items, numbering) = ReadListParagraphs(_writer.Convert(html));

        items.Should().HaveCount(3);
        items.Select(i => i.numId).Distinct().Should().HaveCount(1,
            "fragmenty tej samej listy logicznej muszą współdzielić numId — Word kontynuuje numerację");
        numbering.Elements<NumberingInstance>().Should().HaveCount(1);
    }

    [Test]
    public void Writer_DistinctDataNumIds_CreateSeparateInstances()
    {
        var html =
            "<ol data-num-id=\"7\" data-num-fmt=\"decimal\"><li>a</li></ol>" +
            "<ol data-num-id=\"8\" data-num-fmt=\"decimal\"><li>b</li></ol>";

        var (items, numbering) = ReadListParagraphs(_writer.Convert(html));

        items.Should().HaveCount(2);
        items.Select(i => i.numId).Distinct().Should().HaveCount(2,
            "niezależne listy nie mogą zostać sklejone w jedną numerację");
        numbering.Elements<NumberingInstance>().Should().HaveCount(2);
    }

    [Test]
    public void Writer_CustomFormat_RoundTripsToAbstractNum()
    {
        var html =
            "<ol data-num-id=\"7\" data-ilvl=\"0\" data-num-fmt=\"upperRoman\" data-lvl-text=\"%1)\" data-start=\"3\">" +
            "<li>rzymski</li></ol>";

        var (_, numbering) = ReadListParagraphs(_writer.Convert(html));

        var lvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
            .First(l => l.LevelIndex?.Value == 0);
        lvl0.NumberingFormat!.Val!.Value.Should().Be(NumberFormatValues.UpperRoman);
        lvl0.LevelText!.Val!.Value.Should().Be("%1)");
        lvl0.StartNumberingValue!.Val!.Value.Should().Be(3);
    }


    [Test]
    public void FullRoundTrip_PreservesListStructureAndContinuation()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."), Lvl(1, NumberFormatValues.Bullet, "•"))],
            [Num(1, 1)],
            [
                ListItem("Jeden", 1, 0),
                ListItem("Podpunkt", 1, 1),
                ListItem("Dwa", 1, 0),
                Plain("Przerwa"),
                ListItem("Trzy", 1, 0),
            ]);

        var html1 = _reader.Convert(docx).Html;
        var regenerated = _writer.Convert(html1);
        using var second = new MemoryStream(regenerated);
        var html2 = _reader.Convert(second).Html;

        var ols = ListOpenTags(html2).Where(t => t.StartsWith("<ol")).ToList();
        ols.Should().HaveCount(2);
        ols[1].Should().Contain("start=\"3\"");
        ListOpenTags(html2).Should().Contain(t => t.StartsWith("<ul"));

        string NumIdOf(string tag) => Regex.Match(tag, "data-num-id=\"(\\d+)\"").Groups[1].Value;
        NumIdOf(ols[0]).Should().NotBeEmpty();
        NumIdOf(ols[0]).Should().Be(NumIdOf(ols[1]),
            "po zapisie i ponownym otwarciu fragmenty muszą nadal należeć do jednej listy");

        ols[0].Should().Contain("data-num-fmt=\"decimal\"");
    }


    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static void AssertNoValidationErrors(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(doc).ToList();
        errors.Should().BeEmpty(
            "wygenerowany pakiet nie może zawierać błędów walidacji OOXML wprowadzonych przez aplikację; " +
            "błędy: " + string.Join("; ", errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
    }

    [Test]
    public void Reader_StartOverride_EmitsSeparateDataAttribute()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."))],
            [Num(1, 1, (0, 5))],
            [ListItem("Piąty", 1, 0), ListItem("Szósty", 1, 0)]);

        var html = _reader.Convert(docx).Html;

        var ol = ListOpenTags(html).First(t => t.StartsWith("<ol"));
        ol.Should().Contain("data-start-override=\"5\"");
        ol.Should().NotContain("data-start=\"5\"", "definicja poziomu ma w:start=1, nie 5");
        ol.Should().Contain("start=\"5\"", "prezentacja: pierwszy element pokazuje 5");
    }

    [Test]
    public void Writer_StartOverride_EmitsLvlOverrideOnSharedAbstract()
    {
        var html =
            "<ol data-num-id=\"1\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\"><li>a</li><li>b</li></ol>" +
            "<p>przerwa</p>" +
            "<ol data-num-id=\"2\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\" data-start-override=\"1\"><li>restart</li></ol>";

        var docx = _writer.Convert(html);
        var (items, numbering) = ReadListParagraphs(docx);

        numbering.Elements<AbstractNum>().Should().HaveCount(1,
            "listy o tym samym data-abstract-num-id współdzielą jedną definicję");
        var nums = numbering.Elements<NumberingInstance>().ToList();
        nums.Should().HaveCount(2);
        items.Select(i => i.numId).Distinct().Should().HaveCount(2);

        var restarted = nums.Single(n => n.Elements<LevelOverride>().Any());
        var levelOverride = restarted.Elements<LevelOverride>().Single();
        levelOverride.LevelIndex!.Value.Should().Be(0);
        levelOverride.GetFirstChild<StartOverrideNumberingValue>()!.Val!.Value.Should().Be(1);

        AssertNoValidationErrors(docx);
    }

    [Test]
    public void FullRoundTrip_RestartedList_KeepsSharedAbstractAndOverride()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."))],
            [Num(1, 1), Num(2, 1, (0, 1))],
            [
                ListItem("Jeden", 1, 0),
                ListItem("Dwa", 1, 0),
                Plain("Przerwa"),
                ListItem("Restart-jeden", 2, 0),
            ]);

        var html1 = _reader.Convert(docx).Html;
        var regenerated = _writer.Convert(html1);
        var (_, numbering) = ReadListParagraphs(regenerated);

        numbering.Elements<AbstractNum>().Should().HaveCount(1);
        numbering.Elements<NumberingInstance>().Should().HaveCount(2);
        numbering.Descendants<StartOverrideNumberingValue>().Single().Val!.Value.Should().Be(1);

        using var second = new MemoryStream(regenerated);
        var html2 = _reader.Convert(second).Html;
        var ols = ListOpenTags(html2).Where(t => t.StartsWith("<ol")).ToList();
        ols.Should().HaveCount(2);
        ols[1].Should().NotContain("start=\"", "restart musi zaczynać od 1, nie kontynuować od 3");
        ols[1].Should().Contain("data-start-override=\"1\"");
    }

    [Test]
    public void SuffixIsLegalLvlRestartAndIndent_RoundTripToAbstractNum()
    {
        var level = new Level { LevelIndex = 0 };
        level.Append(new StartNumberingValue { Val = 1 });
        level.Append(new NumberingFormat { Val = NumberFormatValues.UpperRoman });
        level.Append(new LevelRestart { Val = 0 });
        level.Append(new IsLegalNumberingStyle());
        level.Append(new LevelSuffix { Val = LevelSuffixValues.Space });
        level.Append(new LevelText { Val = "%1." });
        level.Append(new LevelJustification { Val = LevelJustificationValues.Left });
        level.Append(new PreviousParagraphProperties(
            new Indentation { Left = "1134", Hanging = "397" }));

        using var docx = BuildDocx(
            [Abstract(1, level)],
            [Num(1, 1)],
            [ListItem("Jeden", 1, 0)]);

        var html = _reader.Convert(docx).Html;
        var ol = ListOpenTags(html).First(t => t.StartsWith("<ol"));
        ol.Should().Contain("data-suffix=\"space\"");
        ol.Should().Contain("data-is-legal=\"1\"");
        ol.Should().Contain("data-lvl-restart=\"0\"");
        ol.Should().Contain("data-ind-left-tw=\"1134\"");
        ol.Should().Contain("data-ind-hanging-tw=\"397\"");

        var regenerated = _writer.Convert(html);
        var (_, numbering) = ReadListParagraphs(regenerated);
        var lvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
            .First(l => l.LevelIndex?.Value == 0);
        lvl0.LevelRestart!.Val!.Value.Should().Be(0);
        lvl0.IsLegalNumberingStyle.Should().NotBeNull();
        lvl0.LevelSuffix!.Val!.Value.Should().Be(LevelSuffixValues.Space);
        var ind = lvl0.PreviousParagraphProperties!.GetFirstChild<Indentation>()!;
        ind.Left!.Value.Should().Be("1134");
        ind.Hanging!.Value.Should().Be("397");

        AssertNoValidationErrors(regenerated);
    }

    [Test]
    public void PictureBullet_FullRoundTrip_RecreatesNumPicBullet()
    {
        var pngBytes = System.Convert.FromBase64String(OnePixelPngBase64);
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());

            var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
            var imagePart = numberingPart.AddImagePart(ImagePartType.Png);
            using (var img = new MemoryStream(pngBytes)) imagePart.FeedData(img);
            var relId = numberingPart.GetIdOfPart(imagePart);

            var level = new Level { LevelIndex = 0 };
            level.Append(new StartNumberingValue { Val = 1 });
            level.Append(new NumberingFormat { Val = NumberFormatValues.Bullet });
            level.Append(new LevelText { Val = "" });
            level.Append(new LevelPictureBulletId { Val = 3 });
            level.Append(new LevelJustification { Val = LevelJustificationValues.Left });

            numberingPart.Numbering = new Numbering(
                new NumberingPictureBullet(
                    new PictureBulletBase(
                        new V.Shape(new V.ImageData { RelationshipId = relId })
                        { Style = "width:12pt;height:12pt" }))
                { NumberingPictureBulletId = 3 },
                Abstract(1, level),
                Num(1, 1));
            numberingPart.Numbering.Save();

            var body = mainPart.Document.Body!;
            body.Append(ListItem("Obrazkowy punkt", 1, 0));
            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1417, Bottom = 1417, Left = 1417, Right = 1417 }));
            mainPart.Document.Save();
        }
        ms.Position = 0;

        var html1 = _reader.Convert(ms).Html;
        html1.Should().Contain("list-marker");
        html1.Should().Contain($"data:image/png;base64,{OnePixelPngBase64}");
        ListOpenTags(html1).First(t => t.StartsWith("<ul")).Should().Contain("data-pic-bullet=\"1\"");

        var regenerated = _writer.Convert(html1);
        using (var check = new MemoryStream(regenerated))
        using (var doc = WordprocessingDocument.Open(check, false))
        {
            var numberingPart = doc.MainDocumentPart!.NumberingDefinitionsPart!;
            var numbering = numberingPart.Numbering;

            var picBullet = numbering.Elements<NumberingPictureBullet>().Single();
            var picBulletId = picBullet.NumberingPictureBulletId!.Value;

            var lvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
                .First(l => l.LevelIndex?.Value == 0);
            lvl0.LevelPictureBulletId!.Val!.Value.Should().Be(picBulletId);
            lvl0.NumberingFormat!.Val!.Value.Should().Be(NumberFormatValues.Bullet);

            var exportedImage = numberingPart.ImageParts.Single();
            using var stream = exportedImage.GetStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.ToArray().Should().Equal(pngBytes);

            var listPara = doc.MainDocumentPart.Document.Body!.Descendants<Paragraph>()
                .Single(p => p.ParagraphProperties?.NumberingProperties != null);
            listPara.Descendants<Drawing>().Should().BeEmpty();
        }

        using var second = new MemoryStream(regenerated);
        var html2 = _reader.Convert(second).Html;
        html2.Should().Contain($"data:image/png;base64,{OnePixelPngBase64}");
        ListOpenTags(html2).First(t => t.StartsWith("<ul")).Should().Contain("data-pic-bullet=\"1\"");
    }

    [Test]
    public void FullRoundTrip_FragmentStartingAtDeeperLevel_KeepsIlvl()
    {
        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."), Lvl(1, NumberFormatValues.LowerLetter, "%2)"))],
            [Num(1, 1)],
            [
                ListItem("Jeden", 1, 0),
                ListItem("a", 1, 1),
                Plain("Przerwa"),
                ListItem("b — nadal poziom 1", 1, 1),
            ]);

        var html1 = _reader.Convert(docx).Html;
        ListOpenTags(html1).Last().Should().Contain("data-ilvl=\"1\"");

        var (items, numbering) = ReadListParagraphs(_writer.Convert(html1));

        items.Should().HaveCount(3);
        items.Select(i => i.ilvl).Should().Equal(new[] { 0, 1, 1 },
            "poziom fragmentu kontynuacji musi wrócić jako ilvl=1, nie 0");
        items.Select(i => i.numId).Distinct().Should().HaveCount(1);

        var lvl1 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
            .First(l => l.LevelIndex?.Value == 1);
        lvl1.NumberingFormat!.Val!.Value.Should().Be(NumberFormatValues.LowerLetter);
        lvl1.LevelText!.Val!.Value.Should().Be("%2)");
    }

    [Test]
    public void Writer_SharedAbstract_LaterFragmentUpgradesUnusedLevels()
    {
        var html =
            "<ol data-num-id=\"1\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\"><li>a</li></ol>" +
            "<ol data-num-id=\"2\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\" data-start-override=\"1\">" +
            "<li>b" +
            "<ol data-num-id=\"2\" data-abstract-num-id=\"1\" data-ilvl=\"1\" data-num-fmt=\"upperLetter\" data-lvl-text=\"%2)\">" +
            "<li>zagnieżdżony</li></ol></li></ol>";

        var docx = _writer.Convert(html);
        var (_, numbering) = ReadListParagraphs(docx);

        numbering.Elements<AbstractNum>().Should().HaveCount(1);
        var lvl1 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
            .First(l => l.LevelIndex?.Value == 1);
        lvl1.NumberingFormat!.Val!.Value.Should().Be(NumberFormatValues.UpperLetter);
        lvl1.LevelText!.Val!.Value.Should().Be("%2)");

        AssertNoValidationErrors(docx);
    }

    [Test]
    public void FullRoundTrip_InstanceLevelOverride_StaysOnInstanceNotAbstract()
    {
        var fullOverride = new NumberingInstance { NumberID = 2 };
        fullOverride.Append(new AbstractNumId { Val = 1 });
        fullOverride.Append(new LevelOverride(
            Lvl(0, NumberFormatValues.UpperRoman, "%1)"))
        { LevelIndex = 0 });

        using var docx = BuildDocx(
            [Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1."))],
            [Num(1, 1), fullOverride],
            [
                ListItem("Decimal", 1, 0),
                Plain("Przerwa"),
                ListItem("Rzymski przez lvlOverride", 2, 0),
            ]);

        var html1 = _reader.Convert(docx).Html;
        var ols1 = ListOpenTags(html1).Where(t => t.StartsWith("<ol")).ToList();
        ols1[0].Should().Contain("data-num-fmt=\"decimal\"").And.NotContain("data-lvl-override");
        ols1[1].Should().Contain("data-num-fmt=\"upperRoman\"").And.Contain("data-lvl-override=\"1\"");

        var regenerated = _writer.Convert(html1);
        var (_, numbering) = ReadListParagraphs(regenerated);

        numbering.Elements<AbstractNum>().Should().HaveCount(1);
        var absLvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
            .First(l => l.LevelIndex?.Value == 0);
        absLvl0.NumberingFormat!.Val!.Value.Should().Be(NumberFormatValues.Decimal);

        var overridden = numbering.Elements<NumberingInstance>()
            .Single(n => n.Elements<LevelOverride>().Any(lo => lo.GetFirstChild<Level>() != null));
        var overrideLvl = overridden.Elements<LevelOverride>().Single().GetFirstChild<Level>()!;
        overrideLvl.NumberingFormat!.Val!.Value.Should().Be(NumberFormatValues.UpperRoman);
        overrideLvl.LevelText!.Val!.Value.Should().Be("%1)");

        AssertNoValidationErrors(regenerated);

        using var second = new MemoryStream(regenerated);
        var html2 = _reader.Convert(second).Html;
        var ols2 = ListOpenTags(html2).Where(t => t.StartsWith("<ol")).ToList();
        ols2[0].Should().Contain("data-num-fmt=\"decimal\"");
        ols2[1].Should().Contain("data-num-fmt=\"upperRoman\"").And.Contain("data-lvl-override=\"1\"");
    }


    private static readonly (NumberFormatValues Fmt, string Tpl)[][] MultilevelSchemes =
    [
        [(NumberFormatValues.Decimal, "%1."), (NumberFormatValues.Decimal, "%1.%2."), (NumberFormatValues.Decimal, "%1.%2.%3.")],
        [(NumberFormatValues.Decimal, "%1)"), (NumberFormatValues.Decimal, "%1.%2)"), (NumberFormatValues.Decimal, "%1.%2.%3)")],
        [(NumberFormatValues.UpperLetter, "%1."), (NumberFormatValues.Decimal, "%1.%2."), (NumberFormatValues.LowerLetter, "%1.%2.%3.")],
        [(NumberFormatValues.UpperRoman, "%1."), (NumberFormatValues.UpperLetter, "%1.%2."), (NumberFormatValues.Decimal, "%1.%2.%3.")],
        [(NumberFormatValues.Decimal, "§ %1"), (NumberFormatValues.Decimal, "§ %1.%2"), (NumberFormatValues.Decimal, "§ %1.%2.%3")],
    ];

    [Test]
    public void MultilevelSchemes_FormatsAndTemplates_SurviveRoundTrip()
    {
        foreach (var scheme in MultilevelSchemes)
        {
            var reader = new DocxToHtmlConverter();
            var writer = new HtmlToDocxConverter();
            using var docx = BuildDocx(
                [Abstract(1,
                    Lvl(0, scheme[0].Fmt, scheme[0].Tpl),
                    Lvl(1, scheme[1].Fmt, scheme[1].Tpl),
                    Lvl(2, scheme[2].Fmt, scheme[2].Tpl))],
                [Num(1, 1)],
                [
                    ListItem("poziom 0", 1, 0),
                    ListItem("poziom 1", 1, 1),
                    ListItem("poziom 2", 1, 2),
                ]);

            var html = reader.Convert(docx).Html;
            var (items, numbering) = ReadListParagraphs(writer.Convert(html));

            items.Select(i => i.ilvl).Should().Equal(new[] { 0, 1, 2 },
                $"poziomy schematu {scheme[2].Tpl} muszą przetrwać round-trip");
            var levels = numbering.Elements<AbstractNum>().Single().Elements<Level>()
                .Where(l => l.LevelIndex?.Value is >= 0 and <= 2)
                .OrderBy(l => l.LevelIndex!.Value)
                .ToList();
            for (var lvl = 0; lvl < 3; lvl++)
            {
                levels[lvl].NumberingFormat!.Val!.Value.Should().Be(scheme[lvl].Fmt,
                    $"format poziomu {lvl} schematu {scheme[2].Tpl}");
                levels[lvl].LevelText!.Val!.Value.Should().Be(scheme[lvl].Tpl,
                    $"szablon poziomu {lvl} schematu {scheme[2].Tpl}");
            }
        }
    }

    private static readonly string[] UnicodeBullets =
    [
        "•", "‣", "⁃", "◦", "∙", "·", "●", "○", "◉", "◎", "◐", "⦿", "⦾",
        "■", "□", "▪", "▫", "▣", "◼", "◻", "⬛", "⬜",
        "▶", "▷", "►", "➤", "➢", "➜", "→", "⇒", "➔", "⟶", "❯", "›", "←", "↑", "↓", "▲", "▼",
        "-", "‐", "‒", "–", "—", "―", "−", "⸺",
        "*", "⁎", "∗", "✱", "✳", "✴", "★", "☆", "⭑",
        "◆", "◇", "◈", "♦", "♢", "⬥", "⋄",
        "✓", "✔", "☑", "☐", "☒", "✗", "✘", "×", "⊗", "⊕", "⊙",
    ];

    [Test]
    public void UnicodeBulletCatalog_LvlTextSurvivesRoundTrip()
    {
        foreach (var bullet in UnicodeBullets)
        {
            var reader = new DocxToHtmlConverter();
            var writer = new HtmlToDocxConverter();
            using var docx = BuildDocx(
                [Abstract(1, Lvl(0, NumberFormatValues.Bullet, bullet))],
                [Num(1, 1)],
                [ListItem("punkt", 1, 0)]);

            var html = reader.Convert(docx).Html;
            var (_, numbering) = ReadListParagraphs(writer.Convert(html));

            var lvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
                .First(l => l.LevelIndex?.Value == 0);
            lvl0.NumberingFormat!.Val!.Value.Should().Be(NumberFormatValues.Bullet,
                $"punktator '{bullet}' (U+{char.ConvertToUtf32(bullet, 0):X4})");
            lvl0.LevelText!.Val!.Value.Should().Be(bullet,
                $"znak punktatora '{bullet}' (U+{char.ConvertToUtf32(bullet, 0):X4}) nie może się zdegradować");
        }
    }

    [Test]
    public void TextMarkerBullets_RenderFullTextAndRoundTrip()
    {
        foreach (var marker in new[] { "TODO:", "UWAGA:", "Pkt", "Krok", "(1)", "[1]", "§ 1", "o czym mowa" })
        {
            var reader = new DocxToHtmlConverter();
            var writer = new HtmlToDocxConverter();
            using var docx = BuildDocx(
                [Abstract(1, Lvl(0, NumberFormatValues.Bullet, marker))],
                [Num(1, 1)],
                [ListItem("treść", 1, 0)]);

            var html = reader.Convert(docx).Html;
            var markerSpan = Regex.Match(html, "<span class=\"list-marker\"[^>]*>([^<]*)</span>");
            markerSpan.Success.Should().BeTrue($"marker '{marker}' musi mieć własny span (nie natywną kropkę)");
            System.Net.WebUtility.HtmlDecode(markerSpan.Groups[1].Value).Should().Be(marker,
                "podgląd pokazuje CAŁY tekst markera, nie pierwszy znak");

            var (_, numbering) = ReadListParagraphs(writer.Convert(html));
            var lvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
                .First(l => l.LevelIndex?.Value == 0);
            lvl0.LevelText!.Val!.Value.Should().Be(marker);
        }
    }

    [Test]
    public void Writer_EditorHtmlAfterEnterAndListExit_KeepsOneLogicalList()
    {
        const string editorHtml =
            "<ol data-num-id=\"1\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\" data-lvl-text=\"%1.\">" +
            "<li>Alfa</li><li>Nowy po Enterze</li></ol>" +
            "<div>Akapit po wyjściu z listy</div>" +
            "<ol data-num-id=\"1\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\" data-lvl-text=\"%1.\">" +
            "<li>Beta</li></ol>" +
            "<p>Przerwa</p>" +
            "<ol start=\"3\" data-num-id=\"1\" data-abstract-num-id=\"1\" data-ilvl=\"0\" data-num-fmt=\"decimal\" data-lvl-text=\"%1.\">" +
            "<li>Gamma (dalszy fragment)</li></ol>";

        var docx = _writer.Convert(editorHtml);
        var (items, numbering) = ReadListParagraphs(docx);

        items.Should().HaveCount(4);
        items.Select(i => i.numId).Distinct().Should().HaveCount(1,
            "wszystkie fragmenty (w tym rozdzielone Enterem w edytorze) = jedna instancja numeracji");
        numbering.Elements<AbstractNum>().Should().HaveCount(1);
        numbering.Elements<NumberingInstance>().Should().HaveCount(1);

        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var paragraphTexts = doc.MainDocumentPart!.Document.Body!
            .Elements<Paragraph>()
            .Select(p => p.InnerText)
            .Where(t => t.Length > 0)
            .ToList();
        paragraphTexts.Should().ContainInOrder(
            "Alfa", "Nowy po Enterze", "Akapit po wyjściu z listy", "Beta", "Przerwa", "Gamma (dalszy fragment)");
        var interruption = doc.MainDocumentPart.Document.Body.Elements<Paragraph>()
            .Single(p => p.InnerText == "Akapit po wyjściu z listy");
        interruption.ParagraphProperties?.NumberingProperties.Should().BeNull(
            "akapit po wyjściu z listy nie może być elementem listy");

        AssertNoValidationErrors(docx);

        using var second = new MemoryStream(docx);
        var html2 = _reader.Convert(second).Html;
        var ols = ListOpenTags(html2).Where(t => t.StartsWith("<ol")).ToList();
        ols.Should().HaveCount(3);
        ols[1].Should().Contain("start=\"3\"");
        ols[2].Should().Contain("start=\"4\"");
    }

    [Test]
    public void ListsInTableCells_GroupIntoOlAndSurviveRoundTrip()
    {
        var cellA = new TableCell(
            ListItem("Jeden", 1, 0),
            ListItem("Dwa", 1, 0));
        var cellB = new TableCell(
            new Paragraph(new Run(new Text("zwykły akapit"))),
            ListItem("Trzy — kontynuacja w drugiej komórce", 1, 0),
            ListItem("Punkt", 2, 0));
        var table = new Table(
            new TableGrid(new GridColumn(), new GridColumn()),
            new TableRow(cellA, cellB));

        using var docx = BuildDocx(
            [
                Abstract(1, Lvl(0, NumberFormatValues.Decimal, "%1.")),
                Abstract(2, Lvl(0, NumberFormatValues.Bullet, "•")),
            ],
            [Num(1, 1), Num(2, 2)],
            [table, Plain("po tabeli")]);

        var html1 = _reader.Convert(docx).Html;

        var tdOls = Regex.Matches(html1, "<td[^>]*>.*?</td>", RegexOptions.Singleline)
            .SelectMany(td => Regex.Matches(td.Value, "<(ol|ul)[^>]*>").Select(m => m.Value))
            .ToList();
        tdOls.Should().HaveCount(3, "dwa fragmenty listy numerowanej + lista punktowana");
        tdOls.Count(t => t.Contains("data-num-id=\"1\"")).Should().Be(2);
        tdOls.First(t => t.Contains("data-num-id=\"1\"") && t.Contains("start="))
            .Should().Contain("start=\"3\"");

        var regenerated = _writer.Convert(html1);
        using (var ms = new MemoryStream(regenerated))
        using (var doc = WordprocessingDocument.Open(ms, false))
        {
            var cellListParas = doc.MainDocumentPart!.Document.Body!
                .Descendants<TableCell>()
                .SelectMany(c => c.Descendants<Paragraph>())
                .Where(p => p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value > 0)
                .ToList();
            cellListParas.Should().HaveCount(4, "3 elementy numerowane + 1 punktowany w komórkach");
            cellListParas
                .Select(p => p.ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value)
                .Distinct().Should().HaveCount(2, "lista numerowana (jedna instancja) + punktowana");
        }
        AssertNoValidationErrors(regenerated);

        using var second = new MemoryStream(regenerated);
        var html2 = _reader.Convert(second).Html;
        Regex.Matches(html2, "<td[^>]*>.*?</td>", RegexOptions.Singleline)
            .SelectMany(td => Regex.Matches(td.Value, "<(ol|ul)[^>]*>").Select(m => m.Value))
            .Should().HaveCount(3);
    }

    [Test]
    public void UnknownNumFmt_RoundTripsRawToken_InsteadOfDecimalDegradation()
    {
        foreach (var fmt in new[]
                 {
                     NumberFormatValues.Ordinal,
                     NumberFormatValues.CardinalText,
                     NumberFormatValues.OrdinalText,
                     NumberFormatValues.Chicago,
                 })
        {
            var reader = new DocxToHtmlConverter();
            var writer = new HtmlToDocxConverter();
            using var docx = BuildDocx(
                [Abstract(1, Lvl(0, fmt, "%1."))],
                [Num(1, 1)],
                [ListItem("Jeden", 1, 0), ListItem("Dwa", 1, 0)]);

            var html = reader.Convert(docx).Html;
            var regenerated = writer.Convert(html);
            var (_, numbering) = ReadListParagraphs(regenerated);

            var lvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
                .First(l => l.LevelIndex?.Value == 0);
            lvl0.NumberingFormat!.Val!.Value.Should().Be(fmt,
                $"format {fmt} nie może zdegradować się do decimal przy zapisie");
            AssertNoValidationErrors(regenerated);
        }
    }

    [Test]
    public void Writer_ListsWithoutDataAttributes_StillProduceValidPackage()
    {
        var html = "<ol><li>a</li><li>b<ul><li>c</li></ul></li></ol>";

        var docx = _writer.Convert(html);
        var (items, numbering) = ReadListParagraphs(docx);

        items.Should().HaveCount(3);
        numbering.Elements<AbstractNum>().Should().HaveCount(1);
        AssertNoValidationErrors(docx);
    }


    private static Level LvlWithMarkerColor(int index, string colorHex)
    {
        var level = new Level { LevelIndex = index };
        level.Append(new StartNumberingValue { Val = 1 });
        level.Append(new NumberingFormat { Val = NumberFormatValues.Decimal });
        level.Append(new LevelText { Val = $"%{index + 1}." });
        level.Append(new LevelJustification { Val = LevelJustificationValues.Left });
        level.Append(new NumberingSymbolRunProperties(
            new RunFonts { Hint = FontTypeHintValues.Default },
            new Color { Val = colorHex }));
        return level;
    }

    [Test]
    public void Reader_MarkerColorFromLevelRunProperties_IsEmittedOnContainer()
    {
        using var docx = BuildDocx(
            [Abstract(1, LvlWithMarkerColor(0, "ED7D31"))],
            [Num(1, 1)],
            [ListItem("Pomarańczowy numer", 1, 0)]);

        var html = _reader.Convert(docx).Html;

        var olTag = ListOpenTags(html).First(t => t.StartsWith("<ol"));
        olTag.Should().Contain("data-marker-color=\"ED7D31\"",
            "surowy kolor z w:lvl/w:rPr musi round-tripować");
        olTag.Should().Contain("--marker-color:#ED7D31",
            "CSS var konsumują etykiety ::before i span.list-marker");
    }

    [Test]
    public void RoundTrip_MarkerColor_SurvivesInLevelRunProperties()
    {
        using var docx = BuildDocx(
            [Abstract(1, LvlWithMarkerColor(0, "ED7D31"))],
            [Num(1, 1)],
            [ListItem("Jeden", 1, 0), ListItem("Dwa", 1, 0)]);

        var regenerated = _writer.Convert(_reader.Convert(docx).Html);
        var (_, numbering) = ReadListParagraphs(regenerated);

        var lvl0 = numbering.Elements<AbstractNum>().Single().Elements<Level>()
            .First(l => l.LevelIndex?.Value == 0);
        var markerColor = lvl0.NumberingSymbolRunProperties?.GetFirstChild<Color>();
        markerColor.Should().NotBeNull("kolor znacznika nie może ginąć przy zapisie");
        markerColor!.Val!.Value.Should().Be("ED7D31");
        AssertNoValidationErrors(regenerated);
    }


    private static Paragraph ColoredMarkItem(string text, int numId, int ilvl, string markHex, string? runHex = null)
    {
        var run = new Run(new Text(text));
        if (runHex != null)
            run.PrependChild(new RunProperties(new Color { Val = runHex }));
        return new Paragraph(
            new ParagraphProperties(
                new NumberingProperties(
                    new NumberingLevelReference { Val = ilvl },
                    new NumberingId { Val = numId }),
                new ParagraphMarkRunProperties(new Color { Val = markHex })),
            run);
    }

    [Test]
    public void Reader_MarkerColorFromParagraphMark_IsEmittedPerItem()
    {
        using var docx = BuildDocx(
            [Abstract(1)],
            [Num(1, 1)],
            [ColoredMarkItem("Czerwony", 1, 0, "FF0000", "FF0000"),
             ColoredMarkItem("Niebieski", 1, 0, "0000FF", "0000FF")]);

        var html = _reader.Convert(docx).Html;

        html.Should().Contain("data-mark-color=\"FF0000\"");
        html.Should().Contain("data-mark-color=\"0000FF\"");
        html.Should().Contain("--marker-color:#FF0000", "CSS var na <li> nadpisuje wariant z kontenera");
        html.Should().Contain("--marker-color:#0000FF");
    }

    [Test]
    public void Reader_MarkerColorFromLevel_WinsOverParagraphMark()
    {
        using var docx = BuildDocx(
            [Abstract(1, LvlWithMarkerColor(0, "ED7D31"))],
            [Num(1, 1)],
            [ColoredMarkItem("Pozycja", 1, 0, "FF0000")]);

        var html = _reader.Convert(docx).Html;

        html.Should().Contain("--marker-color:#ED7D31");
        html.Should().NotContain("data-mark-color=");
    }

    [Test]
    public void RoundTrip_ParagraphMarkColor_SurvivesInParagraphMarkRunProperties()
    {
        using var docx = BuildDocx(
            [Abstract(1)],
            [Num(1, 1)],
            [ColoredMarkItem("Czerwony", 1, 0, "FF0000")]);

        var regenerated = _writer.Convert(_reader.Convert(docx).Html);

        using var ms = new MemoryStream(regenerated);
        using var doc = WordprocessingDocument.Open(ms, false);
        var listPara = doc.MainDocumentPart!.Document.Body!
            .Descendants<Paragraph>()
            .First(p => p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value is > 0);
        var markColor = listPara.ParagraphProperties
            ?.GetFirstChild<ParagraphMarkRunProperties>()
            ?.GetFirstChild<Color>();
        markColor.Should().NotBeNull("kolor znacznika z ¶-mark nie może ginąć przy zapisie");
        markColor!.Val!.Value.Should().Be("FF0000");
        AssertNoValidationErrors(regenerated);
    }
}
