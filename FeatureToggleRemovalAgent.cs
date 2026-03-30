using GitHub.Copilot.SDK;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System.Text;

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
    }
}
