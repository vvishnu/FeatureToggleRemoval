using System.Text.RegularExpressions;

namespace FeatureToggleAgent;

/// <summary>
/// Fast string-based pre-scanner that identifies files containing references
/// to a specific FiscaalGemak feature toggle — before sending anything to Claude.
/// Uses the exact search patterns from the Universal Feature Toggle Removal Guide.
/// </summary>
public class TogglePatternScanner
{
    private readonly string _featureName;
    private readonly string[] _extensions;
    private readonly string[] _excludedFolders;

    // Derived variable name patterns (camelCase + variations)
    private readonly string _camelFeature;
    private readonly string[] _variablePatterns;

    public TogglePatternScanner(AgentSettings settings)
    {
        _featureName = settings.FeatureName;
        _extensions = settings.GetExtensions();
        _excludedFolders = settings.GetExcludedFolders();

        // e.g. "ShowSheetPreview" → "showSheetPreview"
        _camelFeature = char.ToLower(_featureName[0]) + _featureName[1..];

        _variablePatterns =
        [
            $"is{_featureName}Enabled",
            $"{_camelFeature}Enabled",
            $"{_camelFeature}Flag",
            $"show{_featureName}",
            $"is{_camelFeature}Enabled",
        ];
    }

    /// <summary>
    /// Scan a list of (path, content) pairs. Returns only those containing toggle references.
    /// </summary>
    public List<CandidateFile> FindCandidates(IEnumerable<(string path, string content)> files)
    {
        var candidates = new List<CandidateFile>();

        foreach (var (path, content) in files)
        {
            if (!HasRelevantExtension(path)) continue;
            if (IsExcluded(path)) continue;

            var matched = FindMatchedPatterns(path, content);
            if (matched.Count > 0)
                candidates.Add(new CandidateFile(path, content, matched));
        }

        return candidates;
    }

    private List<string> FindMatchedPatterns(string path, string content)
    {
        var found = new HashSet<string>();

        // ── Backend C# patterns ───────────────────────────────────────────────
        if (path.EndsWith(".cs") || path.EndsWith(".cshtml"))
        {
            Check(content, found, $"FiscaalGemakFeatures.{_featureName}");
            Check(content, found, "pilotFeatureService.IsFeatureEnabled");
            Check(content, found, "_pilotFeatureService.IsFeatureEnabled");
            Check(content, found, "IPilotFeatureService");
        }

        // ── TypeScript / Angular patterns ─────────────────────────────────────
        if (path.EndsWith(".ts") || path.EndsWith(".js"))
        {
            Check(content, found, $"FiscaalGemakFeatures.{_featureName}");
            Check(content, found, $"FiscaalGemakFeatures[FiscaalGemakFeatures.{_featureName}]");
            Check(content, found, $"fiscaalGemakFeatures[FiscaalGemakFeatures");
            Check(content, found, $"fiscaalGemakFeaturesDescriptions");
            Check(content, found, $"{_featureName} =");     // enum definition line

            foreach (var v in _variablePatterns)
                Check(content, found, v);
        }

        // ── HTML / Angular template patterns ──────────────────────────────────
        if (path.EndsWith(".html") || path.EndsWith(".cshtml"))
        {
            Check(content, found, $"is{_featureName}Enabled");
            Check(content, found, $"{_camelFeature}Enabled");
            Check(content, found, $"{_camelFeature}Flag");

            foreach (var v in _variablePatterns)
                Check(content, found, v);
        }

        // ── Enum / config files (any extension) ───────────────────────────────
        Check(content, found, $"{_featureName}");   // broad last-pass

        return [.. found];
    }

    private static void Check(string content, HashSet<string> found, string pattern)
    {
        if (content.Contains(pattern, StringComparison.Ordinal))
            found.Add(pattern);
    }

    private bool HasRelevantExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return _extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsExcluded(string path)
    {
        return _excludedFolders.Any(folder =>
            path.Contains($"/{folder}/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"\\{folder}\\", StringComparison.OrdinalIgnoreCase));
    }
}
