using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SwarmCore.Coding;
using SwarmCore.Coding.Routing;
using SwarmCore.Missions;

namespace SwarmCode.Hosting;

/// <summary>
/// Reads trusted host configuration at the verification boundary so a corrected
/// check does not remain cached for the lifetime of an open workspace.
/// Command execution stays in the shared coding verifier.
/// </summary>
public sealed class ReloadingCodingAcceptanceVerifier(ShellExecTool shell, IConfiguration configuration)
    : ICodingAcceptanceVerifier
{
    public Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var acceptance = new CodingAcceptanceOptions();
        lock (configuration)
        {
            if (configuration is IConfigurationRoot root) root.Reload();
            configuration.GetSection(CodingOptions.SectionName).GetSection("Acceptance").Bind(acceptance);
        }
        var verifier = new CodingCommandAcceptanceVerifier(shell,
            Options.Create(new CodingOptions { Acceptance = acceptance }));
        return verifier.VerifyAsync(context, ct);
    }
}
