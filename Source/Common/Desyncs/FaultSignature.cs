using System.Text;

namespace Multiplayer.Common.Desyncs;

/// <summary>
/// A process-independent signature for a logged error. Numbers (ids, addresses, timings) are folded to '#', so
/// the same error in two games compares equal while the first line still says what it was.
/// </summary>
public static class FaultSignature
{
    public static string Normalize(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";

        // The first line holds the error itself; the stack below it differs between processes on CoreCLR
        var end = message.IndexOf('\n');
        var line = end >= 0 ? message.Substring(0, end) : message;

        var sb = new StringBuilder(line.Length);
        bool lastWasDigit = false;
        foreach (var c in line)
        {
            if (char.IsDigit(c))
            {
                if (!lastWasDigit) sb.Append('#');
                lastWasDigit = true;
                continue;
            }

            lastWasDigit = false;
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>FNV-1a over the UTF-16 code units; string.GetHashCode is randomized per process.</summary>
    public static int Hash(string text)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in text)
            {
                h = (h ^ (byte)c) * 16777619;
                h = (h ^ (byte)(c >> 8)) * 16777619;
            }
            return (int)h;
        }
    }
}
