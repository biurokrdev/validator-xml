namespace Mass.AddressWindow;

public sealed class AddressWindowSpec
{
    public AddressWindowSpec(string name, RectangleMm area)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (area.Width <= 0 || area.Height <= 0)
        {
            throw new ArgumentException("Okno musi mieć dodatnią szerokość i wysokość.", nameof(area));
        }

        Name = name;
        Area = area;
    }

    public string Name { get; }

    public RectangleMm Area { get; }

    public double ClearanceMm { get; init; } = 3.0;

    public string? ElementNameHint { get; init; }
}

public sealed class EnvelopeLayout
{
    public EnvelopeLayout(string name, AddressWindowSpec recipientWindow, AddressWindowSpec? senderWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(recipientWindow);

        if (senderWindow is not null && senderWindow.Area.IntersectionArea(recipientWindow.Area) > 0)
        {
            throw new ArgumentException("Okno nadawcy nie może nachodzić na okno adresata.", nameof(senderWindow));
        }

        Name = name;
        RecipientWindow = recipientWindow;
        SenderWindow = senderWindow;
    }

    public static EnvelopeLayout FromEnvelope(
        string name,
        double envelopeWidth,
        double envelopeHeight,
        EnvelopeWindow recipientWindow,
        EnvelopeWindow? senderWindow = null,
        LetterPlay play = LetterPlay.AnyPosition,
        double sheetWidth = 210,
        double sheetHeight = 99)
    {
        ArgumentNullException.ThrowIfNull(recipientWindow);
        if (sheetWidth > envelopeWidth || sheetHeight > envelopeHeight)
        {
            throw new ArgumentException($"Kartka {sheetWidth}×{sheetHeight} mm nie mieści się w kopercie {envelopeWidth}×{envelopeHeight} mm.");
        }

        var slackX = envelopeWidth - sheetWidth;
        var slackY = envelopeHeight - sheetHeight;

        AddressWindowSpec ToPage(string windowName, EnvelopeWindow window)
        {
            var onEnvelope = window.OnEnvelope(envelopeWidth, envelopeHeight);
            RectangleMm onPage;
            double clearance;
            if (play == LetterPlay.AnyPosition)
            {
                onPage = RectangleMm.FromEdges(
                    onEnvelope.Left,
                    onEnvelope.Top,
                    Math.Min(onEnvelope.Right - slackX, sheetWidth),
                    Math.Min(onEnvelope.Bottom - slackY, sheetHeight));
                clearance = 1.0;
            }
            else
            {
                onPage = onEnvelope.Offset(-slackX / 2, -slackY / 2);
                clearance = 3.0;
            }

            if (onPage.Width <= 2 * clearance || onPage.Height <= 2 * clearance)
            {
                throw new ArgumentException(
                    $"{windowName}: przy luzie kartki {slackX}×{slackY} mm żadna część strony nie jest widoczna w oknie zawsze. "
                    + "Użyj LetterPlay.Centered albo mniejszej koperty.");
            }

            return new AddressWindowSpec(windowName, onPage) { ClearanceMm = clearance };
        }

        return new EnvelopeLayout(
            name,
            ToPage("okno adresata", recipientWindow),
            senderWindow is null ? null : ToPage("okno nadawcy", senderWindow));
    }

    public string Name { get; }

    public AddressWindowSpec RecipientWindow { get; }

    public AddressWindowSpec? SenderWindow { get; }

    public bool SupportsDoubleWindow => SenderWindow is not null;
}
