using D2ViewerEditor.Application.Common.Security;
using D2ViewerEditor.Application.Features.Documents.Commands.FinishAndSendDocument;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace D2ViewerEditor.Application.UnitTests.Features.Documents.Commands;

[TestFixture]
public class FinishAndSendDocumentCommandHandlerTests
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private Mock<IDocumentRepository> _documentRepo = null!;
    private Mock<IDocumentDeliveryRepository> _deliveryRepo = null!;
    private Mock<IDocumentStorageService> _storage = null!;
    private Mock<IDeliverySender> _sender = null!;
    private Mock<ICurrentUserProvider> _currentUser = null!;
    private Mock<IReturnUrlValidator> _returnUrlValidator = null!;
    private FinishAndSendDocumentCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _documentRepo = new Mock<IDocumentRepository>();
        _deliveryRepo = new Mock<IDocumentDeliveryRepository>();
        _storage = new Mock<IDocumentStorageService>();
        _sender = new Mock<IDeliverySender>();
        _currentUser = new Mock<ICurrentUserProvider>();
        _currentUser.SetupGet(u => u.CorporateKey).Returns("ACME-42");
        _returnUrlValidator = new Mock<IReturnUrlValidator>();
        _returnUrlValidator.Setup(v => v.Validate(It.IsAny<string?>()))
            .Returns((string? url) => string.IsNullOrWhiteSpace(url)
                ? ReturnUrlValidationResult.Failure(ReturnUrlRejectionCode.Empty, "Callback URL jest wymagany.")
                : ReturnUrlValidationResult.Success(url!));
        _handler = new FinishAndSendDocumentCommandHandler(
            _documentRepo.Object,
            _deliveryRepo.Object,
            _storage.Object,
            _sender.Object,
            _currentUser.Object,
            _returnUrlValidator.Object);
    }

    private static Document BuildDocumentWithEditableVersion(out Guid versionId, string? metadata)
    {
        var document = new Document(Guid.NewGuid(), "doc.docx", DocxMime, "User", metadata);
        document.AddVersion(Guid.NewGuid(), "documents/v1", 100, "User");
        var v2 = document.AddVersion(Guid.NewGuid(), "documents/v2", 100, "User");
        versionId = v2.Id;
        return document;
    }

    [Test]
    public async Task Handle_WhenFirstAttemptSucceeds_ShouldMarkSentAndReportDelivered()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId,
            "{\"returnUrl\":\"https://example.com/cb\",\"classification\":\"C2\"}");

        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        _deliveryRepo.Setup(r => r.GetActiveByDocumentIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDelivery?)null);
        _currentUser.SetupGet(u => u.CorporateKey).Returns("ACME-42");
        _sender.Setup(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeliveryResult.Succeeded());

        DocumentDelivery? created = null;
        _deliveryRepo.Setup(r => r.AddAsync(It.IsAny<DocumentDelivery>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentDelivery, CancellationToken>((d, _) => created = d);

        var command = new FinishAndSendDocumentCommand(document.Id, versionId, new byte[] { 1, 2, 3 }, "User");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Delivered.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(DeliveryStatus.Sent));
        result.Value.DocumentStatus.Should().Be(nameof(DocumentStatus.Sent));
        document.Status.Should().Be(DocumentStatus.Sent);

        created.Should().NotBeNull();
        created!.CorporateKey.Should().Be("ACME-42");
        _storage.Verify(s => s.UploadAsync(versionId, It.IsAny<byte[]>(), DocxMime, It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(s => s.UploadRawAsync(It.Is<string>(n => n.StartsWith("deliveries/")), It.IsAny<byte[]>(), DocxMime, It.IsAny<CancellationToken>()), Times.Once);
        _sender.Verify(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Handle_NewDelivery_IsPersistedAsSending_NeverClaimablePending()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId,
            "{\"returnUrl\":\"https://example.com/cb\"}");
        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        _deliveryRepo.Setup(r => r.GetActiveByDocumentIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDelivery?)null);

        DocumentDelivery? created = null;
        _deliveryRepo.Setup(r => r.AddAsync(It.IsAny<DocumentDelivery>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentDelivery, CancellationToken>((d, _) => created = d);

        var statusAtEachSave = new List<DeliveryStatus>();
        _deliveryRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => { if (created is not null) statusAtEachSave.Add(created.Status); })
            .ReturnsAsync(1);

        _sender.Setup(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeliveryResult.Succeeded());

        var result = await _handler.Handle(
            new FinishAndSendDocumentCommand(document.Id, versionId, new byte[] { 1 }, "User"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        statusAtEachSave.Should().NotBeEmpty();
        statusAtEachSave.Should().NotContain(DeliveryStatus.Pending);
        statusAtEachSave[0].Should().Be(DeliveryStatus.Sending);
    }

    [Test]
    public async Task Handle_WhenFirstAttemptFails_ShouldMarkDeliveryFailedAndHoldJob()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId,
            "{\"returnUrl\":\"https://example.com/cb\"}");

        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        _deliveryRepo.Setup(r => r.GetActiveByDocumentIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDelivery?)null);

        DocumentDelivery? created = null;
        _deliveryRepo.Setup(r => r.AddAsync(It.IsAny<DocumentDelivery>(), It.IsAny<CancellationToken>()))
            .Callback<DocumentDelivery, CancellationToken>((d, _) => created = d);
        _sender.Setup(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeliveryResult.Retryable("recipient down"));

        var command = new FinishAndSendDocumentCommand(document.Id, versionId, new byte[] { 1 }, "User");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Delivered.Should().BeFalse();
        result.Value.DocumentStatus.Should().Be(nameof(DocumentStatus.DeliveryFailed));
        document.Status.Should().Be(DocumentStatus.DeliveryFailed);

        created!.Status.Should().Be(DeliveryStatus.RetryScheduled);
        created.NextAttemptAt.Should().Be(created.DeadlineAt);
    }

    [Test]
    public async Task Handle_WhenSenderThrows_ShouldTreatAsFailedAndHoldJob()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId,
            "{\"returnUrl\":\"https://example.com/cb\"}");

        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        _deliveryRepo.Setup(r => r.GetActiveByDocumentIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDelivery?)null);
        _sender.Setup(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network"));

        var command = new FinishAndSendDocumentCommand(document.Id, versionId, new byte[] { 1 }, "User");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Delivered.Should().BeFalse();
        document.Status.Should().Be(DocumentStatus.DeliveryFailed);
    }

    [Test]
    public async Task Handle_WhenAttemptAlreadyInProgress_ShouldReturnInProgressWithoutResending()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId,
            "{\"returnUrl\":\"https://example.com/cb\"}");
        var inFlight = DocumentDelivery.Create(Guid.NewGuid(), document.Id, versionId,
            "deliveries/x", 10, "h", "https://example.com/cb", "User", Guid.NewGuid(), TimeSpan.FromHours(24));
        inFlight.BeginInlineAttempt();

        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        _deliveryRepo.Setup(r => r.GetActiveByDocumentIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(inFlight);

        var command = new FinishAndSendDocumentCommand(document.Id, versionId, new byte[] { 1 }, "User");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.DeliveryId.Should().Be(inFlight.Id);
        result.Value.Delivered.Should().BeFalse();
        _deliveryRepo.Verify(r => r.AddAsync(It.IsAny<DocumentDelivery>(), It.IsAny<CancellationToken>()), Times.Never);
        _storage.Verify(s => s.UploadRawAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _sender.Verify(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Handle_WhenSendingCancelled_ShouldPropagateCancellation_NotMarkFailed()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId,
            "{\"returnUrl\":\"https://example.com/cb\"}");

        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        _deliveryRepo.Setup(r => r.GetActiveByDocumentIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDelivery?)null);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _sender.Setup(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var command = new FinishAndSendDocumentCommand(document.Id, versionId, new byte[] { 1 }, "User");
        var act = async () => await _handler.Handle(command, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        document.Status.Should().Be(DocumentStatus.Sending);
    }

    [Test]
    public async Task Handle_WhenReusingHeldJob_ShouldRefreshSnapshotToNewContent()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId,
            "{\"returnUrl\":\"https://example.com/cb\"}");
        var oldContent = new byte[] { 1, 1, 1 };
        var held = DocumentDelivery.Create(
            id: Guid.NewGuid(), documentId: document.Id, sourceVersionId: versionId,
            snapshotObjectName: "deliveries/held", snapshotSizeBytes: oldContent.Length,
            snapshotSha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(oldContent)),
            recipientUrl: "https://example.com/cb", createdBy: "User", correlationId: Guid.NewGuid(),
            retentionWindow: TimeSpan.FromHours(24));
        held.BeginInlineAttempt();
        held.HoldAfterFailedInlineAttempt("timeout");

        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        _deliveryRepo.Setup(r => r.GetActiveByDocumentIdAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(held);
        _deliveryRepo.Setup(r => r.GetByIdAsync(held.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(held);
        DeliveryDispatch? sent = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<DeliveryDispatch>(), It.IsAny<CancellationToken>()))
            .Callback<DeliveryDispatch, CancellationToken>((d, _) => sent = d)
            .ReturnsAsync(DeliveryResult.Succeeded());

        var newContent = new byte[] { 9, 9, 9, 9 };
        var result = await _handler.Handle(
            new FinishAndSendDocumentCommand(document.Id, versionId, newContent, "User"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(newContent));
        _storage.Verify(s => s.UploadRawAsync("deliveries/held", newContent, DocxMime, It.IsAny<CancellationToken>()), Times.Once);
        held.SnapshotSha256.Should().Be(expectedSha);
        held.SnapshotSizeBytes.Should().Be(newContent.Length);
        sent!.Sha256.Should().Be(expectedSha, "próba inline i snapshot dla workera to te same bajty");
        _deliveryRepo.Verify(r => r.AddAsync(It.IsAny<DocumentDelivery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Handle_WithoutReturnUrl_ShouldFail()
    {
        var document = BuildDocumentWithEditableVersion(out var versionId, metadata: null);
        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);

        var command = new FinishAndSendDocumentCommand(document.Id, versionId, new byte[] { 1 }, "User");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("returnUrl");
    }

    [Test]
    public async Task Handle_WhenDocumentMissing_ShouldReturnNotFound()
    {
        _documentRepo.Setup(r => r.GetByIdWithVersionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Document?)null);

        var command = new FinishAndSendDocumentCommand(Guid.NewGuid(), Guid.NewGuid(), new byte[] { 1 }, "User");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsNotFound.Should().BeTrue();
    }
}
