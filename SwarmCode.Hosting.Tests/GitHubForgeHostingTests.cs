using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SwarmCode.Hosting.Mcp;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Coding;
using SwarmCore.Coding.Forge;
using SwarmCore.Missions;
using SwarmCore.Secrets;
using SwarmCore.Testing;

namespace SwarmCode.Hosting.Tests;

/// <summary>SwarmCode opts into the forge tools (catalog for API lanes, github_* on the bridge); no test calls GitHub.</summary>
public class GitHubForgeHostingTests : TempDirTest
{
    private const string Token = "ghp_HOSTINGTESTTOKEN0123456789abcdefXYZ";

    [Fact]
    public async Task SwarmCode_host_publishes_the_forge_tools_binds_options_and_adds_guidance()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            Configuration = SwarmCodeHostComposer.BuildConfiguration(Paths, new Dictionary<string, string?>
            {
                ["Coding:GitHub:TokenSecret"] = "work-github",
                ["Coding:GitHub:EnterpriseHosts:0"] = "ghe.example.com",
            }),
        });
        var sp = workspace.Services;
        var catalog = sp.GetRequiredService<ICapabilityCatalog>();
        foreach (var id in CodingForgeToolRegistrar.ForgeToolIds) Assert.NotNull(catalog.All().FirstOrDefault(t => t.ToolId == id));
        var options = sp.GetRequiredService<IOptions<GitHubForgeOptions>>().Value;
        Assert.Equal("work-github", options.TokenSecret);
        Assert.Equal(["ghe.example.com"], options.EnterpriseHosts);
        Assert.Contains("github.pr_create", sp.GetRequiredService<IOptions<CodingOptions>>().Value.AdditionalInstructions);
        Assert.Empty(CodingForgeToolRegistrar.ForgeToolIds.Intersect(CodingToolRegistrar.CodingToolIds));
    }

    [Fact]
    public void Bridge_offers_the_github_tools_and_pr_status_is_read_only()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<SwarmCore.Missions.SandboxOptions>().Configure(o => o.Root = Tmp);
        services.AddSingleton<SwarmCore.Missions.ISandboxPathResolver, SwarmCore.Missions.SandboxPathResolver>();
        services.AddSwarmCoreCodingForgeServices(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();
        var tools = SwarmCore.Coding.Native.HostToolBridgeCatalog.Select(CodingForgeToolRegistrar.Descriptors(sp), SwarmCore.Coding.Native.HostToolProfile.Full);
        Assert.Equal(["git_push", "git_switch_branch", "github_pr_comment", "github_pr_create", "github_pr_for_branch", "github_pr_status"], tools.Keys.Order());
        Assert.True(SwarmCore.Coding.Native.HostToolBridgeCatalog.Manifest("github_pr_status", tools["github_pr_status"]).ReadOnly);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private sealed class SignedOutGh : IGhCli
    {
        public string? Locate() => null;
        public Task<bool> IsSignedInAsync(string ghPath, string host, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordedTransport : IGitHubApiTransport, IGitHubApiTransportFactory
    {
        public readonly List<string> Requests = [];
        public GitHubCredential? Credential;
        public IGitHubApiTransport Create(GitHubCredential credential, string workingDirectory) { Credential = credential; return this; }
        public Task<GitHubApiResponse> SendAsync(ForgeRepository repository, HttpMethod method, string path, string? jsonBody, CancellationToken ct)
        {
            Requests.Add(method.Method + " " + path);
            var body = path switch
            {
                _ when path.StartsWith("repos/octo/repo/pulls?head=", StringComparison.Ordinal) =>
                    """[{"number":7,"title":"T","state":"open","head":{"ref":"feature","sha":"abc"},"base":{"ref":"main"},"html_url":"https://github.com/octo/repo/pull/7"}]""",
                "repos/octo/repo/pulls/7" => """{"number":7,"title":"T","state":"open","mergeable":true,"head":{"ref":"feature","sha":"abc"},"base":{"ref":"main"},"html_url":"https://github.com/octo/repo/pull/7"}""",
                _ when path.Contains("/check-runs", StringComparison.Ordinal) => """{"check_runs":[{"name":"build","status":"completed","conclusion":"success"}]}""",
                _ when path.Contains("/status", StringComparison.Ordinal) => """{"statuses":[]}""",
                _ => "[]",
            };
            return Task.FromResult(new GitHubApiResponse(200, body));
        }
    }

    private (ServiceProvider Services, RecordedTransport Transport, string Repo) Bridge()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        TestGit.Run(repo, "init", "-q", "-b", "feature");
        TestGit.Run(repo, "remote", "add", "origin", "https://github.com/octo/repo.git");
        var transport = new RecordedTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeSecrets(Paths);
        services.AddOptions<SandboxOptions>().Configure(o => o.Root = repo);
        services.AddSingleton<ISandboxPathResolver, SandboxPathResolver>();
        services.AddSingleton<IGhCli>(new SignedOutGh());
        services.AddSingleton<IGitHubApiTransportFactory>(transport);
        // Same registration the bridge process makes (SwarmCodeMcpServer).
        services.AddSwarmCoreCodingForgeServices(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Coding:GitHub:TokenSecret"] = "github-token",
        }).Build());
        return (services.BuildServiceProvider(), transport, repo);
    }

    [GitFact]
    public async Task Bridge_pr_status_uses_the_vault_token_without_exposing_it()
    {
        var (sp, transport, _) = Bridge();
        using var _sp = sp;
        await sp.GetRequiredService<ISecretVault>().SetAsync(new SecretWriteRequest { Name = "github-token", Value = Token, Kind = SecretKinds.Token, Site = "https://github.com" });

        var json = System.Text.Json.JsonSerializer.Serialize(await sp.GetRequiredService<GitHubPrStatusTool>().Descriptor.Invoke(new GitHubPrStatusTool.Input(), default), JsonOptions);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out _), json);
        Assert.Equal(7, doc.RootElement.GetProperty("status").GetProperty("pullRequest").GetProperty("number").GetInt32());
        Assert.Equal("success", doc.RootElement.GetProperty("status").GetProperty("checksRollup").GetString());
        Assert.Equal("{{secret:github-token}}", doc.RootElement.GetProperty("credential").GetString());
        Assert.DoesNotContain(Token, json);
        Assert.Equal(GitHubCredentialKind.VaultToken, transport.Credential!.Kind);
        Assert.All(transport.Requests, r => Assert.StartsWith("GET ", r));
    }

    [GitFact]
    public async Task Bridge_comment_without_credentials_fails_clearly_and_sends_nothing()
    {
        var (sp, transport, _) = Bridge();
        using var _sp = sp;
        var json = System.Text.Json.JsonSerializer.Serialize(await sp.GetRequiredService<GitHubPrCommentTool>().Descriptor.Invoke(new GitHubPrCommentTool.Input { Body = "hello", Number = 7 }, default), JsonOptions);
        // gh is signed out and the configured token secret is not stored: the error names it and the stored names.
        Assert.Contains("No stored secret named 'github-token' (Coding:GitHub:TokenSecret)", JsonDocument.Parse(json).RootElement.GetProperty("error").GetString());
        Assert.Empty(transport.Requests);
    }
}
