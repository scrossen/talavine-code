using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding;
using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.Models;
using SwarmCore.Missions;

namespace SwarmCode.Hosting.Tests;

public sealed class McpPublicationSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentConfiguredServersAllocateFromCompleteSnapshotRegardlessOfListFinishOrder(bool reverse)
    {
        var manager = new Connector();
        var catalog = new InMemoryCapabilityCatalog();
        using var host = new ExternalMcpToolCatalog(Options.Create(new CodingOptions { NativeAgents = new() { ExternalMcpServers =
            [new() { ServerName = "a_b", Command = "unused" }, new() { ServerName = "a", Command = "unused" }] } }), manager, catalog, NullLogger<ExternalMcpToolCatalog>.Instance);
        var startup = host.EnsurePublishedAsync();
        var first = reverse ? "a" : "a_b";
        var last = reverse ? "a_b" : "a";
        manager.Lists[first].SetResult([new(first == "a" ? "b_c" : "c", first, null)]);
        manager.Lists[last].SetResult([new(last == "a" ? "b_c" : "c", last, null)]);
        await startup;
        var expected = McpCatalogProjection.AllocateToolIds([new("a_b", "c"), new("a", "b_c")]);
        Assert.Equal(expected.Values.Order(), catalog.All().Select(t => t.ToolId).Order());
        manager.Fault("a");
        Assert.Equal(expected[new("a_b", "c")], Assert.Single(catalog.All()).ToolId);
        manager.Lists["a"] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var restart = host.EnsurePublishedAsync();
        manager.Lists["a"].SetResult([new("b_c", "a", null)]);
        await restart;
        Assert.Equal(expected.Values.Order(), catalog.All().Select(t => t.ToolId).Order());
    }

    [Fact]
    public async Task ServerFaultDuringListNeverPublishesItsLateTools()
    {
        var manager = new Connector();
        var catalog = new InMemoryCapabilityCatalog();
        using var host = new ExternalMcpToolCatalog(Options.Create(new CodingOptions { NativeAgents = new() { ExternalMcpServers =
            [new() { ServerName = "a", Command = "unused" }] } }), manager, catalog, NullLogger<ExternalMcpToolCatalog>.Instance);
        var startup = host.EnsurePublishedAsync();
        manager.Fault("a");
        manager.Lists["a"].SetResult([new("late", "late", null)]);
        await startup;
        Assert.Empty(catalog.All());
        Assert.NotNull(Assert.Single(host.Status()).Problem);
    }

    private sealed class Connector : IMcpConnectorManager
    {
        private readonly Dictionary<string, Guid> _ids = new();
        public Dictionary<string, TaskCompletionSource<IReadOnlyList<McpToolInfo>>> Lists { get; } = new()
        { ["a"] = new(TaskCreationOptions.RunContinuationsAsynchronously), ["a_b"] = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        public event EventHandler<McpConnectionChangedEventArgs>? ConnectionsChanged;
        public Task<Guid> ConnectAsync(McpConnectorOptions options, CancellationToken ct = default)
        { var id = Guid.NewGuid(); _ids[options.Name] = id; return Task.FromResult(id); }
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(Guid id, CancellationToken ct = default)
            => Lists[_ids.Single(pair => pair.Value == id).Key].Task;
        public void Fault(string name) => ConnectionsChanged?.Invoke(this, new(_ids[name], name, McpConnectionChangeKind.Faulted));
        public Task DisconnectAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<McpConnectionInfo>> GetConnectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpToolResult> InvokeToolAsync(Guid id, string tool, IDictionary<string, object?>? arguments = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
