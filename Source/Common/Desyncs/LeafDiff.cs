using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Multiplayer.Common.Desyncs;

/// <summary>
/// Diffs two games' leaf lists. A leaf line starts with the entity's id and goes on with space-separated
/// key=value fields; a value in brackets is a comma-separated list of name=value elements (needs, hediffs).
/// The result names each entity that differs and each field or element: "Human538 needs.Beauty: 0.105 vs 0.129".
/// </summary>
public static class LeafDiff
{
    public const int MaxLines = 300;

    public static string Diff(string reference, string other, string refName, string otherName)
    {
        var refLines = Index(reference);
        var otherLines = Index(other);
        var sb = new StringBuilder();
        int lines = 0, differing = 0;

        foreach (var pair in refLines)
        {
            var id = pair.Key;
            if (!otherLines.TryGetValue(id, out var theirs))
            {
                differing++;
                if (lines++ < MaxLines) sb.AppendLine($"{id}: only in {refName}'s game ({Trim(pair.Value)})");
                continue;
            }

            if (theirs == pair.Value) continue;
            differing++;
            foreach (var field in Fields(pair.Value, theirs))
                if (lines++ < MaxLines)
                    sb.AppendLine($"{id} {field.key}: {field.a} ({refName}) vs {field.b} ({otherName})");
        }

        foreach (var pair in otherLines)
            if (!refLines.ContainsKey(pair.Key))
            {
                differing++;
                if (lines++ < MaxLines) sb.AppendLine($"{pair.Key}: only in {otherName}'s game ({Trim(pair.Value)})");
            }

        if (differing == 0)
            return "No entity differs (the difference is outside what the leaves list, or it was gone by this tick)";

        if (lines > MaxLines)
            sb.AppendLine($"... and {lines - MaxLines} more lines");
        return $"{differing} differ\n" + sb;
    }

    private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) + "..." : s;

    private static Dictionary<string, string> Index(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var space = line.IndexOf(' ');
            var id = space < 0 ? line : line.Substring(0, space);
            result[id] = line;
        }
        return result;
    }

    private static IEnumerable<(string key, string a, string b)> Fields(string a, string b)
    {
        var fa = Split(a);
        var fb = Split(b);
        foreach (var key in fa.Keys.Union(fb.Keys))
        {
            fa.TryGetValue(key, out var va);
            fb.TryGetValue(key, out var vb);
            if (va == vb) continue;
            yield return (key, va ?? "(none)", vb ?? "(none)");
        }
    }

    // "Human1 pos=(1, 0, 2) needs=[Food=0.5,Rest=0.2]" -> pos, needs.Food, needs.Rest
    public static Dictionary<string, string> Split(string line)
    {
        var result = new Dictionary<string, string>();
        int i = line.IndexOf(' ');
        if (i < 0) return result;
        var rest = line.Substring(i + 1);

        int pos = 0;
        while (pos < rest.Length)
        {
            int eq = rest.IndexOf('=', pos);
            if (eq < 0) break;
            var key = rest.Substring(pos, eq - pos).Trim();
            int start = eq + 1, end;
            if (start < rest.Length && (rest[start] == '[' || rest[start] == '('))
            {
                char close = rest[start] == '[' ? ']' : ')';
                end = rest.IndexOf(close, start);
                end = end < 0 ? rest.Length : end + 1;
            }
            else
            {
                end = rest.IndexOf(' ', start);
                if (end < 0) end = rest.Length;
            }

            var value = rest.Substring(start, end - start);
            if (value.StartsWith("[") && value.EndsWith("]"))
            {
                foreach (var element in value.Substring(1, value.Length - 2).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = element.IndexOf('=');
                    if (e < 0) result[$"{key}.{element}"] = "present";
                    else result[$"{key}.{element.Substring(0, e)}"] = element.Substring(e + 1);
                }
            }
            else
            {
                result[key] = value;
            }

            pos = end;
            while (pos < rest.Length && rest[pos] == ' ') pos++;
        }

        return result;
    }

    public static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gz.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    public static string Gunzip(byte[]? data)
    {
        if (data == null || data.Length == 0) return "";
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
