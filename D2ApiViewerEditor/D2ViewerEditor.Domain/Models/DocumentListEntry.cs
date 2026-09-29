using D2ViewerEditor.Domain.Entities;

namespace D2ViewerEditor.Domain.Models;

public sealed record DocumentListEntry(
    Guid Id,
    string Name,
    string MimeType,
    DateTime CreatedAt,
    DocumentStatus Status,
    string? LastModifiedBy,
    Guid? ActiveVersionId,
    int? ActiveVersionNumber);
