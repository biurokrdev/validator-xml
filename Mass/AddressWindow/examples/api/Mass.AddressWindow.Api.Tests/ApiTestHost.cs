using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mass.AddressWindow.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Mass.AddressWindow.Api.Tests;

// Test host with configuration overrides, e.g. the mock document format or a disabled converter.
public class ApiTestHost(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))));
    }

    public static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", name));

    public static MultipartFormDataContent UploadForm(byte[]? file, string fileName, string? envelope, bool includePreview = false)
    {
        var form = new MultipartFormDataContent();
        if (file is not null)
        {
            form.Add(new ByteArrayContent(file), "file", fileName);
        }

        if (envelope is not null)
        {
            form.Add(new StringContent(envelope), "envelope");
        }

        form.Add(new StringContent(includePreview ? "true" : "false"), "includePreview");
        return form;
    }

    public static async Task<LetterInspectionResponse> Body(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<LetterInspectionResponse>(Json))!;
}

// xUnit class fixtures need a parameterless constructor: default configuration (mock returns PDF, conversion enabled).
public sealed class DefaultApiTestHost : ApiTestHost;
