namespace SwarmCore.Secrets;

/// <summary>
/// The one definition of the <c>{{secret:name}}</c> reference syntax. This file is compiled into
/// SwarmCore.Secrets (<see cref="SecretReference"/>) and linked into SwarmCore.Abstractions, where the
/// credential detectors use it so a well-formed reference is never mistaken for a credential.
/// Neither assembly references the other, so the source file is shared instead of a type.
/// </summary>
internal static class SecretReferenceSyntax
{
    /// <summary>
    /// Vendor-issued tokens with a distinctive, high-entropy prefix (OpenAI/Anthropic, GitHub, Slack, AWS access
    /// key ids, HubSpot, Google). Shared with the credential detectors' vendor pattern so a name with one of these
    /// shapes is never protected as a reference.
    /// </summary>
    internal const string VendorTokenShape =
        @"sk-(?:ant-)?[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[bpsare]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|pat-[a-z]{2}\d-[A-Za-z0-9\-]{20,}|AIza[A-Za-z0-9_\-]{20,}";

    /// <summary>
    /// Matches one reference; the <c>name</c> group is the referenced vault name. Case-insensitive. The name must
    /// have a vault-name shape (<c>SecretNames.Normalize</c>'s alphabet <c>a-z 0-9 . _ / -</c>, at most 120
    /// characters; letters in either case) and must not be a vendor token, so <c>{{secret:ghp_…}}</c> or
    /// <c>{{secret:Hunter2!}}</c> is not a reference and the detectors still see the credential inside it.
    /// </summary>
    internal const string Pattern =
        @"\{\{\s*secret\s*:\s*(?!(?:" + VendorTokenShape + @")\s*\}\})(?<name>[A-Za-z0-9_][A-Za-z0-9._/\-]{0,119})\s*\}\}";
}
