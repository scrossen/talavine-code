namespace SwarmCore.Abstractions.GitHub;

/// <summary>
/// Portable GitHub service interface for accessing repository information.
/// All methods require explicit owner/repo parameters - no hard-coded defaults.
/// </summary>
public interface IGitHubService
{
    // Repository Operations
    
    /// <summary>
    /// Get information about a repository.
    /// </summary>
    Task<GitHubRepository> GetRepositoryInfoAsync(
        string owner, 
        string repo, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// List files in a repository directory. Returns empty list if path doesn't exist.
    /// </summary>
    Task<List<GitHubFile>> GetRepositoryFilesAsync(
        string owner, 
        string repo, 
        string path = "", 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get the content of a file.
    /// </summary>
    Task<GitHubFileContent> GetFileContentAsync(
        string owner, 
        string repo, 
        string path, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Recursively discover all files in a repository (or under a specific path).
    /// This dynamically discovers the structure - no hard-coded folder lists.
    /// </summary>
    Task<List<GitHubFile>> DiscoverAllFilesAsync(
        string owner,
        string repo,
        string basePath = "",
        string? extensionFilter = null,
        CancellationToken cancellationToken = default);
    
    // Commit Operations
    
    /// <summary>
    /// Get recent commits for a repository.
    /// </summary>
    Task<List<GitHubCommit>> GetRecentCommitsAsync(
        string owner, 
        string repo, 
        int count = 10, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get details for a specific commit.
    /// </summary>
    Task<GitHubCommit> GetCommitDetailsAsync(
        string owner, 
        string repo, 
        string sha, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get file changes for a specific commit.
    /// </summary>
    Task<List<GitHubFileChange>> GetCommitChangesAsync(
        string owner, 
        string repo, 
        string sha, 
        CancellationToken cancellationToken = default);
    
    // Issues and Pull Requests
    
    /// <summary>
    /// Get open issues for a repository.
    /// </summary>
    Task<List<GitHubIssue>> GetOpenIssuesAsync(
        string owner, 
        string repo, 
        int count = 10, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get open pull requests for a repository.
    /// </summary>
    Task<List<GitHubPullRequest>> GetOpenPullRequestsAsync(
        string owner, 
        string repo, 
        int count = 10, 
        CancellationToken cancellationToken = default);
    
    // Code Search
    
    /// <summary>
    /// Search for code in a repository.
    /// </summary>
    Task<List<GitHubSearchResult>> SearchCodeAsync(
        string owner,
        string repo,
        string query,
        CancellationToken cancellationToken = default);
    
    // Validation
    
    /// <summary>
    /// Test if the service is properly configured and can authenticate.
    /// </summary>
    Task<(bool IsHealthy, string Message)> TestConnectionAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for the GitHub service. All values must be provided by the application.
/// </summary>
public class GitHubServiceConfiguration
{
    /// <summary>
    /// GitHub Personal Access Token. Required for authentication.
    /// Can be classic (ghp_*) or fine-grained (github_pat_*).
    /// </summary>
    public string AccessToken { get; set; } = "";
    
    /// <summary>
    /// User agent string for API requests. Required by GitHub API.
    /// </summary>
    public string UserAgent { get; set; } = "SwarmCore-GitHub/1.0";
    
    /// <summary>
    /// Base URL for GitHub API. Defaults to api.github.com.
    /// Can be changed for GitHub Enterprise.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.github.com/";
}

/// <summary>
/// Provider interface for dynamic token retrieval.
/// Applications can implement this to fetch tokens from databases, key vaults, etc.
/// </summary>
public interface IGitHubTokenProvider
{
    /// <summary>
    /// Get the current access token.
    /// </summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

