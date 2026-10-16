namespace SwarmCore.Abstractions.Security;

/// <summary>Local credential selection for setup forms. Discovery returns metadata only.</summary>
public interface ISavedCredentialSuggestions
{
    Task<IReadOnlyList<SavedCredentialSuggestion>> FindAsync(SavedCredentialQuery query, CancellationToken ct = default);
    Task<SavedCredentialValue?> ResolveAsync(string reference, SavedCredentialQuery query, CancellationToken ct = default);
    Task<SavedCredentialSuggestion> SaveAsync(SavedCredentialSaveRequest request, CancellationToken ct = default);
}

public sealed record SavedCredentialQuery
{
    public IReadOnlyList<string> Sites { get; init; } = [];
    public string? Username { get; init; }
    public bool RequireUsername { get; init; } = true;
    public string? ServiceHint { get; init; }
}

public sealed record SavedCredentialSuggestion(string Reference, string Label, string? Username, string? Site);

/// <summary>For immediate local form population only; never include in prompts, logs, or receipts.</summary>
public sealed class SavedCredentialValue(string? username, string password)
{
    public string? Username { get; } = username;
    public string Password { get; } = password;
    public override string ToString() => nameof(SavedCredentialValue) + " { Password = [redacted] }";
}

/// <summary>An explicit user request to save a form's credential locally.</summary>
public sealed class SavedCredentialSaveRequest
{
    public string Label { get; init; } = "";
    public string? Username { get; init; }
    public string? Site { get; init; }
    public string Password { get; init; } = "";
    public override string ToString() => nameof(SavedCredentialSaveRequest) + " { Password = [redacted] }";
}
