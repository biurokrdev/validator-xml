using System.IO.Compression;
using static Mass.AddressWindow.Tests.TestDocx;

namespace Mass.AddressWindow.Tests;

public class AddressWindowValidatorTests
{
    private static readonly ValidationProfile LeftWindow = new() { Layout = EnvelopeLayouts.DlTwoWindowsLeft };

    private readonly IAddressWindowValidator _validator = new AddressWindowValidator(LeftWindow);

    private static string RecipientBox(IEnumerable<string>? lines = null, double fontPt = 10) =>
        TextBox(21, 47, 82, 40, lines ?? Recipient, fontPt, name: "Adresat");

    private static string SenderBox() => TextBox(21, 14, 82, 24, Sender, 9, name: "Nadawca");

    private static string[] Codes(AddressWindowValidationResult result, IssueSeverity severity) =>
        result.Issues.Where(i => i.Severity == severity).Select(i => i.Code).ToArray();


    [Fact]
    public void Single_TextBoxInsideRecipientWindow_IsValid()
    {
        var result = _validator.Validate(Create(RecipientBox()), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        var window = Assert.Single(result.Windows);
        Assert.Equal(WindowRole.Recipient, window.Role);
        Assert.Equal(AddressSourceKind.TextBox, window.Block!.Kind);
        Assert.Equal(Recipient, window.Block.Lines);
        Assert.False(window.Block.PositionEstimated);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Single_DocumentWithoutAddress_ReportsWindowNotFound()
    {
        var body = Paragraphs(["Szanowni Państwo,"], pPr: "<w:spacing w:before=\"5669\"/>");

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.False(result.IsValid);
        Assert.False(result.For(WindowRole.Recipient)!.Found);
        Assert.Contains(IssueCodes.WindowNotFound, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void TextBoxMostlyOutsideWindow_ReportsAddressOutsideWindow()
    {
        var result = _validator.Validate(Create(TextBox(90, 47, 82, 40, Recipient)), WindowMode.Single);

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Errors, e => e.Code == IssueCodes.AddressOutsideWindow);
        Assert.Contains("z prawej", issue.Message);
    }

    [Fact]
    public void TextInsideWindowButWithoutClearance_ReportsTooCloseToEdge()
    {
        var result = _validator.Validate(Create(TextBox(20, 45.5, 85, 40, Recipient)), WindowMode.Single);

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Errors);
        Assert.Equal(IssueCodes.AddressTooCloseToEdge, issue.Code);
        Assert.Contains("z lewej", issue.Message);
        Assert.Contains("u góry", issue.Message);
    }

    [Fact]
    public void ClearanceCanBeDisabledPerWindow()
    {
        var profile = new ValidationProfile
        {
            Layout = new EnvelopeLayout("bez odstępu", new AddressWindowSpec("okno", new RectangleMm(20, 45, 85, 45)) { ClearanceMm = 0 }),
        };

        var result = _validator.Validate(Create(TextBox(20, 45.5, 85, 40, Recipient)), WindowMode.Single, profile);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
    }

    [Fact]
    public void RotatedTextBox_IsRejected()
    {
        var box = TextBox(21, 47, 82, 40, Recipient, xfrmAttrs: "rot=\"5400000\"");

        var result = _validator.Validate(Create(box), WindowMode.Single);

        Assert.Contains(IssueCodes.TextRotated, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void FixedHeightTextBoxTooSmallForText_WarnsAboutOverflow()
    {
        var result = _validator.Validate(Create(TextBox(21, 47, 82, 10, Recipient)), WindowMode.Single);

        Assert.Contains(IssueCodes.TextOverflowsContainer, Codes(result, IssueSeverity.Warning));
    }

    [Fact]
    public void Frame_IsDetected()
    {
        var result = _validator.Validate(Create(Frame(23, 48, 79, 30, Recipient)), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(AddressSourceKind.Frame, result.Windows[0].Block!.Kind);
    }

    [Fact]
    public void VmlTextBox_IsDetected()
    {
        var result = _validator.Validate(Create(VmlTextBox(21, 47, 82, 40, Recipient)), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(AddressSourceKind.VmlTextBox, result.Windows[0].Block!.Kind);
    }

    [Fact]
    public void AlternateContent_FallbackIsNotCountedTwice()
    {
        var result = _validator.Validate(Create(AlternateContentTextBox(21, 47, 82, 40, Recipient)), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(AddressSourceKind.TextBox, result.Windows[0].Block!.Kind);
        Assert.DoesNotContain(result.Issues, i => i.Code == IssueCodes.OtherTextInWindow);
    }

    [Fact]
    public void PositionedTableCell_IsDetected()
    {
        var result = _validator.Validate(Create(PositionedTable(3, 48, 21, 78, 35, Recipient)), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(AddressSourceKind.TableCell, result.Windows[0].Block!.Kind);
    }

    [Fact]
    public void ParagraphsInFlow_AreDetectedWithEstimatedPosition()
    {
        var body = Paragraphs([Recipient[0]], pPr: "<w:spacing w:before=\"1417\" w:after=\"0\"/>")
                   + Paragraphs(Recipient.Skip(1))
                   + Paragraphs(["", "Szanowny Panie,", "treść pisma..."]);

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        var block = result.Windows[0].Block!;
        Assert.Equal(AddressSourceKind.Paragraphs, block.Kind);
        Assert.True(block.PositionEstimated);
        Assert.Equal(Recipient, block.Lines);
        Assert.Contains(result.Issues, i => i.Code == IssueCodes.PositionEstimated && i.Severity == IssueSeverity.Info);
    }

    [Fact]
    public void TextBoxInWindow_WinsOverLongBodyTextCrossingWindow()
    {
        var bodyText = string.Concat(Enumerable.Range(1, 40).Select(i =>
            $"<w:p><w:pPr><w:spacing w:after=\"160\"/></w:pPr>{Runs([$"Akapit treści pisma numer {i}, który ciągnie się przez całą szerokość strony."], 11)}</w:p>"));

        var result = _validator.Validate(Create(RecipientBox() + bodyText), WindowMode.Single);

        var block = result.Windows[0].Block!;
        Assert.Equal(AddressSourceKind.TextBox, block.Kind);
        Assert.Equal(Recipient, block.Lines);
        Assert.Contains(IssueCodes.OtherTextInWindow, Codes(result, IssueSeverity.Warning));
    }

    [Fact]
    public void FlowAddress_IsSeparatedFromDateAndBodyBySpacing()
    {
        var body = Paragraphs(["Lubartów, 28 września 2026 r."], pPr: "<w:jc w:val=\"right\"/>")
                   + Paragraphs([Recipient[0]], pPr: "<w:spacing w:before=\"1417\" w:after=\"0\"/>")
                   + Paragraphs(Recipient.Skip(1))
                   + Paragraphs(["Wezwanie do zapłaty"], pPr: "<w:spacing w:before=\"1134\" w:after=\"0\"/>")
                   + Paragraphs(["Treść pisma."]);

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(Recipient, result.Windows[0].Block!.Lines);
    }

    [Fact]
    public void FrameWithTextWrapping_PushesOverlappingParagraphAside()
    {
        var body = Frame(24, 16, 78, 18, Sender, 9).Replace("w:wrap=\"around\"", "w:wrap=\"around\" w:hSpace=\"142\"")
                   + Paragraphs([Recipient[0]], pPr: "<w:spacing w:before=\"1134\" w:after=\"0\"/>")
                   + Paragraphs(Recipient.Skip(1));

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.False(result.IsValid);
        Assert.DoesNotContain(Recipient[0], result.Windows[0].Block!.Lines);
    }

    [Fact]
    public void AddressOnSecondPage_IsNotDetected()
    {
        var body = "<w:p><w:r><w:t>Strona 1</w:t></w:r><w:r><w:br w:type=\"page\"/></w:r></w:p>" + RecipientBox();

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.Contains(IssueCodes.WindowNotFound, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void TextBoxInFirstPageHeader_IsDetected()
    {
        var result = _validator.Validate(Create("<w:p/>", header: RecipientBox(), titlePage: true), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(DocumentPartKind.Header, result.Windows[0].Block!.Part);
    }

    [Fact]
    public void ElementNameHint_SelectsNamedBlockEvenOutsideWindow()
    {
        var profile = new ValidationProfile
        {
            Layout = new EnvelopeLayout(
                "z nazwą",
                new AddressWindowSpec("okno adresata", new RectangleMm(20, 45, 85, 45)) { ElementNameHint = "AdresOdbiorcy" }),
        };
        var body = TextBox(110, 150, 70, 30, Recipient, name: "AdresOdbiorcy");

        var result = _validator.Validate(Create(body), WindowMode.Single, profile);

        Assert.True(result.Windows[0].Found);
        Assert.Contains(IssueCodes.AddressOutsideWindow, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void OtherTextOverlappingWindow_IsReportedAsWarning()
    {
        var body = RecipientBox() + TextBox(60, 80, 40, 15, ["Nr sprawy: 12/2026"], name: "Znak");

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.Contains(IssueCodes.OtherTextInWindow, Codes(result, IssueSeverity.Warning));
    }


    [Fact]
    public void Double_BothWindowsFilled_IsValid()
    {
        var result = _validator.Validate(Create(SenderBox() + RecipientBox()), WindowMode.Double);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(Sender, result.For(WindowRole.Sender)!.Block!.Lines);
        Assert.Equal(Recipient, result.For(WindowRole.Recipient)!.Block!.Lines);
    }

    [Fact]
    public void Double_SenderMissing_FailsOnlyForSender()
    {
        var result = _validator.Validate(Create(RecipientBox()), WindowMode.Double);

        Assert.False(result.IsValid);
        Assert.True(result.For(WindowRole.Recipient)!.IsValid);
        Assert.False(result.For(WindowRole.Sender)!.Found);
        var error = Assert.Single(result.Errors);
        Assert.Equal(WindowRole.Sender, error.Window);
    }

    [Fact]
    public void Single_SameDocument_IgnoresSenderWindow()
    {
        var result = _validator.Validate(Create(RecipientBox()), WindowMode.Single);

        Assert.True(result.IsValid);
        Assert.Null(result.For(WindowRole.Sender));
    }

    [Fact]
    public void Double_WithLayoutWithoutSenderWindow_Throws()
    {
        var profile = new ValidationProfile { Layout = EnvelopeLayouts.DlSingleWindowDin5008B };

        Assert.Throws<ArgumentException>(() => _validator.Validate(Create(RecipientBox()), WindowMode.Double, profile));
    }


    [Fact]
    public void TooManyLines_IsError()
    {
        string[] lines = ["Firma Sp. z o.o.", "Dział Kadr", "Jan Kowalski", "p. 204", "ul. Polna 1", "skr. poczt. 12", "00-061 Warszawa"];

        var result = _validator.Validate(Create(RecipientBox(lines)), WindowMode.Single);

        Assert.Contains(IssueCodes.TooManyLines, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void TooFewLines_IsError()
    {
        var result = _validator.Validate(Create(RecipientBox(["Jan Kowalski", "00-061 Warszawa"])), WindowMode.Single);

        Assert.Contains(IssueCodes.TooFewLines, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void MissingPostalCode_IsError()
    {
        var result = _validator.Validate(Create(RecipientBox(["Jan Kowalski", "ul. Polna 1", "Warszawa"])), WindowMode.Single);

        Assert.Contains(IssueCodes.PostalCodeLineMissing, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void PostalCodeWithoutDash_IsErrorWithHint()
    {
        var result = _validator.Validate(Create(RecipientBox(["Jan Kowalski", "ul. Polna 1", "00061 Warszawa"])), WindowMode.Single);

        var issue = Assert.Single(result.Errors);
        Assert.Equal(IssueCodes.PostalCodeLineMissing, issue.Code);
        Assert.Contains("00061", issue.Message);
    }

    [Fact]
    public void PostalCodeNotInLastLine_IsError()
    {
        var result = _validator.Validate(Create(RecipientBox(["Jan Kowalski", "00-061 Warszawa", "ul. Polna 1"])), WindowMode.Single);

        Assert.Contains(IssueCodes.TextAfterPostalCodeLine, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void ForeignAddressWithCountryInCapitals_IsValid()
    {
        string[] lines = ["John Smith", "10 Downing Street", "London SW1A 2AA", "WIELKA BRYTANIA"];

        var result = _validator.Validate(Create(RecipientBox(lines)), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
    }

    [Fact]
    public void ForeignAddress_CanBeForbidden()
    {
        var profile = new ValidationProfile
        {
            Layout = EnvelopeLayouts.DlTwoWindowsLeft,
            RecipientRules = new AddressContentRules { AllowForeignAddress = false },
        };
        string[] lines = ["John Smith", "10 Downing Street", "London SW1A 2AA", "WIELKA BRYTANIA"];

        var result = _validator.Validate(Create(RecipientBox(lines)), WindowMode.Single, profile);

        Assert.Contains(IssueCodes.PostalCodeLineMissing, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void LineTooLong_IsErrorAndWrapWarning()
    {
        string[] lines = ["Jan Kowalski", "ul. Generała Władysława Andersa 12345678 m. 99", "00-061 Warszawa"];

        var result = _validator.Validate(Create(RecipientBox(lines)), WindowMode.Single);

        Assert.Contains(IssueCodes.LineTooLong, Codes(result, IssueSeverity.Error));
        Assert.Contains(IssueCodes.LineWraps, Codes(result, IssueSeverity.Warning));
    }

    [Fact]
    public void FontTooSmall_IsError()
    {
        var result = _validator.Validate(Create(RecipientBox(fontPt: 7)), WindowMode.Single);

        Assert.Contains(IssueCodes.FontTooSmall, Codes(result, IssueSeverity.Error));
        Assert.Equal(7, result.Windows[0].Block!.MinFontSizePt);
    }

    [Fact]
    public void FontSizeInheritedFromDocumentDefaults_IsChecked()
    {
        const string styles = "<w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"14\"/></w:rPr></w:rPrDefault></w:docDefaults>";
        var content = string.Concat(Recipient.Select(l => $"<w:p><w:r><w:t>{l}</w:t></w:r></w:p>"));
        var body = TextBox(21, 47, 82, 40, [], content: content);

        var result = _validator.Validate(Create(body, styles: styles), WindowMode.Single);

        Assert.Contains(IssueCodes.FontTooSmall, Codes(result, IssueSeverity.Error));
    }

    [Fact]
    public void ItalicAndCenteredAddress_Warns()
    {
        var content = string.Concat(Recipient.Select(l => $"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr>{Runs([l], 10, "<w:i/>")}</w:p>"));
        var body = TextBox(21, 47, 82, 40, [], content: content);

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Contains(IssueCodes.DecoratedText, Codes(result, IssueSeverity.Warning));
        Assert.Contains(IssueCodes.NonLeftAlignment, Codes(result, IssueSeverity.Warning));
    }

    [Fact]
    public void MergeFields_DowngradePostalCheckToWarning()
    {
        string[] lines = ["«Imie» «Nazwisko»", "«Ulica» «Nr»", "«Kod» «Miejscowosc»"];

        var result = _validator.Validate(Create(RecipientBox(lines)), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Contains(IssueCodes.MergeFieldsPresent, Codes(result, IssueSeverity.Warning));
        Assert.Contains(IssueCodes.PostalCodeLineMissing, Codes(result, IssueSeverity.Warning));
    }

    [Fact]
    public void LineBreaksInsideOneParagraph_AreSeparateLines()
    {
        var content = $"<w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>{Runs(Recipient)}</w:p>";
        var body = TextBox(21, 47, 82, 40, [], content: content);

        var result = _validator.Validate(Create(body), WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
        Assert.Equal(Recipient, result.Windows[0].Block!.Lines);
    }


    [Fact]
    public void NotAZipFile_IsReportedAsInvalidDocument()
    {
        var result = _validator.Validate("To nie jest docx"u8.ToArray(), WindowMode.Single);

        Assert.False(result.IsDocumentReadable);
        Assert.False(result.IsValid);
        Assert.Equal(IssueCodes.InvalidDocument, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void ZipWithoutWordDocument_IsReportedAsInvalidDocument()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("readme.txt");
        }

        var result = _validator.Validate(buffer.ToArray(), WindowMode.Single);

        Assert.False(result.IsDocumentReadable);
    }

    [Fact]
    public async Task ValidateAsync_WorksWithNonSeekableStream()
    {
        using var stream = new NonSeekableStream(Create(RecipientBox()));

        var result = await _validator.ValidateAsync(stream, WindowMode.Single);

        Assert.True(result.IsValid, string.Join("\n", result.Issues));
    }

    [Fact]
    public void ValidateFile_ReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"okienko-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(path, Create(SenderBox() + RecipientBox()));
        try
        {
            Assert.True(_validator.ValidateFile(path, WindowMode.Double).IsValid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
