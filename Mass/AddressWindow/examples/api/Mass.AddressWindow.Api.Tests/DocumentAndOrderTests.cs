using System.IO.Compression;
using System.Text;
using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Documents;
using Mass.AddressWindow.Api.External;

namespace Mass.AddressWindow.Api.Tests;

public class DocumentAndOrderTests
{
    [Fact]
    public void FormatIsDetectedByContent_NotByName()
    {
        Assert.Equal(DocumentFormat.Pdf, LetterDocument.Create("cokolwiek.docx", "%PDF-1.7\n%%EOF"u8.ToArray()).Format);
        Assert.Equal(DocumentFormat.Docx, LetterDocument.Create("cokolwiek.pdf", MinimalDocx()).Format);
    }

    [Fact]
    public void ZipWithoutWordDocument_IsRejected()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            writer.Write("nie Word");
        }

        Assert.Throws<DocumentRejectedException>(() => LetterDocument.Create("archiwum.docx", buffer.ToArray()));
    }

    [Fact]
    public void EmptyAndOversizedFiles_AreRejected()
    {
        Assert.Throws<DocumentRejectedException>(() => LetterDocument.Create("a.pdf", []));
        var huge = new byte[LetterDocument.MaxSizeBytes + 1];
        "%PDF-"u8.CopyTo(huge);
        Assert.Throws<DocumentRejectedException>(() => LetterDocument.Create("a.pdf", huge));
    }

    [Fact]
    public void Order_ForSingleWindow_DropsRegisteredNumber()
    {
        var order = LetterOrder.Create(EnvelopeType.SingleWindow, Recipient.Create(["Jan Kowalski", "00-001 Warszawa"]), "00759007731512000621");

        Assert.Null(order.RegisteredNumber);
    }

    [Fact]
    public void Order_ForDoubleWindow_RequiresRegisteredNumber()
    {
        var recipient = Recipient.Create(["Jan Kowalski", "00-001 Warszawa"]);

        Assert.Throws<OrderRejectedException>(() => LetterOrder.Create(EnvelopeType.DoubleWindow, recipient, "  "));
        Assert.Equal("RR473124829PL", LetterOrder.Create(EnvelopeType.DoubleWindow, recipient, " RR473124829PL ").RegisteredNumber);
    }

    [Fact]
    public void Recipient_TrimsAndRejectsWrongLineCount()
    {
        Assert.Equal(["Jan Kowalski", "ul. Polna 1", "00-001 Warszawa"], Recipient.Create(["  Jan   Kowalski ", "", "ul. Polna 1", "00-001 Warszawa"]).Lines);
        Assert.Throws<OrderRejectedException>(() => Recipient.Create(["Jan Kowalski"]));
        Assert.Throws<OrderRejectedException>(() => Recipient.Create(Enumerable.Repeat("x", 7)));
    }

    private static byte[] MinimalDocx()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
            writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");
        }

        return buffer.ToArray();
    }
}
