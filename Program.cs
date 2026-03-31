using FeatureToggleAgent;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

IServiceCollection services = new ServiceCollection();
IConfiguration configBuilder = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddCommandLine(args)
    .Build();

services.AddSingleton<IConfiguration>(configBuilder);


// Add Azure DevOps configuration
services.Configure<AzureDevOpsConfigOptions>((x) => {
    IConfigurationSection section = configBuilder.GetSection(AzureDevOpsConfigOptions.Section);
    x.OrganizationUrl = section.GetValue<string>(nameof(AzureDevOpsConfigOptions.OrganizationUrl)) 
        ?? throw new InvalidOperationException("AzureDevOpsConfigOptions: OrganizationUrl is required");
    x.Project = section.GetValue<string>(nameof(AzureDevOpsConfigOptions.Project)) 
        ?? throw new InvalidOperationException("AzureDevOpsConfigOptions: Project is required");
    x.Repository = section.GetValue<string>(nameof(AzureDevOpsConfigOptions.Repository)) 
        ?? throw new InvalidOperationException("AzureDevOpsConfigOptions: Repository is required");
    x.PersonalAccessToken = section.GetValue<string>(nameof(AzureDevOpsConfigOptions.PersonalAccessToken)) 
        ?? throw new InvalidOperationException("AzureDevOpsConfigOptions: PersonalAccessToken is required");
});

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddProvider(new ConsoleLoggerProvider());
});
var logger = loggerFactory.CreateLogger<FeatureToggleRemovalAgent>();

var serviceProvider = services.BuildServiceProvider();

var devOpsConfig = serviceProvider.GetRequiredService<IOptions<AzureDevOpsConfigOptions>>().Value;

try
{
    // Parse command line arguments
    var featureName = args.Length > 0 ? args[0] : "FoundationsAndAssociationsCodeDriven";
    var description = args.Length > 1 ? args[1] : "";
    var guideFilePath = args.Length > 2 ? args[2] : "C:\\Learnings\\ToggleFeatureRemoval\\Universal_Feature_Toggle_Removal.txt";
    var repositoryPath = args.Length > 3 ? args[3] : "C:\\Repositories\\eol-annual-reporting-fiscal";

    if (string.IsNullOrWhiteSpace(featureName))
    {
        Console.WriteLine("Usage: FeatureToggleRemovalAgent <FeatureName> [Description] [GuideFilePath] [RepositoryPath]");
        Console.WriteLine("Example: FeatureToggleRemovalAgent ShowSheetPreview \"Removing obsolete feature\" ./guide.txt ./repo/");
        return;
    }

    Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║   Feature Toggle Removal Agent - Powered by GithubCopilot     ║");
    Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Feature Name:     {featureName}");
    Console.WriteLine($"Repository Path:  {repositoryPath}");
    Console.WriteLine($"Guide File:       {guideFilePath}");
    Console.WriteLine();

    // Create and execute agent
    //var logger = serviceProvider.GetRequiredService<ILogger<FeatureToggleRemovalAgent>>();
    var featureToggleRemovalGuide = File.ReadAllText(guideFilePath);
    var systemMessageContent = $"""
        You are a feature toggle removal assistant. Follow the guide below to remove feature toggle references from code files.
        IMPORTANT RULES:
        1. Never comment out code - DELETE it entirely
        2. Never leave comments about the removal
        3. Preserve all business logic that is NOT the feature toggle check
        4. For if/else blocks, keep the 'if' content and remove the 'else' if the toggle was the condition
        5. For simple if blocks wrapping new feature code, remove the wrapper but keep the content
        6. For negated conditions (!toggle), delete the entire block
        7. Remove variable declarations that stored the toggle state
        8. Remove all usages of toggle variables in templates and code
        9. Search for variable patterns like isXxxEnabled, xxxFlag, showXxx
        10. Only remove toggle-related code, not per-entity configuration properties
        FEATURE TOGGLE REMOVAL GUIDE:
        {featureToggleRemovalGuide}
        Return modified code without any explanation or markdown formatting.
        """;
    await using CopilotClient copilotClient = new();

    await using var session = await copilotClient.CreateSessionAsync(
        new SessionConfig
        {
            Model = "claude-sonnet-4.6",
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Content = systemMessageContent
            }
        });

    var agent = new FeatureToggleRemovalAgent(
        session,
        logger,
        repositoryPath,
        guideFilePath,
        devOpsConfig,
        configBuilder.GetConnectionString("FiscaalGemak")
            ?? throw new InvalidOperationException("ConnectionStrings:FiscaalGemak is not configured in appsettings.json")
    );

    var result = await agent.ExecuteRemovalAsync(featureName, description);

    // Display results
    Console.WriteLine("╔══════════════════════════════════════════════════════╗");
    Console.WriteLine("║                  Removal Completed                   ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Status:           {result.Status}");
    Console.WriteLine($"Branch:           {result.BranchName}");
    Console.WriteLine($"Files Identified: {result.FilesIdentified}");
    Console.WriteLine($"Files Modified:   {result.FilesModified}");
    Console.WriteLine($"Commit Hash:      {result.CommitHash}");

    if (result.PullRequestNumber.HasValue)
    {
        Console.WriteLine($"Pull Request:     #{result.PullRequestNumber}");
        Console.WriteLine($"PR URL:           {result.PullRequestUrl}");
    }

    Console.WriteLine($"Execution Time:   {result.ExecutionTime.TotalSeconds:F2} seconds");
    Console.WriteLine();

    if (result.FailedFiles.Any())
    {
        Console.WriteLine("Failed Files:");
        foreach (var failed in result.FailedFiles)
        {
            Console.WriteLine($"  - {failed.FilePath}: {failed.Error}");
        }
    }

    if (result.Status == RemovalStatus.Success)
    {
        Console.WriteLine("Feature toggle removal completed successfully!");
        Environment.Exit(0);
    }
    else
    {
        Console.WriteLine("Feature toggle removal failed!");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            Console.WriteLine($"Error: {result.ErrorMessage}");
        }
        Environment.Exit(1);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Fatal error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    Environment.Exit(1);
}
