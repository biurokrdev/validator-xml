using D2ViewerEditor.Domain.Entities;

namespace D2ViewerEditor.Domain.Models;

/// <summary>
/// Wiersz listy dokumentów dla panelu administratora — WYŁĄCZNIE kolumny potrzebne gridowi.
/// Świadomie bez <c>metadata</c> (JSON aplikacji źródłowej) i bez kolekcji wersji: te dane są
/// doładowywane osobno, gdy administrator rozwinie konkretny dokument.
/// Aktywna wersja jest wyznaczana w bazie (podzapytanie po <c>is_active</c>), a nie przez
/// materializację encji.
/// </summary>
public sealed record DocumentListEntry(
    Guid Id,
    string Name,
    string MimeType,
    DateTime CreatedAt,
    DocumentStatus Status,
    string? LastModifiedBy,
    Guid? ActiveVersionId,
    int? ActiveVersionNumber);
