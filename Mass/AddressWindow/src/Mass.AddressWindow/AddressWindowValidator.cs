using Mass.AddressWindow.Docx;
using Mass.AddressWindow.Rules;

namespace Mass.AddressWindow;

public sealed class AddressWindowValidator : IAddressWindowValidator
{
    private readonly ValidationProfile _defaultProfile;

    public AddressWindowValidator(ValidationProfile? defaultProfile = null)
    {
        _defaultProfile = defaultProfile ?? ValidationProfile.Default;
    }

    public AddressWindowValidationResult Validate(Stream document, WindowMode mode, ValidationProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        profile ??= _defaultProfile;
        profile.EnsureUsableFor(mode);

        DocxPackage package;
        try
        {
            package = DocxPackage.Open(document);
            _ = package.Body;
        }
        catch (DocxFormatException ex)
        {
            return AddressWindowValidationResult.Unreadable(mode, ex.Message);
        }

        var analyzer = new FirstPageAnalyzer(
            package,
            new StyleResolver(package.Styles),
            new TextMetrics(profile.AverageCharacterWidthEm, profile.LineHeightFactor));
        var candidates = analyzer.Analyze();

        var documentIssues = new List<ValidationIssue>();
        if (!analyzer.Page.IsA4Portrait)
        {
            documentIssues.Add(new ValidationIssue(
                IssueCodes.UnexpectedPageFormat, IssueSeverity.Warning,
                $"Pierwsza strona ma {GeometryRules.Mm(analyzer.Page.Width)} × {GeometryRules.Mm(analyzer.Page.Height)}, "
                + $"a układ „{profile.Layout.Name}” zakłada A4 w pionie. Położenie okien może się nie zgadzać."));
        }

        var claimed = new HashSet<IAddressCandidate>();
        var windows = new List<WindowCheckResult>
        {
            WindowChecker.Check(WindowRole.Recipient, profile.Layout.RecipientWindow, profile.RecipientRules, candidates, claimed),
        };

        if (mode == WindowMode.Double)
        {
            windows.Add(WindowChecker.Check(WindowRole.Sender, profile.Layout.SenderWindow!, profile.SenderRules, candidates, claimed));
        }

        return new AddressWindowValidationResult(mode, isDocumentReadable: true, windows, documentIssues);
    }

    public AddressWindowValidationResult Validate(byte[] document, WindowMode mode, ValidationProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream(document, writable: false);
        return Validate(stream, mode, profile);
    }

    public AddressWindowValidationResult ValidateFile(string path, WindowMode mode, ValidationProfile? profile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Validate(stream, mode, profile);
    }

    public async Task<AddressWindowValidationResult> ValidateAsync(
        Stream document,
        WindowMode mode,
        ValidationProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        (profile ?? _defaultProfile).EnsureUsableFor(mode);

        if (document.CanSeek)
        {
            return Validate(document, mode, profile);
        }

        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return Validate(buffer, mode, profile);
    }
}
