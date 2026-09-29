using System.IO.Compression;
using System.Text;
using D2ViewerEditor.Domain.Models;
using Microsoft.Extensions.Options;
using OpenMcdf;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

/// <summary>Wpis archiwum odczytany łagodnie: bajty albo powód, dla którego ich nie ma.</summary>
public sealed class LenientPackageEntry
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required long CompressedLength { get; init; }
    public byte[]? Bytes { get; set; }
    public string? ReadError { get; set; }

    public bool IsXml =>
        Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        Path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Zawartość archiwum ZIP odczytana bez przerywania na pierwszym błędzie — każdy wpis niesie
/// albo bajty, albo opis problemu. Odczyt zatrzymuje się tylko na limitach bezpieczeństwa.
/// </summary>
public sealed class LenientPackage
{
    private readonly Dictionary<string, LenientPackageEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public string DetectedFormat { get; set; } = "zip";
    public List<LenientPackageEntry> Entries { get; } = [];

    public IReadOnlyDictionary<string, LenientPackageEntry> ByPath => _byPath;

    public void Add(LenientPackageEntry entry)
    {
        Entries.Add(entry);
        _byPath.TryAdd(entry.Path, entry);
    }

    public LenientPackageEntry? Find(string path) => _byPath.GetValueOrDefault(path);

    public bool Contains(string path) => _byPath.ContainsKey(path);

    /// <summary>Tekst części XML (UTF-8/UTF-16 po BOM); <c>null</c>, gdy wpisu nie ma albo nie dało się go odczytać.</summary>
    public string? ReadText(string path)
    {
        var entry = Find(path);

        if (entry?.Bytes is null)
        {
            return null;
        }

        using var stream = new MemoryStream(entry.Bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }
}

/// <summary>
/// Etap „Plik": co to za bajty (sygnatura, nie rozszerzenie), czy archiwum ZIP da się odczytać,
/// czy każdy wpis rozpakowuje się do zadeklarowanego rozmiaru i zgadza z sumą CRC-32. To warstwa,
/// na której Word mówi „plik jest uszkodzony" jeszcze przed zajrzeniem do XML.
/// </summary>
public sealed class FileContainerCheck
{
    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEndOfCentralDirectory = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] CfbMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly DocumentHealthOptions _options;

    public FileContainerCheck(IOptions<DocumentHealthOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>Zwraca odczytane archiwum albo <c>null</c>, gdy plik nie jest pakietem ZIP (format opisany w ustaleniach).</summary>
    public LenientPackage? Run(byte[] bytes, HealthFindingCollector findings, out string detectedFormat)
    {
        detectedFormat = "unknown";

        if (bytes.Length == 0)
        {
            findings.Add(DocumentHealthCodes.FileEmpty, StructureIssueSeverity.Error, HealthStage.File,
                "Plik jest pusty", "Plik ma 0 bajtów — nie ma czego analizować.",
                wordImpact: WordOpenImpact.CannotOpen, pdfImpact: PdfConversionImpact.Blocking,
                remedy: "Wgraj plik ponownie ze źródła; pusty plik zwykle oznacza przerwany transfer albo błąd eksportu.");
            detectedFormat = "empty";
            return null;
        }

        if (bytes.LongLength > _options.MaxUploadBytes)
        {
            findings.Add(DocumentHealthCodes.FileTooLarge, StructureIssueSeverity.Error, HealthStage.File,
                "Plik przekracza limit narzędzia",
                $"Plik ma {bytes.LongLength} B, limit diagnostyki to {_options.MaxUploadBytes} B. Analiza zawartości została pominięta.",
                remedy: "Zwiększ DocumentHealth:MaxUploadBytes w konfiguracji albo zmniejsz plik (obrazy).");
            detectedFormat = "too-large";
            return null;
        }

        var leadingOffset = IndexOf(bytes, ZipLocalHeader, Math.Min(bytes.Length, 64 * 1024));

        if (leadingOffset != 0 && !StartsWith(bytes, ZipEndOfCentralDirectory))
        {
            if (leadingOffset > 0)
            {
                findings.Add(DocumentHealthCodes.FileZipLeadingData, StructureIssueSeverity.Error, HealthStage.File,
                    "Śmieci przed początkiem archiwum",
                    $"Sygnatura ZIP (PK\\x03\\x04) pojawia się dopiero na bajcie {leadingOffset}. Word wymaga, żeby pakiet zaczynał się od pierwszego wpisu; czytniki oparte na katalogu centralnym mogą go otworzyć.",
                    wordImpact: WordOpenImpact.CannotOpen, pdfImpact: PdfConversionImpact.Blocking,
                    remedy: "Usuń nagłówek dodany przed archiwum (np. przez nieprawidłowe składanie odpowiedzi HTTP / e-mail) albo wyeksportuj dokument ponownie.");
                detectedFormat = "zip-with-prefix";
            }
            else
            {
                detectedFormat = DescribeNonZip(bytes, findings);
                return null;
            }
        }

        var package = OpenArchive(bytes, findings);

        if (package is null)
        {
            detectedFormat = detectedFormat == "unknown" ? "zip-unreadable" : detectedFormat;
            return null;
        }

        CheckTrailingData(bytes, findings);

        if (package.Contains("[Content_Types].xml"))
        {
            package.DetectedFormat = "ooxml";
        }
        else if (package.Contains("mimetype") && package.Contains("content.xml"))
        {
            package.DetectedFormat = "odt";
            findings.Add(DocumentHealthCodes.FileNotOoxmlPackage, StructureIssueSeverity.Error, HealthStage.File,
                "To dokument OpenDocument (ODT), nie DOCX",
                "Archiwum zawiera mimetype i content.xml — to pakiet OpenDocument. Word otwiera ODT tylko z rozszerzeniem .odt; konwertery oczekujące OOXML odrzucą plik.",
                wordImpact: WordOpenImpact.CannotOpen, pdfImpact: PdfConversionImpact.Blocking,
                remedy: "Zapisz dokument jako DOCX (Word / LibreOffice: Zapisz jako → Word 2007–365).");
        }
        else
        {
            package.DetectedFormat = detectedFormat == "zip-with-prefix" ? detectedFormat : "zip";
        }

        detectedFormat = package.DetectedFormat;

        return package;
    }

    private LenientPackage? OpenArchive(byte[] bytes, HealthFindingCollector findings)
    {
        ZipArchive archive;
        var stream = new MemoryStream(bytes, writable: false);

        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            stream.Dispose();
            var truncated = exception.Message.Contains("Central Directory", StringComparison.OrdinalIgnoreCase);
            findings.Add(DocumentHealthCodes.FileZipUnreadable, StructureIssueSeverity.Error, HealthStage.File,
                "Archiwum ZIP nie daje się odczytać",
                truncated
                    ? $"Brak rekordu końca katalogu centralnego ({exception.Message}). Plik jest najprawdopodobniej UCIĘTY — niepełny transfer albo zapis przerwany w trakcie."
                    : $"Nie udało się odczytać struktury archiwum: {exception.Message}",
                wordImpact: WordOpenImpact.CannotOpen, pdfImpact: PdfConversionImpact.Blocking,
                remedy: "Porównaj rozmiar z oryginałem u nadawcy i pobierz plik ponownie. Uszkodzenia kontenera nie da się naprawić w Wordzie.");
            return null;
        }

        using (archive)
        {
            var package = new LenientPackage();

            if (archive.Entries.Count == 0)
            {
                findings.Add(DocumentHealthCodes.FileZipEmpty, StructureIssueSeverity.Error, HealthStage.File,
                    "Archiwum jest puste", "Archiwum ZIP nie zawiera żadnych wpisów.",
                    wordImpact: WordOpenImpact.CannotOpen, pdfImpact: PdfConversionImpact.Blocking);
                return package;
            }

            if (archive.Entries.Count > _options.MaxZipEntries)
            {
                findings.Add(DocumentHealthCodes.ZipTooManyEntries, StructureIssueSeverity.Error, HealthStage.File,
                    "Za dużo wpisów w archiwum",
                    $"Archiwum ma {archive.Entries.Count} wpisów; limit diagnostyki to {_options.MaxZipEntries}. Typowy DOCX ma kilkanaście–kilkaset części.",
                    pdfImpact: PdfConversionImpact.Possible);
                return package;
            }

            long totalUncompressed = 0;
            var totalExceeded = false;

            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0)
                {
                    continue;
                }

                var rawPath = entry.FullName;
                var path = rawPath.Replace('\\', '/').TrimStart('/');
                var lenient = new LenientPackageEntry
                {
                    Path = path,
                    Length = entry.Length,
                    CompressedLength = entry.CompressedLength
                };

                if (IsUnsafePath(rawPath))
                {
                    findings.Add(DocumentHealthCodes.ZipEntryUnsafePath, StructureIssueSeverity.Error, HealthStage.File,
                        "Niedozwolona ścieżka wpisu",
                        $"Wpis '{rawPath}' ma ścieżkę bezwzględną, z '..' albo z odwrotnym ukośnikiem. Word odrzuca takie pakiety, a bramka uploadu aplikacji blokuje je jako zip-slip.",
                        path, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                        "Przepakuj dokument narzędziem, które zapisuje ścieżki względne z '/' (np. zapis w Wordzie).");
                    lenient.ReadError = "Niedozwolona ścieżka";
                    package.Add(lenient);
                    continue;
                }

                if (package.Contains(path))
                {
                    findings.Add(DocumentHealthCodes.ZipEntryDuplicate, StructureIssueSeverity.Error, HealthStage.File,
                        "Zduplikowany wpis archiwum",
                        $"Wpis '{path}' występuje w archiwum więcej niż raz. Pakiet OPC wymaga unikalnych nazw części — Word zgłasza uszkodzony plik.",
                        path, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                        "Przepakuj dokument; duplikaty powstają zwykle przy ręcznym składaniu ZIP-a albo błędnej bibliotece zapisu.");
                    lenient.ReadError = "Duplikat";
                    package.Entries.Add(lenient);
                    continue;
                }

                if (entry.Length > _options.MaxSingleEntryBytes)
                {
                    findings.Add(DocumentHealthCodes.ZipEntryTooLarge, StructureIssueSeverity.Warning, HealthStage.File,
                        "Wpis przekracza limit narzędzia",
                        $"Wpis '{path}' deklaruje {entry.Length} B (limit diagnostyki {_options.MaxSingleEntryBytes} B) — zawartość pominięta w analizie.",
                        path, pdfImpact: PdfConversionImpact.Possible);
                    lenient.ReadError = "Pominięty (limit rozmiaru)";
                    package.Add(lenient);
                    continue;
                }

                if (entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > _options.MaxCompressionRatio)
                {
                    findings.Add(DocumentHealthCodes.ZipCompressionRatioSuspicious, StructureIssueSeverity.Warning, HealthStage.File,
                        "Podejrzany współczynnik kompresji",
                        $"Wpis '{path}' rozpakowuje się {entry.Length / Math.Max(1, entry.CompressedLength)}× — bramka uploadu aplikacji odrzuca takie pliki (ochrona przed zip bomb).",
                        path, pdfImpact: PdfConversionImpact.Possible);
                    lenient.ReadError = "Pominięty (współczynnik kompresji)";
                    package.Add(lenient);
                    continue;
                }

                totalUncompressed += entry.Length;

                if (totalUncompressed > _options.MaxTotalUncompressedBytes)
                {
                    if (!totalExceeded)
                    {
                        totalExceeded = true;
                        findings.Add(DocumentHealthCodes.ZipTotalSizeExceeded, StructureIssueSeverity.Warning, HealthStage.File,
                            "Pakiet po rozpakowaniu przekracza limit narzędzia",
                            $"Łączny rozmiar wpisów przekroczył {_options.MaxTotalUncompressedBytes} B — dalsze wpisy nie zostały odczytane.",
                            path, pdfImpact: PdfConversionImpact.Possible);
                    }

                    lenient.ReadError = "Pominięty (limit łączny)";
                    package.Add(lenient);
                    continue;
                }

                ReadEntry(entry, lenient, findings);
                package.Add(lenient);
            }

            return package;
        }
    }

    private static void ReadEntry(ZipArchiveEntry entry, LenientPackageEntry lenient, HealthFindingCollector findings)
    {
        byte[] bytes;

        try
        {
            using var source = entry.Open();
            using var buffer = new MemoryStream(entry.Length > int.MaxValue ? 0 : (int)entry.Length);
            source.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            lenient.ReadError = exception.Message;
            findings.Add(DocumentHealthCodes.ZipEntryUnreadable, StructureIssueSeverity.Error, HealthStage.File,
                "Wpisu nie da się rozpakować",
                $"Wpis '{lenient.Path}' nie rozpakowuje się: {exception.Message}. Typowe przyczyny: uszkodzony strumień deflate (przekłamane bajty), nieobsługiwana metoda kompresji, szyfrowanie ZIP.",
                lenient.Path, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                "Pobierz plik ponownie ze źródła; jeśli powtarza się u nadawcy — dokument jest uszkodzony w miejscu powstania.");
            return;
        }

        if (bytes.LongLength != entry.Length)
        {
            findings.Add(DocumentHealthCodes.ZipEntrySizeMismatch, StructureIssueSeverity.Error, HealthStage.File,
                "Rozmiar wpisu nie zgadza się z deklaracją",
                $"Wpis '{lenient.Path}' deklaruje {entry.Length} B, rozpakowano {bytes.LongLength} B — strumień jest ucięty albo nagłówek przekłamany.",
                lenient.Path, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                "Pobierz plik ponownie ze źródła.");
        }

        var crc = Crc32.Compute(bytes);

        if (crc != entry.Crc32)
        {
            findings.Add(DocumentHealthCodes.ZipEntryCrcMismatch, StructureIssueSeverity.Error, HealthStage.File,
                "Niezgodna suma kontrolna CRC-32",
                $"Wpis '{lenient.Path}': CRC w nagłówku 0x{entry.Crc32:X8}, obliczona 0x{crc:X8}. Zawartość została przekłamana po zapisaniu (transfer, nośnik, edycja binarna). Word zgłasza taki plik jako uszkodzony.",
                lenient.Path, WordOpenImpact.CannotOpen, PdfConversionImpact.Blocking,
                "Pobierz plik ponownie ze źródła; porównaj skrót (SHA-256) z kopią nadawcy.");
        }

        lenient.Bytes = bytes;
    }

    private static void CheckTrailingData(byte[] bytes, HealthFindingCollector findings)
    {
        var searchStart = Math.Max(0, bytes.Length - (64 * 1024 + 22));
        var eocd = LastIndexOf(bytes, ZipEndOfCentralDirectory, searchStart);

        if (eocd < 0 || eocd + 22 > bytes.Length)
        {
            return;
        }

        var commentLength = bytes[eocd + 20] | (bytes[eocd + 21] << 8);
        var expectedEnd = eocd + 22 + commentLength;

        if (bytes.Length > expectedEnd)
        {
            findings.Add(DocumentHealthCodes.FileZipTrailingData, StructureIssueSeverity.Warning, HealthStage.File,
                "Dane za końcem archiwum",
                $"Za rekordem końca katalogu centralnego jest jeszcze {bytes.Length - expectedEnd} B. Word zwykle to toleruje; rygorystyczne czytniki (część usług konwertujących) odrzucają taki plik jako uszkodzony.",
                pdfImpact: PdfConversionImpact.Possible,
                remedy: "Zapisz dokument ponownie w Wordzie — zapis usuwa nadmiarowe bajty.");
        }
    }

    private static string DescribeNonZip(byte[] bytes, HealthFindingCollector findings)
    {
        if (StartsWith(bytes, CfbMagic))
        {
            return DescribeCompoundFile(bytes, findings);
        }

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        var trimmed = head.TrimStart('﻿', 'ï', '»', '¿', ' ', '\t', '\r', '\n');

        string format;
        string description;
        var wordImpact = WordOpenImpact.CannotOpen;

        if (trimmed.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            format = "rtf";
            description = "Plik jest dokumentem RTF, nie pakietem DOCX. Word otworzy go po rozszerzeniu (rozpoznaje treść), ale usługi konwertujące oczekujące OOXML odrzucą plik.";
            wordImpact = WordOpenImpact.None;
        }
        else if (trimmed.StartsWith("%PDF", StringComparison.Ordinal))
        {
            format = "pdf";
            description = "Plik jest dokumentem PDF z rozszerzeniem .docx. Konwersja DOCX→PDF nie ma sensu; Word otworzy go przez własny import PDF.";
            wordImpact = WordOpenImpact.None;
        }
        else if (trimmed.Contains("<w:wordDocument", StringComparison.OrdinalIgnoreCase))
        {
            format = "word-2003-xml";
            description = "Plik jest dokumentem Word 2003 XML (WordprocessingML 2003), nie pakietem OOXML. Word go otworzy; konwertery OOXML — nie.";
            wordImpact = WordOpenImpact.None;
        }
        else if (trimmed.StartsWith("MIME-Version", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
        {
            format = "mht";
            description = "Plik jest archiwum MHTML (Word „Strona sieci Web w jednym pliku”), nie pakietem DOCX.";
            wordImpact = WordOpenImpact.None;
        }
        else if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            format = trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ? "html" : "xml";
            description = format == "html"
                ? "Plik jest dokumentem HTML z rozszerzeniem .docx (częsty przypadek: strona błędu lub logowania zapisana zamiast pliku przez integrację). Word otworzy HTML; konwertery OOXML — nie."
                : "Plik jest tekstem XML, nie archiwum ZIP z pakietem OOXML.";
            wordImpact = format == "html" ? WordOpenImpact.None : WordOpenImpact.CannotOpen;
        }
        else if (bytes.Take(Math.Min(bytes.Length, 4096)).All(value => value == 0))
        {
            format = "zeros";
            description = "Plik składa się z samych bajtów zerowych — zapis nigdy nie zawierał treści (typowe dla przerwanego zapisu na dysk sieciowy albo błędu w źródle).";
        }
        else
        {
            format = "unknown";
            description = $"Nie rozpoznano sygnatury pliku (pierwsze bajty: {ToHex(bytes, 8)}). To nie jest archiwum ZIP, więc nie może być pakietem DOCX.";
        }

        findings.Add(DocumentHealthCodes.FileNotOoxmlPackage, StructureIssueSeverity.Error, HealthStage.File,
            $"Plik nie jest pakietem DOCX (rozpoznano: {format})", description,
            wordImpact: wordImpact, pdfImpact: PdfConversionImpact.Blocking,
            remedy: "Sprawdź, co naprawdę wysyła aplikacja źródłowa (rozszerzenie nie decyduje o formacie). Jeśli to HTML/strona błędu — integracja zapisała odpowiedź serwera zamiast dokumentu.");

        return format;
    }

    private static string DescribeCompoundFile(byte[] bytes, HealthFindingCollector findings)
    {
        bool encrypted = false, legacyDoc = false;

        try
        {
            using var root = RootStorage.Open(new MemoryStream(bytes, writable: false), StorageModeFlags.LeaveOpen);
            encrypted = root.ContainsEntry("EncryptionInfo") && root.ContainsEntry("EncryptedPackage");
            legacyDoc = root.ContainsEntry("WordDocument");
        }
        catch
        {
            // Kontener CFB nieczytelny — opis poniżej jako nierozpoznany OLE.
        }

        if (encrypted)
        {
            findings.Add(DocumentHealthCodes.FileEncryptedPackage, StructureIssueSeverity.Error, HealthStage.File,
                "Dokument jest zaszyfrowany hasłem",
                "Plik to kontener OLE z zaszyfrowanym pakietem OOXML (EncryptionInfo + EncryptedPackage). Word poprosi o hasło; usługi konwertujące bez hasła nie otworzą dokumentu.",
                wordImpact: WordOpenImpact.None, pdfImpact: PdfConversionImpact.Blocking,
                remedy: "Zdejmij hasło w Wordzie (Plik → Informacje → Chroń dokument → Szyfruj przy użyciu hasła → puste) albo wgraj plik z hasłem — normalizator wejścia edytora obsługuje szyfrowanie Agile.");
            return "encrypted-ooxml";
        }

        if (legacyDoc)
        {
            findings.Add(DocumentHealthCodes.FileLegacyBinaryDoc, StructureIssueSeverity.Error, HealthStage.File,
                "To binarny dokument Word 97–2003 (.doc), nie DOCX",
                "Plik to kontener OLE ze strumieniem WordDocument. Word otworzy go niezależnie od rozszerzenia; usługi konwertujące oparte na OOXML i bramka uploadu (.docx = ZIP) odrzucą go.",
                wordImpact: WordOpenImpact.None, pdfImpact: PdfConversionImpact.Likely,
                remedy: "Zapisz jako DOCX w Wordzie albo wyślij z rozszerzeniem .doc — aplikacja ma własny konwerter .doc (ADR-0074).");
            return "doc";
        }

        findings.Add(DocumentHealthCodes.FileNotOoxmlPackage, StructureIssueSeverity.Error, HealthStage.File,
            "Plik nie jest pakietem DOCX (rozpoznano: kontener OLE)",
            "Plik to kontener OLE Compound File bez strumienia WordDocument ani zaszyfrowanego pakietu — np. XLS/PPT/MSG albo uszkodzony .doc.",
            wordImpact: WordOpenImpact.CannotOpen, pdfImpact: PdfConversionImpact.Blocking,
            remedy: "Sprawdź u nadawcy, jaki plik został faktycznie wysłany.");
        return "cfb";
    }

    private static bool IsUnsafePath(string rawPath) =>
        rawPath.StartsWith('/') || rawPath.StartsWith('\\') || rawPath.Contains(':') || rawPath.Contains('\\') ||
        rawPath.Split('/').Any(segment => segment == "..");

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (bytes[index] != prefix[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(byte[] bytes, byte[] pattern, int limit)
    {
        var last = Math.Min(limit, bytes.Length) - pattern.Length;

        for (var index = 0; index <= last; index++)
        {
            if (Matches(bytes, pattern, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static int LastIndexOf(byte[] bytes, byte[] pattern, int start)
    {
        for (var index = bytes.Length - pattern.Length; index >= start; index--)
        {
            if (Matches(bytes, pattern, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Matches(byte[] bytes, byte[] pattern, int offset)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if (bytes[offset + index] != pattern[index])
            {
                return false;
            }
        }

        return true;
    }

    private static string ToHex(byte[] bytes, int count) =>
        string.Join(" ", bytes.Take(count).Select(value => value.ToString("X2")));
}
