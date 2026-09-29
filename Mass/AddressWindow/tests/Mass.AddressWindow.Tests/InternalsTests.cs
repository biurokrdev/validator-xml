using Mass.AddressWindow.Docx;

namespace Mass.AddressWindow.Tests;

public class InternalsTests
{
    [Theory]
    [InlineData("12pt", 4.2333)]
    [InlineData("1in", 25.4)]
    [InlineData("2.5cm", 25)]
    [InlineData("10mm", 10)]
    [InlineData("96px", 25.4)]
    [InlineData("96", 25.4)]
    public void TryParseLength_ConvertsUnitsToMillimetres(string raw, double expectedMm)
    {
        Assert.True(Units.TryParseLength(raw, 0.75 * Units.MmPerPt, out var mm));
        Assert.Equal(expectedMm, mm, 3);
    }

    [Fact]
    public void TryParseLength_RejectsUnknownUnit() =>
        Assert.False(Units.TryParseLength("5furlongs", 1, out _));

    [Theory]
    [InlineData("word/document.xml", "header1.xml", "word/header1.xml")]
    [InlineData("word/document.xml", "../customXml/item1.xml", "customXml/item1.xml")]
    [InlineData("word/document.xml", "/word/styles.xml", "word/styles.xml")]
    public void ResolvePath_HandlesRelativeAndAbsoluteTargets(string source, string target, string expected) =>
        Assert.Equal(expected, DocxPackage.ResolvePath(source, target));

    [Fact]
    public void Rectangle_ContainsAndIntersection()
    {
        var window = new RectangleMm(20, 45, 85, 45);

        Assert.True(window.Contains(new RectangleMm(23, 48, 50, 20)));
        Assert.False(window.Contains(new RectangleMm(19, 48, 50, 20)));
        Assert.Equal(25, window.IntersectionArea(new RectangleMm(100, 85, 10, 10)), 6);
        Assert.Equal(new RectangleMm(23, 48, 79, 39), window.Deflate(3));
    }

    [Fact]
    public void EnvelopeLayout_RejectsOverlappingWindows() =>
        Assert.Throws<ArgumentException>(() => new EnvelopeLayout(
            "zły",
            new AddressWindowSpec("adresat", new RectangleMm(20, 45, 85, 45)),
            new AddressWindowSpec("nadawca", new RectangleMm(20, 40, 85, 20))));
}
