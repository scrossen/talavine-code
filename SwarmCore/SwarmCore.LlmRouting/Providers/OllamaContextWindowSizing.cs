using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Sizes the window (num_ctx) an Ollama runner serves a native chat request
/// with, so the request is served whole. The window is a runtime setting, not a
/// model fact: a daemon loads a model at its Modelfile num_ctx or its own
/// default, which differs by version and by the machine's GPU memory. A prompt
/// longer than that window is either rejected or, when truncation is allowed,
/// silently cut down to whatever fits. The adapter therefore forbids
/// truncation and, when the runtime reports a request that does not fit,
/// reloads the model once at a larger window chosen here. The window is capped
/// by the model's trained context length and by the KV cache its memory
/// envelope (<see cref="LocalModelMemoryEnvelope"/>) leaves room for; a request
/// beyond either cap fails instead of being truncated.
/// </summary>
internal static partial class OllamaContextWindowSizing
{
    /// <summary>The smallest window the adapter ever requests.</summary>
    internal const int MinimumWindowTokens = 2048;

    /// <summary>Chat-template and stop tokens beyond the prompt the runtime counted.</summary>
    internal const int TemplateReserveTokens = 64;

    /// <summary>Output room for a request that set no output limit.</summary>
    internal const int UncappedOutputReserveTokens = 1024;

    /// <summary>
    /// Tokens charged per image when the runtime did not count the prompt. An
    /// image costs anywhere from nothing (cross-attention) to a few thousand
    /// tokens, so the estimate errs large.
    /// </summary>
    internal const int ImageReserveTokens = 4096;

    /// <summary>Envelope-capped windows are rounded down to this step.</summary>
    internal const int WindowStepTokens = 256;

    /// <summary>
    /// The runtime's cache type is not reported; f16, Ollama's default, is the
    /// largest it uses without an explicit setting, so the estimate never
    /// understates memory.
    /// </summary>
    private const int KvBytesPerElement = 2;

    /// <summary>
    /// A sliding-window layer keeps its window plus one processing batch.
    /// </summary>
    private const int SlidingWindowBatchTokens = 512;

    /// <summary>
    /// The first Ollama release whose chat and generate requests accept
    /// truncate (0.12.6, October 2025). An older runtime ignores it and cuts an
    /// over-window prompt down to what fits, so its window never grows to serve
    /// a request whole.
    /// </summary>
    internal static readonly Version MinimumServeWholeRuntime = new(0, 12, 6);

    /// <summary>
    /// True when a runtime of this version rejects an over-window request that
    /// forbids truncation instead of cutting it. An unknown or unreadable
    /// version (a development build reports 0.0.0) is not assumed to.
    /// </summary>
    internal static bool RuntimeServesWhole(string? runtimeVersion)
    {
        if (string.IsNullOrWhiteSpace(runtimeVersion)) return false;
        var text = runtimeVersion.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.')) end++;
        return Version.TryParse(text[..end].TrimEnd('.'), out var version)
               && version >= MinimumServeWholeRuntime;
    }

    /// <summary>
    /// True when <paramref name="body"/> is the runtime saying the request did
    /// not fit its window. The llama.cpp server reports exact counts
    /// (n_prompt_tokens, n_ctx); an engine that reports only "input length
    /// exceeds the context length" leaves them null.
    /// </summary>
    internal static bool TryReadOverflow(
        HttpStatusCode status,
        string? body,
        out int? promptTokens,
        out int? windowTokens)
    {
        promptTokens = null;
        windowTokens = null;
        if (string.IsNullOrWhiteSpace(body)
            || status is not (HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError))
        {
            return false;
        }

        // The Ollama server wraps the runner's JSON error inside a JSON string,
        // so the field names can arrive with escaped quotes.
        var text = body.Replace("\\\"", "\"", StringComparison.Ordinal);
        var reported = text.Contains("exceed_context_size_error", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase);
        var inputTooLong = text.Contains("input length", StringComparison.OrdinalIgnoreCase)
                           && text.Contains("context length", StringComparison.OrdinalIgnoreCase);
        if (!reported && !inputTooLong)
        {
            return false;
        }

        promptTokens = ReadCount(PromptTokensField().Match(text))
                       ?? ReadCount(OverflowSentence().Match(text), "prompt");
        windowTokens = ReadCount(WindowTokensField().Match(text))
                       ?? ReadCount(OverflowSentence().Match(text), "window");
        return true;
    }

    /// <summary>
    /// The window a request needs: its prompt, its output budget, and the
    /// template reserve. Without a runtime count the prompt is charged its
    /// UTF-8 length, an upper bound for byte-level tokenizers, plus
    /// <see cref="ImageReserveTokens"/> per image.
    /// </summary>
    internal static int RequiredTokens(
        int? countedPromptTokens,
        string prompt,
        int imageCount,
        int? outputTokens)
    {
        long promptTokens = countedPromptTokens is > 0
            ? countedPromptTokens.Value
            : Encoding.UTF8.GetByteCount(prompt ?? string.Empty) + (long)Math.Max(0, imageCount) * ImageReserveTokens;
        long output = outputTokens is > 0 ? outputTokens.Value : UncappedOutputReserveTokens;
        return (int)Math.Min(int.MaxValue, promptTokens + output + TemplateReserveTokens);
    }

    /// <summary>
    /// The model facts that bound its window, read from an <c>/api/show</c>
    /// model_info object. Null when the architecture's attention shape is not
    /// reported, since the memory a window costs is then unknown.
    /// </summary>
    internal static OllamaModelContextProfile? ReadProfile(JsonElement? modelInfo)
    {
        if (modelInfo is not { ValueKind: JsonValueKind.Object } info
            || !info.TryGetProperty("general.architecture", out var architectureValue)
            || architectureValue.ValueKind != JsonValueKind.String
            || architectureValue.GetString() is not { Length: > 0 } architecture)
        {
            return null;
        }

        var prefix = architecture + ".";
        var trained = ReadPositiveInt(info, prefix + "context_length");
        var layers = ReadPositiveInt(info, prefix + "block_count");
        var heads = ReadPositiveInt(info, prefix + "attention.head_count");
        var embedding = ReadPositiveInt(info, prefix + "embedding_length");
        if (layers is not int blockCount)
        {
            return new OllamaModelContextProfile(trained, null, 0);
        }

        var kvHeads = ReadPerLayer(info, prefix + "attention.head_count_kv", blockCount);
        var keyLength = ReadPositiveInt(info, prefix + "attention.key_length")
                        ?? (embedding is int width && heads is int count ? width / count : null);
        var valueLength = ReadPositiveInt(info, prefix + "attention.value_length") ?? keyLength;
        if (keyLength is not int key || valueLength is not int value || (kvHeads is null && heads is null))
        {
            return new OllamaModelContextProfile(trained, null, 0);
        }

        var keySwa = ReadPositiveInt(info, prefix + "attention.key_length_swa") ?? key;
        var valueSwa = ReadPositiveInt(info, prefix + "attention.value_length_swa") ?? value;
        var slidingWindow = ReadPositiveInt(info, prefix + "attention.sliding_window");
        // Only a per-layer pattern says which layers are windowed. Without one,
        // every layer is counted at full length, which overstates memory.
        var windowed = slidingWindow is > 0
            ? ReadPerLayerFlags(info, prefix + "attention.sliding_window_pattern", blockCount)
            : null;

        long perToken = 0;
        long fixedBytes = 0;
        for (var layer = 0; layer < blockCount; layer++)
        {
            long layerKvHeads = kvHeads?[layer] ?? heads!.Value;
            if (windowed?[layer] == true)
            {
                fixedBytes += layerKvHeads * (keySwa + valueSwa) * KvBytesPerElement
                              * (slidingWindow!.Value + (long)SlidingWindowBatchTokens);
            }
            else
            {
                perToken += layerKvHeads * (key + value) * KvBytesPerElement;
            }
        }

        return new OllamaModelContextProfile(trained, perToken > 0 ? perToken : null, fixedBytes);
    }

    /// <summary>
    /// The largest window whose KV cache fits the envelope left after the
    /// model's weights, or null when either input is unknown.
    /// </summary>
    internal static int? MaximumWindowInEnvelope(OllamaModelContextProfile profile, long artifactBytes)
    {
        if (profile.KvBytesPerToken is not long perToken || artifactBytes <= 0)
        {
            return null;
        }

        var budget = LocalModelMemoryEnvelope.KvCacheBudgetBytes(artifactBytes) - profile.FixedKvBytes;
        return budget <= 0 ? 0 : (int)Math.Min(int.MaxValue, budget / perToken);
    }

    /// <summary>
    /// The largest window <see cref="Plan"/> can give the model: its trained
    /// context length or the largest step-rounded window its envelope holds,
    /// whichever is smaller. Every request needing at most this many tokens is
    /// served whole. Null when no window can be planned: the envelope window is
    /// unknown or leaves no room for a KV cache.
    /// </summary>
    internal static int? MaximumWindow(OllamaModelContextProfile profile, long artifactBytes)
    {
        if (MaximumWindowInEnvelope(profile, artifactBytes) is not int allowed)
        {
            return null;
        }

        var window = allowed / WindowStepTokens * WindowStepTokens;
        if (profile.TrainedContextTokens is int trained)
        {
            window = Math.Min(window, trained);
        }

        return window > 0 ? window : null;
    }

    /// <summary>
    /// The window to reload the model at, or null with the reason no larger
    /// window can hold <paramref name="requiredTokens"/>. The first choice is
    /// the next power of two, so a model grows in few steps; a cap lowers it
    /// to the largest multiple of <see cref="WindowStepTokens"/> that fits.
    /// When <paramref name="requiredIsUpperBound"/> is set, the requirement is
    /// an uncounted estimate (UTF-8 bytes, several times the real token count),
    /// so a cap below it is not proof the request cannot fit: the largest
    /// allowed window is tried once and the runtime's next answer decides.
    /// </summary>
    internal static OllamaContextWindowPlan Plan(
        int requiredTokens,
        int? currentWindowTokens,
        OllamaModelContextProfile? profile,
        long artifactBytes,
        bool requiredIsUpperBound = false)
    {
        var target = NextPowerOfTwo(Math.Max(requiredTokens, MinimumWindowTokens));
        var limit = "";
        if (profile?.TrainedContextTokens is int trained && trained < target)
        {
            target = trained;
            limit = $"the model's trained context length ({trained:N0} tokens)";
        }

        var envelopeWindow = profile is null ? null : MaximumWindowInEnvelope(profile, artifactBytes);
        if (envelopeWindow is not int allowed)
        {
            return new(null, "the memory a larger context window needs is unknown for this model");
        }

        if (allowed < target)
        {
            target = allowed / WindowStepTokens * WindowStepTokens;
            limit = $"the model's memory envelope ({allowed:N0} tokens of KV cache)";
        }

        if (target < requiredTokens
            && !(requiredIsUpperBound && target > 0 && (currentWindowTokens is not int grown || target > grown)))
        {
            return new(null, $"it needs about {requiredTokens:N0} tokens but {limit} allows at most {target:N0}");
        }

        if (currentWindowTokens is int current && target <= current)
        {
            return new(null, $"the runtime's {current:N0}-token window already reaches the largest allowed");
        }

        return new(target, $"{requiredTokens:N0} tokens needed");
    }

    private static int NextPowerOfTwo(int value)
    {
        var power = MinimumWindowTokens;
        while (power < value && power <= int.MaxValue / 2)
        {
            power *= 2;
        }

        return power < value ? int.MaxValue : power;
    }

    private static int? ReadCount(Match match, string group = "count")
        => match.Success
           && int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
           && count > 0
            ? count
            : null;

    private static int? ReadPositiveInt(JsonElement info, string name)
        => info.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
           && number > 0
            ? number
            : null;

    /// <summary>A per-layer array, or one value repeated for every layer.</summary>
    private static long[]? ReadPerLayer(JsonElement info, string name, int layers)
    {
        if (!info.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var uniform) && uniform > 0)
        {
            return Enumerable.Repeat((long)uniform, layers).ToArray();
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != layers)
        {
            return null;
        }

        var result = new long[layers];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var count) || count < 0)
            {
                return null;
            }

            result[index++] = count;
        }

        return result;
    }

    private static bool[]? ReadPerLayerFlags(JsonElement info, string name, int layers)
    {
        if (!info.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() != layers)
        {
            return null;
        }

        var result = new bool[layers];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            result[index++] = item.GetBoolean();
        }

        return result;
    }

    [GeneratedRegex(@"""n_prompt_tokens""\s*:\s*(?<count>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PromptTokensField();

    [GeneratedRegex(@"""n_ctx""\s*:\s*(?<count>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowTokensField();

    [GeneratedRegex(
        @"request \((?<prompt>\d+) tokens\) exceeds the available context size \((?<window>\d+) tokens\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OverflowSentence();
}

/// <summary>
/// The largest window the native Ollama adapter serves a request whole at, for
/// hosts that describe routes before any request is sent, such as Model
/// Scout's inventory probes. Growing a window reloads the model; the window is
/// capped by the artifact's trained context length and by the KV cache its
/// memory envelope leaves.
/// </summary>
public static class OllamaServableContextWindow
{
    /// <summary>
    /// True when a runtime of this version rejects an over-window request that
    /// forbids truncation (Ollama 0.12.6 and later) instead of cutting it.
    /// </summary>
    public static bool RuntimeServesWhole(string? runtimeVersion)
        => OllamaContextWindowSizing.RuntimeServesWhole(runtimeVersion);

    /// <summary>
    /// The largest window for an artifact, from its <c>/api/show</c> model_info
    /// and installed size; null when its attention shape or size is unknown.
    /// It says nothing about the runtime: check <see cref="RuntimeServesWhole"/>.
    /// </summary>
    public static int? LargestWindow(IReadOnlyDictionary<string, JsonElement>? modelInfo, long artifactBytes)
        => modelInfo is { Count: > 0 }
           && OllamaContextWindowSizing.ReadProfile(JsonSerializer.SerializeToElement(modelInfo)) is { } profile
            ? OllamaContextWindowSizing.MaximumWindow(profile, artifactBytes)
            : null;
}

/// <summary>
/// KV-cache cost of one model: bytes per token of the full-attention layers,
/// plus the fixed bytes of sliding-window layers, which never grow past their
/// window. A null per-token cost means the attention shape was not reported.
/// </summary>
internal sealed record OllamaModelContextProfile(
    int? TrainedContextTokens,
    long? KvBytesPerToken,
    long FixedKvBytes);

/// <summary>The window to reload at, or null with the reason none can serve the request.</summary>
internal sealed record OllamaContextWindowPlan(int? WindowTokens, string Reason);
