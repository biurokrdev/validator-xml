using System.Net;
using System.Net.Http.Json;
using Mass.AddressWindow.Api.Contracts;
using Mass.AddressWindow.Api.Documents;
using Microsoft.AspNetCore.Mvc;
using static Mass.AddressWindow.Api.Tests.ApiTestHost;

namespace Mass.AddressWindow.Api.Tests;

// Variant 2: the document comes from the mock of the external application, as PDF or DOCX (configuration);
// a DOCX goes back to the same mock for rendering, like to a real conversion service.
public class OrderedLetterApiTests
{
    private const string Url = "/api/letter-inspections/from-recipient";

    private static readonly string[] Recipient = ["Pani Anna Zielińska", "ul. Długa 12/3", "20-020 Lublin"];

    private static object Order(string envelope, string[]? recipient = null, string? number = null) => new
    {
        envelope,
        recipientLines = recipient ?? Recipient,
        registeredNumber = number,
        includePreview = false,
    };

    // 9. Mock returns PDF.
    [Fact]
    public async Task MockPdf_DoubleWindow_IsValid_AndMatchesRecipientAndNumber()
    {
        using var host = new ApiTestHost(("External:Mock:LetterFormat", "Pdf"));
        var response = await host.CreateClient().PostAsJsonAsync(Url, Order("DoubleWindow", number: "00759007731512000621"), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Equal(DocumentSource.ExternalMock, result.Document.Source);
        Assert.Equal(DocumentFormat.Pdf, result.Document.SourceFormat);
        Assert.False(result.Document.ConvertedToPdf);
        Assert.Equal(RecipientMatch.Matched, result.Address!.RecipientMatch);
        Assert.Equal(Recipient, result.Address.Address!.Lines);
        Assert.Equal("00759007731512000621", result.Label!.Label!.Number);
    }

    // 9. Mock returns DOCX: conversion happens before validation.
    [Fact]
    public async Task MockDocx_DoubleWindow_IsConvertedAndValid()
    {
        using var host = new ApiTestHost(("External:Mock:LetterFormat", "Docx"));
        var response = await host.CreateClient().PostAsJsonAsync(Url, Order("DoubleWindow", number: "RR473124829PL"), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(DocumentFormat.Docx, result.Document.SourceFormat);
        Assert.True(result.Document.ConvertedToPdf);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Equal(RecipientMatch.Matched, result.Address!.RecipientMatch);
        Assert.Equal("RR473124829PL", result.Label!.Label!.Number);
        Assert.Equal(RegisteredMailType.International, result.Label.Label.Kind);
    }

    // 10. Single window: no registered number required, and a leftover number is ignored.
    [Fact]
    public async Task SingleWindow_DoesNotRequireRegisteredNumber()
    {
        using var host = new ApiTestHost();
        var response = await host.CreateClient().PostAsJsonAsync(Url, Order("SingleWindow"), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Null(result.Label);
    }

    [Fact]
    public async Task SingleWindow_IgnoresStaleRegisteredNumber()
    {
        using var host = new ApiTestHost();
        var response = await host.CreateClient().PostAsJsonAsync(Url, Order("SingleWindow", number: "00759007731512000622"), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(ValidationOutcome.Valid, result.Status);
        Assert.Null(result.Label);
    }

    [Fact]
    public async Task DoubleWindow_RequiresRegisteredNumber()
    {
        using var host = new ApiTestHost();
        var response = await host.CreateClient().PostAsJsonAsync(Url, Order("DoubleWindow"), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json))!;
        Assert.Contains("numeru R", problem.Detail);
    }

    // The data passed to the mock proves nothing by itself: the number is read back from the barcode.
    [Fact]
    public async Task WrongCheckDigitInOrder_ComesBackAsInvalidLabel()
    {
        using var host = new ApiTestHost();
        var response = await host.CreateClient().PostAsJsonAsync(Url, Order("DoubleWindow", number: "00759007731512000622"), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Body(response);
        Assert.Equal(ValidationOutcome.Invalid, result.Status);
        Assert.Contains(result.Label!.Reasons, r => r.Code == "LABEL_NUMBER_INVALID");
    }

    [Fact]
    public async Task RecipientWithTooFewLines_Returns400()
    {
        using var host = new ApiTestHost();
        var response = await host.CreateClient().PostAsJsonAsync(Url, Order("SingleWindow", recipient: ["Tylko nazwisko"]), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
