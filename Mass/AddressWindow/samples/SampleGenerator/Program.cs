using Mass.AddressWindow;
using Mass.RLabel;
using SampleGenerator;
using static SampleGenerator.WordDocument;

var outputDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
Directory.CreateDirectory(outputDir);

var recipientWindow = EnvelopeLayouts.C65TwoWindows.RecipientWindow.Area;
var senderWindow = EnvelopeLayouts.C65TwoWindows.SenderWindow!.Area;

string[] recipient = ["Pan Jan Kowalski", "ul. Marszałkowska 142 m. 5", "00-061 Warszawa"];
string[] sender = ["Urząd Gminy Wólka", "ul. Polna 1", "21-100 Lubartów"];

// Nalepka R jako grafika z Mass.RLabel. W oknie nadawcy C65 zawsze widać tylko 51×15 mm strony,
// więc nalepka musi mieć najwyżej 49×13 mm; standardowa 65×25 mm się nie mieści (plik 05).
var labelRenderer = new RegisteredLabelRenderer();
byte[] Label(double widthMm, double heightMm) => labelRenderer.Render(new RegisteredLabelRequest
{
    Number = "00759007731512000621",
    Type = MailType.Domestic,
    WidthMm = widthMm,
    HeightMm = heightMm,
    Dpi = 600,
}).Bytes;

string Letterhead() =>
    P("URZĄD GMINY WÓLKA", "<w:pStyle w:val=\"Nagwek\"/>", fontPt: 12, bold: true) +
    P("ul. Polna 1, 21-100 Lubartów  •  tel. 81 555 12 00  •  www.wolka.example.pl", "<w:pStyle w:val=\"Nagwek\"/>", fontPt: 8);

string Letter(string subject) =>
    P("Znak sprawy: OR.6220.14.2026", Spacing(beforeMm: 62, afterMm: 2), fontPt: 10) +
    P(subject, Spacing(afterMm: 4), bold: true) +
    P("Szanowny Panie,") +
    P("w nawiązaniu do Pana wniosku z dnia 12 września 2026 r. uprzejmie informuję, że sprawa została przekazana " +
      "do rozpatrzenia właściwemu referatowi. O sposobie jej załatwienia zostanie Pan powiadomiony odrębnym pismem " +
      "w terminie 30 dni od dnia jego wpływu.", "<w:jc w:val=\"both\"/>") +
    P("W razie pytań prosimy o kontakt z Biurem Obsługi Interesantów, tel. 81 555 12 34.", "<w:jc w:val=\"both\"/>") +
    P("Z poważaniem", Spacing(beforeMm: 8, afterMm: 12), fontPt: null) +
    P("Anna Nowak", "<w:ind w:left=\"5103\"/>") +
    P("Kierownik Referatu Organizacyjnego", "<w:ind w:left=\"5103\"/>", fontPt: 9);

const string DatePPr = "<w:jc w:val=\"right\"/>";
const string Date = "Lubartów, 28 września 2026 r.";

var files = new List<(string Name, string Description)>();

{
    var doc = new WordDocument();
    doc.Header(P("URZĄD GMINY WÓLKA", "<w:pStyle w:val=\"Nagwek\"/>", fontPt: 12, bold: true) +
               P("ul. Polna 1, 21-100 Lubartów  •  tel. 81 555 12 00  •  www.wolka.example.pl", "<w:pStyle w:val=\"Nagwek\"/>", fontPt: 8));
    var anchors = doc.WindowGuide(recipientWindow.Left, recipientWindow.Top, recipientWindow.Width, recipientWindow.Height, "Okno adresata (pomocnicze)")
                  + doc.TextBox(117.5, 53.8, 72, 30, "AdresOdbiorcy", AddressParagraphs(recipient));
    doc.Body($"<w:p><w:pPr>{DatePPr}</w:pPr>{anchors}{Run(Date)}</w:p>" + Letter("Informacja o przekazaniu wniosku"));
    const string name = "01_C65_jedno_okienko_poprawny.docx";
    doc.Save(Path.Combine(outputDir, name));
    files.Add((name, "Jedno okienko po prawej. Adres w polu tekstowym w oknie, papier firmowy w nagłówku po lewej (poza oknami)."));
}

{
    var doc = new WordDocument();
    doc.Header(Letterhead());
    var anchors = doc.WindowGuide(senderWindow.Left, senderWindow.Top, senderWindow.Width, senderWindow.Height, "Okno nadawcy (pomocnicze)")
                  + doc.WindowGuide(recipientWindow.Left, recipientWindow.Top, recipientWindow.Width, recipientWindow.Height, "Okno adresata (pomocnicze)")
                  + doc.Picture(29.5, 65.5, 48, 12, "NalepkaR", Label(48, 12))
                  + doc.TextBox(117.5, 53.8, 72, 30, "AdresOdbiorcy",
                      AddressParagraphs(["Kancelaria Radców Prawnych", "Wiśniewski i Wspólnicy sp.k.", "al. Jerozolimskie 65/79", "00-697 Warszawa"]));
    doc.Body($"<w:p><w:pPr>{DatePPr}</w:pPr>{anchors}{Run(Date)}</w:p>" + Letter("Odpowiedź na wezwanie do uzupełnienia braków"));
    const string name = "02_C65_dwa_okienka_poprawny.docx";
    doc.Save(Path.Combine(outputDir, name));
    files.Add((name, "Dwa okienka. Nalepka R 48×12 mm jako grafika w oknie nadawcy, adresat (4 wiersze) w polu tekstowym."));
}

{
    var doc = new WordDocument();
    string[] badAddress =
    [
        "Szanowny Pan",
        "Prof. dr hab. inż. Aleksander Brzęczyszczykiewicz",
        "Instytut Badań Systemowych",
        "Zakład Analiz",
        "pokój 204, II piętro",
        "ul. Newelska 6",
        "01447 Warszawa",
    ];
    var anchors = doc.WindowGuide(senderWindow.Left, senderWindow.Top, senderWindow.Width, senderWindow.Height, "Okno nadawcy (pomocnicze)")
                  + doc.WindowGuide(recipientWindow.Left, recipientWindow.Top, recipientWindow.Width, recipientWindow.Height, "Okno adresata (pomocnicze)")
                  + doc.TextBox(140, 60, 63, 20, "AdresOdbiorcy", AddressParagraphs(badAddress, fontPt: 7, italic: true));
    doc.Body($"<w:p><w:pPr>{DatePPr}</w:pPr>{anchors}{Run(Date)}</w:p>" + Letter("Zaproszenie na posiedzenie komisji"));
    const string name = "03_C65_bledy_adresu.docx";
    doc.Save(Path.Combine(outputDir, name));
    files.Add((name, "Błędy: pole wystaje poza okno, 7 wierszy, za długi wiersz, kod „01447”, 7 pt kursywą, tekst ucięty w polu."));
}

{
    var doc = new WordDocument();
    var anchors = doc.WindowGuide(senderWindow.Left, senderWindow.Top, senderWindow.Width, senderWindow.Height, "Okno nadawcy (pomocnicze)")
                  + doc.WindowGuide(recipientWindow.Left, recipientWindow.Top, recipientWindow.Width, recipientWindow.Height, "Okno adresata (pomocnicze)");
    var senderFrame = AddressParagraphs(sender, fontPt: 9, extraPPr: FramePPr(29, 65, 49, 13));
    string[] flowAddress = ["Spółdzielnia Mieszkaniowa „Zacisze”", "ul. Lipowa 17", "20-020 Lublin"];
    const string indentToWindow = "<w:ind w:left=\"5386\"/>";
    var recipientFlow =
        P(flowAddress[0], "<w:pStyle w:val=\"Adres\"/>" + Spacing(beforeMm: 25) + indentToWindow) +
        AddressParagraphs(flowAddress.Skip(1), extraPPr: indentToWindow);
    var spacer = P("", "<w:spacing w:before=\"0\" w:after=\"0\" w:line=\"1701\" w:lineRule=\"exact\"/>");
    doc.Body(senderFrame + $"<w:p><w:pPr>{DatePPr}</w:pPr>{anchors}{Run(Date)}</w:p>" + recipientFlow + spacer +
             Letter("Wezwanie do zapłaty").Replace(Spacing(beforeMm: 62, afterMm: 2), Spacing(beforeMm: 0, afterMm: 2)));
    const string name = "04_C65_ramka_i_adres_w_tresci.docx";
    doc.Save(Path.Combine(outputDir, name));
    files.Add((name, "Bez pól tekstowych: nadawca w ramce akapitu, adresat jako zwykłe akapity z wcięciem do okna po prawej (położenie szacowane)."));
}

{
    var doc = new WordDocument();
    doc.Header(Letterhead());
    var anchors = doc.WindowGuide(senderWindow.Left, senderWindow.Top, senderWindow.Width, senderWindow.Height, "Okno nadawcy (pomocnicze)")
                  + doc.WindowGuide(recipientWindow.Left, recipientWindow.Top, recipientWindow.Width, recipientWindow.Height, "Okno adresata (pomocnicze)")
                  + doc.Picture(21, 59, 65, 25, "NalepkaR", Label(65, 25))
                  + doc.TextBox(117.5, 53.8, 72, 30, "AdresOdbiorcy", AddressParagraphs(recipient));
    doc.Body($"<w:p><w:pPr>{DatePPr}</w:pPr>{anchors}{Run(Date)}</w:p>" + Letter("Decyzja w sprawie wniosku"));
    const string name = "05_C65_nalepka_R_za_duza.docx";
    doc.Save(Path.Combine(outputDir, name));
    files.Add((name, "Dwa okienka. Nalepka R w standardowym rozmiarze 65×25 mm: większa niż obszar okna nadawcy widoczny zawsze."));
}

var validator = new AddressWindowValidator();
foreach (var (name, description) in files)
{
    Console.WriteLine($"=== {name}");
    Console.WriteLine($"    {description}");
    foreach (var mode in new[] { WindowMode.Single, WindowMode.Double })
    {
        var result = validator.ValidateFile(Path.Combine(outputDir, name), mode);
        Console.WriteLine($"  [{mode}] IsValid = {result.IsValid}");
        foreach (var window in result.Windows)
        {
            var block = window.Block;
            Console.WriteLine(window.Label is { } label
                ? $"    {window.Role}: nalepka R {label.Bounds}"
                : block is null
                    ? $"    {window.Role}: nie znaleziono"
                    : $"    {window.Role}: {block.Kind}, tekst {block.TextBounds}, wiersze: {string.Join(" | ", block.Lines)}");
        }

        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"    - {issue.Severity,-7} {issue.Code} ({issue.Window}): {issue.Message}");
        }
    }

    Console.WriteLine();
}

Console.WriteLine($"Zapisano w: {outputDir}");
