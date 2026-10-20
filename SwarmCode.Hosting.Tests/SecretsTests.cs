using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.Hosting.Llm;
using SwarmCode.Hosting.Secrets;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.Secrets;
using SwarmCore.Services.Abstractions;

namespace SwarmCode.Hosting.Tests;

public class SecretsCompositionTests : TempDirTest
{
    [Fact]
    public void AddSwarmCodeHost_RegistersVaultStack_AtSecretsDbPath()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = repo, DataPaths = Paths });
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var vault = Assert.IsType<SqliteSecretVault>(sp.GetRequiredService<ISecretVault>());
        Assert.Equal(Path.GetFullPath(Paths.SecretsDbPath), vault.DatabasePath);
        Assert.IsType<VaultCredentialStore>(sp.GetRequiredService<ICredentialStore>());
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            Assert.IsType<EncryptionRequiredSecretProtector>(sp.GetRequiredService<ISecretProtector>());
        else Assert.IsType<EnvelopeSecretProtector>(sp.GetRequiredService<ISecretProtector>());
        Assert.NotNull(sp.GetRequiredService<SecretIntake>());
        Assert.NotNull(sp.GetRequiredService<PasswordCsvImporter>());
        Assert.IsType<SwarmCodeApiKeyProvider>(sp.GetRequiredService<SwarmCodeApiKeyProvider>());
        var keyStore = sp.GetRequiredService<IOsKeyStore>();
        Assert.Equal(OperatingSystem.IsWindows() ? "dpapi" : "none", keyStore.ProviderId);
    }

    [Fact]
    public void PreRegisteredKeyStore_Wins()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        var mine = new NoOpKeyStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = repo, DataPaths = Paths, ConfigureServices = s => s.AddSingleton<IOsKeyStore>(mine) });
        using var sp = services.BuildServiceProvider();
        Assert.Same(mine, sp.GetRequiredService<IOsKeyStore>());
    }
}

public class VaultCredentialStoreBindingTests : TempDirTest
{
    [Fact]
    public async Task A_vault_secret_is_bound_to_its_site_and_one_without_a_site_to_nothing()
    {
        var vault = new SqliteSecretVault(Paths.SecretsDbPath, new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance));
        await vault.SetAsync(new SecretWriteRequest { Name = "billing-key", Value = "synthetic-value", Site = "https://api.billing.example/v2" });
        await vault.SetAsync(new SecretWriteRequest { Name = "loose-key", Value = "synthetic-value-2" });
        ICredentialStore store = new VaultCredentialStore(vault);

        var bound = await store.GetBoundHostsAsync("billing-key");
        Assert.True(SwarmCore.Services.Credentials.CredentialHostBinding.Allows(bound, "api.billing.example"));
        Assert.False(SwarmCore.Services.Credentials.CredentialHostBinding.Allows(bound, "collector.example"));
        Assert.Empty(await store.GetBoundHostsAsync("loose-key"));
        Assert.Empty(await store.GetBoundHostsAsync("absent"));
    }
}

public class VaultKeyProviderTests : TempDirTest
{
    private SqliteSecretVault Vault() => new(Paths.SecretsDbPath, new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance));

    [Fact]
    public async Task Aliases_ResolveToOneEntry_AndReferenceIsNonSecret()
    {
        var keys = new VaultApiKeyProvider(Vault());
        await keys.SetApiKeyAsync("anthropic", "sk-ant-test-123");
        Assert.Equal("sk-ant-test-123", await keys.GetApiKeyAsync("Claude"));
        Assert.True(await keys.HasApiKeyAsync("ANTHROPIC"));
        var reference = await keys.GetCredentialReferenceAsync("claude");
        Assert.NotNull(reference);
        Assert.StartsWith("vault:llm.claude:", reference);
        Assert.DoesNotContain("sk-ant", reference);
        Assert.Equal("llm.claude", (await Vault().ListAsync()).Single().Name);
        Assert.True(await keys.ClearApiKeyAsync("Anthropic"));
        Assert.Null(await keys.GetApiKeyAsync("Claude"));
    }

    [Fact]
    public async Task LegacyJsonKey_IsMigratedIntoVault_AndBlankedInJson()
    {
        var legacy = new SettingsApiKeyProvider(new JsonLlmSettingsProvider(Paths));
        await legacy.SetApiKeyAsync("OpenAI", "sk-legacy-plaintext");
        Assert.Contains("sk-legacy-plaintext", await File.ReadAllTextAsync(Paths.SettingsPath));

        var vaultKeys = new VaultApiKeyProvider(Vault());
        var provider = new SwarmCodeApiKeyProvider(vaultKeys, legacy, new LlmProvidersOptions());
        Assert.Equal("sk-legacy-plaintext", await provider.GetApiKeyAsync("OpenAI"));

        Assert.Equal("sk-legacy-plaintext", await vaultKeys.GetApiKeyAsync("OpenAI"));
        Assert.DoesNotContain("sk-legacy-plaintext", await File.ReadAllTextAsync(Paths.SettingsPath));
        Assert.Null(await legacy.GetApiKeyAsync("OpenAI"));
        // Second read comes straight from the vault.
        Assert.Equal("sk-legacy-plaintext", await provider.GetApiKeyAsync("openai"));
    }

    [Fact]
    public async Task SetApiKey_GoesToVault_NotJson()
    {
        var legacy = new SettingsApiKeyProvider(new JsonLlmSettingsProvider(Paths));
        var provider = new SwarmCodeApiKeyProvider(new VaultApiKeyProvider(Vault()), legacy, new LlmProvidersOptions());
        await provider.SetApiKeyAsync("Gemini", "AIza-new-key-value");
        Assert.Null(await legacy.GetApiKeyAsync("Gemini"));
        Assert.Equal("AIza-new-key-value", await provider.GetApiKeyAsync("google"));
        if (File.Exists(Paths.SettingsPath)) Assert.DoesNotContain("AIza-new-key-value", await File.ReadAllTextAsync(Paths.SettingsPath));
    }
}

public class LegacyMigrationConcurrencyTests : TempDirTest
{
    // Hosted runners execute this suite alongside many other asynchronous tests.
    // Allow scheduling headroom; the protector signals, rather than elapsed time,
    // still establish the exact competing-writer interleaving.
    private static readonly TimeSpan SchedulingTimeout = TimeSpan.FromSeconds(30);
    private SqliteSecretVault Vault() => new(Paths.SecretsDbPath, new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance));

    [Fact]
    public async Task HeldLegacyMigration_CannotOverwriteNewerVaultKey()
    {
        var legacy = new SettingsApiKeyProvider(new JsonLlmSettingsProvider(Paths));
        await legacy.SetApiKeyAsync("OpenAI", "synthetic-legacy-key");
        var protector = new HoldingProtector(new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance));
        var migratingKeys = new VaultApiKeyProvider(new SqliteSecretVault(Paths.SecretsDbPath, protector));
        var provider = new SwarmCodeApiKeyProvider(migratingKeys, legacy, new LlmProvidersOptions());
        var migration = Task.Run(() => provider.GetApiKeyAsync("OpenAI"));
        try
        {
            await protector.Entered.Task.WaitAsync(SchedulingTimeout);
            var explicitWriter = new VaultApiKeyProvider(Vault());
            await explicitWriter.SetApiKeyAsync("OpenAI", "synthetic-new-user-key");
        }
        finally { protector.Release.TrySetResult(); }

        Assert.Equal("synthetic-new-user-key", await migration.WaitAsync(SchedulingTimeout));
        Assert.Equal("synthetic-new-user-key", await new VaultApiKeyProvider(Vault()).GetApiKeyAsync("OpenAI"));
        // The old source was not adopted, so migration must not erase it.
        Assert.Equal("synthetic-legacy-key", await legacy.GetApiKeyAsync("OpenAI"));
        Assert.True(await provider.ClearApiKeyAsync("OpenAI"));
        Assert.Null(await legacy.GetApiKeyAsync("OpenAI"));
        Assert.Null(await new VaultApiKeyProvider(Vault()).GetApiKeyAsync("OpenAI"));
        Assert.Null(await provider.GetApiKeyAsync("OpenAI"));
    }

    [Fact]
    public async Task LegacyCleanupFailure_ReturnsAlreadyAdoptedVaultAuthority()
    {
        var legacy = new SettingsApiKeyProvider(new JsonLlmSettingsProvider(Paths));
        await legacy.SetApiKeyAsync("OpenAI", "synthetic-legacy-key");
        var protector = new HoldingProtector(new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance));
        var provider = new SwarmCodeApiKeyProvider(new VaultApiKeyProvider(new SqliteSecretVault(Paths.SecretsDbPath, protector)), legacy, new LlmProvidersOptions());
        var migration = Task.Run(() => provider.GetApiKeyAsync("OpenAI"));
        try
        {
            await protector.Entered.Task.WaitAsync(SchedulingTimeout);
            File.Delete(Paths.SettingsPath);
            Directory.CreateDirectory(Paths.SettingsPath);
        }
        finally { protector.Release.TrySetResult(); }
        Assert.Equal("synthetic-legacy-key", await migration.WaitAsync(SchedulingTimeout));
        Assert.Equal("synthetic-legacy-key", await new VaultApiKeyProvider(Vault()).GetApiKeyAsync("OpenAI"));
    }

    [Fact]
    public async Task ClearWithUnreadableLegacyStore_DoesNotDeleteVaultAuthority()
    {
        var keys = new VaultApiKeyProvider(Vault());
        await keys.SetApiKeyAsync("OpenAI", "synthetic-new-user-key");
        await File.WriteAllTextAsync(Paths.SettingsPath, "{broken");
        var provider = new SwarmCodeApiKeyProvider(keys, new SettingsApiKeyProvider(new JsonLlmSettingsProvider(Paths)), new LlmProvidersOptions());
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => provider.ClearApiKeyAsync("OpenAI"));
        Assert.Equal("synthetic-new-user-key", await keys.GetApiKeyAsync("OpenAI"));
    }

    [Fact]
    public async Task VaultAbsentOnlyAdoption_PreservesExistingKeyAndMetadata()
    {
        var first = new VaultApiKeyProvider(Vault());
        var second = new VaultApiKeyProvider(Vault());
        Assert.True(await first.TrySetApiKeyIfAbsentAsync("OpenAI", "synthetic-original-key"));
        var original = await first.Vault.GetEntryAsync(VaultApiKeyProvider.VaultName("OpenAI"));
        Assert.False(await second.TrySetApiKeyIfAbsentAsync("OpenAI", "synthetic-stale-key"));
        Assert.Equal("synthetic-original-key", await second.GetApiKeyAsync("OpenAI"));
        Assert.Equal(original!.UpdatedUtc, (await second.Vault.GetEntryAsync(original.Name))!.UpdatedUtc);
    }

    private sealed class HoldingProtector(ISecretProtector inner) : ISecretProtector
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Protect(string? value)
        {
            if (value == "synthetic-legacy-key")
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return inner.Protect(value);
        }
        public string Unprotect(string? value) => inner.Unprotect(value);
    }
}

public class SecretIntakeTests : TempDirTest
{
    private SecretIntake Intake() => new(new SqliteSecretVault(Paths.SecretsDbPath, new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance)));

    [Fact]
    public async Task Scrub_StoresKnownTokenShapes_AndReplacesThem()
    {
        var intake = Intake();
        var outcome = await intake.ScrubAsync("use my github token ghp_abcdefghijklmnopqrstuvwxyz0123456789 to open the PR");
        Assert.Equal(["github-api-key"], outcome.StoredNames);
        Assert.Equal("use my github token {{secret:github-api-key}} to open the PR", outcome.Text);
        Assert.NotNull(outcome.Message);
        Assert.DoesNotContain("ghp_", outcome.Message);
        Assert.Equal("ghp_abcdefghijklmnopqrstuvwxyz0123456789", await intake.Vault.GetValueAsync("github-api-key"));
    }

    [Fact]
    public async Task Scrub_StoresLabeledSecret_WithSecretShapedValue()
    {
        var intake = Intake();
        var outcome = await intake.ScrubAsync("the staging db password is Tr0ub4dor&3xyz, please update appsettings");
        Assert.Single(outcome.StoredNames);
        Assert.Contains("{{secret:", outcome.Text);
        Assert.DoesNotContain("Tr0ub4dor", outcome.Text);
    }

    [Theory]
    [InlineData("token = parser.NextToken();")]
    [InlineData("password = request.Form[\"password\"]")]
    [InlineData("set the api key to configuration later")]
    [InlineData("the token is expired, refresh it")]
    public async Task Scrub_LeavesCodeAndProse_Alone(string text)
    {
        var outcome = await Intake().ScrubAsync(text);
        Assert.Empty(outcome.StoredNames);
        Assert.Equal(text, outcome.Text);
    }

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("Tr0ub4dor&3", true)]
    [InlineData("hunter2222", true)]
    [InlineData("NextToken()", false)]
    [InlineData("configuration", false)]
    [InlineData("short1", false)]
    [InlineData("https://example.com/x", false)]
    [InlineData("C:\\Users\\me\\file.txt", false)]
    public void LooksLikeSecretValue(string value, bool expected) => Assert.Equal(expected, SecretIntake.LooksLikeSecretValue(value));

    [Fact]
    public async Task Commands_SaveListForget()
    {
        var intake = Intake();
        Assert.True(SecretIntake.IsCommand("/secret npm-token npm_abc123"));
        Assert.True(SecretIntake.IsCommand("/secrets"));
        Assert.True(SecretIntake.IsCommand("/forget npm-token"));
        Assert.False(SecretIntake.IsCommand("/settings"));
        Assert.False(SecretIntake.IsCommand("tell me a secret"));

        var saved = await intake.HandleCommandAsync("/secret NPM-Token npm_abc123def456");
        Assert.True(saved.HandledAsCommand);
        Assert.Equal(["npm-token"], saved.StoredNames);
        Assert.DoesNotContain("npm_abc", saved.Message);

        var prompt = await intake.HandleCommandAsync("/secret only-name");
        Assert.Equal("only-name", prompt.PromptForValueName);

        var list = await intake.HandleCommandAsync("/secrets");
        Assert.Contains("npm-token", list.Message);
        Assert.DoesNotContain("npm_abc", list.Message);

        var forgot = await intake.HandleCommandAsync("/forget npm-token");
        Assert.Contains("Forgot", forgot.Message);
        Assert.False(await intake.Vault.HasAsync("npm-token"));
    }
}

public class PasswordCsvImporterTests : TempDirTest
{
    [Fact]
    public async Task Import_StoresLogins_AndSkipsUnchangedOnRepeat()
    {
        var vault = new SqliteSecretVault(Paths.SecretsDbPath, new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance));
        var importer = new PasswordCsvImporter(vault);
        var csv = Path.Combine(Tmp, "chrome.csv");
        await File.WriteAllTextAsync(csv, "name,url,username,password,note\ngithub.com,https://github.com/login,octocat,hunter2,\nexample,https://www.example.com,me@example.com,pw2,note here\n");

        var first = await importer.ImportFileAsync(csv);
        Assert.True(first.Success, first.Error);
        Assert.Equal(2, first.Added);
        Assert.Equal(["github.com/octocat", "example.com/me-example.com"], first.Names);
        Assert.Contains("Chrome/Edge", first.Describe());

        var login = await vault.GetLoginAsync("github.com/octocat");
        Assert.Equal("octocat", login!.Username);
        Assert.Equal("hunter2", login.Password);
        var entry = await vault.GetEntryAsync("example.com/me-example.com");
        Assert.Equal("password", entry!.Kind);
        Assert.Equal("https://www.example.com", entry.Site);
        Assert.True(entry.HasNotes);
        Assert.Contains("note here", await vault.GetNotesAsync("example.com/me-example.com"));

        var second = await importer.ImportFileAsync(csv);
        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.Unchanged);

        await File.WriteAllTextAsync(csv, "name,url,username,password\ngithub.com,https://github.com/login,octocat,rotated\n");
        var third = await importer.ImportFileAsync(csv);
        Assert.Equal(1, third.Updated);
        Assert.Equal("rotated", (await vault.GetLoginAsync("github.com/octocat"))!.Password);
    }

    [Fact]
    public async Task Import_MissingFile_Fails()
    {
        var importer = new PasswordCsvImporter(new SqliteSecretVault(Paths.SecretsDbPath, new EnvelopeSecretProtector(new NoOpKeyStore(), NullLogger<EnvelopeSecretProtector>.Instance)));
        var summary = await importer.ImportFileAsync(Path.Combine(Tmp, "nope.csv"));
        Assert.False(summary.Success);
        Assert.Contains("not found", summary.Error);
    }
}

public class ConversationRunnerSecretTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    private sealed class CapturingOrchestrator : ICodingTurnOrchestrator
    {
        public CodingTurnRequest? LastRequest { get; private set; }
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            yield return new TurnCompleted("t", "ok", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task TrySend_MovesTypedSecretIntoVault_BeforeLaneAndTranscript()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        var orchestrator = new CapturingOrchestrator();
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeSecrets(Paths);
        using var provider = services.BuildServiceProvider();
        using var runner = new ConversationRunner(provider, "conv");

        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend("deploy with my openai api key sk-abcdefghijklmnopqrstuvwxyz1234 please", "claude"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("deploy with my openai api key {{secret:api-key}} please", orchestrator.LastRequest!.UserMessage);
        var user = Assert.Single(runner.Entries.OfType<UserEntry>());
        Assert.DoesNotContain("sk-abc", user.Text);
        Assert.Contains(runner.Entries.OfType<NoticeEntry>(), n => n.Kind == NoticeKind.Secret);
        Assert.Equal("sk-abcdefghijklmnopqrstuvwxyz1234", await provider.GetRequiredService<ISecretVault>().GetValueAsync("api-key"));
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz1234", await File.ReadAllTextAsync(Path.Combine(Paths.SessionsDirectory, "jobs", "conv.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VaultFailure_DoesNotSendPersistOrDisplayRawMessage(bool invalidName)
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz1234";
        var services = new ServiceCollection();
        services.AddLogging();
        var orchestrator = new CapturingOrchestrator();
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        services.AddSingleton(new SecretIntake(new FailingVault(invalidName, token)));
        await using var provider = services.BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, "conv");
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend("deploy with my openai api key " + token, "claude"));
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(orchestrator.LastRequest);
        Assert.Empty(runner.Entries.OfType<UserEntry>());
        Assert.False(runner.IsRunning);
        Assert.False(result.Completed);
        Assert.Contains("not sent", result.Error);
        Assert.DoesNotContain(token, result.Error);
        Assert.All(runner.Entries.OfType<NoticeEntry>(), n => Assert.DoesNotContain(token, n.Text + n.Details));
        Assert.False(await runner.EnqueueAsync("my openai api key " + token, "claude"));
        Assert.Empty(runner.QueuedMessages);
        Assert.All(runner.Entries.OfType<NoticeEntry>(), n => Assert.DoesNotContain(token, n.Text + n.Details));
    }

    [Fact]
    public async Task FirstMessageTitle_UsesProtectedText_AndActualRequestedLane()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp);
        services.AddSingleton(catalog);
        services.AddSingleton<ICodingTurnOrchestrator>(new CapturingOrchestrator());
        services.AddSingleton<IOsKeyStore>(new EncryptedTestKeyStore());
        services.AddSwarmCodeSecrets(Paths);
        await using var provider = services.BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, session.Id);
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend("my openai api key sk-abcdefghijklmnopqrstuvwxyz1234", "claude"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id);
        Assert.Contains("{{secret:", saved!.Title);
        Assert.Equal("claude", saved.LaneName);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz1234", await File.ReadAllTextAsync(Paths.SessionIndexPath));
    }

    private sealed class FailingVault(bool invalidName, string token) : ISecretVault
    {
        public Task<SecretEntry> SetAsync(SecretWriteRequest request, CancellationToken ct = default)
            => throw (invalidName ? new ArgumentException(token) : new IOException(token));
        public Task<string?> GetValueAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretLogin?> GetLoginAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretEntry?> GetEntryAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
