using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeatureToggleAgent;

public class AnthropicService(AgentSettings settings, HttpClient httpClient)
{
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── Step 1: Parse the markdown to understand what needs to change ──────────
    public async Task<ParsedMarkdown> ParseMarkdownAsync(string markdownContent)
    {
        Console.WriteLine("  🤖 Sending markdown to Claude for analysis...");

        var prompt = $"""
            You are a code analysis assistant. Analyze this markdown document that describes feature toggle cleanup tasks.
            
            Extract:
            1. The feature name/toggle name being cleaned up
            2. For each file mentioned: the file path, what needs to be removed/changed, and the exact code patterns
            
            Return a JSON object with this exact structure:
            {{
              "featureName": "string - human readable feature name",
              "featureToggleName": "string - the actual toggle/flag identifier (e.g. 'IsFeatureXEnabled')",
              "fileChanges": [
                {{
                  "filePath": "string - relative file path from repo root",
                  "description": "string - what this change does",
                  "removals": [
                    {{
                      "patternOrCode": "string - the exact code block, method call, or pattern to find",
                      "replacementCode": null or "string - what to replace it with (null = delete entirely)",
                      "type": "RemoveBlock|RemoveEntireIfElse|RemoveLines|ReplaceWithContent"
                    }}
                  ]
                }}
              ]
            }}
            
            RemovalType guide:
            - RemoveBlock: Remove the if() wrapper but KEEP the body (feature is now always-on)  
            - RemoveEntireIfElse: Remove the whole if/else including bodies
            - RemoveLines: Remove specific lines that match a pattern
            - ReplaceWithContent: Replace code with something else
            
            Return ONLY valid JSON, no markdown fences, no explanation.
            
            MARKDOWN DOCUMENT:
            {markdownContent}
            """;

        var response = await CallClaudeAsync(prompt, maxTokens: 4096);

        try
        {
            var parsed = JsonSerializer.Deserialize<ParsedMarkdownDto>(response, _json)!;
            return new ParsedMarkdown(
                parsed.FeatureName ?? "Unknown Feature",
                parsed.FeatureToggleName,
                parsed.FileChanges?.Select(fc => new FileChange(
                    fc.FilePath ?? "",
                    fc.Description ?? "",
                    fc.Removals?.Select(r => new CodeBlockRemoval(
                        r.PatternOrCode ?? "",
                        r.ReplacementCode,
                        Enum.TryParse<RemovalType>(r.Type, out var rt) ? rt : RemovalType.RemoveLines
                    )).ToList() ?? []
                )).ToList() ?? [],
                markdownContent
            );
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse Claude's response as JSON: {ex.Message}\nResponse was:\n{response}");
        }
    }

    // ── Step 2: Apply changes to actual file content ───────────────────────────
    public async Task<string> ApplyChangesToFileAsync(string filePath, string fileContent, FileChange change)
    {
        Console.WriteLine($"  🤖 Claude is applying changes to: {filePath}");

        var removalsJson = JsonSerializer.Serialize(change.Removals, _json);

        var prompt = $"""
            You are a precise code editor. Apply the following feature toggle cleanup changes to this file.
            
            FILE PATH: {filePath}
            
            CHANGES TO APPLY:
            {removalsJson}
            
            Change type guide:
            - RemoveBlock: Remove ONLY the if/feature-flag wrapper, but keep the code that was inside (the feature is now permanent)
            - RemoveEntireIfElse: Delete the entire if/else block including all inner code
            - RemoveLines: Remove lines matching the pattern
            - ReplaceWithContent: Replace matching code with the replacementCode value
            
            Rules:
            1. Make ONLY the changes described. Do not refactor, rename, reformat, or change anything else.
            2. Preserve all whitespace, indentation style, and line endings of the surrounding code.
            3. Remove any now-unused variables or imports ONLY if they were solely used by the removed toggle code.
            4. If a change cannot be found, skip it and continue.
            
            Return ONLY the complete updated file content. No explanation, no markdown fences, no commentary.
            
            CURRENT FILE CONTENT:
            ```
            {fileContent}
            ```
            """;

        return await CallClaudeAsync(prompt, maxTokens: 8192);
    }

    // ── Core API call ──────────────────────────────────────────────────────────
    private async Task<string> CallClaudeAsync(string userPrompt, int maxTokens = 4096)
    {
        var request = new
        {
            model = settings.AnthropicModel,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = userPrompt } }
        };

        var requestJson = JsonSerializer.Serialize(request, _json);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        httpRequest.Headers.Add("x-api-key", settings.AnthropicApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(httpRequest);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API error {response.StatusCode}: {responseBody}");

        var json = JsonNode.Parse(responseBody);
        return json?["content"]?[0]?["text"]?.GetValue<string>()
               ?? throw new InvalidOperationException("Could not extract text from Anthropic response.");
    }

    // DTO classes for deserialization
    private class ParsedMarkdownDto
    {
        public string? FeatureName { get; set; }
        public string? FeatureToggleName { get; set; }
        public List<FileChangeDto>? FileChanges { get; set; }
    }

    private class FileChangeDto
    {
        public string? FilePath { get; set; }
        public string? Description { get; set; }
        public List<CodeBlockRemovalDto>? Removals { get; set; }
    }

    private class CodeBlockRemovalDto
    {
        public string? PatternOrCode { get; set; }
        public string? ReplacementCode { get; set; }
        public string? Type { get; set; }
    }
}
