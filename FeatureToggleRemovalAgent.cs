using GitHub.Copilot.SDK;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FeatureToggleAgent
{
    public class FeatureToggleRemovalAgent
    {
        readonly ILogger<FeatureToggleRemovalAgent> _logger;
        private readonly string _repositoryPath;
        private readonly string _featureToggleRemovalGuide;
        private readonly AzureDevOpsConfigOptions _devOpsConfig;
        private readonly CopilotSession _copilotSession;

        public FeatureToggleRemovalAgent(
            CopilotSession copilotSession,
            ILogger<FeatureToggleRemovalAgent> logger,
            string repositoryPath,
            string guideFilePath,
            AzureDevOpsConfigOptions devOpsConfig)
        {
            _copilotSession = copilotSession ?? throw new ArgumentNullException(nameof(copilotSession));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _repositoryPath = repositoryPath ?? throw new ArgumentNullException(nameof(repositoryPath));
            _devOpsConfig = devOpsConfig ?? throw new ArgumentNullException(nameof(devOpsConfig));

            if (!File.Exists(guideFilePath))
                throw new FileNotFoundException($"Guide file not found: {guideFilePath}");

            _featureToggleRemovalGuide = File.ReadAllText(guideFilePath);
        }

        /// <summary>
        /// Main entry point for the feature toggle removal process
        /// </summary>
        public async Task<RemovalResult> ExecuteRemovalAsync(string featureName, string description = "")
        {
            _logger.LogInformation($"Starting feature toggle removal for: {featureName}");

            var result = new RemovalResult { FeatureName = featureName, StartTime = DateTime.UtcNow };

            try
            {
                //Step 1: Create and checkout feature branch
                _logger.LogInformation("Creating feature branch...");
                var branchName = CreateFeatureBranch(featureName);
                result.BranchName = branchName;

                // Step 2: Find all files with feature toggle references
                _logger.LogInformation("Scanning repository for feature toggle references...");
                var filesToModify = FindFilesWithToggleReferences(featureName);
                result.FilesIdentified = filesToModify.Count;

                if (!filesToModify.Any())
                {
                    _logger.LogWarning($"No files found containing feature toggle: {featureName}");
                    result.Status = RemovalStatus.NoFilesFound;
                    return result;
                }

                _logger.LogInformation($"Found {filesToModify.Count} files to modify");

                // Step 3: Process each file with AI assistance
                var modifiedFiles = new List<string>();

                foreach (var file in filesToModify)
                {
                    _logger.LogInformation($"Processing file: {file}");
                    try
                    {
                        var modified = await ProcessFileWithAIAsyncCopilot(file, featureName);
                        if (modified)
                        {
                            modifiedFiles.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing file: {file}");
                        result.FailedFiles.Add(new FileError { FilePath = file, Error = ex.Message });
                    }
                }

                result.FilesModified = modifiedFiles.Count;
                result.ModifiedFiles = modifiedFiles;

                // Step 3b: Generate SQL migration script
                _logger.LogInformation("Generating SQL migration script...");
                var sqlFilePath = GenerateSqlMigrationScript(featureName);
                if (sqlFilePath != null)
                {
                    modifiedFiles.Add(sqlFilePath);
                    result.FilesModified = modifiedFiles.Count;
                    _logger.LogInformation($"SQL migration script created: {sqlFilePath}");
                }

                // Step 4: Commit changes
                _logger.LogInformation("Committing changes...");
                var commitMessage = GenerateCommitMessage(featureName, modifiedFiles.Count);
                CommitChanges(commitMessage, modifiedFiles, branchName);
                result.CommitHash = GetCurrentCommitHash();

                // Step 5: Create Pull Request
                _logger.LogInformation("Creating pull request...");
                var prNumber = await CreatePullRequestAsync(featureName, description, branchName, modifiedFiles);
                result.PullRequestNumber = prNumber;
                result.PullRequestUrl = $"{_devOpsConfig.OrganizationUrl}/{_devOpsConfig.Project}/_git/{_devOpsConfig.Repository}/pullrequest/{prNumber}";

                result.Status = RemovalStatus.Success;
                result.CompletedTime = DateTime.UtcNow;

                _logger.LogInformation($"Feature toggle removal completed successfully. PR: {prNumber}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Feature toggle removal failed");
                result.Status = RemovalStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedTime = DateTime.UtcNow;
            }

            return result;
        }


        private async Task<bool> ProcessFileWithAIAsyncCopilot(string filePath, string featureName)
        {
            var fileContent = File.ReadAllText(filePath);
            var originalContent = fileContent;

            var prompt = 
             $"""
                
                Based on the following feature toggle removal guide, remove all references to the feature '{featureName}' from the code in the mentioned file path below.

                FILE PATH: {filePath}
                
                """;

            try
            {

            var response = await _copilotSession.SendAndWaitAsync(
                    new MessageOptions
                    {
                        Prompt = prompt,
                    },
                    timeout: TimeSpan.FromMinutes(5));
                
                    _logger.LogInformation($"Successfully modified: {filePath}");
                    return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AI processing failed for {filePath}");
                throw;
            }
        }


        private List<string> FindFilesWithToggleReferences(string featureName)
        {
            var searchPatterns = GenerateSearchPatterns(featureName);
            var filesToProcess = new List<string>();
            var fileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".ts", ".html", ".cshtml", ".tsx", ".jsx"
            };

            try
            {
                var searchPath = Path.Combine(_repositoryPath, "fg");
                var allFiles = Directory.EnumerateFiles(searchPath, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                })
                    .Where(f => fileExtensions.Contains(Path.GetExtension(f)))
                    .Where(f => !f.Contains("\\.git\\") && !f.Contains("\\node_modules\\")
                             && !f.Contains("\\bin\\") && !f.Contains("\\obj\\"));

                // Use the shortest pattern for a quick pre-filter
                var shortestPattern = searchPatterns.OrderBy(p => p.Length).First();

                Parallel.ForEach(allFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    file =>
                    {
                        // Quick check: skip files smaller than shortest pattern
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Length < shortestPattern.Length)
                            return;

                        // Read line-by-line instead of entire file to fail fast
                        using var reader = new StreamReader(file, Encoding.UTF8);
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (searchPatterns.Any(pattern => line.Contains(pattern, StringComparison.OrdinalIgnoreCase)) || (fileInfo.Name == "FiscaalGemakFeatures.cs" && fileInfo.Extension == ".cs"))
                            {
                                lock (filesToProcess)
                                {
                                    filesToProcess.Add(file);
                                }
                                return; // Found a match, no need to read more lines
                            }
                        }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning repository");
                throw;
            }

            return filesToProcess;
        }


        /// <summary>
        /// Generates search patterns for the feature toggle
        /// </summary>
        private List<string> GenerateSearchPatterns(string featureName)
        {
            var patterns = new List<string>
            {
                $"FiscaalGemakFeatures.{featureName}",
                $"pilotFeatureService.IsFeatureEnabled({featureName}",
                $"_pilotFeatureService.IsFeatureEnabled({featureName}",
                $"fiscaalGemakFeatures[{featureName}",
                $"this.fiscaalGemakFeatures[{featureName}"
            };

            // Add variable name patterns (camelCase variations)
            var camelCase = ToCamelCase(featureName);
            var pascalCase = ToPascalCase(featureName);

            patterns.AddRange(new[]
            {
                $"is{pascalCase}Enabled",
                $"{camelCase}Enabled",
                $"{camelCase}Flag",
                $"show{pascalCase}",
                $"is{pascalCase}Flag"
            });

            return patterns;
        }

        /// <summary>
        /// Creates a feature branch for the removal changes
        /// </summary>
        private string CreateFeatureBranch(string featureName)
        {
            var branchName = $"feature/fg/remove-{featureName.ToLower()}-featuretoggle-{DateTime.UtcNow:yyyyMMddHHmmss}";

            using (var repo = new Repository(_repositoryPath))
            {
                // Ensure we're on the main branch first
                Commands.Checkout(repo, "main");

                // Pull latest from remote
                var origin = repo.Network.Remotes["origin"];
                var refSpecs = origin.FetchRefSpecs.Select(x => x.Specification);
                var fetchOptions = new FetchOptions
                {
                    CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials
                        {
                            Username = "pat",
                            Password = _devOpsConfig.PersonalAccessToken
                        }
                };
                Commands.Fetch(repo, "origin", refSpecs, fetchOptions, null);

                // Create and checkout new branch
                var branch = repo.CreateBranch(branchName);
                Commands.Checkout(repo, branch);

                _logger.LogInformation($"Created and checked out branch: {branchName}");
            }

            return branchName;
        }

        /// <summary>
        /// Commits the changes to the feature branch
        /// </summary>
        private void CommitChanges(string message, List<string> files, string branchName)
        {
            using (var repo = new Repository(_repositoryPath))
            {
                // Stage all modified files
                foreach (var file in files)
                {
                    repo.Index.Add(Path.GetRelativePath(_repositoryPath, file));
                }

                repo.Index.Write();

                // Create commit
                var author = new Signature("FeatureToggleBot", "bot@fiscaalgemak.nl", DateTime.Now);
                var commit = repo.Commit(message, author, author);

                // Push using explicit refspec since the branch has no upstream tracking configured
                var remote = repo.Network.Remotes["origin"];
                var pushRefSpec = $"refs/heads/{branchName}:refs/heads/{branchName}";
                var pushOptions = new PushOptions
                {
                    CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials
                        {
                            Username = "pat",
                            Password = _devOpsConfig.PersonalAccessToken
                        }
                };
                repo.Network.Push(remote, pushRefSpec, pushOptions);

                _logger.LogInformation($"Committed changes with hash: {commit.Sha.Substring(0, 7)}");
            }
        }

        /// <summary>
        /// Creates a pull request in Azure DevOps
        /// </summary>
        private async Task<int> CreatePullRequestAsync(string featureName, string description, string sourceBranch, List<string> modifiedFiles)
        {
            var client = new AzureDevOpsClient(_devOpsConfig);

            var prDescription = $"""
                ## Feature Toggle Removal: {featureName}
                
                {description}
                
                ### Changes Made
                - Modified {modifiedFiles.Count} files
                - Removed all references to feature toggle '{featureName}'
                - Preserved all business logic
                
                ### Files Modified
                {string.Join("\n", modifiedFiles.Select(f => $"- `{Path.GetRelativePath(_repositoryPath, f)}`"))}
                
                ### Validation
                - [ ] Code compiles without errors
                - [ ] All unit tests pass
                - [ ] Integration tests pass
                - [ ] Feature works as expected (toggle was enabled)
                - [ ] No regressions in related functionality
                - [ ] No commented-out code
                
                ### Related Documentation
                See the feature toggle removal guide for removal patterns and best practices.
                """;

            var prNumber = await client.CreatePullRequestAsync(
                $"Remove {featureName} Feature toggle",
                prDescription,
                sourceBranch,
                "main",
                modifiedFiles
            );

            return prNumber;
        }

        /// <summary>
        /// Gets the current commit hash
        /// </summary>
        private string GetCurrentCommitHash()
        {
            using (var repo = new Repository(_repositoryPath))
            {
                return repo.Head.Tip.Sha.Substring(0, 7);
            }
        }

        /// <summary>
        /// Generates a detailed commit message
        /// </summary>
        private string GenerateCommitMessage(string featureName, int fileCount)
        {
            return $"""
                Remove feature toggle '{featureName}' from codebase
                
                - Removed {fileCount} file(s)
                - Removed all toggle checks and conditions
                - Removed toggle-related variables and declarations
                - Preserved all business logic
                - No commented-out code remaining
                
                This commit removes the '{featureName}' feature toggle completely,
                maintaining the behavior as when the feature was enabled.
                """;
        }

        private string ToCamelCase(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;
            return char.ToLowerInvariant(pascalCase[0]) + pascalCase.Substring(1);
        }

        private string ToPascalCase(string camelCase)
        {
            if (string.IsNullOrEmpty(camelCase)) return camelCase;
            return char.ToUpperInvariant(camelCase[0]) + camelCase.Substring(1);
        }

        /// <summary>
        /// Reads FiscaalGemakFeatures.cs, finds the integer enum value for <paramref name="featureName"/>,

        /// then creates a numbered SQL migration file in the PreCompare folder.
        /// Returns the full path of the created file, or null if it could not be generated.
        /// </summary>
        private string? GenerateSqlMigrationScript(string featureName)
        {
            var preComparePath = Path.Combine(
                _repositoryPath,
                "fg", "Source", "Database", "FiscaalGemak.Database", "_Updates", "PreCompare");

            var featuresFilePath = Path.Combine(
                _repositoryPath,
                "fg", "Source", "Core", "Core.Configuration", "FiscaalGemakFeatures.cs");

            if (!Directory.Exists(preComparePath))
            {
                _logger.LogWarning($"PreCompare directory not found: {preComparePath}");
                return null;
            }

            if (!File.Exists(featuresFilePath))
            {
                _logger.LogWarning($"FiscaalGemakFeatures.cs not found: {featuresFilePath}");
                return null;
            }

            var enumValue = ParseEnumValue(featuresFilePath, featureName);
            if (enumValue == null)
            {
                _logger.LogWarning($"Could not find enum value for '{featureName}' in {featuresFilePath}");
                return null;
            }

            _logger.LogInformation($"Resolved enum value for '{featureName}': {enumValue}");

            var nextNumber = GetNextFileNumber(preComparePath);
            var fileName = $"{nextNumber}_Remove_PilotFeature_{featureName}.sql";
            var fullPath = Path.Combine(preComparePath, fileName);

            var sqlContent =
                $"""
                SET XACT_ABORT ON;
                BEGIN TRAN
                    DELETE FROM [Organisation].[PilotFeatures]
                    WHERE Feature IN ({enumValue});

                    DELETE FROM [Organisation].[PilotFeatureHistory]
                    WHERE FeatureId IN ({enumValue});

                    DELETE FROM dbo.FeatureToggleHistory
                    WHERE FeatureToggleId IN ({enumValue});

                    DELETE FROM dbo.FeatureToggles
                    WHERE Id IN ({enumValue});
                COMMIT TRAN;
                """;

            File.WriteAllText(fullPath, sqlContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var sqlProjPath = Path.Combine(
                _repositoryPath,
                "fg", "Source", "Database", "FiscaalGemak.Database", "FiscaalGemak.Database.sqlproj");

            AddFileToSqlProj(sqlProjPath, fullPath);

            return fullPath;
        }

        /// <summary>
        /// Adds the generated SQL file to FiscaalGemak.Database.sqlproj as a Build item
        /// with <CopyToOutputDirectory>DoNotCopy</CopyToOutputDirectory>.
        /// The include path is relative to the .sqlproj file's directory.
        /// </summary>
        private void AddFileToSqlProj(string sqlProjPath, string sqlFilePath)
        {
            if (!File.Exists(sqlProjPath))
            {
                _logger.LogWarning($"sqlproj not found, skipping project registration: {sqlProjPath}");
                return;
            }

            var sqlProjDir = Path.GetDirectoryName(sqlProjPath)!;
            var relativePath = Path.GetRelativePath(sqlProjDir, sqlFilePath);

            var doc = XDocument.Load(sqlProjPath, LoadOptions.PreserveWhitespace);
            XNamespace ns = doc.Root!.GetDefaultNamespace();

            // Check if the entry already exists (idempotent)
            var alreadyIncluded = doc.Descendants(ns + "None")
                .Any(e => string.Equals(
                    e.Attribute("Include")?.Value,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase));

            if (alreadyIncluded)
            {
                _logger.LogInformation($"File already referenced in sqlproj: {relativePath}");
                return;
            }

            var newItem = new XElement(ns + "None",
                new XAttribute("Include", relativePath),
                new XElement(ns + "CopyToOutputDirectory", "DoNotCopy"));

            // Append to the last ItemGroup that already contains None elements,
            // or add a new ItemGroup at the end of the project.
            var lastBuildGroup = doc.Descendants(ns + "ItemGroup")
                .LastOrDefault(g => g.Elements(ns + "None").Any());

            if (lastBuildGroup != null)
            {
                lastBuildGroup.Add(new XText("  "), newItem, new XText("\n  "));
            }
            else
            {
                var newItemGroup = new XElement(ns + "ItemGroup", new XText("\n    "), newItem, new XText("\n  "));
                doc.Root.Add(new XText("  "), newItemGroup, new XText("\n"));
            }

            doc.Save(sqlProjPath);
            _logger.LogInformation($"Registered '{relativePath}' in {Path.GetFileName(sqlProjPath)}");
        }

        /// <summary>
        /// Parses the integer value assigned to <paramref name="featureName"/> in the enum file.
        /// Handles both explicit assignments (e.g. <c>Foo = 42,</c>) and implicit sequential values.
        /// </summary>
        private int? ParseEnumValue(string featuresFilePath, string featureName)
        {
            var lines = File.ReadAllLines(featuresFilePath);

            // Match:  MemberName = 123,   or   MemberName = 123   (with optional comment)
            var explicitPattern = new Regex(
                @"^\s*(?<name>\w+)\s*=\s*(?<value>\d+)",

                RegexOptions.Compiled);

            // Match a plain enum member line (no assignment):  MemberName,
            var implicitPattern = new Regex(
                @"^\s*(?<name>\w+)\s*[,\s]",

                RegexOptions.Compiled);

            int currentValue = 0;
            bool insideEnum = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Detect enum body start
                if (!insideEnum)
                {
                    if (trimmed.StartsWith("public enum") || trimmed.StartsWith("enum"))
                        insideEnum = true;
                    continue;
                }

                if (trimmed == "{") continue;
                if (trimmed.StartsWith("}")) break;

                // Skip comments and blank lines
                if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.Length == 0)
                    continue;

                var explicitMatch = explicitPattern.Match(trimmed);
                if (explicitMatch.Success)
                {
                    currentValue = int.Parse(explicitMatch.Groups["value"].Value);
                    if (explicitMatch.Groups["name"].Value == featureName)
                        return currentValue;

                    currentValue++; // next implicit member follows from here
                    continue;
                }

                var implicitMatch = implicitPattern.Match(trimmed);
                if (implicitMatch.Success)
                {
                    if (implicitMatch.Groups["name"].Value == featureName)
                        return currentValue;

                    currentValue++;
                }
            }

            return null;
        }

        /// <summary>
        /// Scans <paramref name="directoryPath"/> for files whose names start with a number,
        /// finds the highest such number, and returns it incremented by 1.
        /// </summary>
        private static int GetNextFileNumber(string directoryPath)
        {
            var leadingNumberPattern = new Regex(@"^(?<num>\d+)", RegexOptions.Compiled);

            var maxNumber = Directory
                .EnumerateFiles(directoryPath, "*.sql")
                .Select(f => leadingNumberPattern.Match(Path.GetFileName(f)))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups["num"].Value))
                .DefaultIfEmpty(0)
                .Max();

            return maxNumber + 1;
        }
    }
}
