namespace SwarmCore.Abstractions.OAuth;

/// <summary>Scopes token rejection evidence to the authorization being requested.</summary>
public static class OAuthTokenRejectionPolicy
{
    /// <summary>
    /// Failure to acquire an additional resource permission is not proof that
    /// the saved grant is unusable for its original resource. The original
    /// resource's own refresh remains responsible for detecting revocation.
    /// Use protocol error codes and request context, never provider prose.
    /// </summary>
    public static bool IsResourceAuthorizationFailure(string? errorCode, string? requestedScope) =>
        !string.IsNullOrWhiteSpace(requestedScope) &&
        errorCode?.Trim().ToLowerInvariant() is
            "invalid_grant" or "invalid_scope" or "consent_required" or "interaction_required";
}
