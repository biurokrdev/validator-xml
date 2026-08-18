using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace D2ViewerEditor.Infrastructure.UnitTests.Fixtures;

internal static class EndnoteTestDocuments
{
    internal const long SeparatorId = -1;
    internal const long ContinuationSeparatorId = 0;

    internal static byte[] SingleEndnote()
    {
        return Build(endnotesRoot =>
        {
            AppendTechnicalSeparators(endnotesRoot);
            endnotesRoot.Append(UserEndnote(1,
                new Paragraph(AutoNumberMarkRun(), TextRun("Jedyny przypis końcowy."))));
        },
        body =>
        {
            body.Append(new Paragraph(
                TextRun("Zdanie przed odwołaniem"),
                EndnoteReferenceRun(1),
                TextRun(" i dalej.")));
        });
    }

    internal static byte[] TwoEndnotes()
    {
        return Build(endnotesRoot =>
        {
            AppendTechnicalSeparators(endnotesRoot);

            endnotesRoot.Append(UserEndnote(1,
                new Paragraph(
                    AutoNumberMarkRun(),
                    TextRun("Pierwszy przypis końcowy: zażółć gęślą jaźń — €, ©, →."))));

            endnotesRoot.Append(UserEndnote(2,
                new Paragraph(
                    AutoNumberMarkRun(),
                    TextRun("Drugi przypis końcowy, "),
                    BoldRun("pogrubiony"),
                    TextRun(" oraz "),
                    ItalicRun("kursywa"),
                    TextRun(".")),
                new Paragraph(
                    TextRun("Drugi akapit tego samego przypisu końcowego."))));
        },
        body =>
        {
            body.Append(new Paragraph(
                TextRun("Zdanie przed odwołaniem"),
                EndnoteReferenceRun(1),
                TextRun(" i tekst po odwołaniu.")));

            body.Append(new Paragraph(
                TextRun("Drugi akapit z kolejnym odwołaniem"),
                EndnoteReferenceRun(2),
                TextRun(".")));
        });
    }

    internal static byte[] SharedEndnoteReferencedTwice()
    {
        return Build(endnotesRoot =>
        {
            AppendTechnicalSeparators(endnotesRoot);
            endnotesRoot.Append(UserEndnote(1,
                new Paragraph(AutoNumberMarkRun(), TextRun("Przypis końcowy wskazywany dwukrotnie."))));
        },
        body =>
        {
            body.Append(new Paragraph(TextRun("Pierwsze odwołanie"), EndnoteReferenceRun(1), TextRun(".")));
            body.Append(new Paragraph(TextRun("Drugie odwołanie"), EndnoteReferenceRun(1), TextRun(".")));
        });
    }

    internal static byte[] OrphanReference()
    {
        return Build(endnotesRoot =>
        {
            AppendTechnicalSeparators(endnotesRoot);
        },
        body =>
        {
            body.Append(new Paragraph(TextRun("Odwołanie do brakującego przypisu końcowego"), EndnoteReferenceRun(5), TextRun(".")));
        });
    }

    internal static byte[] NoEndnotes()
    {
        using var ms = new MemoryStream();
        using (var document = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(TextRun("Dokument bez przypisów końcowych.")),
                new Paragraph(TextRun("Drugi zwykły akapit."))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    internal static byte[] FootnoteAndEndnote()
    {
        using var ms = new MemoryStream();
        using (var document = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = new Body();
            main.Document.Body = body;

            body.Append(new Paragraph(
                TextRun("Tekst z przypisem dolnym"),
                FootnoteReferenceRun(1),
                TextRun(" oraz końcowym"),
                EndnoteReferenceRun(1),
                TextRun(".")));

            var footnotesPart = main.AddNewPart<FootnotesPart>();
            var footnotesRoot = new Footnotes();
            footnotesRoot.Append(SeparatorFootnote());
            footnotesRoot.Append(ContinuationSeparatorFootnote());
            footnotesRoot.Append(new Footnote(new Paragraph(AutoNumberMarkRun(), TextRun("Treść przypisu DOLNEGO."))) { Id = 1 });
            footnotesPart.Footnotes = footnotesRoot;
            footnotesPart.Footnotes.Save();

            var endnotesPart = main.AddNewPart<EndnotesPart>();
            var endnotesRoot = new Endnotes();
            AppendTechnicalSeparators(endnotesRoot);
            endnotesRoot.Append(UserEndnote(1, new Paragraph(AutoNumberMarkRun(), TextRun("Treść przypisu KOŃCOWEGO."))));
            endnotesPart.Endnotes = endnotesRoot;
            endnotesPart.Endnotes.Save();

            main.Document.Save();
        }
        return ms.ToArray();
    }


    internal static byte[] EndnoteWithNumberFormat(NumberFormatValues numFmt)
    {
        using var ms = new MemoryStream();
        using (var document = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = new Body();
            main.Document.Body = body;
            body.Append(new Paragraph(TextRun("Zdanie"), EndnoteReferenceRun(1), TextRun(".")));

            var endnotesPart = main.AddNewPart<EndnotesPart>();
            var endnotesRoot = new Endnotes();
            AppendTechnicalSeparators(endnotesRoot);
            endnotesRoot.Append(UserEndnote(1, new Paragraph(AutoNumberMarkRun(), TextRun("Przypis końcowy."))));
            endnotesPart.Endnotes = endnotesRoot;
            endnotesPart.Endnotes.Save();

            var settingsPart = main.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new Settings(
                new FootnoteDocumentWideProperties(
                    new FootnoteSpecialReference { Id = -1 },
                    new FootnoteSpecialReference { Id = 0 }),
                new EndnoteDocumentWideProperties(
                    new NumberingFormat { Val = numFmt },
                    new EndnoteSpecialReference { Id = -1 },
                    new EndnoteSpecialReference { Id = 0 }));
            settingsPart.Settings.Save();

            main.Document.Save();
        }
        return ms.ToArray();
    }

    internal static byte[] EndnoteWithSectionNumberFormat(NumberFormatValues numFmt)
    {
        using var ms = new MemoryStream();
        using (var document = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = new Body();
            main.Document.Body = body;
            body.Append(new Paragraph(TextRun("Zdanie"), EndnoteReferenceRun(1), TextRun(".")));
            body.Append(new SectionProperties(
                new EndnoteProperties(new NumberingFormat { Val = numFmt }),
                new PageSize { Width = 11906, Height = 16838 }));

            var endnotesPart = main.AddNewPart<EndnotesPart>();
            var endnotesRoot = new Endnotes();
            AppendTechnicalSeparators(endnotesRoot);
            endnotesRoot.Append(UserEndnote(1, new Paragraph(AutoNumberMarkRun(), TextRun("Przypis końcowy."))));
            endnotesPart.Endnotes = endnotesRoot;
            endnotesPart.Endnotes.Save();

            main.Document.Save();
        }
        return ms.ToArray();
    }

    internal static byte[] EndnoteWithFullNumberProperties(
        NumberFormatValues numFmt, int numStart, RestartNumberValues numRestart)
    {
        using var ms = new MemoryStream();
        using (var document = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = new Body();
            main.Document.Body = body;
            body.Append(new Paragraph(TextRun("Zdanie"), EndnoteReferenceRun(1), TextRun(".")));

            var endnotesPart = main.AddNewPart<EndnotesPart>();
            var endnotesRoot = new Endnotes();
            AppendTechnicalSeparators(endnotesRoot);
            endnotesRoot.Append(UserEndnote(1, new Paragraph(AutoNumberMarkRun(), TextRun("Przypis końcowy."))));
            endnotesPart.Endnotes = endnotesRoot;
            endnotesPart.Endnotes.Save();

            var settingsPart = main.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new Settings(
                new EndnoteDocumentWideProperties(
                    new NumberingFormat { Val = numFmt },
                    new NumberingStart { Val = (ushort)numStart },
                    new NumberingRestart { Val = numRestart },
                    new EndnoteSpecialReference { Id = -1 },
                    new EndnoteSpecialReference { Id = 0 }));
            settingsPart.Settings.Save();

            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] Build(Action<Endnotes> buildEndnotes, Action<Body> buildBody)
    {
        using var ms = new MemoryStream();
        using (var document = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = new Body();
            main.Document.Body = body;

            buildBody(body);

            var endnotesPart = main.AddNewPart<EndnotesPart>();
            var endnotesRoot = new Endnotes();
            buildEndnotes(endnotesRoot);
            endnotesPart.Endnotes = endnotesRoot;
            endnotesPart.Endnotes.Save();

            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static void AppendTechnicalSeparators(Endnotes root)
    {
        root.Append(new Endnote(new Paragraph(new Run(new SeparatorMark())))
        {
            Type = FootnoteEndnoteValues.Separator,
            Id = SeparatorId
        });
        root.Append(new Endnote(new Paragraph(new Run(new ContinuationSeparatorMark())))
        {
            Type = FootnoteEndnoteValues.ContinuationSeparator,
            Id = ContinuationSeparatorId
        });
    }

    private static Footnote SeparatorFootnote() =>
        new(new Paragraph(new Run(new SeparatorMark()))) { Type = FootnoteEndnoteValues.Separator, Id = SeparatorId };

    private static Footnote ContinuationSeparatorFootnote() =>
        new(new Paragraph(new Run(new ContinuationSeparatorMark()))) { Type = FootnoteEndnoteValues.ContinuationSeparator, Id = ContinuationSeparatorId };

    private static Endnote UserEndnote(long id, params Paragraph[] paragraphs)
    {
        var endnote = new Endnote { Id = id };
        foreach (var paragraph in paragraphs)
            endnote.Append(paragraph);
        return endnote;
    }

    private static Run AutoNumberMarkRun() =>
        new(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }),
            new EndnoteReferenceMark());

    private static Run EndnoteReferenceRun(long id) =>
        new(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }),
            new EndnoteReference { Id = id });

    private static Run FootnoteReferenceRun(long id) =>
        new(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }),
            new FootnoteReference { Id = id });

    private static Run TextRun(string text) =>
        new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Run BoldRun(string text) =>
        new(new RunProperties(new Bold()), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Run ItalicRun(string text) =>
        new(new RunProperties(new Italic()), new Text(text) { Space = SpaceProcessingModeValues.Preserve });
}
