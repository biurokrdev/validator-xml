using Mass.RLabel.Numbers;

namespace Mass.RLabel.Tests;

public class RegisteredNumberTests
{
    // Numery wzorcowe z dokumentacji Mass (ku_ku_lafila.md, sekcja 2).
    [Theory]
    [InlineData("75900773151200062", '1')]
    [InlineData("75900773151200063", '8')]
    [InlineData("75900773151200064", '5')]
    public void Gs1CheckDigit_MatchesMassDocumentation(string digits17, char expected) =>
        Assert.Equal(expected, RegisteredNumber.Gs1CheckDigit(digits17));

    [Theory]
    [InlineData("47312482", '9')]
    [InlineData("47312485", '0')] // 11 − 1 = 10 → 0
    [InlineData("47312488", '5')] // reszta 0 → 5
    public void S10CheckDigit_MatchesMassDocumentation(string serial, char expected) =>
        Assert.Equal(expected, RegisteredNumber.S10CheckDigit(serial));

    [Theory]
    [InlineData("00759007731512000621")]
    [InlineData("(00) 7 5900773 1 51200062 1")]
    [InlineData("00-7590-0773-1-5120-0062-1")]
    public void Domestic_AcceptsLabelNotationAndFormatsHumanReadable(string raw)
    {
        var number = RegisteredNumber.Parse(raw, MailType.Domestic, validateCheckDigit: true);

        Assert.Equal("00759007731512000621", number.Normalized);
        Assert.Equal("(00)75900773 1 51200062 1", number.HumanReadable);
    }

    [Theory]
    [InlineData("RR473124829PL")]
    [InlineData("rr473124829pl")]
    [InlineData("RR 473 124 829 PL")]
    public void International_AcceptsLowercaseAndSpaces(string raw)
    {
        var number = RegisteredNumber.Parse(raw, MailType.International, validateCheckDigit: true);

        Assert.Equal("RR473124829PL", number.Normalized);
        Assert.Equal("RR 473 124 829 PL", number.HumanReadable);
    }

    [Fact]
    public void Domestic_WrongCheckDigit_ThrowsWithBothDigits()
    {
        var ex = Assert.Throws<ArgumentException>(() => RegisteredNumber.Parse("00759007731512000622", MailType.Domestic, true));

        Assert.Contains("jest 2", ex.Message);
        Assert.Contains("powinna być 1", ex.Message);
    }

    [Fact]
    public void WrongCheckDigit_CanBeSkipped() =>
        Assert.False(RegisteredNumber.Parse("00759007731512000622", MailType.Domestic, validateCheckDigit: false).HasValidCheckDigit);

    [Theory]
    [InlineData("0075900773151200062", MailType.Domestic)]   // 19 cyfr
    [InlineData("01759007731512000621", MailType.Domestic)]  // zły AI
    [InlineData("RR47312482PL", MailType.International)]     // 8 cyfr
    [InlineData("R4731248299PL", MailType.International)]    // jedna litera
    [InlineData("RR473124829P1", MailType.International)]    // cyfra w kodzie kraju
    [InlineData("", MailType.Domestic)]
    public void MalformedNumbers_AreRejected(string raw, MailType type) =>
        Assert.Throws<ArgumentException>(() => RegisteredNumber.Parse(raw, type, true));

    [Fact]
    public void DomesticNumberPassedAsInternational_IsRejected() =>
        Assert.Throws<ArgumentException>(() => RegisteredNumber.Parse("00759007731512000621", MailType.International, true));
}
