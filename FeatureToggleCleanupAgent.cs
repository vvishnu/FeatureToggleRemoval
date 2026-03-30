namespace FeatureToggleAgent;

public class FeatureToggleCleanupAgent
{
    private readonly AgentSettings _settings;
    private readonly AnthropicService _ai;
    private readonly AzureDevOpsService _ado;

    public FeatureToggleCleanupAgent(AgentSettings settings)
    {
        _settings = settings;
        var httpClient = new HttpClient();
        _ai = new AnthropicService(settings, httpClient);
        _ado = new AzureDevOpsService(settings, new HttpClient());
    }

    public async Task RunAsync()
    {
        try
        {
            // STEP 1: Read and validate the markdown file
            Log("📄 STEP 1: Reading markdown file", ConsoleColor.Yellow);
            var markdownContent = await ReadMarkdownFileAsync();
            Console.WriteLine($"  ✓ Loaded {markdownContent.Length:N0} characters from {_settings.MarkdownFilePath}");

            // STEP 2: Use Claude to parse the markdown
            Log("🧠 STEP 2: Parsing markdown with AI", ConsoleColor.Yellow);
            var parsed = await _ai.ParseMarkdownAsync(markdownContent);
            Console.WriteLine($"  ✓ Feature: {parsed.FeatureName}");
            if (parsed.FeatureToggleName != null)
                Console.WriteLine($"  ✓ Toggle name: {parsed.FeatureToggleName}");
            Console.WriteLine($"  ✓ Files to change: {parsed.FileChanges.Count}");
            foreach (var fc in parsed.FileChanges)
                Console.WriteLine($"    - {fc.FilePath} ({fc.Removals.Count} change(s))");

            if (parsed.FileChanges.Count == 0)
                throw new InvalidOperationException("No file changes were identified in the markdown. Please check the format.");

            // STEP 3: Create a new branch in Azure DevOps
            Log("🌿 STEP 3: Creating branch in Azure DevOps", ConsoleColor.Yellow);
            var baseRef = await _ado.GetBranchRefAsync(_settings.TargetBranch);
            Console.WriteLine($"  ✓ Base branch '{_settings.TargetBranch}' found (commit: {baseRef.objectId[..8]}...)");

            var branchName = GenerateBranchName(parsed);
            await _ado.CreateBranchAsync(branchName, baseRef.objectId);
            Console.WriteLine($"  ✓ Created branch: {branchName}");

            //STEP 4: Fetch each file, apply changes via AI
            Log("✏️  STEP 4: Fetching files and applying changes", ConsoleColor.Yellow);
            var modifiedFiles = new List<(string filePath, string? newContent)>();
            string currentCommitId = baseRef.objectId;

            foreach (var fileChange in parsed.FileChanges)
            {
                Console.WriteLine($"\n  📁 Processing: {fileChange.FilePath}");
                Console.WriteLine($"     {fileChange.Description}");

                var originalContent = await _ado.GetFileContentAsync(fileChange.FilePath, _settings.TargetBranch);

                if (originalContent == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ⚠ File not found in repo, skipping: {fileChange.FilePath}");
                    Console.ResetColor();
                    continue;
                }

                Console.WriteLine($"  ✓ Fetched file ({originalContent.Length:N0} chars)");

                // Apply changes using AI
                var updatedContent = await _ai.ApplyChangesToFileAsync(
                    fileChange.FilePath,
                    originalContent,
                    fileChange);

                // Clean up potential markdown fences Claude might add
                updatedContent = StripMarkdownFences(updatedContent);

                if (updatedContent == originalContent)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine("  ~ No changes detected in file (already clean or pattern not found)");
                    Console.ResetColor();
                    continue;
                }

                modifiedFiles.Add((fileChange.FilePath, updatedContent));
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Changes applied successfully");
                Console.ResetColor();
            }

            if (modifiedFiles.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("\n⚠ No files were modified. The changes may have already been applied or files were not found.");
                Console.ResetColor();
                return;
            }

            // STEP 5: Push all changes in a single commit
            Log("📤 STEP 5: Pushing changes to Azure DevOps", ConsoleColor.Yellow);
            var commitMessage = $"chore: Remove {parsed.FeatureToggleName ?? parsed.FeatureName} feature toggle checks\n\n" +
                                $"Feature '{parsed.FeatureName}' is now live. Removing toggle checks from {modifiedFiles.Count} file(s).\n\n" +
                                $"Files changed:\n" +
                                string.Join("\n", modifiedFiles.Select(f => $"- {f.filePath}"));

            var commitId = await _ado.PushCommitAsync(
                branchName,
                commitMessage,
                modifiedFiles,
                baseRef.objectId);

            Console.WriteLine($"  ✓ Committed {modifiedFiles.Count} file(s) (commit: {commitId[..8]}...)");

            // STEP 6: Create pull request
            Log("🔀 STEP 6: Creating Pull Request", ConsoleColor.Yellow);
            var prTitle = $"{_settings.PrTitle} - {parsed.FeatureName}";
            var prDescription = BuildPrDescription(parsed, modifiedFiles);

            var pr = await _ado.CreatePullRequestAsync(
                sourceBranch: branchName,
                targetBranch: _settings.TargetBranch,
                title: prTitle,
                description: prDescription,
                featureName: parsed.FeatureName);

            // ── DONE ───────────────────────────────────────────────────────────
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║              ✅ AGENT COMPLETE               ║");
            Console.WriteLine("╠══════════════════════════════════════════════╣");
            Console.WriteLine($"║ PR #{pr.pullRequestId,-43}║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine($"🔗 Pull Request URL:");
            Console.WriteLine($"   {pr.webUrl}");
            Console.WriteLine();
            Console.WriteLine($"📊 Summary:");
            Console.WriteLine($"   Branch:         {branchName}");
            Console.WriteLine($"   Files modified: {modifiedFiles.Count}");
            Console.WriteLine($"   Feature:        {parsed.FeatureName}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n❌ Agent failed: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Stack trace:");
            Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    private async Task<string> ReadMarkdownFileAsync()
    {
        if (!File.Exists(_settings.MarkdownFilePath))
            throw new FileNotFoundException($"Markdown file not found: {_settings.MarkdownFilePath}");

        return await File.ReadAllTextAsync(_settings.MarkdownFilePath);
    }

    private string GenerateBranchName(ParsedMarkdown parsed)
    {
        var safeName = (parsed.FeatureToggleName ?? parsed.FeatureName)
            .ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        // Remove non-alphanumeric except hyphens
        safeName = new string(safeName.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        safeName = safeName.Trim('-');

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
        return $"{_settings.NewBranchPrefix}/{safeName}-{timestamp}";
    }

    private string BuildPrDescription(ParsedMarkdown parsed, List<(string filePath, string? newContent)> modifiedFiles)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Feature Toggle Cleanup");
        sb.AppendLine();
        sb.AppendLine($"**Feature:** {parsed.FeatureName}");
        if (parsed.FeatureToggleName != null)
            sb.AppendLine($"**Toggle identifier:** `{parsed.FeatureToggleName}`");
        sb.AppendLine();
        sb.AppendLine("### What this PR does");
        sb.AppendLine(_settings.PrDescription);
        sb.AppendLine();
        sb.AppendLine("### Files changed");
        foreach (var f in modifiedFiles)
            sb.AppendLine($"- `{f.filePath}`");
        sb.AppendLine();
        sb.AppendLine("### How to review");
        sb.AppendLine("1. Verify the feature toggle code has been correctly removed");
        sb.AppendLine("2. Ensure no references to the toggle remain in the changed files");
        sb.AppendLine("3. Check that the feature behavior is preserved (always-enabled path kept)");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*This PR was created automatically by the Feature Toggle Cleanup Agent.*");
        return sb.ToString();
    }

    private static string StripMarkdownFences(string content)
    {
        // Remove ```lang ... ``` wrapping if Claude added it
        var lines = content.Split('\n').ToList();
        if (lines.Count >= 2 && lines[0].StartsWith("```"))
        {
            lines.RemoveAt(0);
            var lastFence = lines.FindLastIndex(l => l.TrimEnd() == "```");
            if (lastFence >= 0) lines.RemoveAt(lastFence);
        }
        return string.Join('\n', lines);
    }

    private static void Log(string message, ConsoleColor color)
    {
        Console.WriteLine();
        Console.ForegroundColor = color;
        Console.WriteLine($"── {message} ──");
        Console.ResetColor();
    }
}
