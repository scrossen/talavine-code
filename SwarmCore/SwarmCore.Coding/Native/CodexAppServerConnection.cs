using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace SwarmCore.Coding.Native;

/// <summary>One owned stdio connection. It never retries a request whose acknowledgement was lost.</summary>
internal sealed class CodexAppServerConnection : IAsyncDisposable
{
    private readonly TextReader _output;
    private readonly TextWriter _input;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _requests = new();
    private readonly Channel<JsonObject> _notifications = Channel.CreateUnbounded<JsonObject>(new() { SingleWriter = true, SingleReader = true });
    private readonly Task _reader;
    private int _nextId;

    public CodexAppServerConnection(TextReader output, TextWriter input, CancellationToken ct)
    {
        _output = output; _input = input;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reader = ReadAsync(_lifetime.Token);
    }

    public ChannelReader<JsonObject> Notifications => _notifications.Reader;

    public async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate native request identity.");
        try
        {
            await WriteAsync(new() { ["id"] = id, ["method"] = method, ["params"] = parameters }, ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        finally { _requests.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, CancellationToken ct)
        => WriteAsync(new() { ["method"] = method }, ct);

    private async Task WriteAsync(JsonObject frame, CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _input.WriteLineAsync(frame.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            await _input.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (await _output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var frame = CodexNativeLaneRunner.ParseEvent(line) ?? throw new InvalidDataException("Native app-server sent an invalid frame.");
                if (frame["method"] is not null)
                {
                    if (frame["id"] is not null)
                    {
                        // This adapter never invents approval, permissions or user answers.
                        await WriteAsync(new() { ["id"] = frame["id"]!.DeepClone(), ["error"] = new JsonObject
                            { ["code"] = -32601, ["message"] = "This native host does not admit that interactive request." } }, ct).ConfigureAwait(false);
                    }
                    else await _notifications.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
                    continue;
                }
                if (frame["id"] is JsonValue key && key.TryGetValue<int>(out var id) && _requests.TryGetValue(id, out var completion))
                {
                    if (frame["error"] is JsonObject error) completion.TrySetException(new CodexAppServerRejectedException(
                        (int?)error["code"] ?? -1, (string?)error["message"] ?? "Native request was rejected."));
                    else completion.TrySetResult(frame["result"] as JsonObject ?? new());
                }
            }
            failure = new EndOfStreamException("The native app-server connection closed.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            foreach (var pending in _requests.Values) pending.TrySetException(failure!);
            _notifications.Writer.TryComplete(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}

internal sealed class CodexAppServerRejectedException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}
