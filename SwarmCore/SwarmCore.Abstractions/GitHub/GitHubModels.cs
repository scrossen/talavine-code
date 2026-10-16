namespace SwarmCore.Abstractions.GitHub;

/// <summary>
/// Information about a GitHub repository.
/// </summary>
public class GitHubRepository
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Description { get; set; }
    public string? Language { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Size { get; set; }
    public string Owner { get; set; } = "";
    public bool IsPrivate { get; set; }
    public string HtmlUrl { get; set; } = "";
}

/// <summary>
/// Information about a file or directory in a repository.
/// </summary>
public class GitHubFile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>
    /// "file" or "dir"
    /// </summary>
    public string Type { get; set; } = "";
    public int Size { get; set; }
    public string Sha { get; set; } = "";
    public string? DownloadUrl { get; set; }
    
    public bool IsDirectory => Type == "dir";
    public bool IsFile => Type == "file";
}

/// <summary>
/// Content of a file in a repository.
/// </summary>
public class GitHubFileContent
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public int Size { get; set; }
    public string Sha { get; set; } = "";
    public string Encoding { get; set; } = "";
}

/// <summary>
/// Information about a commit.
/// </summary>
public class GitHubCommit
{
    public string Sha { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public DateTime Date { get; set; }
    public string Url { get; set; } = "";
    public GitHubCommitStats? Stats { get; set; }
}

/// <summary>
/// Statistics for a commit.
/// </summary>
public class GitHubCommitStats
{
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// Information about a file changed in a commit.
/// </summary>
public class GitHubFileChange
{
    public string Filename { get; set; } = "";
    /// <summary>
    /// "added", "modified", "removed", "renamed"
    /// </summary>
    public string Status { get; set; } = "";
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public int Changes { get; set; }
    public string? Patch { get; set; }
}

/// <summary>
/// Information about an issue.
/// </summary>
public class GitHubIssue
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<string> Labels { get; set; } = new();
    public string HtmlUrl { get; set; } = "";
}

/// <summary>
/// Information about a pull request.
/// </summary>
public class GitHubPullRequest
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "";
    public string Author { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsMerged { get; set; }
    public string HtmlUrl { get; set; } = "";
}

/// <summary>
/// Result from a code search.
/// </summary>
public class GitHubSearchResult
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha { get; set; } = "";
    public string? HtmlUrl { get; set; }
    public List<string> TextMatches { get; set; } = new();
}

/// <summary>
/// Structure analysis of a code file.
/// </summary>
public class CodeStructure
{
    public string FilePath { get; set; } = "";
    public string Language { get; set; } = "";
    public int LineCount { get; set; }
    public List<string> Classes { get; set; } = new();
    public List<string> Methods { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
}

/// <summary>
/// A business rule extracted from code.
/// </summary>
public class BusinessRule
{
    public string Description { get; set; } = "";
    public int LineNumber { get; set; }
    public string FilePath { get; set; } = "";
    public string Context { get; set; } = "";
}

