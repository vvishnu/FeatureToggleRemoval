using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeatureToggleAgent;

public class AzureDevOpsService
{
    private readonly AgentSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AzureDevOpsService(AgentSettings settings, HttpClient httpClient)
    {
        _settings = settings;
        _httpClient = httpClient;

        // Set auth header
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{settings.AzureDevOpsPat}"));
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", token);

        _baseUrl = $"https://dev.azure.com/{settings.AzureDevOpsOrg}/{settings.AzureDevOpsProject}/_apis/git/repositories/{settings.AzureDevOpsRepo}";
    }

    // ── Get the latest commit SHA of a branch ─────────────────────────────────
    public async Task<AdoRef> GetBranchRefAsync(string branchName)
    {
        var url = $"{_baseUrl}/refs?filter=heads/{branchName}&api-version=7.1";
        var json = await GetAsync(url);
        var refs = json?["value"]?.AsArray();

        if (refs == null || refs.Count == 0)
            throw new InvalidOperationException($"Branch '{branchName}' not found in repo.");

        return new AdoRef(
            refs[0]!["name"]!.GetValue<string>(),
            refs[0]!["objectId"]!.GetValue<string>(),
            refs[0]!["url"]!.GetValue<string>()
        );
    }

    // ── Create a new branch from a base commit ────────────────────────────────
    public async Task CreateBranchAsync(string newBranchName, string fromCommitId)
    {
        var url = $"{_baseUrl}/refs?api-version=7.1";
        var body = new[]
        {
            new
            {
                name = $"refs/heads/{newBranchName}",
                oldObjectId = "0000000000000000000000000000000000000000",
                newObjectId = fromCommitId
            }
        };

        await PostAsync(url, body);
    }

    // ── Get file content from the repo ────────────────────────────────────────
    public async Task<string?> GetFileContentAsync(string filePath, string branchName)
    {
        // URL-encode the path
        var encodedPath = Uri.EscapeDataString(filePath);
        var url = $"{_baseUrl}/items?path={encodedPath}&versionDescriptor.version={branchName}&versionDescriptor.versionType=branch&api-version=7.1";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── Push a commit with multiple file changes ───────────────────────────────
    public async Task<string> PushCommitAsync(
        string branchName,
        string commitMessage,
        List<(string filePath, string? newContent)> fileChanges,
        string baseCommitId)
    {
        var changes = fileChanges.Select(fc =>
        {
            if (fc.newContent == null)
            {
                return (object)new
                {
                    changeType = "delete",
                    item = new { path = fc.filePath }
                };
            }
            else
            {
                return (object)new
                {
                    changeType = "edit",
                    item = new { path = fc.filePath },
                    newContent = new
                    {
                        content = fc.newContent,
                        contentType = "rawtext"
                    }
                };
            }
        }).ToList();

        var body = new
        {
            refUpdates = new[]
            {
                new
                {
                    name = $"refs/heads/{branchName}",
                    oldObjectId = baseCommitId
                }
            },
            commits = new[]
            {
                new
                {
                    comment = commitMessage,
                    changes = changes
                }
            }
        };

        var url = $"{_baseUrl}/pushes?api-version=7.1";
        var json = await PostAsync(url, body);

        return json?["commits"]?[0]?["commitId"]?.GetValue<string>()
               ?? throw new InvalidOperationException("Could not get commit ID from push response.");
    }

    // ── Create a pull request ─────────────────────────────────────────────────
    public async Task<PullRequestResult> CreatePullRequestAsync(
        string sourceBranch,
        string targetBranch,
        string title,
        string description,
        string featureName)
    {
        var url = $"{_baseUrl}/pullrequests?api-version=7.1";

        var body = new
        {
            title,
            description,
            sourceRefName = $"refs/heads/{sourceBranch}",
            targetRefName = $"refs/heads/{targetBranch}",
            labels = new[] { new { name = "feature-toggle-cleanup" } },
            workItemRefs = Array.Empty<object>()
        };

        var json = await PostAsync(url, body);

        var prId = json?["pullRequestId"]?.GetValue<int>()
                   ?? throw new InvalidOperationException("Could not get PR ID from response.");

        var webUrl = $"https://dev.azure.com/{_settings.AzureDevOpsOrg}/{_settings.AzureDevOpsProject}/_git/{_settings.AzureDevOpsRepo}/pullrequest/{prId}";

        return new PullRequestResult(prId, webUrl);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────
    private async Task<JsonNode?> GetAsync(string url)
    {
        var response = await _httpClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ADO GET failed {response.StatusCode}: {body}");
        return JsonNode.Parse(body);
    }

    private async Task<JsonNode?> PostAsync(string url, object body)
    {
        var json = JsonSerializer.Serialize(body, _json);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ADO POST failed {response.StatusCode}: {responseBody}");
        return JsonNode.Parse(responseBody);
    }
}
