using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.DocumentHealth;

public sealed class HealthFindingCollector
{
    private readonly List<HealthFinding> _findings = [];
    private readonly Dictionary<string, int> _countsByCode = new(StringComparer.Ordinal);
    private readonly int _maxFindings;
    private readonly int _maxPerCode;

    public HealthFindingCollector(int maxFindings, int maxPerCode)
    {
        _maxFindings = maxFindings;
        _maxPerCode = maxPerCode;
    }

    public int ErrorCount { get; private set; }
    public int WarningCount { get; private set; }
    public int InfoCount { get; private set; }
    public bool Truncated { get; private set; }

    public IReadOnlyList<HealthFinding> Findings => _findings;

    public bool Has(string code) => _countsByCode.ContainsKey(code);

    public int Count(string code) => _countsByCode.GetValueOrDefault(code);

    public IReadOnlyCollection<string> Codes => _countsByCode.Keys;

    public void Transform(Func<HealthFinding, HealthFinding> transform)
    {
        for (var index = 0; index < _findings.Count; index++)
        {
            _findings[index] = transform(_findings[index]);
        }
    }

    public void Add(
        string code,
        StructureIssueSeverity severity,
        HealthStage stage,
        string title,
        string description,
        string? location = null,
        WordOpenImpact wordImpact = WordOpenImpact.None,
        PdfConversionImpact pdfImpact = PdfConversionImpact.None,
        string? remedy = null,
        AppSupportLevel appSupport = AppSupportLevel.Unknown,
        string? appNote = null)
    {
        Add(new HealthFinding(code, severity, stage, title, description, location, wordImpact, pdfImpact, remedy, appSupport, appNote));
    }

    public void Add(HealthFinding finding)
    {
        switch (finding.Severity)
        {
            case StructureIssueSeverity.Error:
                ErrorCount++;
                break;
            case StructureIssueSeverity.Warning:
                WarningCount++;
                break;
            default:
                InfoCount++;
                break;
        }

        var perCode = _countsByCode.GetValueOrDefault(finding.Code) + 1;
        _countsByCode[finding.Code] = perCode;

        if (perCode > _maxPerCode || _findings.Count >= _maxFindings)
        {
            Truncated = true;
            return;
        }

        _findings.Add(finding);
    }

    public void AnnotateRepetitions()
    {
        foreach (var (code, count) in _countsByCode)
        {
            if (count <= _maxPerCode)
            {
                continue;
            }

            var index = _findings.FindIndex(finding => finding.Code == code);

            if (index < 0)
            {
                continue;
            }

            var first = _findings[index];
            _findings[index] = first with
            {
                Description = $"{first.Description} (Łącznie wystąpień: {count}; pokazano pierwsze {_maxPerCode}.)"
            };
        }
    }
}
