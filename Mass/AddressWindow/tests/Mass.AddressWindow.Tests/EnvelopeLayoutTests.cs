namespace Mass.AddressWindow.Tests;

public class EnvelopeLayoutTests
{
    [Fact]
    public void C65TwoWindows_MapsEnvelopeWindowsOntoA4PageWithClearance()
    {
        var layout = EnvelopeLayouts.C65TwoWindows;

        Assert.True(layout.SupportsDoubleWindow);
        Assert.Equal(new RectangleMm(119, 54, 71, 30), layout.RecipientWindow.Area);
        Assert.Equal(new RectangleMm(28, 64, 51, 15), layout.SenderWindow!.Area);
        Assert.Equal(1.0, layout.RecipientWindow.ClearanceMm);
        Assert.Equal(1.0, layout.SenderWindow.ClearanceMm);
    }

    [Fact]
    public void C65SingleWindow_HasNoSenderWindow()
    {
        var layout = EnvelopeLayouts.C65SingleWindow;

        Assert.False(layout.SupportsDoubleWindow);
        Assert.Equal(EnvelopeLayouts.C65TwoWindows.RecipientWindow.Area, layout.RecipientWindow.Area);
    }

    [Fact]
    public void SingleWindowLayout_RejectsDoubleWindowMode()
    {
        var profile = new ValidationProfile { Layout = EnvelopeLayouts.C65SingleWindow };

        Assert.Throws<ArgumentException>(() => profile.EnsureUsableFor(WindowMode.Double));
    }
}
