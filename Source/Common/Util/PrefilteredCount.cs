using System;
using System.Collections.Generic;

namespace Multiplayer.Common.Util;

/// <summary>
/// Counts the items a predicate is true for, running the predicate only on items whose key is in a set of
/// candidate keys. The count is the same as a plain count when the predicate can only be true for an item whose
/// key is a candidate (the caller collects the keys from everything that could make it true). Items are visited
/// in list order, so the predicate runs in the order a plain count would run it on those items.
///
/// Vanilla's location generator counted planet tiles with a generated location on them by comparing every tile
/// with every world object: 1355 objects on a 100% coverage planet made a 450-560 ms hitch (2026-10-23). With
/// the objects' tile ids as candidates, a tile with no object on its id costs one set lookup.
/// </summary>
public static class PrefilteredCount
{
    public static int Count<T>(IReadOnlyList<T> items, Func<T, int> key, HashSet<int> candidates, Func<T, bool> predicate)
    {
        if (candidates.Count == 0)
            return 0;

        int count = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (candidates.Contains(key(item)) && predicate(item))
                count++;
        }

        return count;
    }
}
