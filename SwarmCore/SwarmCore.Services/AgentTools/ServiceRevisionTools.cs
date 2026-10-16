using SwarmCore.Agent.Catalog;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.Adapters;

namespace SwarmCore.Services.AgentTools;

/// <summary>Reusable inspection, candidate testing and activation over the shared revision state machine.</summary>
public static class ServiceRevisionTools
{
    public sealed class NameInput { public string Name { get; set; } = ""; }
    public sealed class ReviseInput
    {
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public string? Reason { get; set; }
    }
    public sealed class VersionInput { public string Name { get; set; } = ""; public int Version { get; set; } }
    public sealed class TestInput
    {
        public string Name { get; set; } = "";
        public int Version { get; set; }
        public List<ServiceCatalogTools.FieldValueDto>? Args { get; set; }
        public string ExpectedContains { get; set; } = "";
    }
    public sealed record Result(bool Ok, string? Error = null, int? Version = null, object? Value = null) : IToolExecutionEvidence
    {
        bool IToolExecutionEvidence.Succeeded => Ok;
        bool IToolExecutionEvidence.LocalProcessCompleted => false;
    }

    public static IEnumerable<ToolDescriptor> Build(IServiceCatalog catalog)
    {
        yield return ToolDescriptor.Create<NameInput, Result>("catalog.inspect_service", "catalog",
            "Inspect a saved tool's source/config and revision history before reusing or repairing it. Credentials remain references.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly, async (input, ct) =>
            {
                var service = await catalog.GetAsync(input.Name, ct);
                return service is null ? new(false, "Service not found.") : new(true, Version: service.ActiveRevision,
                    Value: new { service.Name, service.Kind, service.Description, service.Enabled, service.Config, service.Revisions });
            });
        yield return ToolDescriptor.Create<ReviseInput, Result>("catalog.revise_service", "catalog",
            "Compile and save a new C# candidate without changing the active tool. Test the returned version, then activate it.",
            ToolCostClass.Moderate, ToolSideEffect.WritesLocal, async (input, ct) =>
            {
                var service = await catalog.GetAsync(input.Name, ct);
                if (service?.Kind != ServiceKind.CSharp) return new(false, "An existing C# service is required.");
                if (string.IsNullOrWhiteSpace(input.Source)) return new(false, "Source is required.");
                var validation = CSharpServiceAdapter.Validate(input.Source);
                if (!validation.Ok) return new(false, validation.Error);
                var config = new Dictionary<string, string>(service.Config) { ["source"] = input.Source };
                var revision = ServiceRevisionManager.AddCandidate(service, config, input.Reason);
                await catalog.AddAsync(service, ct: ct);
                return new(true, Version: revision.Version);
            });
        yield return ToolDescriptor.Create<TestInput, Result>("catalog.test_service", "catalog",
            "Execute an exact saved revision against sample args and a nonempty expectedContains assertion. This executes code and may have side effects; use synthetic inputs. Saves a passing or failing receipt, never activates a candidate.",
            ToolCostClass.Moderate, ToolSideEffect.Exec, async (input, ct) =>
            {
                if (string.IsNullOrWhiteSpace(input.ExpectedContains)) return new(false, "A nonempty expectedContains assertion is required.");
                var service = await catalog.GetAsync(input.Name, ct);
                if (service is null || !service.Enabled) return new(false, "Service missing or disabled.");
                var revision = ServiceRevisionManager.Find(service, input.Version);
                if (revision is null) return new(false, "Revision not found.");
                var pairs = input.Args ?? [];
                if (pairs.Select(p => p.Field).Distinct(StringComparer.Ordinal).Count() != pairs.Count)
                    return new(false, "Pass each argument once.");
                var result = await catalog.CallAuthorizedSnapshotAsync(ServiceRevisionManager.ForInvocation(service, revision),
                    pairs.ToDictionary(p => p.Field, p => (object?)p.Value), ct);
                ServiceRevisionManager.RecordTest(revision, result, input.ExpectedContains);
                await catalog.AddAsync(service, ct: ct);
                return new(revision.TestPassed == true, revision.TestPassed == true ? null : revision.TestSummary, revision.Version, result.Output);
            });
        yield return Transition("catalog.activate_service", "Activate a tested revision; refuses a candidate without a passing receipt.", false);
        yield return Transition("catalog.rollback_service", "Restore an earlier tested/previously active revision. Supply its version.", true);

        ToolDescriptor Transition(string id, string description, bool rollback) => ToolDescriptor.Create<VersionInput, Result>(
            id, "catalog", description, ToolCostClass.Cheap, ToolSideEffect.WritesLocal, async (input, ct) =>
            {
                var service = await catalog.GetAsync(input.Name, ct);
                if (service is null) return new(false, "Service not found.");
                var revision = rollback ? ServiceRevisionManager.ResolveRollbackTarget(service, input.Version)
                    : ServiceRevisionManager.Find(service, input.Version);
                if (revision is null) return new(false, "Eligible revision not found.");
                // An initial revision may predate tests; rollback must not manufacture a passing receipt.
                if (rollback && revision.TestPassed != true) return new(false, "Test the rollback revision before activating it.");
                if (!ServiceRevisionManager.Activate(service, revision, out var error)) return new(false, error);
                await catalog.AddAsync(service, ct: ct);
                return new(true, Version: revision.Version);
            });
    }
}
