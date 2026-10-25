using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.AppControl;
using SwarmCore.Agent.Communication;

namespace SwarmCode.Hosting.Goals;

/// <summary>The desktop's narrow same-user companion endpoint. It cannot start, resume, cancel or execute a goal.</summary>
public sealed class GoalMailboxServer(IGoalMailbox mailbox, SwarmCodeDataPaths paths, ILogger<GoalMailboxServer> logger,
    LoopbackDiscoveryDirectory? discovery = null, Func<string, AppControlEndpoint?>? find = null) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private LoopbackJsonEndpoint? _endpoint;
    private GoalMailboxIdentity? _identity;
    private string? _app;
    private bool _published;
    private bool _disposed;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly LoopbackDiscoveryDirectory _discovery = discovery ?? new(GoalMailboxDiscovery.Directory);
    internal AppControlEndpoint? PublishedEndpoint { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_published) return;
            _identity = await mailbox.GetIdentityAsync(ct).ConfigureAwait(false);
            _app = GoalMailboxDiscovery.Prefix + _identity.InstanceId;
            if ((find is null ? GoalMailboxDiscovery.Find(_identity.InstanceId) : find(_identity.InstanceId)) is { } other && other.ProcessId != Environment.ProcessId)
                throw new InvalidOperationException("Another process already owns this Code goal inbox.");
            var token = _discovery.GetOrCreateToken(_app, () => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            _endpoint = new LoopbackJsonEndpoint("/goal-messages", token, HandleAsync, logger);
            _endpoint.Start();
            PublishedEndpoint = new AppControlEndpoint(_app, "Talavine Code goal conversations", _endpoint.Url,
                token, Environment.ProcessId, paths.RootDirectory, typeof(GoalMailboxServer).Assembly.GetName().Version?.ToString(), DateTimeOffset.UtcNow)
                { ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime() };
            _published = _discovery.Publish(PublishedEndpoint);
            if (!_published) { await _endpoint.DisposeAsync().ConfigureAwait(false); _endpoint = null; throw new InvalidOperationException("The Code goal inbox could not publish its identity."); }
        }
        catch
        {
            if (_endpoint is not null) await _endpoint.DisposeAsync().ConfigureAwait(false);
            _endpoint = null;
            PublishedEndpoint = null;
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    internal async Task<LoopbackJsonResult> HandleAsync(JsonObject input, CancellationToken ct)
    {
        try
        {
            var operation = input["operation"]?.GetValue<string>();
            if (operation == "hello") return Ok("identity", _identity ?? await mailbox.GetIdentityAsync(ct).ConfigureAwait(false));
            var identity = _identity ?? await mailbox.GetIdentityAsync(ct).ConfigureAwait(false);
            if (!string.Equals(input["instanceId"]?.GetValue<string>(), identity.InstanceId, StringComparison.Ordinal))
                return LoopbackJsonResult.Fail(409, "The message targets a different Code profile. Reconnect the intended goal.");
            if (operation == "list") return Ok("contacts", await mailbox.ListAsync(ct).ConfigureAwait(false));
            if (!Guid.TryParse(input["goalId"]?.GetValue<string>(), out var id)) return LoopbackJsonResult.Fail(400, "An immutable goal ID is required.");
            return operation switch
            {
                "rename" => Ok("contact", await mailbox.RenameAsync(id, input["name"]?.GetValue<string>() ?? "", input["handle"]?.GetValue<string>() ?? "", input["expectedRevision"]?.GetValue<long>() ?? -1, ct).ConfigureAwait(false)),
                "read" => Ok("thread", await ReadCompanionAsync(id, input, ct).ConfigureAwait(false)),
                "send" => Ok("message", await SendCompanionAsync(id, input["message"]?.Deserialize<GoalMessageCommand>(Json) ?? throw new ArgumentException("An authenticated message envelope is required."), ct).ConfigureAwait(false)),
                _ => LoopbackJsonResult.Fail(400, "This connection only supports discovering, naming, reading and messaging goals."),
            };
        }
        catch (GoalMailboxClosedException exception) { return new(409, new JsonObject { ["ok"] = false, ["code"] = "goal_closed", ["error"] = exception.Message }); }
        catch (ArgumentException exception) { return LoopbackJsonResult.Fail(400, exception.Message); }
        catch (UnauthorizedAccessException exception) { return LoopbackJsonResult.Fail(403, exception.Message); }
        catch (InvalidOperationException exception) { return LoopbackJsonResult.Fail(409, exception.Message); }
        catch (JsonException) { return LoopbackJsonResult.Fail(400, "The goal message envelope is invalid."); }
    }

    private Task<GoalInboxMessage> SendCompanionAsync(Guid goalId, GoalMessageCommand message, CancellationToken ct)
    {
        if (message.Attachments is { Count: > 0 })
            throw new ArgumentException("File upload is not supported by this companion connection. Attach files in the target Code chat on its host; a remote local path is not an upload.");
        return mailbox.SendAsync(goalId, message, ct);
    }

    private async Task<GoalMessageThread> ReadCompanionAsync(Guid goalId, JsonObject input, CancellationToken ct)
    {
        var thread = await mailbox.ReadAsync(goalId, input["afterSequence"]?.GetValue<long>() ?? 0,
            input["take"]?.GetValue<int>() ?? 200, ct).ConfigureAwait(false);
        // Older companions treat every reply as an agent answer and may send it by email.
        // They must explicitly opt into the new separately attributed status event contract.
        return input["includeHostStatus"]?.GetValue<bool>() == true ? thread
            : thread with { Replies = thread.Replies.Where(GoalMailboxStatus.IsAgentReply).ToArray() };
    }

    private static LoopbackJsonResult Ok<T>(string name, T value) => LoopbackJsonResult.Ok(new JsonObject { [name] = JsonSerializer.SerializeToNode(value, Json) });

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_published && _app is not null) _discovery.Withdraw(_app, Environment.ProcessId);
            if (_endpoint is not null) await _endpoint.DisposeAsync().ConfigureAwait(false);
            _endpoint = null; _published = false; PublishedEndpoint = null;
        }
        finally { _lifecycle.Release(); }
    }
}
