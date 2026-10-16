namespace SwarmCore.Agent.Business;

/// <summary>The same saved draft has one effect identity whether prepared directly or by a scheduled sequence.</summary>
public static class BusinessProspectingActionIdentity
{
    public static string Objective(Guid caseId) => "customer-draft:" + caseId.ToString("N");
    public static string Slot(int draftRevision) => "draft:" + draftRevision;
    public static string SequenceSource(Guid planId, Guid prospectId, Guid caseId, int draftRevision)
        => "customer-draft:" + planId.ToString("N") + ":" + prospectId.ToString("N") + ":" + caseId.ToString("N") + ":" + draftRevision;
    public static bool TrySequenceSource(string source, string binding, out string objective, out string slot)
    {
        objective = slot = "";
        var parts = source.Split(':');
        if (parts.Length != 5 || parts[0] != "customer-draft" || string.Join(':', parts.Take(3)) != binding
            || !Guid.TryParseExact(parts[1], "N", out var plan) || plan == Guid.Empty
            || !Guid.TryParseExact(parts[2], "N", out var prospect) || prospect == Guid.Empty
            || !Guid.TryParseExact(parts[3], "N", out var caseId) || caseId == Guid.Empty
            || !int.TryParse(parts[4], out var revision) || revision < 1) return false;
        objective = Objective(caseId); slot = Slot(revision); return true;
    }
}
