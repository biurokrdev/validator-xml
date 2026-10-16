using Mass.AddressWindow.Api.Contracts;
using Mass.RLabel;

namespace Mass.AddressWindow.Api.External;

// Layout of the mock letter on an A4 page, in millimetres from the top-left corner. The address and label
// positions follow the sample letters for the C65 envelope (see samples/README.md).
internal static class LetterContent
{
    public const double RecipientLeftMm = 122;
    public const double RecipientTopMm = 57;
    public const double RecipientLineStepMm = 4.6;
    public const double RecipientFontPt = 10;

    public const double LabelLeftMm = 29.5;
    public const double LabelTopMm = 65.5;
    public const double LabelWidthMm = 48;
    public const double LabelHeightMm = 12;
    public const int LabelDpi = 600;

    public const string Letterhead = "URZĄD GMINY WÓLKA";
    public const string LetterheadDetails = "ul. Polna 1, 21-100 Lubartów  •  tel. 81 555 12 00";
    public const string Date = "Lubartów, 28 września 2026 r.";
    public const string Subject = "Informacja o przekazaniu wniosku";

    public static readonly string[] Body =
    [
        "Szanowni Państwo,",
        "w nawiązaniu do wniosku uprzejmie informujemy, że sprawa została przekazana do rozpatrzenia",
        "właściwemu referatowi. O sposobie jej załatwienia zostaną Państwo powiadomieni odrębnym pismem.",
        "Z poważaniem",
        "Anna Nowak, Kierownik Referatu Organizacyjnego",
    ];

    public static byte[]? LabelPng(LetterOrder order)
    {
        if (order.Envelope != EnvelopeType.DoubleWindow || order.RegisteredNumber is null)
        {
            return null;
        }

        var number = order.RegisteredNumber;
        var type = number.Any(char.IsLetter) ? MailType.International : MailType.Domestic;
        return new RegisteredLabelRenderer().Render(new RegisteredLabelRequest
        {
            Number = number,
            Type = type,
            WidthMm = LabelWidthMm,
            HeightMm = LabelHeightMm,
            Dpi = LabelDpi,
            ValidateCheckDigit = false,
        }).Bytes;
    }

    public static string SafeFileName(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());
}
