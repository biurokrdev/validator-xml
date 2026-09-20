using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using EfektRuno.Templating.Workers;

namespace EfektRuno.Templating;

/// <summary>Convenience façade: template bytes + JSON -> filled bytes. Composes the default worker pipeline.</summary>
public sealed class DocxTemplateEngine(DocxTemplateOptions? options = null)
{
    public WorkerPipeline Pipeline { get; } = new(options);

    public byte[] Render(byte[] template, JsonNode? data, out RenderReport report)
    {
        using var stream = new MemoryStream();
        stream.Write(template, 0, template.Length);
        report = Render(stream, data);
        return stream.ToArray();
    }

    /// <summary>Renders in place. The stream must be read/write and hold a copy of the template - never the original.</summary>
    public RenderReport Render(Stream docx, JsonNode? data)
    {
        using WordprocessingDocument document = WordprocessingDocument.Open(docx, true);
        return Pipeline.Fill(document, data);
    }
}
