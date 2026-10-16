using System.Text.Json;

using SwarmCore.Abstractions.LlmRouting;
using SwarmCode.Hosting.Llm;

namespace SwarmCode.Hosting.Tests;

public sealed class JsonProviderPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "json-provider-contract-" + Guid.NewGuid().ToString("N"));
    private SwarmCodeDataPaths Paths => new(_directory);
    private JsonLlmSettingsProvider Store() => new(Paths);

    [Fact]
    public async Task IndependentInstances_PreserveFunctionPinsWhenLegacyKeyIsCleared()
    {
        var legacyStore = Store();
        var workspaceStore = Store();
        var keys = new SettingsApiKeyProvider(legacyStore);
        await keys.SetApiKeyAsync("OpenAI", "synthetic-key");
        await workspaceStore.SetFunctionPreferenceAsync(new LlmFunctionPreference
        {
            FunctionType = "agent-chat", PreferredProvider = "OpenAI", PreferredModel = "user-model", IsEnabled = true,
        }, FunctionPreferenceWriteAuthority.UserAuthorizedOverride);
        await keys.ClearApiKeyAsync("OpenAI", "synthetic-key");
        var persisted = (await Store().GetFunctionPreferenceAsync("agent-chat"))!;
        Assert.Equal("user-model", persisted.PreferredModel);
        Assert.NotNull(persisted.PinnedByUserAt);
        Assert.Null((await Store().GetProviderSettingsAsync("OpenAI"))!.ApiKey);
    }

    [Fact]
    public async Task CallerAndReadObjects_CannotMutateStoredProvidersByReference()
    {
        var store = Store();
        var input = new LlmProviderSettings
        {
            ProviderName = "OpenAI", ApiKey = "synthetic-key", DefaultModel = "original",
            AdditionalSettings = new() { ["region"] = "original" },
        };
        Assert.True((await store.SaveProviderAsync(input)).Success);
        input.DefaultModel = "unsaved-caller-change";
        input.AdditionalSettings["region"] = "unsaved-caller-change";
        var loaded = (await store.GetProviderSettingsAsync("OpenAI"))!;
        Assert.Equal("original", loaded.DefaultModel);
        loaded.DefaultModel = "unsaved-reader-change";
        loaded.AdditionalSettings["region"] = "unsaved-reader-change";
        (await store.GetAllProviderSettingsAsync())[0].ApiKey = null;
        await store.EnsureFunctionPreferenceAsync("agent-chat", "OpenAI", "route");
        var reread = (await Store().GetProviderSettingsAsync("OpenAI"))!;
        Assert.Equal("original", reread.DefaultModel);
        Assert.Equal("original", reread.AdditionalSettings["region"]);
        Assert.Equal("synthetic-key", reread.ApiKey);
    }

    [Fact]
    public async Task DelayedProviderEdit_RejectsNewerUserSettingsWithoutRevertingThem()
    {
        var first = Store();
        var second = Store();
        Assert.True((await first.SaveProviderAsync(new LlmProviderSettings { ProviderName = "OpenAI", DefaultModel = "original" })).Success);
        var delayed = (await first.GetProviderSettingsAsync("OpenAI"))!;
        var user = (await second.GetProviderSettingsAsync("OpenAI"))!;
        user.DefaultModel = "new-user-model";
        user.Endpoint = "https://api.example.com/v1";
        user.IsEnabled = false;
        Assert.True((await second.SaveProviderAsync(user)).Success);
        delayed.ApiKey = "synthetic-new-key";
        Assert.False((await first.SaveProviderAsync(delayed)).Success);
        var current = (await first.GetProviderSettingsAsync("OpenAI"))!;
        Assert.Equal("new-user-model", current.DefaultModel);
        Assert.Equal(user.Endpoint, current.Endpoint);
        Assert.False(current.IsEnabled);
        Assert.Null(current.ApiKey);
        Assert.False((await first.SaveProviderAsync(new LlmProviderSettings { ProviderName = "OpenAI" })).Success);
    }

    [Fact]
    public async Task IndependentInstance_SeesNewUserPinAndRejectsStaleConditionalRoute()
    {
        var scout = Store();
        var ui = Store();
        await scout.SetFunctionPreferenceAsync(new LlmFunctionPreference { FunctionType = "agent-chat", PreferredProvider = "OpenAI", PreferredModel = "original", IsEnabled = true });
        var old = (await scout.GetFunctionPreferenceAsync("agent-chat"))!;
        var expected = FunctionPreferenceVersion.Capture("agent-chat", old);
        await ui.SetFunctionPreferenceAsync(new LlmFunctionPreference { FunctionType = "agent-chat", PreferredProvider = "OpenAI", PreferredModel = "user-choice", IsEnabled = true }, FunctionPreferenceWriteAuthority.UserAuthorizedOverride);
        old.PreferredModel = "late-scout-choice";
        Assert.Equal(FunctionPreferenceConditionalWriteStatus.Conflict,
            (await scout.TrySetExactFunctionPreferenceAsync(old, FunctionPreferenceWriteAuthority.RespectUserPin, expected)).Status);
        Assert.False(await scout.SetFunctionPreferenceAsync(old));
        Assert.Equal("user-choice", (await scout.GetFunctionPreferenceAsync("agent-chat"))!.PreferredModel);
    }

    [Fact]
    public async Task LegacyNullRevision_ReadSnapshotCanSaveButUntrackedReplacementCannot()
    {
        var paths = Paths;
        await File.WriteAllTextAsync(paths.SettingsPath, "{\"Providers\":[{\"ProviderName\":\"OpenAI\",\"DefaultModel\":\"legacy\"}],\"Preferences\":[]}");
        var store = Store();
        Assert.False((await store.SaveProviderAsync(new LlmProviderSettings { ProviderName = "OpenAI", DefaultModel = "untracked" })).Success);
        var loaded = (await store.GetProviderSettingsAsync("OpenAI"))!;
        loaded.ApiKey = "synthetic-key";
        Assert.True((await store.SaveProviderAsync(loaded)).Success);
        Assert.NotNull((await Store().GetProviderSettingsAsync("OpenAI"))!.UpdatedAt);
        loaded.ApiKey = null;
        Assert.True((await store.SaveProviderAsync(loaded)).Success);
        Assert.Null((await Store().GetProviderSettingsAsync("OpenAI"))!.ApiKey);
    }

    [Fact]
    public async Task MissingStoreAndChangedIdentity_RejectOldProviderSnapshot()
    {
        var store = Store();
        Assert.True((await store.SaveProviderAsync(new LlmProviderSettings { ProviderName = "OpenAI" })).Success);
        var loaded = (await store.GetProviderSettingsAsync("OpenAI"))!;
        loaded.ProviderName = "Gemini";
        Assert.False((await store.SaveProviderAsync(loaded)).Success);
        loaded.ProviderName = "OpenAI";
        File.Delete(Paths.SettingsPath);
        Assert.False((await store.SaveProviderAsync(loaded)).Success);
        Assert.False(File.Exists(Paths.SettingsPath));
    }

    [Fact]
    public async Task MalformedExistingFile_IsNotSilentlyReplacedByFreshDefaults()
    {
        var paths = Paths;
        const string original = "{ malformed settings";
        await File.WriteAllTextAsync(paths.SettingsPath, original);
        var store = Store();
        await Assert.ThrowsAnyAsync<JsonException>(() => store.EnsureFunctionPreferenceAsync("agent-chat", "OpenAI", "default"));
        Assert.Equal(original, await File.ReadAllTextAsync(paths.SettingsPath));
    }

    [Fact]
    public async Task ParallelIndependentWriters_PreserveEveryDistinctFunctionPreference()
    {
        var stores = Enumerable.Range(0, 12).Select(_ => Store()).ToArray();
        var writes = stores.Select((store, index) => store.EnsureFunctionPreferenceAsync("function-" + index, "OpenAI", "model-" + index));
        Assert.All(await Task.WhenAll(writes), Assert.True);
        Assert.Equal(12, (await Store().GetAllFunctionPreferencesAsync()).Count);
    }

    [Fact]
    public async Task ApiKeyBridge_ReconnectsDisabledProviderAndPreservesOtherConfiguration()
    {
        var store = Store();
        Assert.True((await store.SaveProviderAsync(new LlmProviderSettings
        {
            ProviderName = "OpenAI", IsEnabled = false, DefaultModel = "configured-model",
            Endpoint = "https://api.example.com/v1", Priority = 17, MaxEmailsPerHour = 8,
        })).Success);
        await new SettingsApiKeyProvider(Store()).SetApiKeyAsync("OpenAI", "synthetic-reconnect-key");
        var reconnected = (await Store().GetProviderSettingsAsync("OpenAI"))!;
        Assert.True(reconnected.IsEnabled);
        Assert.Equal("configured-model", reconnected.DefaultModel);
        Assert.Equal("https://api.example.com/v1", reconnected.Endpoint);
        Assert.Equal(17, reconnected.Priority);
        Assert.Equal(8, reconnected.MaxEmailsPerHour);
    }

    [Fact]
    public async Task PendingSave_CapturesCallerIntentBeforeBackgroundPersistence()
    {
        var store = Store();
        Assert.True((await store.SaveProviderAsync(new LlmProviderSettings
        { ProviderName = "OpenAI", ApiKey = "synthetic-key", DefaultModel = "original" })).Success);
        var input = (await store.GetProviderSettingsAsync("OpenAI"))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = store.TryConsumeProviderApiKeyAsync("OpenAI", async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return false;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<SaveProviderResult> pending;
        try
        {
            input.DefaultModel = "submitted";
            pending = store.SaveProviderAsync(input);
            input.DefaultModel = "unsaved-later-change";
        }
        finally { release.TrySetResult(); }
        await held;
        Assert.True((await pending).Success);
        Assert.Equal("submitted", (await Store().GetProviderSettingsAsync("OpenAI"))!.DefaultModel);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
