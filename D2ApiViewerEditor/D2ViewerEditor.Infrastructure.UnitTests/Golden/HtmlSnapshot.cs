using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace D2ViewerEditor.Infrastructure.UnitTests.Golden;

public static class HtmlSnapshot
{
    public static void Verify(string actual, string snapshotName, [CallerFilePath] string callerPath = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(callerPath)!, "__snapshots__");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, snapshotName + ".approved.html");
        var normalized = Normalize(actual);

        if (!File.Exists(file))
        {
            File.WriteAllText(file, normalized);
            TestContext.Out.WriteLine($"Snapshot baseline created: {file}. Commit it and re-run to lock.");
            return;
        }

        var expected = File.ReadAllText(file).Replace("\r\n", "\n");
        if (normalized != expected)
        {
            Assert.Fail(
                $"HTML snapshot '{snapshotName}' changed.\n" +
                $"--- expected (approved) ---\n{expected}\n" +
                $"--- actual ---\n{normalized}\n" +
                $"If the change is intended, delete {file} and re-run to regenerate.");
        }
    }

    public static string Normalize(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        html = Regex.Replace(html, "(data:[^;]+;base64,)[A-Za-z0-9+/=]+",
            m => $"{m.Groups[1].Value}[base64]");

        html = Regex.Replace(html, "(<span class=\"field-date\"[^>]*>)[^<]*", "$1[date]");

        html = Regex.Replace(html, "(data-image-id=\")[^\"]*", "$1[id]");

        html = html.Replace("\r\n", "\n").Replace("><", ">\n<");

        return html.TrimEnd();
    }
}
