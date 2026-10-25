using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>Host-neutral JSON settings persistence with fresh reads, checked provider revisions,
/// and atomic replacement serialized across independent hosts/processes.</summary>
public class JsonFileLlmSettingsProvider : ILlmSettingsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<LlmProviderSettings, ProviderBaseline> Baselines = new();
    private sealed record ProviderBaseline(string Path, string Name, DateTime? Revision);
    private readonly object _lock;
    private SettingsFile _data;

    public JsonFileLlmSettingsProvider(string path)
    {
        _path = Path.GetFullPath(path);
        _lock = Gates.GetOrAdd(_path, _ => new object());
        _data = new SettingsFile();
    }

    private sealed class SettingsFile
    {
        public List<LlmProviderSettings> Providers { get; set; } = new();
        public List<LlmFunctionPreference> Preferences { get; set; } = new();
    }

    private static SettingsFile Load(string path)
        => File.Exists(path)
            ? JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path))
              ?? throw new JsonException("The settings file has no settings object.")
            : new SettingsFile();

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
            _data = Load(_path);
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
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _data, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        // Same deep fallback as the shipped host: the reviewed provider-pack
        // tier, never another provider's model id for an unknown provider.
        return ModelDefaults.ForProvider(provider) ?? "unknown-model";
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
