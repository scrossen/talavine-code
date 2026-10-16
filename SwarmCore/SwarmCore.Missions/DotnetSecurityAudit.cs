using System.Text.Json;
using SwarmCore.Abstractions.Security;
using SwarmCore.Agent.Continual;
namespace SwarmCore.Missions;

/// <summary>Parses the installed NuGet CLI v1 schema; command success and coverage are distinct.</summary>
public static class DotnetSecurityAudit
{
    public static readonly string[] Arguments = ["package", "list", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1", "--no-restore"];
    public static (SecurityCoverage Coverage, IReadOnlyList<SecurityFinding> Findings) Parse(string target, string? version, SecurityCommandEvidence evidence)
    {
        SecurityCoverage Coverage(SecurityCoverageStatus status,string reason) => new("dotnet-package-audit",version,status,reason,[evidence.Id]);
        if (evidence.Cancelled) return (Coverage(SecurityCoverageStatus.Cancelled,"Audit cancelled; feed coverage is unknown."),[]);
        if (evidence.TimedOut || !evidence.Settled) return (Coverage(SecurityCoverageStatus.Inconclusive,"Audit did not settle successfully."),[]);
        if (evidence.ExitCode != 0) return (Coverage(SecurityCoverageStatus.Failed,"Audit command failed; no clean conclusion."),[]);
        try
        {
            using var document = JsonDocument.Parse(evidence.Stdout);
            var root = document.RootElement;
            if (!root.TryGetProperty("version",out var schema) || schema.GetInt32()!=1
                || !root.TryGetProperty("projects",out var projects) || projects.ValueKind != JsonValueKind.Array
                || projects.GetArrayLength()==0 || !string.IsNullOrWhiteSpace(evidence.Stderr)
                || !root.TryGetProperty("sources",out var sources) || sources.ValueKind!=JsonValueKind.Array || sources.GetArrayLength()==0)
                return (Coverage(SecurityCoverageStatus.Inconclusive,"Missing/partial project data or scanner diagnostics."),[]);
            var findings = new Dictionary<string,SecurityFinding>(StringComparer.Ordinal);
            var complete = true;
            foreach (var project in projects.EnumerateArray())
            {
                if (!project.TryGetProperty("frameworks",out var frameworks) || frameworks.ValueKind!=JsonValueKind.Array || frameworks.GetArrayLength()==0)
                { complete=false; continue; }
                if (project.TryGetProperty("logs",out _) || project.TryGetProperty("problems",out _)) complete=false;
                foreach(var framework in frameworks.EnumerateArray())
                {
                if(framework.TryGetProperty("logs",out _) || framework.TryGetProperty("problems",out _)) complete=false;
                foreach(var kind in new[]{"topLevelPackages","transitivePackages"})
                {
                    if(!framework.TryGetProperty(kind,out var packages)) continue;
                    foreach(var package in packages.EnumerateArray())
                    {
                        if(!package.TryGetProperty("vulnerabilities",out var vulnerabilities)) continue;
                        var id=package.GetProperty("id").GetString()!; var resolved=package.GetProperty("resolvedVersion").GetString()!;
                        foreach(var vulnerability in vulnerabilities.EnumerateArray())
                        {
                            var advisory=vulnerability.GetProperty("advisoryurl").GetString()!;
                            var severity=vulnerability.GetProperty("severity").GetString()!;
                            var fingerprint=SecurityFindingIdentity.Create(target,"dependency-advisory",id+"@"+resolved+":"+advisory,pathLocation:false);
                            findings[fingerprint]=new(fingerprint,"dependency-advisory","dependency",null,null,id,resolved,
                                "Installed package is listed in the vulnerability feed.","Advisory impact requires application-specific triage.",
                                "Feed severity: "+severity,0.9,SecurityFindingStatus.Suspected,[evidence.Id],["Run dotnet "+string.Join(" ",Arguments)],
                                "Review the supplied advisory and repository version/lockfile conventions before updating."){AdvisoryUrl=advisory};
                        }
                    }
                }
                }
            }
            if(root.TryGetProperty("logs",out _) || root.TryGetProperty("problems",out _)) complete=false;
            return(Coverage(complete?SecurityCoverageStatus.Completed:SecurityCoverageStatus.Inconclusive,
                complete?"Requested package-feed query completed; this is not source or whole-system security coverage.":"Some project/feed coverage is incomplete."),findings.Values.ToArray());
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {return(Coverage(SecurityCoverageStatus.Inconclusive,"Unrecognized or malformed audit output; no clean conclusion."),[]);}
    }
}
public static class SecurityFindingIdentity
{
    public static string Create(string target,string rule,string location,bool pathLocation=true)
    {
        var canonical=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if(pathLocation)
        {
            location=Path.GetRelativePath(canonical,Path.GetFullPath(Path.Combine(canonical,location))).Replace('\\','/');
            if(OperatingSystem.IsWindows()) location=location.ToUpperInvariant();
        }
        if(OperatingSystem.IsWindows()) canonical=canonical.ToUpperInvariant();
        return MissionVerification.Digest(JsonSerializer.Serialize(new[]{canonical,rule,location}));
    }
}
