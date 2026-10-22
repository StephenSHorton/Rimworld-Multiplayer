using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common.Desyncs;

/// <summary>
/// One named fact about a game process that the simulation depends on but no save or command carries: the sync
/// handler table, an item's market value as the process computed it, a price cache, a mod's settings. Two games
/// whose rows differ will diverge, so a join with a differing row is refused with the row's name. A warn row
/// (language, runtime) is reported but doesn't block.
/// </summary>
public record struct FingerprintRow : IPacketBufferable
{
    public string name;
    public string value;
    public bool warn;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref name, maxLength: 256);
        buf.Bind(ref value, maxLength: 1024);
        buf.Bind(ref warn);
    }
}

public static class Fingerprint
{
    public const int MaxRows = 1 << 16;

    /// <summary>The name prefix of the rows that hash a section of the game's last join point save.</summary>
    public const string JoinSaveRowPrefix = "Join point save: ";

    /// <summary>
    /// The rows without the join point save's sections. A player joining from the menu has no join point save yet,
    /// so at the door the host's save rows are only noise (2026-10-21: about 295 "only host's game has it" lines).
    /// </summary>
    public static IEnumerable<FingerprintRow> WithoutJoinSave(IEnumerable<FingerprintRow>? rows) =>
        (rows ?? []).Where(r => r.name == null || !r.name.StartsWith(JoinSaveRowPrefix, System.StringComparison.Ordinal));

    /// <summary>The rows that differ, blocking ones first; each line names the row and both values.</summary>
    public static (List<string> blocking, List<string> warnings) Compare(
        IEnumerable<FingerprintRow>? reference, IEnumerable<FingerprintRow>? other, string refName, string otherName)
    {
        var blocking = new List<string>();
        var warnings = new List<string>();
        var refRows = (reference ?? []).GroupBy(r => r.name).ToDictionary(g => g.Key, g => g.First());
        var otherRows = (other ?? []).GroupBy(r => r.name).ToDictionary(g => g.Key, g => g.First());

        foreach (var name in refRows.Keys.Union(otherRows.Keys).OrderBy(n => n, System.StringComparer.Ordinal))
        {
            refRows.TryGetValue(name, out var a);
            otherRows.TryGetValue(name, out var b);
            var hasA = refRows.ContainsKey(name);
            var hasB = otherRows.ContainsKey(name);
            if (hasA && hasB && a.value == b.value) continue;

            var line = !hasA ? $"{name}: only {otherName}'s game has it ({b.value})"
                : !hasB ? $"{name}: only {refName}'s game has it ({a.value})"
                : $"{name}: {a.value} ({refName}) vs {b.value} ({otherName})";
            (a.warn || b.warn ? warnings : blocking).Add(line);
        }

        return (blocking, warnings);
    }

    /// <summary>The names of the rows that differ or that only one side has, in ordinal order.</summary>
    public static List<string> DifferingNames(IEnumerable<FingerprintRow>? reference, IEnumerable<FingerprintRow>? other)
    {
        var refRows = (reference ?? []).GroupBy(r => r.name).ToDictionary(g => g.Key, g => g.First().value);
        var otherRows = (other ?? []).GroupBy(r => r.name).ToDictionary(g => g.Key, g => g.First().value);
        return refRows.Keys.Union(otherRows.Keys)
            .Where(n => !refRows.TryGetValue(n, out var a) || !otherRows.TryGetValue(n, out var b) || a != b)
            .OrderBy(n => n, System.StringComparer.Ordinal)
            .ToList();
    }

    public static string Describe(List<string> lines, int max = 15) =>
        string.Join("\n", lines.Take(max)) + (lines.Count > max ? $"\n... and {lines.Count - max} more" : "");
}

/// <summary>
/// A player's fingerprint after a join point reload, compared with the reference game's by the server.
/// <see cref="joinPoint"/> is the tick of the join point's command: one command reaches every game with the same
/// tick, so it names the join point the rows belong to.
/// </summary>
[PacketDefinition(Packets.Client_Fingerprint, allowFragmented: true)]
public record struct ClientFingerprintPacket : IPacket
{
    public int joinPoint;
    public FingerprintRow[] rows;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref joinPoint);
        buf.Bind(ref rows, BinderOf.Identity<FingerprintRow>(), maxLength: Fingerprint.MaxRows);
    }
}
