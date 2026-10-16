using System.Threading.Channels;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Process-local invalidations for committed conversation changes. Hosts publish
/// after persistence; views subscribe before loading and always read the store.
/// A subscription retains one wake-up, so slow views coalesce changes without
/// delaying a writer or retaining conversation content.
/// </summary>
public sealed class ConversationChangeFeed
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<Subscription>> _subscriptions = new(StringComparer.Ordinal);

    public Subscription Subscribe(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(conversationId, out var subscribers))
                _subscriptions.Add(conversationId, subscribers = []);
            var subscription = new Subscription(this, conversationId);
            subscribers.Add(subscription);
            return subscription;
        }
    }

    public void Publish(string conversationId)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(conversationId, out var subscribers))
                foreach (var subscription in subscribers)
                    subscription.Signal();
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.ConversationId, out var subscribers))
            {
                subscribers.Remove(subscription);
                if (subscribers.Count == 0) _subscriptions.Remove(subscription.ConversationId);
            }
            subscription.Complete();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly ConversationChangeFeed _owner;
        private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        internal Subscription(ConversationChangeFeed owner, string conversationId)
            => (_owner, ConversationId) = (owner, conversationId);

        public string ConversationId { get; }
        public ChannelReader<bool> Changes => _changes.Reader;
        internal void Signal() => _changes.Writer.TryWrite(true);
        internal void Complete() => _changes.Writer.TryComplete();
        public void Dispose() => _owner.Remove(this);
    }
}
