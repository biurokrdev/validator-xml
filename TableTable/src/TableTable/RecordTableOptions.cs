using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace TableTable;

/// <summary>Ustawienia wspólne dla wszystkich tabel.</summary>
public sealed class RecordTableOptions
{
    /// <summary>Początek znacznika; domyślnie <c>&lt;%</c>.</summary>
    public string TagOpen { get; set; } = "<%";

    /// <summary>Koniec znacznika; domyślnie <c>%&gt;</c>.</summary>
    public string TagClose { get; set; } = "%>";

    /// <summary>Kultura liczb i dat; domyślnie <c>pl-PL</c>.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>Strefa prezentacji dat UTC/lokalnych; domyślnie polska (na Linuksie automatycznie <c>Europe/Warsaw</c>). <see langword="null"/> = bez konwersji.</summary>
    public string? TimeZoneId { get; set; } = "Central European Standard Time";

    /// <summary>Domyślny format daty z godziną.</summary>
    public string DateTimeFormat { get; set; } = "dd.MM.yyyy HH:mm";

    /// <summary>Domyślny format daty bez godziny (czas 00:00:00).</summary>
    public string DateFormat { get; set; } = "dd.MM.yyyy";

    /// <summary>Tekst dla <see langword="true"/>.</summary>
    public string TrueText { get; set; } = "Tak";

    /// <summary>Tekst dla <see langword="false"/>.</summary>
    public string FalseText { get; set; } = "Nie";

    /// <summary>Silnik wyrażeń dla <c>replacement-property-expression</c> / <c>visible-property-expression</c>; bez niego takie pola dają ostrzeżenie i pusty tekst.</summary>
    public IExpressionEvaluator? ExpressionEvaluator { get; set; }

    /// <summary>Logger; domyślnie <see cref="NullLogger.Instance"/>.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;
}

/// <summary>Obliczanie wyrażeń zewnętrznym silnikiem (np. tym z workerów).</summary>
public interface IExpressionEvaluator
{
    /// <summary>Wartość wyrażenia dla bieżącego zasięgu; <see langword="null"/> = pusty tekst (lub <c>null-text</c>).</summary>
    object? Evaluate(string expression, ExpressionScope scope);
}

/// <summary>Zasięg wyrażenia: cały model, bieżący rekord/element i jego numer od 1.</summary>
public sealed record ExpressionScope(JToken Root, JToken Current, int Index);

/// <summary>Adapter <see cref="IExpressionEvaluator"/> na delegat.</summary>
public sealed class DelegateExpressionEvaluator(Func<string, ExpressionScope, object?> evaluate) : IExpressionEvaluator
{
    /// <inheritdoc />
    public object? Evaluate(string expression, ExpressionScope scope) => evaluate(expression, scope);
}
