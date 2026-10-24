using System.Collections.Generic;

namespace Multiplayer.Common.Desyncs;

/// <summary>
/// A window's trace hashes as the host sends them off Mono: <see cref="Marker"/> and <see cref="Marker2"/>, the count, then one hash per
/// <see cref="Size"/> traces. With desync traces on, every Rand call adds a trace, and the host's opinion carried one
/// int per call to every client, fragmented on the channel the commands use (2026-10-23: about 1 MB/s at Ultrafast
/// during a raid; the commands waited behind it, the client fell 98 ticks behind and the server paused every few
/// seconds). On CoreCLR a trace difference is only a soft one, so a block is enough to find where two games' traces
/// part. A list of blocks says so itself, so opinions read back from a desync zip need no other flag.
/// </summary>
public static class TraceBlocks
{
    public const int Size = 256;
    public const int Marker = int.MinValue;
    // A second marker, so a full list that happens to start with Marker (one in 2^32) still reads as a full list
    public const int Marker2 = 0x5452424B;
    private const int Header = 3;

    public static bool IsBlocks(List<int> hashes) => hashes.Count >= Header && hashes[0] == Marker && hashes[1] == Marker2;

    /// <summary>Blocks of a full list; a list that already is one comes back as it is.</summary>
    public static List<int> Of(List<int> hashes)
    {
        if (IsBlocks(hashes)) return hashes;

        var blocks = new List<int>(Header + 1 + hashes.Count / Size) { Marker, Marker2, hashes.Count };
        for (int start = 0; start < hashes.Count; start += Size)
        {
            int end = System.Math.Min(start + Size, hashes.Count);
            uint h = 2166136261;
            unchecked
            {
                for (int i = start; i < end; i++)
                    h = (h ^ (uint)hashes[i]) * 16777619;
            }
            blocks.Add((int)h);
        }
        return blocks;
    }

    /// <summary>How many traces a list stands for, full or in blocks.</summary>
    public static int Count(List<int> hashes) => IsBlocks(hashes) ? hashes[2] : hashes.Count;

    /// <summary>
    /// The first trace of the first block that differs, -1 when both are the same. <paramref name="found"/> is false
    /// when every block they share is the same and only the counts differ (one game's window ended sooner); then
    /// the result is the last trace of the shorter one. The last block of the shorter list can be partial and then
    /// differs from the longer one's full block, which still names the block the traces part in.
    /// </summary>
    public static int FirstDifference(List<int> a, List<int> b, out bool found)
    {
        a = Of(a);
        b = Of(b);
        found = true;
        int shared = System.Math.Min(a.Count, b.Count);
        for (int i = Header; i < shared; i++)
            if (a[i] != b[i])
                return (i - Header) * Size;

        found = false;
        int countA = a[2], countB = b[2];
        if (countA != countB)
            return System.Math.Min(countA, countB) - 1;
        return -1;
    }
}
