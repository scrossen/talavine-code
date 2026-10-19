using System.Text.Json;
using SwarmCode.Hosting.Observability;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Tests;

public sealed class UsageOverviewTests : TempDirTest
{
    [Fact]
    public async Task Fresh_reader_observes_other_workspace_usage_without_rewriting_the_ledger()
    {
        var paths = Paths;
        var record = new LlmUsageRecord { Provider = "provider", Model = "model", FunctionType = "test",
            InputTokens = 10, OutputTokens = 5, TotalTokens = 15, RequestTimestamp = DateTime.UtcNow };
        await File.WriteAllTextAsync(paths.UsagePath, JsonSerializer.Serialize(new[] { record }));
        Assert.Equal(15, (await SwarmCodeUsageOverview.ReadAsync(paths, 7)).Tokens);
        var next = JsonSerializer.Serialize(new[] { record, new LlmUsageRecord { Provider = "provider", Model = "model",
            FunctionType = "test", InputTokens = 20, OutputTokens = 2, TotalTokens = 22, RequestTimestamp = DateTime.UtcNow } });
        await File.WriteAllTextAsync(paths.UsagePath, next);
        Assert.Equal(37, (await SwarmCodeUsageOverview.ReadAsync(paths, 7)).Tokens);
        Assert.Equal(next, await File.ReadAllTextAsync(paths.UsagePath));
    }

    [Fact]
    public async Task Corrupt_usage_fails_visibly_instead_of_replacing_it_with_zero()
    {
        var paths = Paths;
        await File.WriteAllTextAsync(paths.UsagePath, "invalid json");
        await Assert.ThrowsAsync<JsonException>(() => SwarmCodeUsageOverview.ReadAsync(paths, 7));
        Assert.Equal("invalid json", await File.ReadAllTextAsync(paths.UsagePath));
    }
}
