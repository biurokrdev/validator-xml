using System.Text;
using Mass.AddressWindow.Domain.Letters;

namespace Mass.AddressWindow.Api.Tests;

public class PdfLetterTests
{
    private static readonly byte[] MinimalPdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF");

    [Fact]
    public void Create_AcceptsPdfHeader_AndKeepsOnlyFileName()
    {
        var letter = PdfLetter.Create(@"C:\pisma\wezwanie.pdf", MinimalPdf);

        Assert.Equal("wezwanie.pdf", letter.FileName);
        Assert.Equal(MinimalPdf.Length, letter.SizeBytes);
    }

    [Fact]
    public void Create_WithoutFileName_UsesDefault()
    {
        Assert.Equal("pismo.pdf", PdfLetter.Create("  ", MinimalPdf).FileName);
    }

    [Fact]
    public void Create_RejectsEmptyContent()
    {
        Assert.Throws<InvalidLetterException>(() => PdfLetter.Create("a.pdf", []));
        Assert.Throws<InvalidLetterException>(() => PdfLetter.Create("a.pdf", null));
    }

    [Fact]
    public void Create_RejectsContentWithoutPdfHeader()
    {
        var docx = Encoding.ASCII.GetBytes("PK\u0003\u0004 to nie jest pdf");

        Assert.Throws<InvalidLetterException>(() => PdfLetter.Create("a.pdf", docx));
    }

    [Fact]
    public void Create_RejectsTooLargeContent()
    {
        var content = new byte[PdfLetter.MaxSizeBytes + 1];
        MinimalPdf.CopyTo(content, 0);

        Assert.Throws<InvalidLetterException>(() => PdfLetter.Create("a.pdf", content));
    }
}
