using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public class CodingCatalogDiscoveryTests
{
    [Fact]
    public async Task Live_models_add_missing_routes_without_replacing_explicit_configuration()
    {
        CodingLane[] candidates = [new("local", "Local", ""), new("saved", "Saved", "")];
        CodingLane[] existing = [new("custom-name", "Saved", "pinned")];
        var calls = new List<string>();
        var found = await CodingCatalogDiscovery.DiscoverAsync(candidates, existing, (lane, _) =>
        {
            calls.Add(lane.Provider);
            return Task.FromResult<IReadOnlyList<string>>(["", "installed:tag", "another:tag"]);
        });
        Assert.Equal("installed:tag", Assert.Single(found).Model);
        Assert.Equal(["Local"], calls);
        Assert.Equal("pinned", existing[0].Model);
    }

    [Fact]
    public async Task Offline_and_empty_servers_do_not_advertise_nonexistent_models()
    {
        var found = await CodingCatalogDiscovery.DiscoverAsync(
            [new("offline", "Offline", ""), new("empty", "Empty", "")], [],
            (lane, _) => lane.Provider == "Offline" ? throw new HttpRequestException() : Task.FromResult<IReadOnlyList<string>>([]));
        Assert.Empty(found);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodingCatalogDiscovery.DiscoverAsync(
            [new("local", "Local", "")], [], (_, ct) => Task.FromCanceled<IReadOnlyList<string>>(ct), cancellation.Token));
    }
}
