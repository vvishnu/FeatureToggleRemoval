namespace FeatureToggleAgent;

// Parsed from the markdown file via AI
public record FileChange(
    string FilePath,
    string Description,
    List<CodeBlockRemoval> Removals
);

public record CodeBlockRemoval(
    string PatternOrCode,  // the code/pattern to find and remove
    string? ReplacementCode, // null = delete entirely, non-null = replace with this
    RemovalType Type
);

public enum RemovalType
{
    RemoveBlock,            // Remove the entire if block and keep inner body (feature enabled path)
    RemoveEntireIfElse,     // Remove entire if/else, no replacement
    RemoveLines,            // Remove specific lines matching pattern
    ReplaceWithContent      // Replace block with specific content
}

public record ParsedMarkdown(
    string FeatureName,
    string? FeatureToggleName,
    List<FileChange> FileChanges,
    string RawMarkdown
);

// Azure DevOps API models
public record AdoRef(string name, string objectId, string url);

public record AdoPushItem(
    string path,
    string changeType,  // "edit" | "delete"
    AdoItemContent? newContent = null
);

public record AdoItemContent(string content, string contentType = "rawtext");

public record AdoPushRequest(
    List<AdoRefUpdate> refUpdates,
    List<AdoCommit> commits
);

public record AdoRefUpdate(string name, string oldObjectId);

public record AdoCommit(string comment, List<AdoChange> changes);

public record AdoChange(string changeType, AdoItem item, AdoNewContent? newContent = null);

public record AdoItem(string path);

public record AdoNewContent(string content, string contentType = "rawtext");

public record PullRequestResult(int pullRequestId, string webUrl);
