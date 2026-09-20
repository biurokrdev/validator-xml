using System.Text.RegularExpressions;

namespace EfektRuno.Templating;

public enum TokenKind
{
    Value,
    Each,
    If,
    IfNot,
    End
}

/// <summary>
/// Single &lt;%...%&gt; token. Supported forms:
/// &lt;%name%&gt;, &lt;%name|format%&gt;, &lt;%#list%&gt;, &lt;%?flag%&gt;, &lt;%^flag%&gt;, &lt;%?field==LITERAL%&gt;, &lt;%/name%&gt;.
/// </summary>
public sealed record TemplateToken(TokenKind Kind, string Path, string? Literal, string? Format)
{
    public static readonly Regex Pattern = new(
        @"<%\s*(?<kind>[#?^/])?\s*(?<path>[\p{L}\p{N}_.\-]+)\s*(?:==\s*(?<lit>[^%|]*?)\s*)?(?:\|\s*(?<fmt>[^%]*?)\s*)?%>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsOpen => Kind is TokenKind.Each or TokenKind.If or TokenKind.IfNot;

    public static TemplateToken? TryParse(string text)
    {
        if (!text.StartsWith("<%", StringComparison.Ordinal))
            return null;

        Match match = Pattern.Match(text);
        if (!match.Success || match.Index != 0 || match.Length != text.Length)
            return null;

        return FromMatch(match);
    }

    public static TemplateToken FromMatch(Match match)
    {
        TokenKind kind = match.Groups["kind"].Value switch
        {
            "#" => TokenKind.Each,
            "?" => TokenKind.If,
            "^" => TokenKind.IfNot,
            "/" => TokenKind.End,
            _ => TokenKind.Value
        };

        return new TemplateToken(
            kind,
            match.Groups["path"].Value,
            match.Groups["lit"].Success ? match.Groups["lit"].Value : null,
            match.Groups["fmt"].Success ? match.Groups["fmt"].Value : null);
    }
}

public sealed class TemplateException(string message) : Exception(message);
