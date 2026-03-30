# FiscaalGemak Feature Toggle Cleanup Agent

An AI-powered C# agent that automatically removes a feature toggle from the entire codebase and opens an Azure DevOps PR.

## What it does (8 steps)

```
STEP 1  Connect to ADO, verify repo + branch
STEP 2  List all files in the repository
STEP 3  Scan every file for references to the toggle (fast string-match pre-filter)
STEP 4  Create a cleanup branch  cleanup/remove-{feature}-toggle-{date}
STEP 5  Send each candidate file to Claude to apply all removal rules from the guide
STEP 6  Push all cleaned files as a single commit
STEP 7  Ask Claude to write a detailed PR description
STEP 8  Create the Pull Request in Azure DevOps
```

## How Claude knows the rules

The **Universal Feature Toggle Removal Guide** rules are embedded directly into Claude's system prompt.
Claude applies all 10 patterns from the guide to every file it receives:

| Pattern | Example | Action |
|---------|---------|--------|
| Simple if block | `if (toggle) { A(); }` | Remove wrapper, keep `A()` |
| If-else block | `if (toggle) { A() } else { B() }` | Keep `A()`, delete `else` |
| Negated if | `if (!toggle) { ... }` | Delete entire block |
| Complex boolean | `cond1 && toggle && cond2` | Remove toggle from expression |
| Method parameter | `method(id, toggle)` | Replace with `method(id, true)` |
| Ternary | `toggle ? a : b` | Use `a` |
| Variable (Pattern C) | `isFlagEnabled: boolean` + assignment + usages | Delete all three |
| Dictionary access | `fiscaalGemakFeatures[FiscaalGemakFeatures[...]]` | Apply Pattern 1 rules |
| Enum entry | `FeatureName = 42,` | Delete line + description |
| Unused service | `IPilotFeatureService` injection | Remove if no longer needed |

## Requirements

- .NET 8 SDK
- Azure DevOps PAT with: **Code → Read & Write**, **Pull Requests → Contribute**
- Anthropic API key

## Setup

```bash
# 1. Edit appsettings.json with your credentials and feature name
# 2. Build and run
dotnet run

# Or pass everything via CLI:
dotnet run -- \
  --feature ShowSheetPreview \
  --org fiscaalgemak \
  --project MyProject \
  --repo MyRepo \
  --pat YOUR_ADO_PAT \
  --anthropic-key sk-ant-...
```

## Config reference

| Key | CLI flag | Description |
|-----|----------|-------------|
| `FeatureName` | `--feature` | Exact enum value, e.g. `ShowSheetPreview` |
| `AzureDevOpsOrg` | `--org` | ADO organisation name |
| `AzureDevOpsProject` | `--project` | ADO project name |
| `AzureDevOpsRepo` | `--repo` | Repository name |
| `AzureDevOpsPat` | `--pat` | Personal Access Token |
| `AnthropicApiKey` | `--anthropic-key` | Anthropic API key |
| `AnthropicModel` | `--model` | Default: `claude-opus-4-6` |
| `BaseBranch` | `--branch` | Default: `main` |
| `FileExtensions` | `--extensions` | Comma-separated, default: `.cs,.ts,.html,.cshtml,.js` |
| `ExcludedFolders` | `--exclude` | Comma-separated folders to skip |

## Environment variables

All settings can also be set as environment variables (same key names):

```bash
export FeatureName=ShowSheetPreview
export AzureDevOpsPat=your_pat
export AnthropicApiKey=sk-ant-...
dotnet run
```
"# FeatureToggleRemoval" 
