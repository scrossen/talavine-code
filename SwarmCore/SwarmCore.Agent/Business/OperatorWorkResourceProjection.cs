using System.Net.Mail;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.AgentWork;
using SwarmCore.Agent.Continual;
namespace SwarmCore.Agent.Business;

public enum OperatorWorkResourceProjectionKind { Unknown, Sha256Template, FreshOriginalSourceBirth }
public sealed record OperatorWorkResourceRule(string Domain,OperatorWorkResourceAccess Access,
    OperatorWorkResourceProjectionKind Kind,string? IdentityTemplate=null,bool PartitionWideRead=false,string? WhenToolId=null);
public sealed record OperatorWorkResourceProjection(int SchemaVersion,IReadOnlyList<OperatorWorkResourceRule> Rules);
public sealed record OperatorWorkProjectedResource(OperatorWorkResource Resource,bool PartitionWideRead=false);
public sealed record OperatorWorkLateBoundResource(string Domain,OperatorWorkResourceAccess Access,string Reason);
public sealed record OperatorWorkResourcePlan(IReadOnlyList<OperatorWorkProjectedResource> KnownResources,IReadOnlyList<OperatorWorkLateBoundResource> LateBoundResources);

/// <summary>Metadata-only preflight; fresh birth identities are resolved by normal admission, never invented by this planner.</summary>
public static class OperatorWorkResourceProjectionPolicy
{
    public static string PolicySha256 {get;}=ContinualVerificationCandidatePolicy.Hash("operator-work-resource-plan/v1|registered-complete-rules|exact-template-inputs|plain-contact-email|fresh-source-inventory|late-bound-normal-birth|tenant-wide-read-write-conflict|opaque-domain-target");
    public static OperatorWorkResourcePlan? Project(OperatorWorkResourceProjection? projection,int tenantId,int employeeId,
        string sourceId,string? contactEmail,bool originalSourceIsProvenFresh,IReadOnlySet<string>? requestedToolIds=null)
    {
        if(projection?.SchemaVersion!=1 || projection.Rules is null || projection.Rules.Count is <1 or >128 || tenantId<=0 || employeeId<=0 || string.IsNullOrWhiteSpace(sourceId))return null;
        var values=new Dictionary<string,string>(StringComparer.Ordinal){{"tenantId",tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture)},
            {"employeeId",employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"sourceId",sourceId}};
        if(MailAddress.TryCreate(contactEmail?.Trim(),out var address) && address.Address==contactEmail?.Trim() && address.Address.Length<=255)
            values["contactEmail"]=address.Address.ToLowerInvariant();
        var known=new List<OperatorWorkProjectedResource>();var late=new List<OperatorWorkLateBoundResource>();
        foreach(var rule in projection.Rules)
        {
            if(rule.WhenToolId is not null){if(requestedToolIds is null)return null;if(!requestedToolIds.Contains(rule.WhenToolId))continue;}
            if(string.IsNullOrWhiteSpace(rule.Domain)||rule.Domain.Length>100||rule.Access is not (OperatorWorkResourceAccess.Read or OperatorWorkResourceAccess.Write)
                || rule.PartitionWideRead&&rule.Access!=OperatorWorkResourceAccess.Read)return null;
            if(rule.Kind==OperatorWorkResourceProjectionKind.FreshOriginalSourceBirth)
            {
                if(!originalSourceIsProvenFresh||rule.IdentityTemplate is not null||rule.PartitionWideRead)return null;
                late.Add(new(rule.Domain,rule.Access,"Fresh source has no original operation; normal atomic admission must allocate and verify the unique trigger/source-job identity before inference."));
                continue;
            }
            if(rule.Kind!=OperatorWorkResourceProjectionKind.Sha256Template || string.IsNullOrWhiteSpace(rule.IdentityTemplate)||rule.IdentityTemplate.Length>512)return null;
            var invalid=false;
            var expanded=Regex.Replace(rule.IdentityTemplate,"\\{([A-Za-z][A-Za-z0-9]*)\\}",m=>{if(!values.TryGetValue(m.Groups[1].Value,out var value)){invalid=true;return "";}return value;});
            var remainder=Regex.Replace(rule.IdentityTemplate,"\\{([A-Za-z][A-Za-z0-9]*)\\}","" );
            if(invalid||remainder.Contains('{')||remainder.Contains('}'))return null;
            known.Add(new(new(rule.Domain,ContinualVerificationCandidatePolicy.Hash(expanded),rule.Access),rule.PartitionWideRead));
        }
        if(known.Count==0 || known.Select(r=>(r.Resource.Domain,r.Resource.Target)).Distinct().Count()!=known.Count
            || late.Select(r=>r.Domain).Distinct(StringComparer.Ordinal).Count()!=late.Count)return null;
        return new(known,late);
    }
    public static bool HasConflict(OperatorWorkResourcePlan plan,IReadOnlyList<OperatorWorkProjectedResource> occupied)
        => plan.KnownResources.Any(wanted=>occupied.Any(current=>
            wanted.PartitionWideRead&&current.Resource.Access==OperatorWorkResourceAccess.Write
            || current.PartitionWideRead&&wanted.Resource.Access==OperatorWorkResourceAccess.Write
            || wanted.Resource.Domain==current.Resource.Domain&&wanted.Resource.Target==current.Resource.Target
                &&(wanted.Resource.Access==OperatorWorkResourceAccess.Write||current.Resource.Access==OperatorWorkResourceAccess.Write)));
}
