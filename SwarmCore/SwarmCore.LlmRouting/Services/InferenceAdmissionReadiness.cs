namespace SwarmCore.LlmRouting.Services;

public sealed record InferenceAdmissionIdentity(string Provider,string Model,string ProofSha256);
public sealed record InferenceAdmissionReadiness(int SchemaVersion,string State,DateTimeOffset CheckedAtUtc,
    InferenceAdmissionIdentity? Identity,string ReasonCode,string Limits);

/// <summary>Observes the same current validator used before dispatch. It performs no inference or transport probe and grants no future allowance.</summary>
public static class InferenceAdmissionReadinessPolicy
{
    private const string Limits="Point-in-time admission validation only. Queue, provider and physical dispatch must revalidate; no registration, reservation, network probe or inference.";
    public static InferenceAdmissionReadiness Capture(IInferenceAdmission? admission,string? expectedProvider,string? expectedModel)
    {
        InferenceAdmissionReadiness Result(string state,InferenceAdmissionIdentity? identity,string reason)
            => new(1,state,DateTimeOffset.UtcNow,identity,reason,Limits);
        try
        {
            var identity=admission?.Identity;
            if(identity is null||string.IsNullOrWhiteSpace(identity.Provider)||string.IsNullOrWhiteSpace(identity.Model)
                ||identity.Provider.Length>100||identity.Model.Length>2000||identity.Provider.Any(char.IsControl)||identity.Model.Any(char.IsControl)
                ||identity.ProofSha256 is not {Length:64}||!identity.ProofSha256.All(Uri.IsHexDigit))
                return Result("unknown",null,"admission-identity-unavailable");
            if(identity.Provider!=expectedProvider||identity.Model!=expectedModel)return Result("denied",identity,"admission-route-mismatch");
            admission!.Validate();
            if(admission.Identity!=identity)return Result("unknown",null,"admission-identity-changed");
            return Result("verified",identity,"admission-currently-valid");
        }
        catch(UnauthorizedAccessException){return Result("denied",null,"admission-validation-denied");}
        catch(Exception error) when(error is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {return Result("unknown",null,"admission-validation-unknown");}
    }
}
