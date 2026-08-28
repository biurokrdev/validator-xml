using System.Security.Cryptography;
using D2ViewerEditor.Application.Features.Documents.Queries.GetDeliverySnapshotContent;
using D2ViewerEditor.Domain.Entities;
using D2ViewerEditor.Domain.Interfaces;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace D2ViewerEditor.Application.UnitTests.Features.Documents.Queries;

[TestFixture]
public class GetDeliverySnapshotContentQueryHandlerTests
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private Mock<IDocumentDeliveryRepository> _deliveryRepo = null!;
    private Mock<IDocumentRepository> _documentRepo = null!;
    private Mock<IDocumentStorageService> _storage = null!;
    private GetDeliverySnapshotContentQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _deliveryRepo = new Mock<IDocumentDeliveryRepository>();
        _documentRepo = new Mock<IDocumentRepository>();
        _storage = new Mock<IDocumentStorageService>();
        _handler = new GetDeliverySnapshotContentQueryHandler(_deliveryRepo.Object, _documentRepo.Object, _storage.Object);
    }

    private static (Document doc, DocumentDelivery delivery, byte[] bytes) Arrange(string sha256Override = "")
    {
        var doc = new Document(Guid.NewGuid(), "pismo.docx", DocxMime, "User", "{\"returnUrl\":\"https://x/cb\"}");
        var bytes = new byte[] { 10, 20, 30, 40 };
        var sha = sha256Override.Length > 0 ? sha256Override : Convert.ToHexString(SHA256.HashData(bytes));
        var delivery = DocumentDelivery.Create(
            id: Guid.NewGuid(), documentId: doc.Id, sourceVersionId: Guid.NewGuid(),
            snapshotObjectName: "deliveries/snap", snapshotSizeBytes: bytes.Length, snapshotSha256: sha,
            recipientUrl: "https://x/cb", createdBy: "User", correlationId: Guid.NewGuid(),
            retentionWindow: TimeSpan.FromHours(24));
        return (doc, delivery, bytes);
    }

    [Test]
    public async Task Handle_ReturnsSnapshotBytes_WithDocumentNameAndVerifiedHash()
    {
        var (doc, delivery, bytes) = Arrange();
        _deliveryRepo.Setup(r => r.GetByIdAsync(delivery.Id, It.IsAny<CancellationToken>())).ReturnsAsync(delivery);
        _documentRepo.Setup(r => r.GetByIdAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _storage.Setup(s => s.DownloadAsync("deliveries/snap", It.IsAny<CancellationToken>())).ReturnsAsync(bytes);

        var result = await _handler.Handle(new GetDeliverySnapshotContentQuery(delivery.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Content.Should().Equal(bytes);
        result.Value.MimeType.Should().Be(DocxMime);
        result.Value.Sha256.Should().Be(delivery.SnapshotSha256);
        result.Value.FileName.Should().StartWith("pismo_wyslany_").And.EndWith(".docx");
        _storage.Verify(s => s.DownloadAsync("deliveries/snap", It.IsAny<CancellationToken>()), Times.Once);
        _storage.Verify(s => s.DownloadAsync(It.Is<string>(p => p.StartsWith("documents/")), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Handle_WhenStoredHashDiffers_FailsInsteadOfReturningInconsistentFile()
    {
        var (doc, delivery, bytes) = Arrange(sha256Override: "DEADBEEF");
        _deliveryRepo.Setup(r => r.GetByIdAsync(delivery.Id, It.IsAny<CancellationToken>())).ReturnsAsync(delivery);
        _documentRepo.Setup(r => r.GetByIdAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _storage.Setup(s => s.DownloadAsync("deliveries/snap", It.IsAny<CancellationToken>())).ReturnsAsync(bytes);

        var result = await _handler.Handle(new GetDeliverySnapshotContentQuery(delivery.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsNotFound.Should().BeFalse();
        result.Error.Should().Contain("SHA-256");
    }

    [Test]
    public async Task Handle_WhenDeliveryMissing_ReturnsNotFound()
    {
        _deliveryRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDelivery?)null);

        var result = await _handler.Handle(new GetDeliverySnapshotContentQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsNotFound.Should().BeTrue();
    }

    [Test]
    public async Task Handle_WhenStorageThrows_ReturnsFailureNotException()
    {
        var (doc, delivery, _) = Arrange();
        _deliveryRepo.Setup(r => r.GetByIdAsync(delivery.Id, It.IsAny<CancellationToken>())).ReturnsAsync(delivery);
        _documentRepo.Setup(r => r.GetByIdAsync(doc.Id, It.IsAny<CancellationToken>())).ReturnsAsync(doc);
        _storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("bucket down"));

        var result = await _handler.Handle(new GetDeliverySnapshotContentQuery(delivery.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("bucket down");
    }
}
