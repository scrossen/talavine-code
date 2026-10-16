using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Secrets;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Host-neutral JSON settings persistence with fresh reads, checked provider revisions,
/// and atomic replacement serialized across independent hosts/processes.</summary>
/// <remarks>
/// A host that supplies an <see cref="ISecretProtector"/> gets provider API keys
/// protected at rest: the file holds only <c>ProtectedApiKey</c> (the protector's
/// envelope) and never a plaintext <c>ApiKey</c>. Plaintext keys written by earlier
/// builds are rewritten protected the first time the file is read, and an envelope
/// that cannot be decrypted on this machine reads as "no key" but is never
/// overwritten or dropped by an unrelated save. Without a protector the file keeps
/// its legacy plaintext shape.
/// </remarks>
public class JsonFileLlmSettingsProvider : ILlmSettingsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string ApiKeyProperty = nameof(LlmProviderSettings.ApiKey);
    internal const string ProtectedApiKeyProperty = "ProtectedApiKey";

    private readonly string _path;
    private readonly ISecretProtector? _apiKeyProtector;
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<LlmProviderSettings, ProviderBaseline> Baselines = new();
    private sealed record ProviderBaseline(string Path, string Name, DateTime? Revision);
    private readonly object _lock;
    private SettingsFile _data;
    // At-rest key envelopes from the last load, by provider name. Plaintext is null when
    // the envelope cannot be read here; such an envelope is carried forward unchanged.
    private Dictionary<string, StoredApiKey> _storedKeys = new(StringComparer.OrdinalIgnoreCase);
    private sealed record StoredApiKey(string Protected, string? Plaintext);

    public JsonFileLlmSettingsProvider(string path)
        : this(path, apiKeyProtector: null)
    {
    }

    /// <param name="path">The settings file.</param>
    /// <param name="apiKeyProtector">
    /// Protects provider API keys at rest (for example the host's OS-keystore
    /// <see cref="EnvelopeSecretProtector"/>); null keeps the legacy plaintext file.
    /// </param>
    public JsonFileLlmSettingsProvider(string path, ISecretProtector? apiKeyProtector)
    {
        _path = Path.GetFullPath(path);
        _apiKeyProtector = apiKeyProtector;
        _lock = Gates.GetOrAdd(_path, _ => new object());
        _data = new SettingsFile();
    }

    private sealed class SettingsFile
    {
        public List<LlmProviderSettings> Providers { get; set; } = new();
        public List<LlmFunctionPreference> Preferences { get; set; } = new();
    }

    /// <summary>Loads the file, unwrapping protected keys; returns true when a plaintext
    /// (or plaintext-marked) key should be rewritten under the protector.</summary>
    private bool LoadLocked()
    {
        _storedKeys = new Dictionary<string, StoredApiKey>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path))
        {
            _data = new SettingsFile();
            return false;
        }

        var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
                   ?? throw new JsonException("The settings file has no settings object.");
        var upgrade = false;
        if (root[nameof(SettingsFile.Providers)] is JsonArray providers)
        {
            foreach (var provider in providers.OfType<JsonObject>())
            {
                var name = ReadString(provider, nameof(LlmProviderSettings.ProviderName));
                var stored = ReadString(provider, ProtectedApiKeyProperty);
                provider.Remove(ProtectedApiKeyProperty);
                var plaintext = ReadString(provider, ApiKeyProperty);
                if (!string.IsNullOrEmpty(plaintext))
                {
                    // A plaintext key (an earlier build, or a host without a protector).
                    upgrade |= _apiKeyProtector is not null;
                    continue;
                }

                if (string.IsNullOrEmpty(stored) || name is null) continue;
                var unwrapped = _apiKeyProtector?.Unprotect(stored);
                if (string.IsNullOrEmpty(unwrapped))
                {
                    // Fail closed: the key is unusable here, but its envelope is kept.
                    _storedKeys[name] = new StoredApiKey(stored, null);
                    continue;
                }

                provider[ApiKeyProperty] = unwrapped;
                _storedKeys[name] = new StoredApiKey(stored, unwrapped);
                if (!stored.StartsWith(EnvelopeSecretProtector.EnvelopeMarker, StringComparison.Ordinal))
                {
                    // Written while no OS keystore was available: upgrade once one is.
                    var reprotected = _apiKeyProtector!.Protect(unwrapped);
                    if (reprotected.StartsWith(EnvelopeSecretProtector.EnvelopeMarker, StringComparison.Ordinal))
                    {
                        _storedKeys[name] = new StoredApiKey(reprotected, unwrapped);
                        upgrade = true;
                    }
                }
            }
        }

        _data = root.Deserialize<SettingsFile>()
                ?? throw new JsonException("The settings file has no settings object.");
        return upgrade;
    }

    private static string? ReadString(JsonObject node, string property)
        => node[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private IDisposable Acquire()
    {
        Monitor.Enter(_lock);
        FileStream? lease = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var deadline = Environment.TickCount64 + 5000;
            while (lease is null)
            {
                try { lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(10); }
            }
            if (LoadLocked())
            {
                try { SaveLocked(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // The rewrite is retried on the next access; the loaded settings stay usable.
                }
            }
            return new Lease(_lock, lease);
        }
        catch
        {
            lease?.Dispose();
            Monitor.Exit(_lock);
            throw;
        }
    }

    private sealed class Lease(object gate, FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); Monitor.Exit(gate); }
    }

    private void SaveLocked()
    {
        // Protect before touching the disk so a protector failure writes nothing.
        var document = _apiKeyProtector is null && _storedKeys.Count == 0 ? null : ToProtectedDocument();
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (document is null) JsonSerializer.Serialize(stream, _data, JsonOptions);
                else JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// The on-disk form when keys are protected: no plaintext <c>ApiKey</c> while a
    /// protector is configured, an unchanged key keeps its existing envelope, and an
    /// envelope this host cannot read is carried forward unless its provider now holds
    /// a new key (an empty key on save never destroys an unreadable credential).
    /// </summary>
    private JsonObject ToProtectedDocument()
    {
        var root = JsonSerializer.SerializeToNode(_data)!.AsObject();
        if (root[nameof(SettingsFile.Providers)] is not JsonArray providers) return root;
        var written = new Dictionary<string, StoredApiKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers.OfType<JsonObject>())
        {
            var name = ReadString(provider, nameof(LlmProviderSettings.ProviderName));
            if (name is null) continue;
            _storedKeys.TryGetValue(name, out var stored);
            var plaintext = ReadString(provider, ApiKeyProperty);
            if (!string.IsNullOrEmpty(plaintext))
            {
                if (_apiKeyProtector is null) continue;
                var envelope = stored is { Plaintext: not null }
                               && string.Equals(stored.Plaintext, plaintext, StringComparison.Ordinal)
                               && stored.Protected.StartsWith(EnvelopeSecretProtector.EnvelopeMarker, StringComparison.Ordinal)
                    ? stored.Protected
                    : _apiKeyProtector.Protect(plaintext);
                if (string.IsNullOrEmpty(envelope))
                    throw new InvalidOperationException("The provider API key could not be protected for storage.");
                provider[ApiKeyProperty] = null;
                provider[ProtectedApiKeyProperty] = envelope;
                written[name] = new StoredApiKey(envelope, plaintext);
            }
            else if (stored is { Plaintext: null })
            {
                provider[ProtectedApiKeyProperty] = stored.Protected;
                written[name] = stored;
            }
        }

        _storedKeys = written;
        return root;
    }

    private LlmProviderSettings TrackProvider(LlmProviderSettings value)
    {
        var copy = CloneProvider(value);
        Baselines.Add(copy, new ProviderBaseline(_path, copy.ProviderName, copy.UpdatedAt));
        return copy;
    }

    private static LlmProviderSettings CloneProvider(LlmProviderSettings value)
        => JsonSerializer.Deserialize<LlmProviderSettings>(JsonSerializer.Serialize(value))!;

    public Task<LlmFunctionPreference?> GetFunctionPreferenceAsync(string functionType)
    {
        return Task.Run(() =>
        {
            using (Acquire())
            {
                var pref = _data.Preferences.FirstOrDefault(p =>
                    string.Equals(p.FunctionType, functionType, StringComparison.OrdinalIgnoreCase) && p.IsEnabled);
                return ClonePreference(pref);
            }
        });
    }

    public Task<List<LlmFunctionPreference>> GetAllFunctionPreferencesAsync()
    {
        return Task.Run(() =>
        {
            using (Acquire())
            {
                return _data.Preferences
                    .Select(preference => ClonePreference(preference)!)
                    .ToList();
            }
        });
    }

    public Task<bool> SetFunctionPreferenceAsync(LlmFunctionPreference preference)
        => SetFunctionPreferenceAsync(preference, FunctionPreferenceWriteAuthority.RespectUserPin);

    public Task<bool> SetFunctionPreferenceAsync(
        LlmFunctionPreference preference,
        FunctionPreferenceWriteAuthority authority)
    {
        preference = ClonePreference(preference)!;
        return Task.Run(() =>
        {
            if (!LlmFunctionPreferenceValidation.TryValidateFallbackPair(preference, out _))
                return false;

            using (Acquire())
            {
                return ApplyPreferenceLocked(
                    preference,
                    authority,
                    preserveRequestedMutationRevision: false) is not null;
            }
        });
    }

    public Task<FunctionPreferenceConditionalWriteResult> TrySetFunctionPreferenceAsync(
        LlmFunctionPreference preference,
        FunctionPreferenceWriteAuthority authority,
        FunctionPreferenceVersion expected)
        => TrySetExactFunctionPreferenceAsync(preference, authority, expected);

    public Task<FunctionPreferenceConditionalWriteResult> TrySetExactFunctionPreferenceAsync(
        LlmFunctionPreference preference,
        FunctionPreferenceWriteAuthority authority,
        FunctionPreferenceVersion expected)
    {
        preference = ClonePreference(preference)!;
        return Task.Run(() =>
        {
            if (!LlmFunctionPreferenceValidation.TryValidateFallbackPair(preference, out _))
            {
                return new FunctionPreferenceConditionalWriteResult(
                    FunctionPreferenceConditionalWriteStatus.Rejected);
            }

            using (Acquire())
            {
                var current = FindPreferenceLocked(preference.FunctionType);
                if (FunctionPreferenceVersion.Capture(preference.FunctionType, current) != expected)
                {
                    return new FunctionPreferenceConditionalWriteResult(
                        FunctionPreferenceConditionalWriteStatus.Conflict,
                        ClonePreference(current));
                }

                var applied = ApplyPreferenceLocked(
                        preference,
                        authority,
                        preserveRequestedMutationRevision: true);
                if (applied is null)
                {
                    return new FunctionPreferenceConditionalWriteResult(
                        FunctionPreferenceConditionalWriteStatus.Rejected,
                        ClonePreference(current));
                }

                return new FunctionPreferenceConditionalWriteResult(
                    FunctionPreferenceConditionalWriteStatus.Applied,
                    ClonePreference(applied));
            }
        });
    }

    public Task<FunctionPreferenceConditionalWriteResult> TryDeleteFunctionPreferenceAsync(
        string functionType,
        FunctionPreferenceWriteAuthority authority,
        FunctionPreferenceVersion expected)
    {
        return Task.Run(() =>
        {
            using (Acquire())
            {
                var current = FindPreferenceLocked(functionType);
                if (FunctionPreferenceVersion.Capture(functionType, current) != expected)
                {
                    return new FunctionPreferenceConditionalWriteResult(
                        FunctionPreferenceConditionalWriteStatus.Conflict,
                        ClonePreference(current));
                }

                if (current is null
                    || (authority != FunctionPreferenceWriteAuthority.UserAuthorizedOverride
                        && LlmPreferenceAuthors.IsUserPinned(current.PinnedByUserAt)))
                {
                    return new FunctionPreferenceConditionalWriteResult(
                        FunctionPreferenceConditionalWriteStatus.Rejected,
                        ClonePreference(current));
                }

                _data.Preferences.Remove(current);
                SaveLocked();
                return new FunctionPreferenceConditionalWriteResult(
                    FunctionPreferenceConditionalWriteStatus.Applied);
            }
        });
    }

    private LlmFunctionPreference? FindPreferenceLocked(string functionType)
        => _data.Preferences.FirstOrDefault(p =>
            string.Equals(p.FunctionType, functionType, StringComparison.OrdinalIgnoreCase));

    private LlmFunctionPreference? ApplyPreferenceLocked(
        LlmFunctionPreference preference,
        FunctionPreferenceWriteAuthority authority,
        bool preserveRequestedMutationRevision)
    {
        // The provider owns its stored objects. Never retain or mutate a caller's
        // mutable DTO: doing so would let later caller-side changes bypass the host
        // lock, user-pin authority check, mutation revision, and durable write.
        var candidate = ClonePreference(preference)!;
        var existing = FindPreferenceLocked(preference.FunctionType);
        if (existing is not null
            && LlmPreferenceAuthors.IsUserPinned(existing.PinnedByUserAt)
            && authority != FunctionPreferenceWriteAuthority.UserAuthorizedOverride)
        {
            var changesRoute = !string.Equals(existing.PreferredProvider, candidate.PreferredProvider, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.PreferredModel, candidate.PreferredModel, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.FallbackProvider, candidate.FallbackProvider, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.FallbackModel, candidate.FallbackModel, StringComparison.OrdinalIgnoreCase)
                || existing.IsEnabled != candidate.IsEnabled
                || existing.RequiresVision != candidate.RequiresVision;
            if (changesRoute) return null;
            candidate.UpdatedBy = existing.UpdatedBy;
            // An autonomous metadata write must not release the pin it just deferred to.
            candidate.PinnedByUserAt = existing.PinnedByUserAt;
        }

        _data.Preferences.RemoveAll(p =>
            string.Equals(p.FunctionType, candidate.FunctionType, StringComparison.OrdinalIgnoreCase));
        candidate.UpdatedAt = DateTime.UtcNow;
        candidate.MutationRevision = preserveRequestedMutationRevision
                                      && Guid.TryParseExact(
                                          candidate.MutationRevision,
                                          "N",
                                          out var requestedRevision)
            ? requestedRevision.ToString("N")
            : Guid.NewGuid().ToString("N");
        if (authority == FunctionPreferenceWriteAuthority.UserAuthorizedOverride)
        {
            candidate.UpdatedBy = LlmPreferenceAuthors.User;
            candidate.PinnedByUserAt ??= DateTime.UtcNow;
        }
        _data.Preferences.Add(candidate);
        // Comparison, mutation, and durable replacement stay under one host lock so a
        // stale writer cannot pass its check and race a later user pin or same-value write.
        SaveLocked();
        return candidate;
    }

    private static LlmFunctionPreference? ClonePreference(LlmFunctionPreference? preference)
        => preference is null
            ? null
            : new LlmFunctionPreference
            {
                Id = preference.Id,
                FunctionType = preference.FunctionType,
                PreferredProvider = preference.PreferredProvider,
                PreferredModel = preference.PreferredModel,
                FallbackProvider = preference.FallbackProvider,
                FallbackModel = preference.FallbackModel,
                IsEnabled = preference.IsEnabled,
                RequiresVision = preference.RequiresVision,
                CreatedAt = preference.CreatedAt,
                UpdatedAt = preference.UpdatedAt,
                CreatedBy = preference.CreatedBy,
                UpdatedBy = preference.UpdatedBy,
                PinnedByUserAt = preference.PinnedByUserAt,
                AuditCorrelationId = preference.AuditCorrelationId,
                MutationRevision = preference.MutationRevision,
                KnownGoodProvider = preference.KnownGoodProvider,
                KnownGoodModel = preference.KnownGoodModel,
                KnownGoodConfirmedAt = preference.KnownGoodConfirmedAt,
            };

    public Task<LlmProviderSettings?> GetProviderSettingsAsync(string providerName)
    {
        return Task.Run(() =>
        {
            using (Acquire())
            {
                var p = _data.Providers.FirstOrDefault(x =>
                    string.Equals(x.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
                return p is null ? null : TrackProvider(p);
            }
        });
    }

    public Task<List<LlmProviderSettings>> GetAllProviderSettingsAsync()
    {
        return Task.Run(() =>
        {
            using (Acquire()) return _data.Providers.Where(p => p.IsEnabled).Select(TrackProvider).ToList();
        });
    }

    public string GetDefaultProvider()
    {
        using (Acquire())
        {
            var withKey = _data.Providers.FirstOrDefault(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.ApiKey));
            if (withKey != null) return withKey.ProviderName;
            var local = _data.Providers.FirstOrDefault(p => p.IsEnabled && p.IsLocalProvider);
            if (local != null) return local.ProviderName;
        }
        return "Ollama";
    }

    public string GetDefaultModel(string provider)
    {
        using (Acquire())
        {
            var p = _data.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderName, provider, StringComparison.OrdinalIgnoreCase));
            if (p != null && !string.IsNullOrWhiteSpace(p.DefaultModel)) return p.DefaultModel!;
        }
        return provider.ToLowerInvariant() switch
        {
            "openai" => "gpt-5-mini",
            "claude" => "claude-haiku-4-5-20251001",
            "gemini" => "gemini-2.5-flash",
            "ollama" => "llama3.1:8b",
            _ => "gpt-5-mini",
        };
    }

    public Task<bool> DeleteFunctionPreferenceAsync(string functionType)
        => DeleteFunctionPreferenceAsync(
            functionType,
            FunctionPreferenceWriteAuthority.RespectUserPin);

    public Task<bool> DeleteFunctionPreferenceAsync(
        string functionType,
        FunctionPreferenceWriteAuthority authority)
    {
        return Task.Run(() =>
        {
            using (Acquire())
            {
                var current = FindPreferenceLocked(functionType);
                if (current is null
                    || (authority != FunctionPreferenceWriteAuthority.UserAuthorizedOverride
                        && LlmPreferenceAuthors.IsUserPinned(current.PinnedByUserAt)))
                {
                    return false;
                }

                _data.Preferences.Remove(current);
                SaveLocked();
                return true;
            }
        });
    }

    public Task<SaveProviderResult> SaveProviderAsync(LlmProviderSettings settings, ProviderSaveSource source = ProviderSaveSource.UserEdit)
    {
        var candidate = CloneProvider(settings);
        Baselines.TryGetValue(settings, out var baseline);
        return Task.Run(() =>
        {
            if (!LlmProviderLocalityPolicy.TryValidateForSave(candidate, out var localityError))
                return new SaveProviderResult { Success = false, Error = localityError };

            using (Acquire())
            {
                var current = _data.Providers.FirstOrDefault(p =>
                    string.Equals(p.ProviderName, candidate.ProviderName, StringComparison.OrdinalIgnoreCase));
                if (baseline is not null && (!string.Equals(baseline.Path, _path,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || !string.Equals(baseline.Name, candidate.ProviderName, StringComparison.OrdinalIgnoreCase)))
                    return new SaveProviderResult { Success = false, Error = "The provider identity changed. Reload before saving." };
                var originalRevision = baseline is null ? candidate.UpdatedAt : baseline.Revision;
                if (current is null && (baseline is not null || originalRevision is not null)
                    || current is not null && (current.UpdatedAt != originalRevision
                        || baseline is null && originalRevision is null))
                    return new SaveProviderResult { Success = false, Error = "The provider changed. Reload before saving." };

                var previousDefaultModel = current?.DefaultModel;
                var now = DateTime.UtcNow;
                candidate.UpdatedAt = current?.UpdatedAt is DateTime previous && previous >= now ? previous.AddTicks(1) : now;
                candidate.UserEditedAt = source == ProviderSaveSource.UserEdit ? now : current?.UserEditedAt;
                _data.Providers.RemoveAll(p =>
                    string.Equals(p.ProviderName, candidate.ProviderName, StringComparison.OrdinalIgnoreCase));
                _data.Providers.Add(candidate);
                SaveLocked();
                settings.UpdatedAt = candidate.UpdatedAt;
                settings.UserEditedAt = candidate.UserEditedAt;
                Baselines.Remove(settings);
                Baselines.Add(settings, new ProviderBaseline(_path, candidate.ProviderName, candidate.UpdatedAt));
                return new SaveProviderResult { Success = true, PreviousDefaultModel = previousDefaultModel };
            }
        });
    }

    /// <summary>Adopts a legacy provider key under the settings lease, then clears only
    /// that source key after successful adoption. The callback must not reenter this store.</summary>
    public Task<bool> TryConsumeProviderApiKeyAsync(string providerName, Func<string, Task<bool>> adoptIfAbsent)
        => Task.Run(() =>
        {
            using (Acquire())
            {
                var current = _data.Providers.FirstOrDefault(p =>
                    string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(current?.ApiKey)) return false;
                // Stay on this worker while the independent vault operation runs:
                // releasing the source lease would allow a newer JSON key to intervene.
                if (!adoptIfAbsent(current.ApiKey).GetAwaiter().GetResult()) return false;
                current.ApiKey = null;
                var now = DateTime.UtcNow;
                current.UpdatedAt = current.UpdatedAt is DateTime previous && previous >= now ? previous.AddTicks(1) : now;
                SaveLocked();
                return true;
            }
        });

    /// <summary>Clears the legacy source before clearing its authoritative credential.
    /// The callback must not reenter this store. A source persistence failure leaves
    /// the authoritative credential intact so a successful clear cannot resurrect it.</summary>
    public Task<bool> ClearProviderApiKeyAsync(string providerName, Func<Task<bool>> clearAuthoritative)
        => Task.Run(() =>
        {
            using (Acquire())
            {
                var current = _data.Providers.FirstOrDefault(p =>
                    string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
                var clearedSource = !string.IsNullOrEmpty(current?.ApiKey);
                if (clearedSource)
                {
                    current!.ApiKey = null;
                    var now = DateTime.UtcNow;
                    current.UpdatedAt = current.UpdatedAt is DateTime previous && previous >= now ? previous.AddTicks(1) : now;
                    SaveLocked();
                }
                var clearedAuthority = clearAuthoritative().GetAwaiter().GetResult();
                return clearedSource || clearedAuthority;
            }
        });

    public Task<bool> EnsureFunctionPreferenceAsync(string functionType, string preferredProvider, string preferredModel,
        string? fallbackProvider = null, string? fallbackModel = null, bool requiresVision = false)
    {
        return Task.Run(() =>
        {
            if (!LlmFunctionPreferenceValidation.TryValidateFallbackPair(
                    fallbackProvider,
                    fallbackModel,
                    out _))
            {
                return false;
            }

            using (Acquire())
            {
                if (_data.Preferences.Any(p => string.Equals(p.FunctionType, functionType, StringComparison.OrdinalIgnoreCase)))
                    return false;
                return ApplyPreferenceLocked(
                    new LlmFunctionPreference
                    {
                        FunctionType = functionType,
                        PreferredProvider = preferredProvider,
                        PreferredModel = preferredModel,
                        FallbackProvider = fallbackProvider,
                        FallbackModel = fallbackModel,
                        RequiresVision = requiresVision,
                        UpdatedBy = LlmPreferenceAuthors.RouterAutoMaterialization,
                    },
                    FunctionPreferenceWriteAuthority.RespectUserPin,
                    preserveRequestedMutationRevision: false) is not null;
            }
        });
    }
}
