namespace Mass.AddressWindow.Rules;

/// <summary>Grafika na pierwszej stronie: kandydat na nalepkę R.</summary>
internal sealed record ImageCandidate(RectangleMm Bounds, DocumentPartKind Part, string? Name, bool Estimated);
