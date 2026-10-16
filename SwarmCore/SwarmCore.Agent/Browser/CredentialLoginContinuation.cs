namespace SwarmCore.Agent.Browser;

/// <summary>Credential-free progress for a bounded, user-started browser login.</summary>
public enum CredentialLoginProgress
{
    WaitingForTerms,
    WaitingForVerification,
    WaitingForPassword,
    VerifyingAccount,
    SigningIn
}

public enum CredentialLoginStage { Loading, Username, UsernameWithConsent, Password, Verification }
public enum CredentialLoginAction { Wait, PrepareUsername, SubmitUsername, SubmitPassword, WaitForConsent, WaitForVerification }

/// <summary>Retained while the same browser login is pending, including across explicit resume calls.</summary>
public sealed record CredentialLoginAttemptState(bool UsernamePrepared = false, bool UsernameSubmitted = false,
    bool PasswordPrepared = false, bool PasswordSubmitted = false);

/// <summary>
/// A host identifies visible, trusted login controls and verifies the final account. This policy
/// permits each credential submission only once, requires explicit host authority for consent,
/// and leaves identity verification challenges to the user.
/// It holds no credential values, browser handles, selectors, or site-specific paths.
/// </summary>
public static class CredentialLoginContinuation
{
    public static CredentialLoginAction Next(CredentialLoginStage stage, CredentialLoginAttemptState state,
        bool allowConsentAcceptance = false) => stage switch
    {
        CredentialLoginStage.Verification => CredentialLoginAction.WaitForVerification,
        CredentialLoginStage.UsernameWithConsent when state.UsernameSubmitted || state.PasswordSubmitted => CredentialLoginAction.Wait,
        CredentialLoginStage.UsernameWithConsent when allowConsentAcceptance => CredentialLoginAction.SubmitUsername,
        CredentialLoginStage.UsernameWithConsent when !state.UsernamePrepared && !state.PasswordSubmitted => CredentialLoginAction.PrepareUsername,
        CredentialLoginStage.UsernameWithConsent => CredentialLoginAction.WaitForConsent,
        CredentialLoginStage.Username when !state.UsernameSubmitted && !state.PasswordSubmitted => CredentialLoginAction.SubmitUsername,
        CredentialLoginStage.Password when !state.PasswordSubmitted => CredentialLoginAction.SubmitPassword,
        _ => CredentialLoginAction.Wait
    };
}
