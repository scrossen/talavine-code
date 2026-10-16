namespace SwarmCore.Knowledge;

/// <summary>Message identities resolve inside an account; an account alone may describe another source kind.</summary>
public static class KnowledgeMessageIdentity
{
    public static void Validate(Guid? accountId, Guid? messageId)
    {
        if (messageId is null) return;
        if (messageId == Guid.Empty || accountId is null || accountId == Guid.Empty)
            throw new InvalidDataException("A message source requires non-empty account and message identities; source reprojection is required.");
    }
}
