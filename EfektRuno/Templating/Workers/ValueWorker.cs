using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace EfektRuno.Templating.Workers;

/// <summary>Replaces &lt;%name%&gt; / &lt;%name|format%&gt; tokens inside a scope. Successor of TextReplacer, but scoped.</summary>
public sealed class ValueWorker(DocxTemplateOptions options)
{
    private readonly ValueFormatter _formatter = new(options);

    public void Fill(OpenXmlElement scope, DataContext context)
    {
        foreach (Run run in scope.Descendants<Run>().ToList())
        {
            TemplateToken? token = Section.TokenOf(run);
            if (token is not { Kind: TokenKind.Value })
                continue;

            // Unknown names stay in place on purpose - the lint step reports them.
            if (context.TryResolve(options.MapPath(token.Path), out JsonNode? value))
                ValueFormatter.SetRunText(run, _formatter.Format(value, token.Format));
        }
    }
}
