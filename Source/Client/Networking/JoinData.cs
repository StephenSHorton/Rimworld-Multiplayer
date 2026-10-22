using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Steamworks;
using Verse;

namespace Multiplayer.Client
{

    public static class JoinData
    {
        public static List<ModMetaData> activeModsSnapshot;
        public static ModFileDict modFilesSnapshot;

        public static List<ClientInitDataPacket.ModData> WriteServerData(bool writeConfigs)
        {
            var configs = writeConfigs ? SyncConfigs.GetSyncableConfigContents(
                activeModsSnapshot.Select(m => m.PackageIdNonUnique).ToList()
            ) : [];
            return activeModsSnapshot.Select(meta =>
            {
                var files = modFilesSnapshot
                    .Where(kv => kv.Key == meta.PackageIdNonUnique)
                    .SelectMany(modIdToFilesPair => modIdToFilesPair.Value)
                    .Select(pathToFilePair => new ClientInitDataPacket.ModFile
                    {
                        path = pathToFilePair.Value.relPath,
                        hash = pathToFilePair.Value.hash,
                    })
                    .ToList();

                var localConfig = configs.FirstOrDefault(localConfig => localConfig.ModId == meta.PackageIdNonUnique);
                var source = meta.Source switch
                {
                    ContentSource.Undefined => ClientInitDataPacket.ModSource.Undefined,
                    ContentSource.OfficialModsFolder => ClientInitDataPacket.ModSource.OfficialModsFolder,
                    ContentSource.ModsFolder => ClientInitDataPacket.ModSource.ModsFolder,
                    ContentSource.SteamWorkshop => ClientInitDataPacket.ModSource.SteamWorkshop,
                    _ => throw new ArgumentOutOfRangeException()
                };

                return new ClientInitDataPacket.ModData
                {
                    packageIdNonUnique = meta.PackageIdNonUnique,
                    name = meta.Name,
                    publishedFileId = (ulong)meta.GetPublishedFileId(),
                    source = source,
                    files = files,
                    config = localConfig == null
                        ? null
                        : new ClientInitDataPacket.ModConfig
                        {
                            fileName = localConfig.FileName,
                            contents = localConfig.Contents,
                        }
                };
            }).ToList();
        }

        public static ModMetaData GetInstalledMod(string id)
        {
            if (ModsConfig.IsActive(id + ModMetaData.SteamModPostfix))
                id += ModMetaData.SteamModPostfix;

            return ModLister.GetModWithIdentifier(id);
        }

        public static bool CompareToLocal(RemoteData remote)
        {
            return
                remote.remoteRwVersion == VersionControl.CurrentVersionString &&
                remote.CompareMods(activeModsSnapshot) == ModListDiff.None &&
                remote.remoteFiles.DictsEqual(modFilesSnapshot) &&
                (!remote.hasConfigs || remote.remoteModConfigs.EqualAsSets(SyncConfigs.GetSyncableConfigContents(remote.RemoteModIds.ToList())));
        }

        internal static void TakeModDataSnapshot()
        {
            activeModsSnapshot = ModsConfig.ActiveModsInLoadOrder.ToList();
            modFilesSnapshot = GetModFiles(activeModsSnapshot.Select(m => m.PackageIdNonUnique));
        }

        internal static ModFileDict GetModFiles(IEnumerable<string> modIds)
        {
            var fileDict = new ModFileDict();

            foreach (var modId in modIds)
            {
                var mod = GetInstalledMod(modId);
                if (mod == null || !mod.RootDir.Exists) continue;

                var contentPack = MultiplayerData.DummyContentPack(mod);

                foreach (var asm in MultiplayerData.GetModAssemblies(contentPack))
                {
                    var relPath = asm.FullName.RemovePrefix(contentPack.RootDir).NormalizePath();
                    fileDict.Add(modId, new ModFile(asm.FullName, relPath, asm.CRC32()));
                }

                foreach (var xmlFile in GetModDefsAndPatches(contentPack))
                {
                    var relPath = xmlFile.FullName.RemovePrefix(contentPack.RootDir).NormalizePath();
                    fileDict.Add(modId, new ModFile(xmlFile.FullName, relPath, xmlFile.CRC32()));
                }
            }

            return fileDict;
        }

        public static IEnumerable<FileInfo> GetModDefsAndPatches(ModContentPack mod)
        {
            foreach (var f in ModContentPack.GetAllFilesForModPreserveOrder(mod, "Defs/", f => f.ToLower() == ".xml"))
                yield return f.Item2;

            foreach (var f in ModContentPack.GetAllFilesForModPreserveOrder(mod, "Patches/", f => f.ToLower() == ".xml"))
                yield return f.Item2;
        }

        private static Dictionary<string, bool> modInstalled = new();

        public static bool IsModInstalledCached(string packageId)
        {
            if (modInstalled.TryGetValue(packageId, out var result))
                return result;

            return modInstalled[packageId] =
                GetInstalledMod(packageId) is { } m && (!m.OnSteamWorkshop || SteamModInstalled(m));
        }

        public static void ClearInstalledCache()
        {
            modInstalled.Clear();
        }

        private static bool SteamModInstalled(ModMetaData mod) =>
            ((EItemState)SteamUGC.GetItemState(mod.GetPublishedFileId()))
            .HasFlag(EItemState.k_EItemStateInstalled | EItemState.k_EItemStateSubscribed);

        // Names the mods behind a mod list mismatch, so the log and the window can say which ones differ
        public static ModListDetails DiffModList(RemoteData remote, List<ModMetaData> localMods)
        {
            var details = new ModListDetails();
            var localById = localMods.ToDictionaryPermissive(m => m.PackageIdNonUnique, m => m);
            var remoteIds = new HashSet<string>(remote.RemoteModIds);

            foreach (var m in remote.remoteMods)
            {
                var row = new ModDiffRow(m.name, m.packageId, m.steamId);

                if (localById.TryGetValue(m.packageId, out var local))
                {
                    if (local.Source != m.source)
                        details.otherSource.Add(row);
                }
                else if (!m.Installed)
                    details.missing.Add(row);
                else
                    details.inactive.Add(row);
            }

            foreach (var m in localMods)
                if (!remoteIds.Contains(m.PackageIdNonUnique))
                    details.extra.Add(new ModDiffRow(m.Name, m.PackageIdNonUnique, (ulong)m.GetPublishedFileId()));

            details.orderDiffers = !details.SetsDiffer &&
                !remote.RemoteModIds.SequenceEqual(localMods.Select(m => m.PackageIdNonUnique));

            return details;
        }

        // Whether a copy of a host mod from the host's source is installed here. The join compares
        // (packageId, source), so a Mods folder copy doesn't stand in for the host's Workshop copy or the reverse
        public static bool HasCopyFromSource(ModInfo mod) =>
            ModLister.AllInstalledMods.Any(a =>
                a.PackageIdNonUnique == mod.packageId &&
                (mod.source == ContentSource.Undefined || a.Source == mod.source) &&
                (!a.OnSteamWorkshop || SteamModInstalled(a)));

        // The id to write into ModsConfig for a host mod: the real id (with the _steam postfix only when
        // RimWorld gave it one) of the copy from the host's source, else the active or any installed copy,
        // or the bare id when nothing is installed
        public static string ResolveInstalledId(ModInfo mod)
        {
            var installed = ModLister.AllInstalledMods.Where(a => a.PackageIdNonUnique == mod.packageId).ToList();
            var pick =
                installed.FirstOrDefault(a => a.Source == mod.source) ??
                ModsConfig.ActiveModsInLoadOrder.FirstOrDefault(a => a.PackageIdNonUnique == mod.packageId) ??
                installed.FirstOrDefault(a => a.OnSteamWorkshop) ??
                installed.FirstOrDefault();

            return pick?.PackageId ?? mod.packageId;
        }
    }

    public class RemoteData
    {
        public string remoteRwVersion;
        public string remoteMpVersion;

        public List<ModInfo> remoteMods = new();
        public ModFileDict remoteFiles = new();
        public List<ModConfig> remoteModConfigs = new();
        public bool hasConfigs;

        public IEnumerable<string> RemoteModIds => remoteMods.Select(m => m.packageId);

        public ModListDiff CompareMods(List<ModMetaData> localMods)
        {
            var mods1 = remoteMods.Select(m => (m.packageId, m.source));
            var mods2 = localMods.Select(m => (m.PackageIdNonUnique, m.Source));

            if (!mods1.EqualAsSets(mods2))
                return ModListDiff.NoMatchAsSets;

            if (!RemoteModIds.SequenceEqual(localMods.Select(m => m.PackageIdNonUnique)))
                return ModListDiff.WrongOrder;

            return ModListDiff.None;
        }

        public static RemoteData FromNet(ServerJoinDataPacket packet)
        {
            var remoteInfo = new RemoteData
            {
                remoteRwVersion = packet.rwVersion,
                remoteMpVersion = packet.mpVersion,
                hasConfigs = packet.configsIncluded,
            };

            foreach (var mod in packet.ServerInitData)
            {
                var modInfo = new ModInfo
                {
                    packageId = mod.packageIdNonUnique, name = mod.name, steamId = mod.publishedFileId,
                    source = mod.source switch
                    {
                        ClientInitDataPacket.ModSource.Undefined => ContentSource.Undefined,
                        ClientInitDataPacket.ModSource.OfficialModsFolder => ContentSource.OfficialModsFolder,
                        ClientInitDataPacket.ModSource.ModsFolder => ContentSource.ModsFolder,
                        ClientInitDataPacket.ModSource.SteamWorkshop => ContentSource.SteamWorkshop,
                        _ => throw new ArgumentOutOfRangeException()
                    }
                };
                remoteInfo.remoteMods.Add(modInfo);

                var modMeta = JoinData.GetInstalledMod(modInfo.packageId);
                foreach (var modFile in mod.files)
                {
                    var absPath = modMeta == null ? null : Path.Combine(modMeta.RootDir.FullName, modFile.path);
                    remoteInfo.remoteFiles.Add(modInfo.packageId, new ModFile(absPath, modFile.path, modFile.hash));
                }

                if (mod.config.HasValue)
                {
                    var modConfig = mod.config.Value;
                    remoteInfo.remoteModConfigs.Add(
                        new ModConfig(modInfo.packageId, modConfig.fileName, modConfig.contents));
                }
            }

            return remoteInfo;
        }
    }

    public enum ModListDiff
    {
        None, NoMatchAsSets, WrongOrder
    }

    public record ModDiffRow(string Name, string PackageId, ulong SteamId)
    {
        public override string ToString() =>
            $"{Name} [{PackageId}{(SteamId != 0 ? $", workshop {SteamId}" : "")}]";
    }

    public class ModListDetails
    {
        public List<ModDiffRow> missing = new();     // On the host's list, not installed here
        public List<ModDiffRow> inactive = new();    // On the host's list, installed here but not active
        public List<ModDiffRow> extra = new();       // Active here, not on the host's list
        public List<ModDiffRow> otherSource = new(); // Active on both sides from a different source (Workshop vs Mods folder)
        public bool orderDiffers;                    // Only set when the sets match

        public bool SetsDiffer => missing.Any() || inactive.Any() || extra.Any() || otherSource.Any();
        public bool Any => SetsDiffer || orderDiffers;

        public IEnumerable<(string label, List<ModDiffRow> rows)> Groups()
        {
            if (missing.Any()) yield return ("missing here", missing);
            if (inactive.Any()) yield return ("not active here", inactive);
            if (extra.Any()) yield return ("active only here", extra);
            if (otherSource.Any()) yield return ("different source", otherSource);
        }

        public override string ToString()
        {
            var parts = Groups().Select(g => $"{g.label}: {string.Join(", ", g.rows)}").ToList();
            if (orderDiffers)
                parts.Add("order differs");
            return string.Join("; ", parts);
        }
    }

    public record ModConfig(string ModId, string FileName, string Contents);

    public class ModFileDict : IEnumerable<KeyValuePair<string, Dictionary<string, ModFile>>>
    {
        // Mod id => (path => file)
        private Dictionary<string, Dictionary<string, ModFile>> files = new();

        public void Add(string mod, ModFile file){
            files.GetOrAddNew(mod)[file.relPath] = file;
        }

        public ModFile? GetOrDefault(string mod, string relPath){
            if (files.TryGetValue(mod, out var modFiles) && modFiles.TryGetValue(relPath, out var file))
                return file;
            return null;
        }

        public bool DictsEqual(ModFileDict other)
        {
            return files.Keys.EqualAsSets(other.files.Keys) &&
                   files.All(kv => kv.Value.Values.EqualAsSets(other.files[kv.Key].Values));
        }

        public IEnumerator<KeyValuePair<string, Dictionary<string, ModFile>>> GetEnumerator()
        {
            return files.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return files.GetEnumerator();
        }

        public ModFileDict CopyWithMods(IEnumerable<string> modIds)
        {
            var dict = new ModFileDict();

            foreach (var modId in modIds)
                if (files.TryGetValue(modId, out var ofMod))
                    dict.files[modId] = ofMod;

            return dict;
        }
    }

    public struct ModInfo
    {
        public string packageId; // Mod package id, lower case, no _steam suffix
        public string name;
        public ulong steamId; // Zero means invalid
        public ContentSource source;

        public bool Installed => JoinData.IsModInstalledCached(packageId);

        public bool CanSubscribe => steamId != 0;

        // Fix and restart can only fetch the host's copy when the host runs it from the Workshop
        public bool CanDownload => CanSubscribe && source == ContentSource.SteamWorkshop;
    }

    public struct ModFile(string absPath, string relPath, int hash)
    {
        public string absPath = absPath?.NormalizePath(); // Can be null on the remote side
        public string relPath = relPath.NormalizePath();
        public int hash = hash;

        public bool Equals(ModFile other) =>
            relPath == other.relPath && hash == other.hash;

        public override bool Equals(object obj) =>
            obj is ModFile other && Equals(other);

        public override int GetHashCode() =>
            Gen.HashCombineInt(relPath.GetHashCode(), hash);
    }

    [HarmonyPatch(typeof(ModLister), nameof(ModLister.RebuildModList))]
    static class ClearCacheRebuildModList
    {
        static void Prefix()
        {
            JoinData.ClearInstalledCache();
        }
    }
}
