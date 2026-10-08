namespace TableTable;

/// <summary>Wynik wypełnienia jednej tabeli.</summary>
public sealed class RecordTableResult
{
    /// <summary>Liczba rekordów, dla których sklonowano szablon.</summary>
    public int RecordCount { get; init; }

    /// <summary>Czy tabela została usunięta (brak rekordów i <see cref="EmptyTableBehavior.RemoveTable"/>).</summary>
    public bool TableRemoved { get; init; }

    /// <summary>Znaczniki (np. <c>&lt;%x%&gt;</c>), dla których nie znaleziono wartości.</summary>
    public IReadOnlyList<string> UnresolvedPlaceholders { get; init; } = [];

    /// <summary>Ostrzeżenia (błędne ścieżki, brak formantu, wyjątki silnika wyrażeń); zgłaszane też do loggera.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
