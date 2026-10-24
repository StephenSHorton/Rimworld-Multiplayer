using Multiplayer.Common.Util;

namespace Tests;

[TestFixture]
public class PrefilteredCountTest
{
    // A tile: its id and its layer. "Occupied" is vanilla's predicate: some object is on the same id and layer.
    private record struct T(int id, int layer);

    private static int Plain(List<T> tiles, List<T> objects) => tiles.Count(t => objects.Contains(t));

    private static int Prefiltered(List<T> tiles, List<T> objects, List<T>? ran = null)
    {
        var ids = objects.Select(o => o.id).ToHashSet();
        return PrefilteredCount.Count(tiles, t => t.id, ids, t =>
        {
            ran?.Add(t);
            return objects.Contains(t);
        });
    }

    [Test]
    public void SameAsPlainCount()
    {
        var tiles = Enumerable.Range(0, 200).Select(i => new T(i, 1)).ToList();
        var objects = new List<T>
        {
            new(3, 1), new(3, 1),  // two objects on one tile count once
            new(7, 0),             // same id, other layer: a candidate the predicate rejects
            new(50, 1),
            new(5000, 1),          // an id the layer doesn't have
        };

        Assert.That(Prefiltered(tiles, objects), Is.EqualTo(Plain(tiles, objects)));
        Assert.That(Prefiltered(tiles, objects), Is.EqualTo(2));
    }

    [Test]
    public void PredicateRunsOnlyOnCandidatesInListOrder()
    {
        var tiles = new List<T> { new(9, 1), new(2, 1), new(4, 1), new(2, 1) };
        var ran = new List<T>();

        int count = Prefiltered(tiles, [new(2, 1), new(9, 0)], ran);

        Assert.That(count, Is.EqualTo(2));
        Assert.That(ran, Is.EqualTo(new[] { new T(9, 1), new T(2, 1), new T(2, 1) }));
    }

    [Test]
    public void NoCandidatesIsZeroWithoutVisiting()
    {
        var tiles = Enumerable.Range(0, 10).Select(i => new T(i, 1)).ToList();
        int keys = 0;

        int count = PrefilteredCount.Count(tiles, t => { keys++; return t.id; }, new HashSet<int>(), _ => true);

        Assert.That(count, Is.EqualTo(0));
        Assert.That(keys, Is.EqualTo(0));
    }

    [Test]
    public void RandomCasesMatchPlainCount()
    {
        var rand = new Random(12345);
        for (int round = 0; round < 200; round++)
        {
            var tiles = Enumerable.Range(0, rand.Next(0, 60)).Select(_ => new T(rand.Next(0, 40), rand.Next(0, 2))).ToList();
            var objects = Enumerable.Range(0, rand.Next(0, 15)).Select(_ => new T(rand.Next(0, 50), rand.Next(0, 2))).ToList();
            Assert.That(Prefiltered(tiles, objects), Is.EqualTo(Plain(tiles, objects)), $"round {round}");
        }
    }
}
