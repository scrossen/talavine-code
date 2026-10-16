namespace SwarmCore.Abstractions.Knowledge;

/// <summary>
/// A source of first-party knowledge that projects its data into the context
/// engine through the normal context-fact funnel. One implementation per source
/// kind (calendar, tasks, contacts, user apps). Each contributor owns the facts
/// it emits and is responsible for keeping them in sync — emit the current
/// desired set, retire what's gone.
///
/// <para>
/// This is the single abstraction that unifies the first-party PIM gaps and the
/// user-extensible vibe case: everything that wants to become knowledge becomes
/// an <see cref="IKnowledgeContributor"/>.
/// </para>
/// </summary>
public interface IKnowledgeContributor
{
    /// <summary>Stable source-kind tag (e.g. "calendar"); also the ContextFact SourceKind.</summary>
    string SourceKind { get; }

    /// <summary>
    /// Reconcile this source's contribution: emit current facts, retire stale
    /// ones. Cheap when nothing changed. <paramref name="force"/> bypasses the
    /// change-skip optimization (used by explicit rebuilds).
    /// </summary>
    Task<int> ReconcileAsync(bool force = false, CancellationToken ct = default);
}
