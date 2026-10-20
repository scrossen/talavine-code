using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.LocalRuntime;

public sealed record LocalVisionProbe(bool Passed, string Model, string ArtifactDigest,
    int ImagesChecked, IReadOnlyList<string> Answers, string Message);

public sealed partial class OllamaSetupClient
{
    /// <summary>Physical image acceptance, not a general quality clearance. Synthetic images never contain user data.</summary>
    public async Task<LocalVisionProbe> TestVisionAsync(string endpoint, string model, ILocalLlmRuntimeManager runtime,
        CancellationToken ct = default)
    {
        endpoint = ValidateEndpoint(endpoint); model = NormalizeModel(model);
        if (!new Uri(endpoint).IsLoopback) throw new InvalidOperationException("This synthetic model probe requires the local Ollama server.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        using var lease = await runtime.AcquireAsync(new("Ollama", model, WorkloadKind: LocalLlmWorkloadKind.Vision), token).ConfigureAwait(false);
        var before = (await InventoryAsync(endpoint, token).ConfigureAwait(false)).Models.SingleOrDefault(m => m.Name == model)
            ?? throw new InvalidOperationException("Install this exact model before testing it.");
        if (!before.IsOnDevice) throw new InvalidOperationException("Ollama did not confirm on-device execution. No image was sent.");
        using var client = Client(); client.Timeout = Timeout.InfiniteTimeSpan;
        var palette = new[] { "red", "blue", "green", "yellow" };
        var left = RandomNumberGenerator.GetInt32(palette.Length);
        var right = (left + RandomNumberGenerator.GetInt32(1, palette.Length)) % palette.Length;
        var answers = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            var expectedLeft = palette[index == 0 ? left : right];
            var expectedRight = palette[index == 0 ? right : left];
            using var response = await client.PostAsJsonAsync(endpoint + "/api/chat", new
            {
                model, stream = false, think = false, keep_alive = "5m",
                messages = new[] { new { role = "user",
                    content = "Identify the background color in each half of this image. Reply only with JSON {\"left\":\"color\",\"right\":\"color\"} using basic English color names.",
                    images = new[] { Convert.ToBase64String(CreateColorImage(expectedLeft, expectedRight)) } } },
                options = new { num_ctx = 8192, num_predict = 128, temperature = 0 }
            }, token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (!response.IsSuccessStatusCode || json.RootElement.TryGetProperty("error", out _))
                return new(false, model, before.Digest, index, answers, "The runtime rejected the synthetic image. Check its matching vision component and runtime support.");
            var root = json.RootElement;
            var answer = root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                ? content.GetString() ?? "" : "";
            answers.Add(answer.Length <= 512 ? answer : answer[..512]);
            if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True
                || !MatchesColors(answer, expectedLeft, expectedRight))
                return new(false, model, before.Digest, index + 1, answers, "Image inference did not satisfy the visual contract. A successful request alone is not vision proof.");
        }
        var after = (await InventoryAsync(endpoint, token).ConfigureAwait(false)).Models.SingleOrDefault(m => m.Name == model);
        if (after?.Digest != before.Digest) throw new InvalidOperationException("The artifact changed during its image test; test the current version again.");
        return new(true, model, before.Digest, 2, answers, "Passed two real image requests with reversed visual content and identical prompts. This is capability proof, not a general quality benchmark.");
    }

    internal static bool MatchesColors(string answer, string left, string right)
    {
        var text = answer.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            if (newline < 0) return false;
            text = text[(newline + 1)..^3].Trim();
        }
        try
        {
            using var result = JsonDocument.Parse(text);
            return result.RootElement.TryGetProperty("left", out var l) && l.ValueKind == JsonValueKind.String
                && result.RootElement.TryGetProperty("right", out var r) && r.ValueKind == JsonValueKind.String
                && string.Equals(l.GetString()?.Trim(), left, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.GetString()?.Trim(), right, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static byte[] CreateColorImage(string left, string right)
    {
        const int width = 256, height = 128;
        static byte[] Color(string value) => value switch
            { "red" => [255, 0, 0], "blue" => [0, 0, 255], "green" => [0, 255, 0], "yellow" => [255, 255, 0], _ => throw new ArgumentException("Unknown fixture color.") };
        var a = Color(left); var b = Color(right);
        using var png = new MemoryStream(); png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                zip.WriteByte(0);
                for (var x = 0; x < width; x++) zip.Write(x < width / 2 ? a : b);
            }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return png.ToArray();
        void Chunk(string kind, byte[] data)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); png.Write(number);
            var bytes = Encoding.ASCII.GetBytes(kind).Concat(data).ToArray(); png.Write(bytes);
            var crc = uint.MaxValue;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); png.Write(number);
        }
    }
}
