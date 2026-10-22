using Multiplayer.Common.Util;

namespace Tests;

[TestFixture]
public class ContextStackTest
{
    private class Owner(string name)
    {
        public override string ToString() => name;
    }

    // A clock like the tick manager's: each owner switches it to its own time and puts back what it found
    private class Clock
    {
        public int now;
        public readonly ContextStack<Owner, int> stack = new();
        public readonly Dictionary<Owner, int> times = new();
        public int switches;

        public void Open(Owner o) => stack.Open(o, () => { switches++; var prev = now; now = times[o]; return prev; });
        public int Close(Owner o) => stack.Close(o, prev => now = prev);
    }

    [Test]
    public void OpeningTheCurrentContextAgainKeepsTheOuterRestore()
    {
        // The 2026-10-21 leak: a map's context opened inside itself, then the world's time not restored
        var map = new Owner("map 0");
        var clock = new Clock { now = 5000 };
        clock.times[map] = 3200;

        clock.Open(map);
        clock.Open(map);
        Assert.That(clock.now, Is.EqualTo(3200));
        Assert.That(clock.Close(map), Is.EqualTo(0));
        Assert.That(clock.now, Is.EqualTo(3200), "the inner close must not switch back");
        Assert.That(clock.Close(map), Is.EqualTo(0));

        Assert.That(clock.now, Is.EqualTo(5000));
        Assert.That(clock.switches, Is.EqualTo(1));
        Assert.That(clock.stack.Count, Is.EqualTo(0));
    }

    [Test]
    public void ADifferentContextInsideSwitchesAndRestores()
    {
        var a = new Owner("a");
        var b = new Owner("b");
        var clock = new Clock { now = 100 };
        clock.times[a] = 10;
        clock.times[b] = 20;

        clock.Open(a);
        clock.Open(b);
        Assert.That(clock.now, Is.EqualTo(20));
        clock.Open(a);
        Assert.That(clock.now, Is.EqualTo(10), "a inside b is not the innermost, so it switches");
        clock.Close(a);
        Assert.That(clock.now, Is.EqualTo(20));
        clock.Close(b);
        Assert.That(clock.now, Is.EqualTo(10));
        clock.Close(a);
        Assert.That(clock.now, Is.EqualTo(100));
    }

    [Test]
    public void ContextsLeftOpenInsideAreDropped()
    {
        var a = new Owner("a");
        var b = new Owner("b");
        var clock = new Clock { now = 100 };
        clock.times[a] = 10;
        clock.times[b] = 20;

        clock.Open(a);
        clock.Open(b); // never closed: an exception skipped its close

        Assert.That(clock.Close(a), Is.EqualTo(1));
        Assert.That(clock.now, Is.EqualTo(100));
        Assert.That(clock.stack.Count, Is.EqualTo(0));
    }

    [Test]
    public void ClosingAContextNeverOpenedChangesNothing()
    {
        var a = new Owner("a");
        var clock = new Clock { now = 100 };
        Assert.That(clock.Close(a), Is.EqualTo(-1));
        Assert.That(clock.now, Is.EqualTo(100));
        Assert.That(clock.stack.Current, Is.Null);
    }
}
