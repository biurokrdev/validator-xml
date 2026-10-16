using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Documents;

namespace Mass.AddressWindow.Api.External;

public sealed class OrderRejectedException(string message) : Exception(message);

public sealed class ExternalSourceException(string message, Exception? inner = null) : Exception(message, inner);

// Recipient address as it should appear in the letter: 2–6 non-empty lines.
public sealed class Recipient
{
    public const int MinLines = 2;
    public const int MaxLines = 6;

    private Recipient(IReadOnlyList<string> lines)
    {
        Lines = lines;
    }

    public IReadOnlyList<string> Lines { get; }

    public static Recipient Create(IEnumerable<string>? lines)
    {
        var cleaned = (lines ?? [])
            .Select(l => string.Join(' ', (l ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0)
            .ToList();

        if (cleaned.Count < MinLines || cleaned.Count > MaxLines)
        {
            throw new OrderRejectedException($"Adres odbiorcy musi mieć od {MinLines} do {MaxLines} wierszy, podano {cleaned.Count}.");
        }

        return new Recipient(cleaned);
    }
}

// What the external application is asked to produce. The registered number is only meaningful for the
// two-window envelope; for a single window it is dropped so stale form data cannot influence the result.
public sealed class LetterOrder
{
    private LetterOrder(EnvelopeType envelope, Recipient recipient, string? registeredNumber)
    {
        Envelope = envelope;
        Recipient = recipient;
        RegisteredNumber = registeredNumber;
    }

    public EnvelopeType Envelope { get; }

    public Recipient Recipient { get; }

    public string? RegisteredNumber { get; }

    // Passed through to the external application as given; the API attaches no meaning to it.
    public string? Source { get; init; }

    public static LetterOrder Create(EnvelopeType envelope, Recipient recipient, string? registeredNumber, string? source = null)
    {
        if (!Enum.IsDefined(envelope))
        {
            throw new OrderRejectedException($"Nieznany rodzaj koperty: {envelope}.");
        }

        var number = string.IsNullOrWhiteSpace(registeredNumber) ? null : registeredNumber.Trim();
        if (envelope == EnvelopeType.DoubleWindow && number is null)
        {
            throw new OrderRejectedException("Koperta z dwoma okienkami wymaga numeru R listu poleconego.");
        }

        return new LetterOrder(envelope, recipient, envelope == EnvelopeType.DoubleWindow ? number : null)
        {
            Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
        };
    }
}

// A form the external application already holds; the letter is fetched by its identifier alone.
public sealed class FormOrder
{
    private FormOrder(Guid formId, EnvelopeType envelope)
    {
        FormId = formId;
        Envelope = envelope;
    }

    public Guid FormId { get; }

    public EnvelopeType Envelope { get; }

    public static FormOrder Create(EnvelopeType envelope, string? formId)
    {
        if (!Enum.IsDefined(envelope))
        {
            throw new OrderRejectedException($"Nieznany rodzaj koperty: {envelope}.");
        }

        if (!Guid.TryParse(formId?.Trim(), out var id) || id == Guid.Empty)
        {
            throw new OrderRejectedException("Identyfikator formularza musi być poprawnym GUID.");
        }

        return new FormOrder(id, envelope);
    }
}

// The real integration point: the external application that produces letters. Today only a mock exists.
public interface IExternalLetterSource
{
    Task<LetterDocument> FetchAsync(LetterOrder order, CancellationToken cancellationToken);

    Task<LetterDocument> FetchFormAsync(FormOrder form, CancellationToken cancellationToken);
}
