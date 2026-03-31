namespace FeatureToggleAgent;

public class AgentSettings
{
    // Azure DevOps
    public string AzureDevOpsOrg { get; set; } = string.Empty;
    public string AzureDevOpsProject { get; set; } = string.Empty;
    public string AzureDevOpsRepo { get; set; } = string.Empty;
    public string AzureDevOpsPat { get; set; } = string.Empty;

    // Anthropic
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string AnthropicModel { get; set; } = "claude-opus-4-6";

    // Feature to remove
    /// <summary>Exact FiscaalGemakFeatures enum value, e.g. "ShowSheetPreview"</summary>
    public string FeatureName { get; set; } = string.Empty;

    // Git / ADO config
    public string BaseBranch { get; set; } = "main";

    /// <summary>Comma-separated file extensions to scan.</summary>
    public string FileExtensions { get; set; } = ".cs,.ts,.html,.cshtml,.js";

    /// <summary>Comma-separated folder names to exclude.</summary>
    public string ExcludedFolders { get; set; } = "node_modules,.git,bin,obj,dist,.angular";

    /// <summary>
    /// When true: scan and report candidate files, but do NOT create a branch or PR.
    /// Useful for previewing what would be changed before committing.
    /// </summary>
    public bool DryRun { get; set; } = false;

    public string DatabaseConnectionString { get; set; } = string.Empty;

    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(AzureDevOpsOrg)) missing.Add("AzureDevOpsOrg (--org)");
        if (string.IsNullOrWhiteSpace(AzureDevOpsProject)) missing.Add("AzureDevOpsProject (--project)");
        if (string.IsNullOrWhiteSpace(AzureDevOpsRepo)) missing.Add("AzureDevOpsRepo (--repo)");
        if (string.IsNullOrWhiteSpace(AzureDevOpsPat)) missing.Add("AzureDevOpsPat (--pat or env var)");
        if (string.IsNullOrWhiteSpace(AnthropicApiKey)) missing.Add("AnthropicApiKey (--anthropic-key or env var)");
        if (string.IsNullOrWhiteSpace(FeatureName)) missing.Add("FeatureName (--feature)");

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing required configuration:\n  {string.Join("\n  ", missing)}");
    }

    public string[] GetExtensions() =>
        FileExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string[] GetExcludedFolders() =>
        ExcludedFolders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
