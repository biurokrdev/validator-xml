namespace Mass.AddressWindow;

public sealed record EnvelopeWindow(
    double Width,
    double Height,
    double? FromLeft = null,
    double? FromRight = null,
    double? FromBottom = null,
    double? FromTop = null)
{
    internal RectangleMm OnEnvelope(double envelopeWidth, double envelopeHeight)
    {
        if (Width <= 0 || Height <= 0)
        {
            throw new ArgumentException("Okno musi mieć dodatnią szerokość i wysokość.");
        }

        if (FromLeft is null == FromRight is null || FromBottom is null == FromTop is null)
        {
            throw new ArgumentException(
                "Podaj dokładnie jedną odległość w poziomie (FromLeft albo FromRight) i jedną w pionie (FromBottom albo FromTop).");
        }

        var left = FromLeft ?? envelopeWidth - FromRight!.Value - Width;
        var top = FromTop ?? envelopeHeight - FromBottom!.Value - Height;
        var rect = new RectangleMm(left, top, Width, Height);
        if (!new RectangleMm(0, 0, envelopeWidth, envelopeHeight).Contains(rect, 0))
        {
            throw new ArgumentException($"Okno {rect} nie mieści się na kopercie {envelopeWidth}×{envelopeHeight} mm.");
        }

        return rect;
    }
}

public enum LetterPlay
{
    AnyPosition,

    Centered,
}
