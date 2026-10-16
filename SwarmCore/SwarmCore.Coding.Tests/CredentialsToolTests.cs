using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Coding.Prompting;
using SwarmCore.Coding.Tools;
using SwarmCore.Secrets;

namespace SwarmCore.Coding.Tests;

public sealed class CredentialsToolTests
{
    private sealed class FakeVault : ISecretVault
    {
        private readonly Dictionary<string, (string Value, SecretEntry Entry)> _items = new(StringComparer.OrdinalIgnoreCase);

        public Task<SecretEntry> SetAsync(SecretWriteRequest request, CancellationToken ct = default)
        {
            var name = SecretNames.Normalize(request.Name);
            var entry = new SecretEntry(name, request.Kind, request.Site, SecretNames.MaskUsername(request.Username), !string.IsNullOrWhiteSpace(request.Notes), DateTime.UtcNow, DateTime.UtcNow, null, true);
            _items[name] = (request.Value, entry);
            return Task.FromResult(entry);
        }
        public Task<string?> GetValueAsync(string name, CancellationToken ct = default) => Task.FromResult(_items.TryGetValue(name, out var i) ? i.Value : null);
        public Task<SecretLogin?> GetLoginAsync(string name, CancellationToken ct = default) => Task.FromResult<SecretLogin?>(null);
        public Task<SecretEntry?> GetEntryAsync(string name, CancellationToken ct = default) => Task.FromResult(_items.TryGetValue(name, out var i) ? i.Entry : null);
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => Task.FromResult(_items.ContainsKey(name));
        public Task<bool> DeleteAsync(string name, CancellationToken ct = default) => Task.FromResult(_items.Remove(name));
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SecretEntry>>(_items.Values.Select(i => i.Entry).OrderBy(e => e.Name).ToList());
    }

    private static async Task<CredentialsListTool.Output> List(IServiceProvider sp, string? filter = null)
        => (CredentialsListTool.Output)(await new CredentialsListTool(sp).Descriptor.Invoke(new CredentialsListTool.Input { Filter = filter }, CancellationToken.None))!;

    [Fact]
    public async Task Lists_names_and_references_never_values()
    {
        var vault = new FakeVault();
        await vault.SetAsync(new SecretWriteRequest { Name = "openai", Value = "sk-very-secret", Kind = SecretKinds.ApiKey });
        await vault.SetAsync(new SecretWriteRequest { Name = "github.com/octocat", Value = "hunter2", Kind = SecretKinds.Password, Site = "https://github.com", Username = "octocat" });
        var sp = new ServiceCollection().AddSingleton<ISecretVault>(vault).BuildServiceProvider();

        var all = await List(sp);
        Assert.True(all.VaultAvailable);
        Assert.Equal(2, all.Credentials.Count);
        var gh = all.Credentials.Single(c => c.Name == "github.com/octocat");
        Assert.Equal("{{secret:github.com/octocat}}", gh.Reference);
        Assert.Equal("o***", gh.UsernameHint);
        var json = System.Text.Json.JsonSerializer.Serialize(all);
        Assert.DoesNotContain("sk-very-secret", json);
        Assert.DoesNotContain("hunter2", json);

        var filtered = await List(sp, "api-key");
        Assert.Single(filtered.Credentials);
        Assert.Equal("openai", filtered.Credentials[0].Name);
    }

    [Fact]
    public async Task Without_a_vault_reports_unavailable()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var output = await List(sp);
        Assert.False(output.VaultAvailable);
        Assert.Empty(output.Credentials);
    }

    [Fact]
    public void Registrar_publishes_credentials_tool_and_prompt_explains_references()
    {
        Assert.Contains(CredentialsListTool.ToolId, CodingToolRegistrar.CodingToolIds);
        var prompt = CodingSystemPrompt.Build(new CodingOptions { RepositoryRoot = Path.GetTempPath() });
        Assert.Contains("{{secret:name}}", prompt);
        Assert.Contains("credentials.list", prompt);
        Assert.Contains("/secret", prompt);
        Assert.DoesNotContain("{{{", prompt);
    }
}
