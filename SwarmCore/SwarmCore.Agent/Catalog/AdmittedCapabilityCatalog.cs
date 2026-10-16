using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Reflection;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.Catalog;

/// <summary>Host-owned, immutable admission snapshot. No model/tool can change its grants.</summary>
public sealed record CapabilityAdmissionManifest(
    string RunId, string ProfileBinding, string TenantBinding, int Revision,
    IReadOnlyDictionary<string, string> DescriptorHashes,
    IReadOnlyList<Guid> SourceAccounts, IReadOnlyList<string> ProviderModelRoutes)
{
    public IReadOnlyList<Guid> AgentProfileIds { get; init; } = [];
    public IReadOnlyList<string> ActingAgentIds { get; init; } = [];
}

/// <summary>
/// Startup registrations remain possible, but invocation is closed until the host
/// admits exact descriptors. Discovery, direct invocation and replay use the same fence.
/// </summary>
public sealed class AdmittedCapabilityCatalog(ICurrentTurnContextAccessor turns,
    Func<CapabilityAdmissionManifest, CurrentTurnContext, CancellationToken, ValueTask<bool>> validateHost) : ICapabilityCatalog
{
    private readonly object gate = new();
    private readonly InMemoryCapabilityCatalog inner = new();
    private static readonly ConcurrentDictionary<Assembly, string> AssemblyHashes = new();
    private CapabilityAdmissionManifest? admitted;
    private IReadOnlySet<string> ids = Array.Empty<string>().ToFrozenSet();
    private IReadOnlySet<Guid> accounts = Array.Empty<Guid>().ToFrozenSet();
    private IReadOnlySet<string> routes = Array.Empty<string>().ToFrozenSet();
    public bool IsAdmitted { get { lock (gate) return admitted is not null; } }

    public void Register(ToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (gate)
        {
            if (admitted is not null && !ids.Contains(tool.ToolId)) return;
            var registered = BuildRegisteredDescriptor(tool);
            if (admitted is not null)
            {
                // Normal hosted registrars can finish after the controlled host seals
                // its reviewed subset. Publication never extends that immutable grant.
                if (Fingerprint(registered) == admitted.DescriptorHashes[registered.ToolId]) return;
                throw new InvalidOperationException("An admitted capability changed; restart and re-admit changed registrations.");
            }
            inner.Register(registered);
        }
    }

    // The same normal registration builder emits data only here. No tool is
    // registered, admitted, exported or invoked by this metadata observation.
    public ToolDispatchDescriptorEvidence DescribeRegistration(ToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock(gate) return ToolDispatchPolicy.Evidence(BuildRegisteredDescriptor(tool));
    }

    private ToolDescriptor BuildRegisteredDescriptor(ToolDescriptor tool)
    {
        var original=tool with
        {
            InputSchema=(JsonObject?)tool.InputSchema?.DeepClone(),
            Keywords=tool.Keywords.ToArray(),UserInvocationPhrases=tool.UserInvocationPhrases.ToArray(),
            ImplementationFingerprint=ImplementationFor(tool),
        };
        return original with {Invoke=(input,ct)=>InvokeAsync(original,input,ct)};
    }

    public void Admit(CapabilityAdmissionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (gate)
        {
            if (admitted is not null) throw new InvalidOperationException("Admission is immutable for this host lifetime.");
            var frozen = JsonSerializer.Deserialize<CapabilityAdmissionManifest>(JsonSerializer.Serialize(manifest))!;
            if (string.IsNullOrWhiteSpace(frozen.RunId) || string.IsNullOrWhiteSpace(frozen.ProfileBinding)
                || string.IsNullOrWhiteSpace(frozen.TenantBinding) || frozen.Revision < 1
                || frozen.ProviderModelRoutes.Count == 0 || frozen.SourceAccounts.Any(id => id == Guid.Empty))
                throw new InvalidOperationException("Admission requires run, profile, tenant, revision, account and route bindings.");
            if (frozen.AgentProfileIds.Count == 0 || frozen.AgentProfileIds.Any(id => id == Guid.Empty)
                || frozen.ActingAgentIds.Count == 0 || frozen.ActingAgentIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Admission requires exact runtime profiles and acting principals.");
            foreach (var (id, expected) in frozen.DescriptorHashes)
            {
                var tool = inner.Get(id) ?? throw new InvalidOperationException("An admitted capability is not registered: " + id);
                if (!string.Equals(Fingerprint(tool), expected, StringComparison.Ordinal))
                    throw new InvalidOperationException("An admitted capability changed: " + id);
            }
            ids = frozen.DescriptorHashes.Keys.ToFrozenSet(StringComparer.Ordinal);
            accounts = frozen.SourceAccounts.ToFrozenSet();
            routes = frozen.ProviderModelRoutes.ToFrozenSet(StringComparer.Ordinal);
            admitted = frozen;
        }
    }

    public bool Unregister(string toolId)
    {
        lock (gate)
        {
            if (admitted is not null) throw new InvalidOperationException("The admitted capability catalog is sealed.");
            return inner.Unregister(toolId);
        }
    }
    public ToolDescriptor? Get(string toolId) { lock (gate) return Visible(toolId) ? inner.Get(toolId) : null; }
    public IReadOnlyList<ToolDescriptor> All() { lock (gate) return inner.All().Where(t => Visible(t.ToolId)).ToArray(); }
    public IReadOnlyList<ToolDescriptor> GetForAgent(string ownerAgentId)
        { lock (gate) return inner.GetForAgent(ownerAgentId).Where(t => Visible(t.ToolId)).ToArray(); }
    public IReadOnlyList<ToolDescriptor> Search(string goal, string? ownerAgentId = null, int topK = 10)
    {
        lock (gate)
            return inner.Search(goal, ownerAgentId, int.MaxValue).Where(t => Visible(t.ToolId)).Take(Math.Clamp(topK, 0, 100)).ToArray();
    }
    private bool Visible(string id) => admitted is null || ids.Contains(id);
    public bool OwnsDescriptor(ToolDescriptor descriptor)
    {
        lock (gate)
        {
            var current = inner.Get(descriptor.ToolId);
            return admitted is not null && ids.Contains(descriptor.ToolId) && current is not null
                && ReferenceEquals(current.Invoke, descriptor.Invoke)
                && Fingerprint(current) == admitted.DescriptorHashes[descriptor.ToolId]
                && Fingerprint(descriptor) == admitted.DescriptorHashes[descriptor.ToolId];
        }
    }

    private async Task<object?> InvokeAsync(ToolDescriptor tool, object input, CancellationToken ct)
    {
        // Freeze all typed caller-owned collections/buffers before any await or effect check.
        var frozenInput = JsonSerializer.SerializeToElement(input, tool.InputType).Deserialize(tool.InputType)
            ?? throw new UnauthorizedAccessException("Capability input could not be frozen.");
        CurrentTurnContext original;
        CapabilityAdmissionManifest manifest;
        IReadOnlySet<Guid> narrowed;
        lock (gate)
        {
            if (admitted is null || !ids.Contains(tool.ToolId))
                throw new UnauthorizedAccessException("Capability has no host admission.");
            original = turns.Current ?? throw new UnauthorizedAccessException("Capability invocation has no authenticated turn.");
            manifest = admitted;
            if (Fingerprint(tool) != manifest.DescriptorHashes[tool.ToolId])
                throw new UnauthorizedAccessException("The admitted descriptor contract changed.");
            if (original.ToolsWithheldByUser == true || original.AdmittedToolIds is null || !original.AdmittedToolIds.Contains(tool.ToolId)
                || original.ProfileId is null || !manifest.AgentProfileIds.Contains(original.ProfileId.Value)
                || !manifest.ActingAgentIds.Contains(original.ActingAgentId ?? "interactive", StringComparer.Ordinal))
                throw new UnauthorizedAccessException("The current principal/profile has not admitted this capability.");
            if (original.Provider is null || original.Model is null || !routes.Contains(original.Provider + "/" + original.Model))
                throw new UnauthorizedAccessException("The effective model route is not admitted.");
            narrowed = original.CommunicationScopeKind == AgentCommunicationScopeKind.None ? Array.Empty<Guid>().ToFrozenSet()
                : original.AllowedSourceAccountIds is not null ? original.AllowedSourceAccountIds.Where(accounts.Contains).ToFrozenSet()
                : original.CommunicationScopeKind == AgentCommunicationScopeKind.TrustedProfileWide ? accounts
                : Array.Empty<Guid>().ToFrozenSet();
            if (tool.NativeComputerUseEnabled || tool.NativeComputerSession is not null)
                throw new UnauthorizedAccessException("Native computer surfaces require separate host admission.");
            // Resolve argument-aware effects before invocation, not only the advertised tier.
            if (tool.EffectiveSideEffect(frozenInput) != tool.SideEffect)
                throw new UnauthorizedAccessException("This invocation changes the admitted effect contract.");
        }
        if (!await validateHost(manifest, original, ct))
            throw new UnauthorizedAccessException("The current host run/profile/tenant binding does not match admission.");
        turns.Current = original with
        {
            AllowedSourceAccountIds = narrowed,
            CommunicationScopeKind = AgentCommunicationScopeKind.SelectedAccounts,
            CommunicationScopeAuthority = "host-capability-admission",
        };
        try { return await tool.Invoke(frozenInput, ct); }
        finally { turns.Current = original; }
    }

    public static string Fingerprint(ToolDescriptor tool)
    {
        var contract = DescriptorContractJson(tool);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract))).ToLowerInvariant();
    }

    public static string DescriptorContractJson(ToolDescriptor tool)
        => JsonSerializer.Serialize(new
        {
            tool.ToolId, tool.OwnerAgentId, tool.Description, tool.SideEffect, tool.DataAccess, tool.Cost,
            Input = tool.InputType.AssemblyQualifiedName, Output = tool.OutputType.AssemblyQualifiedName,
            Schema = tool.InputSchema?.ToJsonString(), tool.RemoteProfileAvailable, tool.NativeComputerUseEnabled,
            HasNativeSession = tool.NativeComputerSession is not null,
            HasEffectResolver = tool.ResolveSideEffect is not null,
            tool.ImplementationFingerprint,
        });

    private static string ImplementationFor(ToolDescriptor tool)
    {
        string Stamp(Delegate? method)
        {
            if (method is null) return "none";
            var assembly = method.Method.Module.Assembly;
            var hash = AssemblyHashes.GetOrAdd(assembly, a => a.IsDynamic || string.IsNullOrEmpty(a.Location)
                ? a.ManifestModule.ModuleVersionId.ToString("N")
                : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))).ToLowerInvariant());
            return hash + ":" + method.Method.Module.ModuleVersionId + ":" + method.Method.MetadataToken;
        }
        // Assembly identity does not capture immutable policy data closed over by
        // a handler. Retain the host's contract binding as well as the physical
        // implementation stamp; neither can substitute for the other.
        var hostContract = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(tool.ImplementationFingerprint ?? ""))).ToLowerInvariant();
        return Stamp(tool.Invoke) + "/" + Stamp(tool.ResolveSideEffect) + "/host-contract:" + hostContract;
    }
}

/// <summary>Turns fail closed during startup, even before the catalog is sealed.</summary>
public sealed class AdmittedCapabilityTurnFilter(AdmittedCapabilityCatalog catalog) : IAgentTurnToolFilter
{
    public ValueTask<IReadOnlyList<ToolDescriptor>> FilterAsync(AgentTurnRequest request,
        SwarmCore.Agent.ToolUseEngine.AgentProfileSnapshot profile, IReadOnlyList<ToolDescriptor> tools,
        CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(catalog.IsAdmitted
            ? tools.Where(catalog.OwnsDescriptor).ToArray() : []);
}
