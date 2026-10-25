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
