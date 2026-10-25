namespace SwarmCore.RemoteControl.Agent;

/// <summary>
/// Host-owned private values a desktop step may type without the value entering tool arguments, receipts,
/// transcripts, or stored procedures. A step names a slot (<c>password</c>, <c>otp:email</c>); the host resolves it at
/// dispatch. A one-time code slot is resolved by the host reading its own mailbox at that moment (for example with
/// <c>SwarmCore.Services.Adapters.LoginVerificationCodeMatcher</c> over a freshly received sign-in email), so a
/// code is never stored or replayed.
/// </summary>
public interface IDesktopSecretSlots
{
    /// <summary>The slot names this host can resolve. Names are durable literals; values never are.</summary>
    IReadOnlyCollection<string> SlotNames { get; }
    /// <summary>The current value, or null when the slot cannot be resolved now.</summary>
    Task<string?> ResolveAsync(string slot, CancellationToken ct);
}

/// <summary>Conventional slot names. <c>otp:email</c> is written <c>{{slot:otp:email}}</c> in prose; in a step it is <c>secret: "otp:email"</c>.</summary>
public static class DesktopSlots
{
    public const string Password = "password";
    public const string EmailCode = "otp:email";
}

/// <summary>Dictionary-backed slots for hosts that already hold the values in memory (and for tests).</summary>
public sealed class DesktopSecretSlots(IReadOnlyDictionary<string, string> values) : IDesktopSecretSlots
{
    public static readonly DesktopSecretSlots None = new(new Dictionary<string, string>());
    public IReadOnlyCollection<string> SlotNames => values.Keys.ToArray();
    public Task<string?> ResolveAsync(string slot, CancellationToken ct) => Task.FromResult(values.TryGetValue(slot, out var v) ? v : null);
}

/// <summary>Slots resolved by a host callback (for example a mailbox read for <see cref="DesktopSlots.EmailCode"/>).</summary>
public sealed class DelegateSecretSlots(IReadOnlyCollection<string> names, Func<string, CancellationToken, Task<string?>> resolve) : IDesktopSecretSlots
{
    public IReadOnlyCollection<string> SlotNames => names;
    public Task<string?> ResolveAsync(string slot, CancellationToken ct) => names.Contains(slot) ? resolve(slot, ct) : Task.FromResult<string?>(null);
}

/// <summary>The first slot source that knows a name wins.</summary>
public sealed class CompositeSecretSlots(params IDesktopSecretSlots[] sources) : IDesktopSecretSlots
{
    public IReadOnlyCollection<string> SlotNames => sources.SelectMany(s => s.SlotNames).Distinct(StringComparer.Ordinal).ToArray();
    public Task<string?> ResolveAsync(string slot, CancellationToken ct) =>
        sources.FirstOrDefault(s => s.SlotNames.Contains(slot)) is { } source ? source.ResolveAsync(slot, ct) : Task.FromResult<string?>(null);
}
