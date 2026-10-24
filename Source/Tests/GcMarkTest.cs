using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class GcMarkTest
{
    [Test]
    public void SinceCountsABlockingGen2()
    {
        var before = GcMark.Now();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var after = GcMark.Now();

        var text = after.Since(before);
        Assert.That(text, Does.Match(@"gen2 \+[1-9]"));
        Assert.That(text, Does.Contain("heap ").And.Contain("working set ").And.Contain("memory load "));
    }

    [Test]
    public void SinceANewMarkIsZero()
    {
        var mark = GcMark.Now();
        Assert.That(mark.Since(mark), Does.StartWith("GC paused 0 ms in it (collections gen0 +0, gen1 +0, gen2 +0)"));
    }

    [Test]
    public void HeapTextNamesTheLastBlockingGen2()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        int before = GC.CollectionCount(2);
        var text = GcMark.HeapText();
        int after = GC.CollectionCount(2);

        Assert.That(text, Does.StartWith("fragmented "));
        var count = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"gen2 collections (\d+)").Groups[1].Value);
        Assert.That(count, Is.InRange(before, after));
        Assert.That(text, Does.Match(@"last blocking gen2 #\d+ compacting paused \d+ ms"));
    }
}
