using SwarmCore.RemoteControl.Mac.Mapping;

namespace SwarmCore.RemoteControl.Mac.Tests;

public class UserIdleTrackerTests
{
    [Fact]
    public void WithoutInjectionsReportsRawAge()
    {
        var t = new UserIdleTracker();
        Assert.Equal(TimeSpan.FromSeconds(3), t.Observe(100, 3));
    }

    [Fact]
    public void OwnInjectionIsDiscounted()
    {
        var t = new UserIdleTracker();
        Assert.Equal(TimeSpan.FromSeconds(10), t.Observe(100, 10)); // person last active at 90
        t.RecordInjection(100.01);
        // At 105 the newest event (100.0, within 50 ms of our post) is ours: idle since 90.
        Assert.Equal(15, t.Observe(105, 5).GetValueOrDefault().TotalSeconds, 6);
    }

    [Fact]
    public void PersonInputAfterInjectionIsSeen()
    {
        var t = new UserIdleTracker();
        t.Observe(100, 10);
        t.RecordInjection(100);
        // Newest event at 103 is 3 s after our post: the person's.
        Assert.Equal(1, t.Observe(104, 1).GetValueOrDefault().TotalSeconds, 6);
    }

    [Fact]
    public void ToleranceBoundary()
    {
        var t = new UserIdleTracker();
        t.Observe(100, 50);
        t.RecordInjection(100);
        Assert.Equal(52, t.Observe(102, 2 - 0.04).GetValueOrDefault().TotalSeconds, 6); // 100.04: ours
        Assert.Equal(1.94, t.Observe(102, 2 - 0.06).GetValueOrDefault().TotalSeconds, 6); // 100.06: person's
    }

    [Fact]
    public void OnlyOwnInputSeenIsUnknown()
    {
        var t = new UserIdleTracker();
        t.RecordInjection(100);
        Assert.Null(t.Observe(101, 1));
    }

    [Fact]
    public void InvalidReadingIsUnknown() => Assert.Null(new UserIdleTracker().Observe(1, double.NaN));
}
