using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

/// <summary>
/// Diagnostyka „Kondycja dokumentu": czy plik DOCX jest uszkodzony (jako plik, pakiet, XML,
/// struktura oczekiwana przez Worda) i co może blokować konwersję do PDF.
///
/// W odróżnieniu od <see cref="IDocumentStructureInspector"/> (eksploracja: drzewo elementów,
/// surowy XML) ta usługa odpowiada na dwa pytania werdyktem i listą ustaleń z oceną wpływu
/// (Word: otworzy / naprawi / nie otworzy; PDF: bez wpływu / ryzyko / prawdopodobny błąd / blokada).
/// Nigdy nie rzuca dla uszkodzonego wejścia — uszkodzenie JEST wynikiem.
/// </summary>
public interface IDocumentHealthInspector
{
    /// <param name="documentBytes">Bajty pliku — dowolne; format rozpoznawany po sygnaturze.</param>
    /// <param name="fileName">Nazwa pliku (do raportu i nazwania wyniku konwersji).</param>
    /// <param name="includeConversionProbes">Czy uruchamiać próby przetworzenia (SDK, edytor, konwerter PDF, LibreOffice).</param>
    Task<DocumentHealthReport> InspectAsync(
        byte[] documentBytes,
        string fileName,
        bool includeConversionProbes,
        CancellationToken cancellationToken);
}
