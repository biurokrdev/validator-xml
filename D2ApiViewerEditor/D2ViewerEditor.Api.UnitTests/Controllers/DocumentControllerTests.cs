using D2ViewerEditor.Api.Controllers;
using D2ViewerEditor.Application.Common;
using D2ViewerEditor.Application.Features.Documents.Commands.GeneratePdfPreview;
using D2ViewerEditor.Application.Features.Documents.Commands.SaveDocument;
using D2ViewerEditor.Application.Features.Documents.Commands.UploadImage;
using D2ViewerEditor.Application.Features.Documents.Queries.GetNewDocument;
using D2ViewerEditor.Application.Features.Documents.Queries.GetTemplate;
using D2ViewerEditor.Application.Features.Documents.Queries.GetTemplates;
using D2ViewerEditor.Application.Features.Documents.Queries.OpenDocument;
using D2ViewerEditor.Domain.Common;
using D2ViewerEditor.Domain.Models;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace D2ViewerEditor.Api.UnitTests.Controllers;

[TestFixture]
public class DocumentControllerTests
{
    private IMediator _mediator;
    private DocumentController _controller;
    private ServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();

        var services = new ServiceCollection();
        services.AddSingleton(_mediator);
        _serviceProvider = services.BuildServiceProvider();

        _controller = new DocumentController();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = _serviceProvider }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
    }

    [Test]
    public async Task NewDocument_ShouldReturnOkWithDocumentContent()
    {
        var content = new DocumentContent
        {
            Html = "<p>&nbsp;</p>",
            Metadata = new DocumentMetadata { Title = "Nowy dokument" }
        };
        _mediator.Send(Arg.Any<GetNewDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<DocumentContent>.Success(content));

        var result = await _controller.NewDocument();

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        ok.Value.Should().Be(content);
    }

    [Test]
    public async Task GetTemplates_ShouldReturnOkWithList()
    {
        var templates = new List<TemplateDto>
        {
            new("blank", "Pusty", "Opis"),
            new("letter", "List", "Opis")
        };
        _mediator.Send(Arg.Any<GetTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<TemplateDto>>.Success(templates));

        var result = await _controller.GetTemplates();

        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;
        (ok.Value as List<TemplateDto>)!.Should().HaveCount(2);
    }

    [Test]
    public async Task GetTemplate_WithValidId_ShouldReturnOkWithContent()
    {
        var content = new DocumentContent { Html = "<p>List</p>", Metadata = new DocumentMetadata() };
        _mediator.Send(Arg.Any<GetTemplateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<DocumentContent>.Success(content));

        var result = await _controller.GetTemplate("letter");

        result.Should().BeOfType<OkObjectResult>();
    }

    [Test]
    public async Task GetTemplate_WhenFails_ShouldReturnBadRequest()
    {
        _mediator.Send(Arg.Any<GetTemplateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<DocumentContent>.Failure("Nieznany szablon"));

        var result = await _controller.GetTemplate("nonexistent");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task SaveDocument_WithValidRequest_ShouldReturnFileResult()
    {
        var docxBytes = new byte[] { 1, 2, 3 };
        _mediator.Send(Arg.Any<SaveDocumentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaveDocumentResult>.Success(new SaveDocumentResult(docxBytes, "test.docx")));

        var request = new SaveDocumentRequest
        {
            Html = "<p>Test</p>",
            OriginalFileName = "test.docx"
        };

        var result = await _controller.SaveDocument(request);

        result.Should().BeOfType<FileContentResult>();
        var fileResult = (FileContentResult)result;
        fileResult.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        fileResult.FileDownloadName.Should().Be("test.docx");
    }

    [Test]
    public async Task SaveDocument_WhenFails_ShouldReturnBadRequest()
    {
        _mediator.Send(Arg.Any<SaveDocumentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaveDocumentResult>.Failure("Błąd konwersji"));

        var request = new SaveDocumentRequest { Html = "" };

        var result = await _controller.SaveDocument(request);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task OpenDocument_WithNullFile_ShouldReturnBadRequest()
    {
        var result = await _controller.OpenDocument(null!);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task OpenDocument_WithNonDocxFile_ShouldReturnBadRequest()
    {
        var fileMock = Substitute.For<IFormFile>();
        fileMock.FileName.Returns("file.pdf");
        fileMock.Length.Returns(100);

        var result = await _controller.OpenDocument(fileMock);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task OpenDocument_WithBinaryLegacyDoc_ReturnsBadRequest_WithConversionGuidanceAndCode()
    {
        var fileMock = Substitute.For<IFormFile>();
        fileMock.FileName.Returns("stary.doc");
        fileMock.Length.Returns(100);
        fileMock.OpenReadStream().Returns(new MemoryStream(new byte[] { 1, 2, 3 }));
        _mediator.Send(Arg.Any<OpenDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<DocumentContent>.Failure(ErrorCodes.UnsupportedLegacyDoc));

        var result = await _controller.OpenDocument(fileMock);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("UNSUPPORTED_LEGACY_DOC");
        bad.Value!.ToString().Should().Contain(".docx");
    }

    [Test]
    public async Task OpenDocument_WhenPasswordRequired_Returns422_WithCode()
    {
        var fileMock = Substitute.For<IFormFile>();
        fileMock.FileName.Returns("tajne.docx");
        fileMock.Length.Returns(100);
        fileMock.OpenReadStream().Returns(new MemoryStream(new byte[] { 1, 2, 3 }));
        _mediator.Send(Arg.Any<OpenDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<DocumentContent>.Failure(ErrorCodes.DocumentProtected));

        var result = await _controller.OpenDocument(fileMock);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        obj.Value!.ToString().Should().Contain("PASSWORD_REQUIRED");
    }

    [Test]
    public async Task UploadImage_WithNullFile_ShouldReturnBadRequest()
    {
        var result = await _controller.UploadImage(null!);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task UploadImage_WithValidFile_ShouldReturnOk()
    {
        var fileContent = new byte[] { 1, 2, 3 };
        var stream = new MemoryStream(fileContent);
        var fileMock = Substitute.For<IFormFile>();
        fileMock.FileName.Returns("photo.jpg");
        fileMock.ContentType.Returns("image/jpeg");
        fileMock.Length.Returns(fileContent.Length);
        fileMock.OpenReadStream().Returns(stream);

        var response = new ImageUploadResponse("data:image/jpeg;base64,AAAA", "photo.jpg", fileContent.Length);
        _mediator.Send(Arg.Any<UploadImageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<ImageUploadResponse>.Success(response));

        var result = await _controller.UploadImage(fileMock);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Test]
    public async Task GeneratePdf_ShouldReturnPdfFile()
    {
        var masterId = Guid.NewGuid();
        var request = new SaveDocumentRequest
        {
            Html = "<p>Bieżący stan edytora</p>",
            OriginalFileName = "umowa.docx",
            MasterId = masterId
        };
        GeneratePdfPreviewCommand? captured = null;
        _mediator.Send(Arg.Any<GeneratePdfPreviewCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<GeneratePdfPreviewCommand>();
                return Result<GeneratePdfPreviewResult>.Success(
                    new GeneratePdfPreviewResult(new byte[] { 0x25, 0x50, 0x44, 0x46 }, "umowa.pdf"));
            });

        var result = await _controller.GeneratePdf(request, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/pdf");
        file.FileDownloadName.Should().Be("umowa.pdf");
        captured!.Html.Should().Be(request.Html);
        captured.MasterId.Should().Be(masterId);
    }

    [Test]
    public async Task GeneratePdf_WhenConversionFails_ShouldReturnBadRequest()
    {
        _mediator.Send(Arg.Any<GeneratePdfPreviewCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<GeneratePdfPreviewResult>.Failure("Usługa konwersji do PDF jest niedostępna."));

        var result = await _controller.GeneratePdf(new SaveDocumentRequest { Html = "<p>x</p>" }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Test]
    public void ExportToPdf_ShouldReturn501()
    {
        var request = new SaveDocumentRequest { Html = "<p>Test</p>" };

        var result = _controller.ExportToPdf(request);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(501);
    }
}
