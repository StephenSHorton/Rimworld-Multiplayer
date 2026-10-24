using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Util;
using RimWorld;
using UnityEngine;
using Verse;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Multiplayer.Client.Desyncs;

public class SaveableDesyncInfo(
    SyncCoordinator coordinator,
    ClientSyncOpinion local,
    ClientSyncOpinion remote,
    int diffAt,
    bool diffAtFound,
    string desyncMessage = null)
{
    public readonly ClientSyncOpinion local = local;
    public readonly ClientSyncOpinion remote = remote;
    public readonly int diffAt = diffAt;
    private readonly Task<string> metadata = Task.Run(MetadataGenerator.Generate);
    // Taken now: the report is saved after the host's traces arrive, when a resync may have replaced the snapshot
    private readonly string commands = CommandReport(local.startTick, coordinator.lastValidTick);
    private readonly Task<FileInfo> replay = Task.Run(SaveReplayIfApplicable);

    public bool ReadyToSave => metadata.IsCompleted && replay.IsCompleted;

    /// <summary>The commands this game ran since the last valid tick and the window's commands (<see cref="CommandLog"/>).</summary>
    public static string CommandReport(int windowStart, int lastValidTick)
    {
        try
        {
            return CommandLog.Report(windowStart, lastValidTick >= 0 ? lastValidTick : windowStart - 300);
        }
        catch (Exception e)
        {
            return $"Couldn't list the commands: {e}";
        }
    }

    public void Save([CanBeNull] HostInfo hostInfo)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var desyncFilePath = FindFileNameForNextDesyncFile();
            using var zip = MpZipFile.Open(Path.Combine(Multiplayer.DesyncsDir, desyncFilePath + ".zip"), ZipArchiveMode.Create);

            zip.AddEntry("desync_info", GetDesyncDetails());
            zip.AddEntry("lanes.txt", FlightRecorder.CompareLanes(local.ToNet(), remote.ToNet()));
            zip.AddEntry("local_flight_recorder.txt", coordinator.recorder.Dump());
            zip.AddEntry("local_commands.txt", commands);
            RandAttrReports.AddToZip(zip, local.startTick, Multiplayer.username);
            zip.AddEntry("entity_diffs.txt", LeafRequests.recentDiffs.Count > 0
                ? string.Join("\n\n", LeafRequests.recentDiffs)
                : "No entity diffs: no lane differed in time for the server to compare entities");

            zip.AddEntry("local_traces.txt", GetLocalTraces(hostInfo));
            zip.AddEntry("host_traces.txt", hostInfo?.Traces ?? "No host traces");

            zip.AddEntry("local_jitted_methods.txt", JittedMethods.GetJittedMethodsString());
            zip.AddEntry("host_jitted_methods.txt", hostInfo?.JittedMethods ?? "No host jitted methods");

            var extraLogs = LogGenerator.PrepareLogData();
            if (extraLogs != null) zip.AddEntry("local_logs.txt", extraLogs);

            zip.AddEntry("local_metadata.txt", metadata.Result);

            // Rimkit's newest perf capture, if this game made one
            try
            {
                var perfDir = new DirectoryInfo(Path.Combine(GenFilePaths.SaveDataFolderPath, "Rimkit_Perf"));
                var perf = perfDir.Exists ? perfDir.GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
                if (perf != null)
                    zip.CreateEntryFromFile(perf.FullName, "local_rimkit_perf_" + perf.Name, CompressionLevel.Optimal);
            }
            catch (Exception e)
            {
                Log.Warning($"Couldn't add the Rimkit perf capture to the desync info: {e.Message}");
            }

            try
            {
                replay.Wait();
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions.SingleOrDefault(inner => inner is TaskCanceledException) == null) throw;
            }
            if (replay.IsCompletedSuccessfully) {
                var replayFile = replay.Result;
                zip.CreateEntryFromFile(replayFile.FullName, "replay.rwmts", CompressionLevel.NoCompression);
                DeleteFileSilent(replayFile);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Exception writing desync info: {e}");
        }

        Log.Message($"Desync info writing took {watch.ElapsedMilliseconds}");
    }

    private string GetLocalTraces([CanBeNull] HostInfo hostInfo)
    {
        var traceMessage = "";
        // The host's come in blocks off Mono (TraceBlocks): diffAt is then the first trace of the block they part in
        var localCount = TraceBlocks.Count(local.desyncStackTraceHashes);
        var remoteCount = TraceBlocks.Count(remote.desyncStackTraceHashes);
        int count = Math.Min(localCount, remoteCount);
        int span = 0;
        int exactAt = diffAt;
        if (TraceBlocks.IsBlocks(remote.desyncStackTraceHashes) && diffAtFound)
        {
            // The host's traces of the block (it sends the whole block) say which trace
            span = TraceBlocks.Size;
            var exact = TraceDiffNote.ExactDifference(local, diffAt, hostInfo?.Traces);
            if (exact >= 0)
            {
                exactAt = exact;
                traceMessage = $"Note: the host sent its trace hashes in blocks of {TraceBlocks.Size}; the block from {diffAt} differs, " +
                               $"and the host's traces of it say the first differing trace is {exact}\n\n";
            }
            else
            {
                exactAt = -1;
                traceMessage = $"Note: the host sent its trace hashes in blocks of {TraceBlocks.Size}; the traces part somewhere in " +
                               $"{diffAt}..{diffAt + TraceBlocks.Size - 1} (the host's traces of it didn't say where)\n\n";
            }
        }

        if (diffAt == -1)
        {
            if (count == 0)
            {
                return $"No traces (remote: {remoteCount}, local: {localCount})";
            }

            traceMessage = "Note: trace hashes are equal between local and remote\n\n";
        }
        else if (!diffAtFound)
        {
            traceMessage = "Note: traces differ in amount, but the existing ones are equal. This means that a tick" +
                           " has ended sooner on one of the connection sides\n\n";
        }

        string note;
        try
        {
            // Only for an exact trace: the first trace of a block names no difference of its own
            note = diffAtFound && exactAt >= 0 ? TraceDiffNote.For(local, exactAt, hostInfo?.Traces, "the host") : "";
        }
        catch (Exception e)
        {
            note = $"Couldn't compare the first differing trace's kind: {e.Message}\n\n";
        }

        traceMessage += note + local.GetFormattedStackTracesForRange(diffAt, span);
        return traceMessage;
    }

    private string GetDesyncDetails()
    {
        var desyncInfo = new StringBuilder();

        desyncInfo
            .AppendLine("###Tick Data###")
            .AppendLine($"Desync Reason|||{desyncMessage ?? "Unknown"}")
            .AppendLine($"Arbiter Connected And Playing|||{Multiplayer.session.ArbiterPlaying}")
            .AppendLine($"Last Valid Tick - Local|||{coordinator.lastValidTick}")
            .AppendLine($"Last Valid Tick - Arbiter|||{coordinator.arbiterWasPlayingOnLastValidTick}")
            .AppendLine("\n###Version Data###")
            .AppendLine($"Multiplayer Mod Version|||{MpVersion.Version}")
            .AppendLine($"Rimworld Version and Rev|||{VersionControl.CurrentVersionStringWithRev}")
            .AppendLine("\n###Debug Options###")
            .AppendLine($"Multiplayer Debug Build - Client|||{MpVersion.IsDebug}")
            .AppendLine($"Multiplayer Debug Mode - Host|||{Multiplayer.GameComp.debugMode}")
            .AppendLine($"Rimworld Developer Mode - Client|||{Prefs.DevMode}")
            .AppendLine("\n###Server Info###")
            .AppendLine($"Player Count|||{Multiplayer.session.players.Count}")
            .AppendLine($"Async time active|||{Multiplayer.GameComp.asyncTime}")
            .AppendLine($"Multifaction active|||{Multiplayer.GameComp.multifaction}")
            .AppendLine($"Map Count|||{Find.Maps?.Count.ToStringSafe()}")
            .AppendLine("\n###CPU Info###")
            .AppendLine($"Processor Name|||{SystemInfo.processorType}")
            .AppendLine($"Processor Speed (MHz)|||{SystemInfo.processorFrequency}")
            .AppendLine($"Thread Count|||{SystemInfo.processorCount}")
            .AppendLine("\n###GPU Info###")
            .AppendLine($"GPU Vendor ID|||{SystemInfo.graphicsDeviceVendorID}")
            .AppendLine($"GPU Type|||{SystemInfo.graphicsDeviceType}")
            .AppendLine($"GPU Name|||{SystemInfo.graphicsDeviceName}")
            .AppendLine($"GPU VRAM|||{SystemInfo.graphicsMemorySize}")
            .AppendLine("\n###RAM Info###")
            .AppendLine($"Physical Memory Present|||{SystemInfo.systemMemorySize}")
            .AppendLine("\n###OS Info###")
            .AppendLine($"OS Type|||{SystemInfo.operatingSystemFamily}")
            .AppendLine($"OS Name and Version|||{SystemInfo.operatingSystem}");

        return desyncInfo.ToString();
    }

    private static string FindFileNameForNextDesyncFile()
    {
        const string FilePrefix = "Desync-";
        const string FileExtension = ".zip";

        //Find all current existing desync zips
        var files = new DirectoryInfo(Multiplayer.DesyncsDir).GetFiles($"{FilePrefix}*{FileExtension}");

        // Kept long enough to compare a whole evening of desyncs
        const int MaxFiles = 40;

        //Delete any pushing us over the limit, and reserve room for one more
        if (files.Length > MaxFiles - 1)
            files.OrderByDescending(f => f.LastWriteTime).Skip(MaxFiles - 1).Do(DeleteFileSilent);

        //Find the current max desync number
        int max = 0;
        foreach (var f in files)
            if (int.TryParse(
                    f.Name.Substring(FilePrefix.Length, f.Name.Length - FilePrefix.Length - FileExtension.Length),
                    out int result) && result > max)
                max = result;

        return $"{FilePrefix}{max + 1:00}";
    }

    private static Task<FileInfo> SaveReplayIfApplicable()
    {
        if (!Multiplayer.settings.includeReplayInDesync) return Task.FromCanceled<FileInfo>(new CancellationToken(true));

        try
        {
            var tmp = new FileInfo(Path.Combine(Multiplayer.DesyncsDir, "desync-replay.tmp.zip"));
            if (tmp.Exists) tmp.Delete();
            Replay.ForSaving(tmp).WriteData(Multiplayer.session.dataSnapshot);
            return Task.FromResult(tmp);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save replay for desync: {e}");
            return Task.FromException<FileInfo>(e);
        }
    }

    private static void DeleteFileSilent(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
    }

    public record HostInfo([CanBeNull] string Traces, [CanBeNull] string JittedMethods);
}
