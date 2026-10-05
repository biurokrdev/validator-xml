namespace Mass.AddressWindow.Domain.Inspection;

public enum FindingSeverity
{
    Info,

    Warning,

    Error,
}

/// <summary>Pojedyncze zgłoszenie ze sprawdzenia: stały kod i komunikat dla użytkownika.</summary>
public sealed record Finding(string Code, FindingSeverity Severity, string Message)
{
    public bool IsError => Severity == FindingSeverity.Error;
}
