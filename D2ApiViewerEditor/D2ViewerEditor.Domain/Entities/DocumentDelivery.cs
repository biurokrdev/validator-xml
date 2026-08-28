using D2ViewerEditor.Domain.Interfaces;

namespace D2ViewerEditor.Domain.Entities;

public class DocumentDelivery
{
    private const int MaxErrorLength = 4000;

    private DocumentDelivery() { }

    public static DocumentDelivery Create(
        Guid id,
        Guid documentId,
        Guid sourceVersionId,
        string snapshotObjectName,
        long snapshotSizeBytes,
        string snapshotSha256,
        string recipientUrl,
        string createdBy,
        Guid correlationId,
        TimeSpan retentionWindow,
        string? corporateKey = null)
    {
        if (string.IsNullOrWhiteSpace(snapshotObjectName))
            throw new ArgumentException("Snapshot object name nie może być pusty", nameof(snapshotObjectName));

        if (string.IsNullOrWhiteSpace(snapshotSha256))
            throw new ArgumentException("Snapshot hash nie może być pusty", nameof(snapshotSha256));

        if (!IsValidRecipientUrl(recipientUrl))
            throw new ArgumentException("recipientUrl musi być absolutnym adresem http(s)", nameof(recipientUrl));

        if (retentionWindow <= TimeSpan.Zero)
            throw new ArgumentException("Okno ponawiania musi być dodatnie", nameof(retentionWindow));

        var now = DateTime.UtcNow;

        return new DocumentDelivery
        {
            Id = id,
            DocumentId = documentId,
            SourceVersionId = sourceVersionId,
            SnapshotObjectName = snapshotObjectName,
            SnapshotSizeBytes = snapshotSizeBytes,
            SnapshotSha256 = snapshotSha256,
            RecipientUrl = recipientUrl,
            Status = DeliveryStatus.Pending,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
            DeadlineAt = now.Add(retentionWindow),
            CorrelationId = correlationId,
            CreatedBy = createdBy,
            CorporateKey = string.IsNullOrWhiteSpace(corporateKey) ? null : corporateKey.Trim()
        };
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }

    public Guid SourceVersionId { get; private set; }

    public string SnapshotObjectName { get; private set; } = string.Empty;
    public long SnapshotSizeBytes { get; private set; }
    public string SnapshotSha256 { get; private set; } = string.Empty;

    public string RecipientUrl { get; private set; } = string.Empty;
    public DeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? FirstAttemptAt { get; private set; }
    public DateTime? LastAttemptAt { get; private set; }
    public DateTime NextAttemptAt { get; private set; }

    public DateTime DeadlineAt { get; private set; }

    public DateTime? LockedUntil { get; private set; }
    public string? LockedBy { get; private set; }

    public string? LastError { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;

    public string? CorporateKey { get; private set; }

    public bool IsTerminal =>
        Status is DeliveryStatus.Sent or DeliveryStatus.FailedPermanently
               or DeliveryStatus.DeadLettered or DeliveryStatus.Cancelled;

    public void BeginInlineAttempt()
    {
        if (Status is not (DeliveryStatus.Pending or DeliveryStatus.RetryScheduled))
            throw new InvalidOperationException(
                "Pierwszą próbę inline można wykonać tylko dla zadania oczekującego lub zaplanowanego");

        var now = DateTime.UtcNow;
        Status = DeliveryStatus.Sending;
        AttemptCount++;
        FirstAttemptAt ??= now;
        LastAttemptAt = now;
        ClearLease();
        UpdatedAt = now;
    }

    public void HoldAfterFailedInlineAttempt(string error)
    {
        Status = DeliveryStatus.RetryScheduled;
        NextAttemptAt = DeadlineAt;
        ClearLease();
        LastError = Truncate(error);
        UpdatedAt = DateTime.UtcNow;
    }

    public void RefreshSnapshot(long snapshotSizeBytes, string snapshotSha256)
    {
        if (IsTerminal || Status == DeliveryStatus.Sending)
            throw new InvalidOperationException(
                "Snapshot można odświeżyć tylko dla zadania oczekującego lub wstrzymanego po nieudanej próbie");
        if (string.IsNullOrWhiteSpace(snapshotSha256))
            throw new ArgumentException("Snapshot hash nie może być pusty", nameof(snapshotSha256));
        if (snapshotSizeBytes <= 0)
            throw new ArgumentException("Snapshot musi mieć dodatni rozmiar", nameof(snapshotSizeBytes));

        SnapshotSizeBytes = snapshotSizeBytes;
        SnapshotSha256 = snapshotSha256;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkSent()
    {
        Status = DeliveryStatus.Sent;
        ClearLease();
        LastError = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkPermanentFailure(string error)
    {
        Status = DeliveryStatus.FailedPermanently;
        ClearLease();
        LastError = Truncate(error);
        UpdatedAt = DateTime.UtcNow;
    }

    public bool ScheduleRetryOrDeadLetter(string error, IBackoffStrategy backoff)
    {
        var now = DateTime.UtcNow;
        var next = backoff.NextAttempt(Math.Max(1, AttemptCount), now);

        if (next > DeadlineAt)
        {
            Status = DeliveryStatus.DeadLettered;
        }
        else
        {
            Status = DeliveryStatus.RetryScheduled;
            NextAttemptAt = next;
        }

        ClearLease();
        LastError = Truncate(error);
        UpdatedAt = now;
        return Status == DeliveryStatus.RetryScheduled;
    }

    public void Requeue(TimeSpan retentionWindow)
    {
        if (Status is not (DeliveryStatus.DeadLettered or DeliveryStatus.FailedPermanently
                        or DeliveryStatus.RetryScheduled or DeliveryStatus.Cancelled))
            throw new InvalidOperationException(
                "Wznowienie dotyczy zadań nieudanych, zaplanowanych lub anulowanych (nie: wysłane/w toku/oczekujące)");

        var now = DateTime.UtcNow;
        Status = DeliveryStatus.Pending;
        NextAttemptAt = now;
        DeadlineAt = now.Add(retentionWindow);
        ClearLease();
        LastError = null;
        UpdatedAt = now;
    }

    public void Cancel()
    {
        if (Status is not (DeliveryStatus.Pending or DeliveryStatus.RetryScheduled))
            throw new InvalidOperationException(
                "Anulować można tylko zadanie oczekujące lub zaplanowane (nie: w toku/wysłane/zakończone)");

        Status = DeliveryStatus.Cancelled;
        ClearLease();
        LastError = "Anulowano ręcznie (administrator).";
        UpdatedAt = DateTime.UtcNow;
    }

    public void CancelByUser()
    {
        if (IsTerminal)
            throw new InvalidOperationException(
                "Zadania w stanie końcowym nie można anulować");

        if (Status is DeliveryStatus.Sending && LockedBy is not null)
            throw new InvalidOperationException(
                "Zadanie jest wysyłane przez proces w tle — nie można go przerwać");

        Status = DeliveryStatus.Cancelled;
        ClearLease();
        LastError = "Anulowano przez użytkownika.";
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateRecipientUrl(string url)
    {
        if (Status is DeliveryStatus.Sent or DeliveryStatus.Sending)
            throw new InvalidOperationException(
                "Adresu odbiorcy nie można zmienić dla zadania wysłanego ani w trakcie wysyłki");

        if (!IsValidRecipientUrl(url))
            throw new ArgumentException("recipientUrl musi być absolutnym adresem http(s)", nameof(url));

        RecipientUrl = url;
        UpdatedAt = DateTime.UtcNow;
    }

    public static bool IsValidRecipientUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void ClearLease()
    {
        LockedUntil = null;
        LockedBy = null;
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length > MaxErrorLength ? s[..MaxErrorLength] : s);
}
