using System.Buffers.Binary;
using Mass.RLabel.Imaging;

namespace Mass.RLabel.Tests;

public class RendererTests
{
    private readonly IRegisteredLabelRenderer _renderer = new RegisteredLabelRenderer();

    [Fact]
    public void DefaultLabel_Is65x25mmAt300Dpi_Png()
    {
        var image = _renderer.Render(new RegisteredLabelRequest { Number = "00759007731512000621", Type = MailType.Domestic });

        Assert.Equal(768, image.WidthPx);   // 65 / 25.4 × 300
        Assert.Equal(295, image.HeightPx);  // 25 / 25.4 × 300
        Assert.Equal(300, image.Dpi);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal("png", image.FileExtension);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Bytes[..4]);
        Assert.Equal("(00)75900773 1 51200062 1", image.HumanReadableNumber);
        Assert.Equal("00759007731512000621", image.NormalizedNumber);
        Assert.InRange(image.BarcodeModuleMm, 0.2, 0.4);
    }

    [Fact]
    public void Png_ContainsPhysChunkWithDpi()
    {
        var image = _renderer.Render(new RegisteredLabelRequest { Number = "RR473124829PL", Type = MailType.International, Dpi = 300 });

        var bytes = image.Bytes;
        var ihdrLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4));
        var physAt = 8 + 12 + ihdrLength;
        Assert.Equal("pHYs"u8.ToArray(), bytes[(physAt + 4)..(physAt + 8)]);
        var ppm = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(physAt + 8, 4));
        Assert.Equal(11811u, ppm); // 300 / 0,0254
        Assert.Equal(1, bytes[physAt + 16]);
        Assert.Equal(ImageDpi.Crc32(bytes.AsSpan(physAt + 4, 13)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(physAt + 17, 4)));
    }

    [Fact]
    public void Crc32_MatchesKnownValue() =>
        Assert.Equal(0xCBF43926u, ImageDpi.Crc32("123456789"u8));

    [Fact]
    public void Jpeg_HasJfifDensityInDpi()
    {
        var image = _renderer.Render(new RegisteredLabelRequest
        {
            Number = "00759007731512000621", Type = MailType.Domestic, Format = LabelImageFormat.Jpeg, Dpi = 203,
        });

        var b = image.Bytes;
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], b[..4]);
        Assert.Equal("JFIF\0"u8.ToArray(), b[6..11]);
        Assert.Equal(1, b[13]);
        Assert.Equal(203, BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(14, 2)));
        Assert.Equal(203, BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(16, 2)));
        Assert.Equal([0xFF, 0xD9], b[^2..]);
    }

    [Fact]
    public void RenderBase64_ReturnsPngBase64()
    {
        var base64 = _renderer.RenderBase64("RR473124829PL", MailType.International);

        var bytes = Convert.FromBase64String(base64);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
    }

    [Fact]
    public void DataUri_HasContentTypePrefix()
    {
        var image = _renderer.Render(new RegisteredLabelRequest { Number = "RR473124829PL", Type = MailType.International, Format = LabelImageFormat.Jpeg });

        Assert.StartsWith("data:image/jpeg;base64,/9j/", image.DataUri);
    }

    [Fact]
    public void CustomSizeAndDpi_AreHonoured()
    {
        var image = _renderer.Render(new RegisteredLabelRequest
        {
            Number = "00759007731512000621", Type = MailType.Domestic, WidthMm = 100, HeightMm = 40, Dpi = 600,
        });

        Assert.Equal(2362, image.WidthPx);
        Assert.Equal(945, image.HeightPx);
    }

    [Fact]
    public void WrongCheckDigit_Throws() =>
        Assert.Throws<ArgumentException>(() => _renderer.RenderBase64("00759007731512000622", MailType.Domestic));

    [Theory]
    [InlineData(0, 25, 300)]
    [InlineData(65, -1, 300)]
    [InlineData(65, 25, 50)]
    [InlineData(5, 25, 300)] // za mało pikseli na kod
    public void InvalidDimensions_Throw(double w, double h, int dpi) =>
        Assert.Throws<ArgumentException>(() => _renderer.Render(new RegisteredLabelRequest
        {
            Number = "00759007731512000621", Type = MailType.Domestic, WidthMm = w, HeightMm = h, Dpi = dpi,
        }));

    [Fact]
    public void Renderer_IsThreadSafe()
    {
        var results = new string[16];
        Parallel.For(0, results.Length, i =>
            results[i] = _renderer.RenderBase64(i % 2 == 0 ? "00759007731512000621" : "RR473124829PL", i % 2 == 0 ? MailType.Domestic : MailType.International));

        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r)));
        Assert.Equal(results[0], results[2]); // deterministyczny wynik
    }
}
