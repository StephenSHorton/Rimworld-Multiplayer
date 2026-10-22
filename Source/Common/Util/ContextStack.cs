using System;
using System.Collections.Generic;

namespace Multiplayer.Common.Util;

/// <summary>
/// The contexts opened inside each other, innermost last. Opening the context that is already innermost again
/// only counts: the caller doesn't switch, and closing it doesn't switch back. Any other context switches on
/// open and switches back, with what it saved on open, when it closes.
///
/// A map's async time context saved the time it replaced in one field, so opening it again while it was open
/// (a quest getter inside that map's own tick) saved the map's time over the world's, and closing the outer one
/// left the map's tick count in the world's clock (2026-10-21: the world clock went back about 1800 ticks at
/// a join point, on both games).
/// </summary>
public class ContextStack<TOwner, TSaved> where TOwner : class
{
    private readonly List<(TOwner owner, bool switched, TSaved saved)> open = new();

    public int Count => open.Count;

    /// <summary>The innermost open context's owner, or null.</summary>
    public TOwner? Current => open.Count > 0 ? open[^1].owner : null;

    public bool IsCurrent(TOwner owner) => open.Count > 0 && ReferenceEquals(open[^1].owner, owner);

    /// <summary>
    /// Opens owner's context. Unless it is already the innermost, calls <paramref name="switchIn"/> and keeps what it
    /// returns for <see cref="Close"/>. Returns whether it switched.
    /// </summary>
    public bool Open(TOwner owner, Func<TSaved> switchIn)
    {
        if (IsCurrent(owner))
        {
            open.Add((owner, false, default!));
            return false;
        }

        var saved = switchIn();
        open.Add((owner, true, saved));
        return true;
    }

    /// <summary>
    /// Closes owner's innermost context, calling <paramref name="switchOut"/> with what its open saved if that open
    /// switched. Contexts opened after it and never closed (an exception skipped their close) are dropped first,
    /// without switching back. Returns how many were dropped, or -1 if owner had no open context.
    /// </summary>
    public int Close(TOwner owner, Action<TSaved> switchOut)
    {
        var i = open.FindLastIndex(e => ReferenceEquals(e.owner, owner));
        if (i < 0) return -1;

        var dropped = open.Count - 1 - i;
        var (_, switched, saved) = open[i];
        open.RemoveRange(i, open.Count - i);
        if (switched) switchOut(saved);
        return dropped;
    }

    public void Clear() => open.Clear();
}
