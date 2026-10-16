using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Record for LLM request debug information
/// </summary>
public record LlmDebugRequest(
    string RequestId,
    string FunctionType,
    string Provider,
    string Model,
    string Prompt,
    string? ImageBase64Summary = null,
    float? Temperature = null,
    int? MaxTokens = null,
    bool IsFallback = false,
    string? FallbackReason = null);

/// <summary>
/// Record for LLM response debug information
/// </summary>
public record LlmDebugResponse(
    string RequestId,
    string FunctionType,
    string Provider,
    string Model,
    string? Response,
    int InputTokens,
    int OutputTokens,
    long ElapsedMs,
    bool IsFallback = false);

/// <summary>
/// Record for LLM error debug information
/// </summary>
public record LlmDebugError(
    string RequestId,
    string FunctionType,
    string Provider,
    string Model,
    string ErrorMessage,
    string? StackTrace,
    long ElapsedMs);

/// <summary>
/// Record for LLM embedding request debug information
/// </summary>
public record LlmDebugEmbeddingRequest(
    string RequestId,
    string FunctionType,
    string Provider,
    string Model,
    int TextLength,
    string? TextPreview = null,
    int? TextCount = null,
    int? TotalChars = null,
    int? BatchSize = null);

/// <summary>
/// Record for LLM embedding response debug information
/// </summary>
public record LlmDebugEmbeddingResponse(
    string RequestId,
    string FunctionType,
    string Provider,
    string Model,
    bool Success,
    int? VectorDimension,
    int? TokenCount,
    long ElapsedMs,
    string? ErrorMessage = null,
    int? EmbeddingCount = null,
    int? Dimensions = null,
    int? TokensUsed = null);

/// <summary>
/// Interface for LLM debug logging. Captures detailed information about LLM requests
/// for debugging and analysis purposes.
/// </summary>
public interface ILlmDebugLogger
{
    /// <summary>
    /// Log an LLM request
    /// </summary>
    void LogRequest(LlmDebugRequest request);

    /// <summary>
    /// Log an LLM response
    /// </summary>
    void LogResponse(LlmDebugResponse response);

    /// <summary>
    /// Log an LLM error
    /// </summary>
    void LogError(LlmDebugError error);

    /// <summary>
    /// Log an embedding request
    /// </summary>
    void LogEmbeddingRequest(LlmDebugEmbeddingRequest request);

    /// <summary>
    /// Log an embedding response
    /// </summary>
    void LogEmbeddingResponse(LlmDebugEmbeddingResponse response);
}

/// <summary>
/// Null implementation that doesn't log anything. Used in production.
/// </summary>
public class NullLlmDebugLogger : ILlmDebugLogger
{
    public static readonly NullLlmDebugLogger Instance = new();

    private NullLlmDebugLogger() { }

    public void LogRequest(LlmDebugRequest request) { }
    public void LogResponse(LlmDebugResponse response) { }
    public void LogError(LlmDebugError error) { }
    public void LogEmbeddingRequest(LlmDebugEmbeddingRequest request) { }
    public void LogEmbeddingResponse(LlmDebugEmbeddingResponse response) { }
}

/// <summary>
/// Options for the file-based LLM debug logger.
/// </summary>
public sealed class LlmDebugLoggerOptions
{
    /// <summary>
    /// Optional directory for the log file. When unset, the logger writes
    /// beneath the current user's local application data folder.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// Application folder name used under local application data when
    /// <see cref="LogDirectory"/> is not provided.
    /// </summary>
    public string ApplicationName { get; set; } = "SwarmCore";

    /// <summary>
    /// File name for the log.
    /// </summary>
    public string LogFileName { get; set; } = "llm-debug.log";
}

/// <summary>
/// File-based debug logger that writes LLM requests/responses to a log file.
/// Used in DEBUG builds for troubleshooting.
///
/// Writes are pushed onto an in-memory channel and drained by a single
/// background task. This keeps `LogRequest`/`LogResponse` non-blocking — the
/// previous implementation took a lock and called `File.AppendAllText` on the
/// caller's thread, which meant any LLM call originating on the Blazor
/// dispatcher (or any LLM call awaiting back to it) had to wait for a
/// synchronous file append. With a multi-hundred-MB log, that quickly turned
/// into multi-second UI hangs.
///
/// The log file is also rotated once it exceeds <see cref="MaxLogBytes"/> so a
/// runaway debug session can't slow appends indefinitely.
/// </summary>
public class FileLlmDebugLogger : ILlmDebugLogger, IAsyncDisposable, IDisposable
{
    // Cap a single log file at 10 MB. Rotated once on overflow (foo.log.prev
    // overwrites the prior rotation), so worst-case disk use is ~20 MB.
    private const long MaxLogBytes = 10L * 1024 * 1024;

    // Bounded channel keeps memory bounded under burst load. DropOldest means
    // we lose the oldest queued debug entry rather than blocking the producer
    // (a debug logger MUST NOT slow the LLM hot path).
    private const int ChannelCapacity = 4096;

    private readonly string _logPath;
    private readonly ILogger<FileLlmDebugLogger> _logger;
    private readonly Channel<string> _channel;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _shutdown = new();

    public FileLlmDebugLogger(ILogger<FileLlmDebugLogger> logger)
        : this(logger, Options.Create(new LlmDebugLoggerOptions()))
    {
    }

    public FileLlmDebugLogger(ILogger<FileLlmDebugLogger> logger, IOptions<LlmDebugLoggerOptions> options)
    {
        _logger = logger;

        var loggerOptions = options.Value;
        var logDirectory = string.IsNullOrWhiteSpace(loggerOptions.LogDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SanitizePathSegment(loggerOptions.ApplicationName))
            : loggerOptions.LogDirectory;

        _logPath = Path.Combine(logDirectory, SanitizeFileName(loggerOptions.LogFileName));

        // Ensure directory exists
        var dir = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _writerTask = Task.Run(DrainAsync);
    }

    public void LogRequest(LlmDebugRequest request)
    {
        var logEntry = $"""
            [{DateTime.UtcNow:O}] REQUEST
            RequestId: {request.RequestId}
            FunctionType: {request.FunctionType}
            Provider: {request.Provider}
            Model: {request.Model}
            Temperature: {request.Temperature}
            MaxTokens: {request.MaxTokens}
            IsFallback: {request.IsFallback}
            FallbackReason: {request.FallbackReason ?? "N/A"}
            ImageSummary: {request.ImageBase64Summary ?? "N/A"}
            Prompt: {TruncateForLog(request.Prompt)}
            ---
            """;

        WriteLog(logEntry);
    }

    public void LogResponse(LlmDebugResponse response)
    {
        var logEntry = $"""
            [{DateTime.UtcNow:O}] RESPONSE
            RequestId: {response.RequestId}
            FunctionType: {response.FunctionType}
            Provider: {response.Provider}
            Model: {response.Model}
            Elapsed: {response.ElapsedMs}ms
            InputTokens: {response.InputTokens}
            OutputTokens: {response.OutputTokens}
            IsFallback: {response.IsFallback}
            Response: {TruncateForLog(response.Response ?? "[null]")}
            ---
            """;

        WriteLog(logEntry);
    }

    public void LogError(LlmDebugError error)
    {
        var logEntry = $"""
            [{DateTime.UtcNow:O}] ERROR
            RequestId: {error.RequestId}
            FunctionType: {error.FunctionType}
            Provider: {error.Provider}
            Model: {error.Model}
            Elapsed: {error.ElapsedMs}ms
            Error: {error.ErrorMessage}
            StackTrace: {TruncateForLog(error.StackTrace ?? "[none]")}
            ---
            """;

        WriteLog(logEntry);
    }

    public void LogEmbeddingRequest(LlmDebugEmbeddingRequest request)
    {
        var logEntry = $"""
            [{DateTime.UtcNow:O}] EMBEDDING REQUEST
            RequestId: {request.RequestId}
            FunctionType: {request.FunctionType}
            Provider: {request.Provider}
            Model: {request.Model}
            TextLength: {request.TextLength}
            TextPreview: {request.TextPreview ?? "N/A"}
            ---
            """;

        WriteLog(logEntry);
    }

    public void LogEmbeddingResponse(LlmDebugEmbeddingResponse response)
    {
        var logEntry = $"""
            [{DateTime.UtcNow:O}] EMBEDDING RESPONSE
            RequestId: {response.RequestId}
            FunctionType: {response.FunctionType}
            Provider: {response.Provider}
            Model: {response.Model}
            Elapsed: {response.ElapsedMs}ms
            Success: {response.Success}
            VectorDimension: {response.VectorDimension}
            TokenCount: {response.TokenCount}
            Error: {response.ErrorMessage ?? "none"}
            ---
            """;

        WriteLog(logEntry);
    }

    private void WriteLog(string entry)
    {
        // Non-blocking enqueue. If the channel is full (writer can't keep up
        // with a burst), DropOldest discards the oldest queued entry. The
        // calling thread never waits on disk I/O.
        _channel.Writer.TryWrite(SanitizeSurrogates(entry) + Environment.NewLine);
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    RotateIfTooLarge();
                    await File.AppendAllTextAsync(_logPath, entry, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to write to LLM debug log (non-fatal)");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RotateIfTooLarge()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < MaxLogBytes) return;

            var rotated = _logPath + ".prev";
            try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }
            File.Move(_logPath, rotated);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to rotate LLM debug log (non-fatal)");
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _channel.Writer.TryComplete();
            _shutdown.Cancel();
            await _writerTask.ConfigureAwait(false);
        }
        catch { }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private static string TruncateForLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return "[empty]";
        if (text.Length <= 500) return text;
        // Don't slice between a high/low surrogate pair — that produces an
        // orphan that the UTF-8 encoder can't represent.
        var cut = 500;
        if (char.IsHighSurrogate(text[cut - 1]) && cut < text.Length && char.IsLowSurrogate(text[cut]))
            cut--;
        return text[..cut] + $"... [truncated, {text.Length} total chars]";
    }

    // Replace any unpaired surrogate with U+FFFD so File.AppendAllText's
    // UTF-8 encoder doesn't throw on malformed input from upstream truncation.
    private static string SanitizeSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool bad = char.IsHighSurrogate(c)
                ? (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                : char.IsLowSurrogate(c);
            if (!bad) { if (char.IsHighSurrogate(c)) i++; continue; }

            var sb = new System.Text.StringBuilder(text.Length);
            sb.Append(text, 0, i);
            for (int j = i; j < text.Length; j++)
            {
                char cj = text[j];
                if (char.IsHighSurrogate(cj))
                {
                    if (j + 1 < text.Length && char.IsLowSurrogate(text[j + 1]))
                    {
                        sb.Append(cj);
                        sb.Append(text[j + 1]);
                        j++;
                    }
                    else { sb.Append('�'); }
                }
                else if (char.IsLowSurrogate(cj)) { sb.Append('�'); }
                else { sb.Append(cj); }
            }
            return sb.ToString();
        }
        return text;
    }

    private static string SanitizePathSegment(string value)
    {
        var segment = string.IsNullOrWhiteSpace(value) ? "SwarmCore" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            segment = segment.Replace(invalid, '_');
        }
        return segment;
    }

    private static string SanitizeFileName(string value)
    {
        var fileName = string.IsNullOrWhiteSpace(value) ? "llm-debug.log" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }
        return fileName;
    }
}
