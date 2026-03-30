//using Microsoft.SemanticKernel;
//using Microsoft.SemanticKernel.ChatCompletion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FeatureToggleAgent
{
    public class AzureDevOpsClient
    {
        private readonly AzureDevOpsConfigOptions _config;
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public AzureDevOpsClient(AzureDevOpsConfigOptions config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _baseUrl = $"{config.OrganizationUrl}/{config.Project}/_apis";

            var handler = new HttpClientHandler();
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{config.PersonalAccessToken}"));
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {token}");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        /// <summary>
        /// Creates a pull request in Azure DevOps
        /// </summary>
        public async Task<int> CreatePullRequestAsync(
            string title,
            string description,
            string sourceBranch,
            string targetBranch,
            List<string> relatedFiles)
        {
            var requestBody = new
            {
                sourceRefName = $"refs/heads/{sourceBranch}",
                targetRefName = $"refs/heads/{targetBranch}",
                title = title,
                description = description,
                reviewers = Array.Empty<object>(),
                isDraft = false,
                workItemRefs = Array.Empty<object>()
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            var url = $"{_baseUrl}/git/repositories/{_config.Repository}/pullrequests?api-version=7.0";
            var response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to create PR: {response.StatusCode} - {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(responseContent))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("pullRequestId", out var prId))
                {
                    return prId.GetInt32();
                }
            }

            throw new Exception("Could not extract PR ID from response");
        }

        /// <summary>
        /// Gets pull request details
        /// </summary>
        public async Task<PullRequestDetails> GetPullRequestAsync(int pullRequestId)
        {
            var url = $"{_baseUrl}/git/repositories/{_config.Repository}/pullrequests/{pullRequestId}?api-version=7.0";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to get PR details: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(content))
            {
                var root = doc.RootElement;
                return new PullRequestDetails
                {
                    Id = root.GetProperty("pullRequestId").GetInt32(),
                    Title = root.GetProperty("title").GetString(),
                    Status = root.GetProperty("status").GetString(),
                    SourceBranch = root.GetProperty("sourceRefName").GetString(),
                    TargetBranch = root.GetProperty("targetRefName").GetString(),
                    CreatedBy = root.GetProperty("createdBy").GetProperty("displayName").GetString()
                };
            }
        }

        /// <summary>
        /// Pushes a tag to mark the removal
        /// </summary>
        public async Task<bool> CreateTagAsync(string tagName, string commitHash)
        {
            var requestBody = new
            {
                name = tagName,
                objectId = commitHash
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            var url = $"{_baseUrl}/git/repositories/{_config.Repository}/refs?api-version=7.0";
            var response = await _httpClient.PostAsync(url, content);

            return response.IsSuccessStatusCode;
        }

        /// <summary>
        /// Updates PR description
        /// </summary>
        public async Task<bool> UpdatePullRequestAsync(int pullRequestId, string description)
        {
            var requestBody = new { description };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            var url = $"{_baseUrl}/git/repositories/{_config.Repository}/pullrequests/{pullRequestId}?api-version=7.0";
            var response = await _httpClient.PatchAsync(url, content);

            return response.IsSuccessStatusCode;
        }
    }

    public class PullRequestDetails
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public string SourceBranch { get; set; }
        public string TargetBranch { get; set; }
        public string CreatedBy { get; set; }
    }
}
