using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>One connection belongs to one host turn. A single reader and writer serialize
/// Responses lineage and acknowledgment correlation; queued steering is connection-local.</summary>
internal sealed class ResponsesSteeringConnection : IChatContextBoundaryTransport
{
    internal sealed record Frame(JsonObject? Wire, ChatSteeringDeliveryEvent? Delivery = null);
    private readonly WebSocket _socket;
    private readonly IChatSteeringSource _source;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Channel<Frame> _frames = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly object _sync = new();
    private readonly Dictionary<string, (ChatSteeringUpdate Update, string Parent)> _accepted = new(StringComparer.Ordinal);
    private (ChatSteeringUpdate Update, string Parent)? _sending;
    private string? _responseId;
    private readonly Task _reader;
    private readonly Task _writer;
    private readonly Task _sourceWake;
    private readonly string _model;
    private readonly ProviderContextBinding? _binding;
    private readonly string? _credential;
    public void ValidateBinding(ProviderContextBinding? current, System.Net.Http.Headers.AuthenticationHeaderValue? authentication)
    {
        // In-memory equality also covers credentials without a durable reference. Such
        // credentials can run normally but never authorize persisted opaque replay.
        if (_credential != authentication?.ToString() || _binding is not null && (current is null
            || _binding.Endpoint != current.Endpoint || _binding.Provider != current.Provider || _binding.Model != current.Model
            || _binding.ConversationId != current.ConversationId || _binding.ProfileId != current.ProfileId || _binding.MissionId != current.MissionId
            || _binding.AuthorityDigest != current.AuthorityDigest || _binding.CredentialReference != current.CredentialReference))
            throw new ProviderContextReconstructionRequiredException("The active connection credential or route binding changed.");
    }
    private bool _started;
    private bool _terminal;
    private bool _detaching;
    private readonly HashSet<string> _createdIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _continuedParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _required = new(StringComparer.Ordinal);
    private int _disposed;
    public bool HasPendingSteering { get { lock (_sync) return _sending is not null || _accepted.Count != 0; } }
    public string? ResponseId { get { lock (_sync) return _responseId; } }

    internal ResponsesSteeringConnection(WebSocket socket, IChatSteeringSource source, string model, CancellationToken ct,
        ProviderContextBinding? binding = null, System.Net.Http.Headers.AuthenticationHeaderValue? authentication = null)
    {
        _binding = binding; _credential = authentication?.ToString();
        _socket = socket; _source = source; _model = model;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reader = ReadAsync(); _writer = WriteSteeringAsync(); _sourceWake = WakeFromSourceAsync();
    }

    public async Task StartResponseAsync(JsonObject request, CancellationToken ct)
    {
        if ((string?)request["model"] != _model) throw new InvalidOperationException("Steering connection cannot change model.");
        if (request["input"] is JsonArray input)
            ResponsesConfigurationUpdateValidation.Validate(input, _model, "responses-websocket");
        lock (_sync)
        {
            var parent = (string?)request["previous_response_id"];
            if (!_started && parent is not null)
                throw new InvalidOperationException("A new steering connection requires reconciled full input, not an old connection's response ID.");
            if (_started && parent != _responseId)
                throw new InvalidOperationException("Continuation must target this connection's latest response.");
            if (parent is not null)
            {
                if (_continuedParents.Contains(parent)) throw new InvalidOperationException("Only one explicit continuation is allowed per parent.");
                var supplied = (request["input"] as JsonArray)?.OfType<JsonObject>()
                    .Where(x => (string?)x["type"] == "function_call_output").Select(x => Required(x, "call_id")).ToArray() ?? [];
                if (supplied.Distinct(StringComparer.Ordinal).Count() != supplied.Length
                    || supplied.Any(id => !_calls.TryGetValue(parent, out var known) || !known.ContainsKey(id))
                    || (_required.TryGetValue(parent, out var required) && required.Except(supplied).Any()))
                    throw new InvalidDataException("Continuation has missing, duplicate or uncorrelated tool output.");
                _continuedParents.Add(parent);
            }
            if (_detaching) throw new InvalidOperationException("The context boundary closed this connection.");
            _terminal = false;
            _started = true;
        }
        request.Remove("stream"); request["type"] = "response.create";
        await SendAsync(request, ct).ConfigureAwait(false);
    }

    public IAsyncEnumerable<Frame> ReadFramesAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(message.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    private async Task WakeFromSourceAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _source.WaitForUpdateAsync(_lifetime.Token).ConfigureAwait(false);
                _wake.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Fail(e); }
    }

    private async Task WriteSteeringAsync()
    {
        try
        {
            await foreach (var _ in _wake.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                (ChatSteeringUpdate Update, string Parent)? send = null;
                lock (_sync)
                {
                    if (!_detaching && _responseId is not null && _sending is null && _source.TryTake(out var update))
                        send = _sending = (update, _responseId);
                }
                if (send is { } pending)
                    await SendAsync(new JsonObject { ["type"] = "response.steer", ["previous_response_id"] = pending.Parent,
                        ["input"] = pending.Update.Message }, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Fail(e); }
    }

    private async Task ReadAsync()
    {
        try
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                using var stream = new MemoryStream();
                ValueWebSocketReceiveResult read;
                do
                {
                    read = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (read.MessageType != WebSocketMessageType.Text) throw new IOException("Responses steering connection closed before reconciliation.");
                    if (stream.Length + read.Count > 16 * 1024 * 1024) throw new InvalidDataException("Oversized Responses event.");
                    stream.Write(buffer, 0, read.Count);
                } while (!read.EndOfMessage);
                var wire = JsonNode.Parse(stream.ToArray()) as JsonObject ?? throw new InvalidDataException("Malformed Responses event.");
                var type = (string?)wire["type"];
                if (type == "response.created")
                {
                    var response = wire["response"] as JsonObject ?? throw new InvalidDataException("Missing response identity.");
                    var id = Required(response, "id");
                    if (Required(response, "model") != _model) throw new InvalidDataException("Steering response changed the selected model.");
                    List<ChatSteeringDeliveryEvent> applied = [];
                    lock (_sync)
                    {
                        var parent = (string?)response["previous_response_id"];
                        if (!_createdIds.Add(id)) throw new InvalidDataException("Duplicate response.created identity.");
                        if (_responseId is not null && parent != _responseId) throw new InvalidDataException("Uncorrelated steering successor.");
                        foreach (var pair in _accepted.Where(x => x.Value.Parent == parent).ToArray())
                        {
                            applied.Add(new(pair.Value.Update.Id, ChatSteeringDelivery.Applied, pair.Key, id, Message: pair.Value.Update.Message));
                            _accepted.Remove(pair.Key);
                        }
                        _responseId = id;
                        _terminal = false;
                    }
                    foreach (var receipt in applied) await _frames.Writer.WriteAsync(new(null, receipt), _lifetime.Token).ConfigureAwait(false);
                    _wake.Writer.TryWrite(true);
                }
                else if (type is "response.steer.accepted" or "response.steer.failed" or "response.steer.pending")
                {
                    var steer = wire["steer"] as JsonObject ?? throw new InvalidDataException("Missing steering correlation.");
                    var id = Required(steer, "id"); var parent = Required(steer, "previous_response_id");
                    ChatSteeringDeliveryEvent? delivery = null;
                    lock (_sync)
                    {
                        if (type == "response.steer.accepted")
                        {
                            if (_sending is not { } sent || sent.Parent != parent || !_accepted.TryAdd(id, sent))
                                throw new InvalidDataException("Uncorrelated steering acknowledgment.");
                            _sending = null;
                            delivery = new(sent.Update.Id, ChatSteeringDelivery.ProviderQueued, id, parent);
                        }
                        else
                        {
                            if (!_accepted.TryGetValue(id, out var accepted))
                            {
                                if (type != "response.steer.failed" || _sending is not { } sent || sent.Parent != parent)
                                    throw new InvalidDataException("Unknown steering receipt.");
                                accepted = sent; _sending = null;
                            }
                            if (accepted.Parent != parent) throw new InvalidDataException("Steering parent changed.");
                            if (type == "response.steer.failed")
                            {
                                _accepted.Remove(id);
                                delivery = new(accepted.Update.Id, ChatSteeringDelivery.Deferred, id, parent,
                                    "Provider did not apply the update; retained for host reconciliation.");
                            }
                            else
                            {
                                if (wire["required_input"] is not JsonArray { Count: > 0 } required)
                                    throw new InvalidDataException("Pending steering omitted required input.");
                                var ids = new HashSet<string>(StringComparer.Ordinal);
                                foreach (var stub in required.OfType<JsonObject>())
                                {
                                    var callId = Required(stub, "call_id");
                                    if ((string?)stub["type"] != "function_call_output" || !ids.Add(callId)
                                        || !_calls.TryGetValue(parent, out var calls) || !calls.TryGetValue(callId, out var name)
                                        || Required(stub, "name") != name)
                                        throw new InvalidDataException("Pending steering required input disagrees with the parent's tool calls.");
                                }
                                if (ids.Count != required.Count) throw new InvalidDataException("Malformed pending tool input.");
                                _required[parent] = ids;
                            }
                        }
                    }
                    if (delivery is not null) await _frames.Writer.WriteAsync(new(null, delivery), _lifetime.Token).ConfigureAwait(false);
                    if (type == "response.steer.failed") throw new InvalidOperationException("Provider steering failed; reconcile accepted host updates before retrying.");
                    _wake.Writer.TryWrite(true);
                    continue;
                }
                if (type is "response.completed" or "response.incomplete" or "response.failed")
                {
                    if (wire["response"] is not JsonObject response || Required(response, "id") != ResponseId
                        || Required(response, "model") != _model)
                        throw new InvalidDataException("Terminal response does not match this steering connection.");
                    lock (_sync) _terminal = true;
                }
                if (type == "response.completed" && wire["response"] is JsonObject terminal)
                {
                    var parent = Required(terminal, "id");
                    var calls = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var call in (terminal["output"] as JsonArray ?? []).OfType<JsonObject>().Where(x => (string?)x["type"] == "function_call"))
                        if (!calls.TryAdd(Required(call, "call_id"), Required(call, "name")))
                            throw new InvalidDataException("Duplicate completed tool call identity.");
                    lock (_sync) _calls[parent] = calls;
                }
                await _frames.Writer.WriteAsync(new(wire), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { _frames.Writer.TryComplete(); }
        catch (Exception e) { Fail(e); }
    }

    private void Fail(Exception e) { _frames.Writer.TryComplete(e); _lifetime.Cancel(); _socket.Abort(); }
    private static string Required(JsonObject value, string key) => (string?)value[key] is { Length: > 0 } text ? text : throw new InvalidDataException($"Missing {key}.");
    public async ValueTask<bool> TryDetachAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_terminal || _sending is not null || _accepted.Count != 0 || _detaching) return false;
            _detaching = true;
        }
        // The source remains durable and unread updates are left for the next connection.
        await DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _socket.Abort();
        await Task.WhenAll(_reader, _writer, _sourceWake).ConfigureAwait(false);
        _socket.Dispose(); _send.Dispose(); _lifetime.Dispose();
    }
}
