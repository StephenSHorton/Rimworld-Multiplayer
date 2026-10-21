using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using Steamworks;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace Multiplayer.Client
{

    public class JoinDataWindow : Window
    {
        public override Vector2 InitialSize => new(660f, 610f);

        private enum Tab
        {
            General, ModList, Files, Configs
        }

        private Tab tab;

        class Node
        {
            public string DisplayName => $"{(children.Any() ? $"[{(collapsed ? '+' : '-')}] " : "")}{name}{(children.Any() ? "/" : "")}";

            public string name;
            public string id;
            public string path;
            public int depth;
            public Node parent;
            public List<Node> children = new();
            public bool collapsed;
            public NodeStatus status;

            public int[] childrenPerStatus;
            public HashSet<string> paths = new();
        }

        enum NodeStatus
        {
            None, Missing, Added, Modified
        }

        private RemoteData remote;
        private IConnector connector;
        private Node filesRoot;
        private Node configsRoot;
        public string connectAnywayDisabled;
        public Action connectAnywayCallback;
        private ModFileDict filesForUI;
        private ModListDiff modListDiff;
        private ModListDetails modListDetails;

        public JoinDataWindow(RemoteData remote, IConnector connector)
        {
            this.remote = remote;
            this.connector = connector;

            closeOnAccept = false;
            closeOnCancel = false;
        }

        public override void PostOpen()
        {
            base.PostOpen();

            JoinData.ClearInstalledCache();
            filesForUI = JoinData.modFilesSnapshot.CopyWithMods(remote.RemoteModIds);
            CheckModLists();
            RefreshFileNodes();
            RefreshConfigNodes();

            Log.Message($"Multiplayer: Mod mismatch window open ({DiffString()})");
        }

        private void CheckModLists()
        {
            modListDiff = remote.CompareMods(JoinData.activeModsSnapshot);
            modListDetails = JoinData.DiffModList(remote, JoinData.activeModsSnapshot);
        }

        private void AddNodesForPath(string path, Node root, NodeStatus status)
        {
            if (status == NodeStatus.None) return;
            if (root.paths.Contains(path)) return;

            var arr = path.Split(new[]{'/'}, StringSplitOptions.RemoveEmptyEntries);
            var cur = root;

            foreach (var part in arr){
                var next = cur.children.FirstOrDefault(n => n.name == part);
                if (next == null){
                    next = new Node(){name=part, parent=cur, depth=cur.depth+1};
                    cur.children.Add(next);
                }
                cur = next;
            }

            cur.status = status;
            root.paths.Add(path);

            if (root.childrenPerStatus != null)
                root.childrenPerStatus[(int)status]++;
        }

        private void RefreshFileNodes()
        {
            filesRoot = new Node();

            Node ModNode(string modId, out bool newNode)
            {
                newNode = false;

                var mod = JoinData.GetInstalledMod(modId);
                if (mod == null) return null;

                var modNode = filesRoot.children.FirstOrDefault(n => n.id == modId);
                if (modNode == null)
                {
                    modNode = new Node { name=mod.Name, id=modId, path=mod.RootDir.FullName, parent=filesRoot, childrenPerStatus=new int[4], collapsed=true };
                    newNode = true;
                }

                return modNode;
            }

            void AddFiles(ModFileDict one, ModFileDict two, NodeStatus notInTwo){
                foreach (var kv in one)
                {
                    var modNode = ModNode(kv.Key, out bool newNode);
                    if (modNode == null) continue;

                    foreach (var f in kv.Value.Values){
                        var inTwo = two.GetOrDefault(kv.Key, f.relPath);
                        AddNodesForPath(
                            f.relPath,
                            modNode,
                            inTwo == null ? notInTwo : inTwo.Value.hash != f.hash ? NodeStatus.Modified : NodeStatus.None
                        );
                    }

                    if (modNode.children.Any() && newNode)
                        filesRoot.children.Add(modNode);
                }
            }

            AddFiles(remote.remoteFiles, filesForUI, NodeStatus.Missing);
            AddFiles(filesForUI, remote.remoteFiles, NodeStatus.Added);
        }

        private void RefreshConfigNodes()
        {
            configsRoot = new Node {depth=-1};

            void AddConfigs(List<ModConfig> one, List<ModConfig> two, NodeStatus notInTwo)
            {
                var oneDict = one.ToDictionaryPermissive(m => m.ModId + "/" + m.FileName, m => m.Contents);
                var twoDict = two.ToDictionaryPermissive(m => m.ModId + "/" + m.FileName, m => m.Contents);

                foreach (var kv in oneDict){
                    AddNodesForPath(
                        kv.Key,
                        configsRoot,
                        twoDict.TryGetValue(kv.Key, out var t) ? (t.Equals(kv.Value) ? NodeStatus.None : NodeStatus.Modified) : notInTwo
                    );
                }
            }

            var localConfigs = SyncConfigs.GetSyncableConfigContents(remote.RemoteModIds.ToList());

            if (remote.hasConfigs)
            {
                AddConfigs(remote.remoteModConfigs, localConfigs, NodeStatus.Missing);
                AddConfigs(localConfigs, remote.remoteModConfigs, NodeStatus.Added);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            using (MpStyle.Set(TextAnchor.UpperCenter))
            using (MpStyle.Set(GameFont.Medium))
                Widgets.Label(inRect, "MpDataMismatch".Translate());

            inRect.yMin += 35f;
            inRect.yMin += 35f;

            var tabsRect = inRect.TopPartPixels(inRect.height - 50f);

            TabDrawer.DrawTabs(tabsRect, new List<TabRecord>() {
                new("MpMismatchGeneral".Translate(), () => tab = Tab.General, tab == Tab.General),
                new("MpMismatchModList".Translate(), () => tab = Tab.ModList, tab == Tab.ModList),
                new("MpMismatchFiles".Translate(), () => tab = Tab.Files, tab == Tab.Files),
                new("MpMismatchConfigs".Translate(), () => tab = Tab.Configs, tab == Tab.Configs)
            });

            GUI.BeginGroup(tabsRect);
            {
                var inTab = new Rect(0, 0, tabsRect.width, tabsRect.height);

                void RefreshFiles()
                {
                    filesForUI = JoinData.GetModFiles(remote.RemoteModIds);
                    RefreshFileNodes();
                }

                if (tab == Tab.Files)
                    DrawTreeTab(
                        inTab,
                        "MpMismatchLocalFiles",
                        filesRoot,
                        RefreshFiles,
                        true,
                        "MpMismatchFilesInfo".Translate() +
                        "\n\n" +
                        "MpMismatchFilesInfoMods".Translate() +
                        (string)(HasCoreMismatches() ? "\n\n" + "MpMismatchFilesInfoCore".Translate() : "") +
                        "\n\n" +
                        "MpMismatchFilesInfoHost".Translate(),
                        filesRoot.children.Any() ? null : "MpFilesMatch"
                    );
                else if (tab == Tab.Configs)
                    DrawTreeTab(
                        inTab,
                        "MpMismatchLocalConfigs",
                        configsRoot,
                        RefreshConfigNodes,
                        false,
                        null,
                        configsRoot.children.Any() ? null : remote.hasConfigs ? "MpConfigsMatch" : "MpConfigSyncDisabled"
                    );
                else if (tab == Tab.ModList)
                    DrawModList(inTab);
                else if (tab == Tab.General)
                    DrawGeneralTab(inTab);
            }
            GUI.EndGroup();

            var btnCenter = new Rect(0, 0, 135f, 35f).CenterOn(inRect.BottomPartPixels(35f)).Up(10f);

            var connectAnyway = MpUI.ButtonTextWithTip(
                btnCenter.Left(150f),
                "MpConnectAnyway".Translate(),
                connectAnywayDisabled,
                connectAnywayDisabled != null
            );

            if (connectAnyway)
            {
                Log.Message($"Multiplayer: Connecting anyway ({DiffString()})");
                connectAnywayCallback();
                Close(false);
            }

            if (MpUI.ButtonTextWithTip(btnCenter, "MpFixAndRestart".Translate(), "MpRestartNeeded".Translate()))
                Find.WindowStack.Add(new FixAndRestartWindow(remote, connector));

            if (Widgets.ButtonText(btnCenter.Right(150f), "MpMismatchQuit".Translate()))
            {
                Multiplayer.StopMultiplayerAndClearAllWindows();
                Find.WindowStack.Add(new ServerBrowser());
            }
        }

        private bool HasCoreMismatches() => filesRoot.children.Any(c => c.id.Contains("ludeon"));

        private string DiffString()
        {
            var str = "";
            str += $"MP version match: {remote.remoteMpVersion == MpVersion.Version}, ";
            str += $"Mod list diff: {modListDiff}{(modListDetails.Any ? $" ({modListDetails})" : "")}, ";
            str += $"Files match: {!filesRoot.children.Any()}, ";
            str += $"Config sync enabled: {remote.hasConfigs}, ";
            str += $"Configs match: {!configsRoot.children.Any()}";
            return str;
        }

        private void DrawGeneralTab(Rect inRect)
        {
            const float rowLabelWidth = 200f;
            const float columnWidth = 150f;
            const float rowHeight = 30f;
            const float modDataHeight = 26f;

            inRect = inRect.ContractedBy(20);
            inRect.yMin += 10;

            GUI.BeginGroup(inRect.Height(modDataHeight * 3).Width(rowLabelWidth).CenteredOnXIn(inRect));
            {
                var modData = new Rect(0, 0, rowLabelWidth, modDataHeight);
                Widgets.DrawAltRect(modData);
                var modListsMatch = modListDiff == ModListDiff.None;
                Widgets.CheckboxLabeled(modData, "MpMismatchModList".Translate(), ref modListsMatch);
                if (modListDetails.Any)
                    TooltipHandler.TipRegion(modData, ModListTip());
                modData = modData.Down(modDataHeight);

                var modFilesMatch = !filesRoot.children.Any();
                Widgets.CheckboxLabeled(modData, "MpMismatchFiles".Translate(), ref modFilesMatch);
                modData = modData.Down(modDataHeight);

                var modConfigsMatch = !remote.hasConfigs || !configsRoot.children.Any();
                Widgets.DrawAltRect(modData);
                Widgets.CheckboxLabeled(modData, "MpMismatchConfigs".Translate(), ref modConfigsMatch);
                modData = modData.Down(modDataHeight);
            }
            GUI.EndGroup();

            inRect.yMin += modDataHeight * 3;
            inRect.yMin += DrawModListSummary(inRect);

            using (MpStyle.Set(GameFont.Tiny))
            using (MpStyle.Set(TextAnchor.MiddleCenter))
            using (MpStyle.Set(Color.grey))
                Widgets.Label(inRect.Height(45).Width(300f).CenteredOnXIn(inRect), "MpMismatchSeeTabs".Translate());

            inRect.yMin += 45f;
            inRect.yMin += 30;

            using (MpStyle.Set(TextAnchor.MiddleCenter))
            {
                var headerRect = inRect.Height(rowHeight);
                var serverColumn = headerRect.Right(rowLabelWidth).Width(columnWidth);
                var clientColumn = serverColumn.Right(columnWidth);

                Widgets.DrawHighlightIfMouseover(serverColumn);
                Widgets.DrawHighlightIfMouseover(clientColumn);

                using (MpStyle.Set(new Color(0.7f, 0.7f, 0.7f)))
                {
                    Widgets.Label(serverColumn, "MpMismatchServerSide".Translate());
                    Widgets.Label(clientColumn, "MpMismatchClientSide".Translate());
                }

                var checkboxColumn = clientColumn.Right(columnWidth).MaxX(inRect.xMax);

                var rwVersionRect = headerRect.Down(rowHeight).Width(rowLabelWidth);
                Widgets.DrawHighlightIfMouseover(rwVersionRect);
                Widgets.DrawAltRect(headerRect.Down(rowHeight));
                Widgets.Label(rwVersionRect, "MpMismatchRwVersion".Translate());
                Widgets.Label(serverColumn.Down(rowHeight), remote.remoteRwVersion);
                Widgets.Label(clientColumn.Down(rowHeight), VersionControl.CurrentVersionString);

                bool rwVersionCheck = remote.remoteRwVersion == VersionControl.CurrentVersionString;
                Widgets.Checkbox(new Rect(0, 0, 24, 24).CenterOn(checkboxColumn.Down(rowHeight)).min, ref rwVersionCheck);

                var mpVersionRect = rwVersionRect.Down(rowHeight).Width(rowLabelWidth);
                Widgets.DrawHighlightIfMouseover(mpVersionRect);
                Widgets.Label(mpVersionRect, "MpMismatchMpVersion".Translate());
                Widgets.Label(serverColumn.Down(2 * rowHeight), remote.remoteMpVersion);
                Widgets.Label(clientColumn.Down(2 * rowHeight), MpVersion.Version);

                bool mpVersionCheck = remote.remoteMpVersion == MpVersion.Version;
                Widgets.Checkbox(new Rect(0, 0, 24, 24).CenterOn(checkboxColumn.Down(2 * rowHeight)).min, ref mpVersionCheck);

                inRect.yMin += rowHeight * 3 + 30f;
            }
        }

        // A red line per kind of mod list difference under the General tab's rows, names cut to fit with "+N more";
        // the tooltip has the full list with package and Workshop ids. Returns the height used.
        private float DrawModListSummary(Rect inRect)
        {
            if (!modListDetails.Any) return 0f;

            const float summaryWidth = 460f;

            using (MpStyle.Set(GameFont.Tiny))
            using (MpStyle.Set(TextAnchor.UpperCenter))
            {
                var lines = modListDetails.Groups()
                    .Select(g => FitNames(g.label.CapitalizeFirst() + ": ", g.rows, summaryWidth))
                    .ToList();
                if (modListDetails.orderDiffers)
                    lines.Add("Same mods, different load order");

                var lineHeight = Text.LineHeightOf(GameFont.Tiny);
                var rect = inRect.Height(lines.Count * lineHeight).Width(summaryWidth).CenteredOnXIn(inRect).Down(4f);

                using (MpStyle.Set(modListDetails.SetsDiffer ? Red : Yellow))
                using (MpStyle.Set(WordWrap.NoWrap))
                    Widgets.Label(rect, string.Join("\n", lines));

                Widgets.DrawHighlightIfMouseover(rect);
                TooltipHandler.TipRegion(rect, ModListTip());

                return rect.height + 8f;
            }
        }

        private static string FitNames(string prefix, List<ModDiffRow> rows, float width)
        {
            string More(int n) => n > 0 ? $" +{n} more" : "";

            var shown = prefix + rows[0].Name;
            var count = 1;
            for (; count < rows.Count; count++)
            {
                var next = shown + ", " + rows[count].Name;
                if (Text.CalcSize(next + More(rows.Count - count - 1)).x > width)
                    break;
                shown = next;
            }

            var more = More(rows.Count - count);
            return shown.Truncate(width - Text.CalcSize(more).x) + more;
        }

        private string ModListTip()
        {
            var tip = modListDetails.Groups()
                .Select(g => g.label.CapitalizeFirst() + ":\n" + string.Join("\n", g.rows.Select(r => "  " + r)))
                .ToList();
            if (modListDetails.orderDiffers)
                tip.Add("Same mods, different load order");
            return string.Join("\n\n", tip);
        }

        private Vector2 treeScroll;
        private int nodeCount;
        private Vector2 descScroll;
        private Dictionary<string, float> strHeight = new();

        static readonly Color Red = new(1f, 0.25f, 0.25f);
        static readonly Color Orange = new(1f, 0.5f, 0.25f);
        static readonly Color Yellow = new(1f, 1f, 0.25f);
        private const string RedStr = "ff4444";
        private const string OrangeStr = "ff8844";
        private const string YellowStr = "ffff44";

        private void DrawTreeTab(Rect inRect, string labelKey, Node root, Action refresh, bool showCounts, string desc, string treeDescKey)
        {
            var listRect = new Rect(0, 20f, 440f, 220f);
            var labelsRect = new Rect(0, 20f, 120f, 220f);

            var combined = new Rect(0, 15f, listRect.width + labelsRect.width + 20f, listRect.yMax).CenteredOnXIn(inRect);

            GUI.BeginGroup(combined);

            MpUI.Label(new Rect(0, 0, 440f, 20f), labelKey.Translate(), GameFont.Tiny);

            var refreshBtn = new Rect(420, 0, 20, 20);
            TooltipHandler.TipRegion(refreshBtn, "MpMismatchTreeRefresh".Translate());
            if (Widgets.ButtonImage(refreshBtn, TexUI.RotRightTex))
                refresh();

            using (MpStyle.Set(new Color(1, 1, 1, 0.1f)))
                Widgets.DrawBox(listRect);

            const float modLabelHeight = 22f;

            var viewRect = new Rect(0, 0, listRect.width - scrollbarWidth, nodeCount * modLabelHeight);
            Widgets.BeginScrollView(listRect, ref treeScroll, viewRect);

            if (treeDescKey == null)
            {
                var toDraw = new Stack<Node>();

                foreach (var n in root.children)
                    toDraw.Push(n);

                if (Event.current.type == EventType.Layout)
                    nodeCount = 0;

                var scrollShown = viewRect.height > listRect.height;

                int i = 0;
                while (toDraw.Count > 0)
                {
                    var n = toDraw.Pop();

                    if (!n.collapsed)
                        foreach (var c in n.children)
                            toDraw.Push(c);

                    var labelRect = new Rect(0, i * modLabelHeight, listRect.width, modLabelHeight);

                    Widgets.DrawHighlightIfMouseover(labelRect);

                    if (n.path != null)
                    {
                        var nodeTip = "";

                        if (Input.GetKey(KeyCode.LeftShift))
                            nodeTip += n.path;
                        else
                            nodeTip += "MpMismatchFileShowPath".Translate();

                        nodeTip += "\n\n";
                        nodeTip += "MpMismatchNodeExpand".Translate();
                        nodeTip += "\n";
                        nodeTip += "MpMismatchFileOpenPath".Translate();

                        TooltipHandler.TipRegion(labelRect, () => nodeTip, 13624604);
                    }

                    if (Widgets.ButtonInvisible(labelRect))
                    {
                        if (Event.current.shift && n.path != null)
                            ShellOpenDirectory.Execute(n.path);
                        else
                            n.collapsed = !n.collapsed;
                    }

                    MpUI.Label(
                        labelRect.MinX(5 + 15f * n.depth),
                        n.DisplayName,
                        color:
                        n.status switch
                        {
                            NodeStatus.Added => Orange,
                            NodeStatus.Missing => Red,
                            NodeStatus.Modified => Yellow,
                            _ => Color.white
                        }
                    );

                    if (showCounts && n.childrenPerStatus != null)
                        MpUI.Label(
                            labelRect.Left(scrollShown ? 21f : 5f),
                            $"<color=#{RedStr}>{n.childrenPerStatus[1]}</color> <color=#{OrangeStr}>{n.childrenPerStatus[2]}</color> <color=#{YellowStr}>{n.childrenPerStatus[3]}</color>",
                            anchor: TextAnchor.UpperRight
                        );

                    i++;
                }

                if (Event.current.type == EventType.Layout)
                    nodeCount = i;
            }
            else
            {
                using (MpStyle.Set(TextAnchor.MiddleCenter))
                using (MpStyle.Set(new Color(0.6f, 0.6f, 0.6f)))
                    Widgets.Label(listRect.AtZero(), treeDescKey.Translate());
            }

            Widgets.EndScrollView();

            GUI.BeginGroup(labelsRect.Right(listRect.width + 20f));

            Text.CurFontStyle.richText = true;
            MpUI.Label(
                labelsRect.AtZero(),
                $"<color=#{RedStr}>({"MpMismatchTreeMissing".Translate()})</color>\n<color=#{OrangeStr}>({"MpMismatchTreeAdded".Translate()})</color>\n<color=#{YellowStr}>({"MpMismatchTreeModified".Translate()})</color>",
                anchor: TextAnchor.MiddleCenter
            );

            GUI.EndGroup();

            GUI.EndGroup();

            if (desc != null)
            {
                var scrollOut = new Rect(0, combined.yMax + 15f, combined.width, 0f).MaxY(inRect.yMax).CenteredOnXIn(inRect);
                var height = Text.CalcHeight(desc, scrollOut.width - 16f);
                var descRect = scrollOut.Width(scrollOut.width - 16f).Height(height);

                Widgets.BeginScrollView(scrollOut, ref descScroll, descRect);
                {
                    MpUI.Label(descRect, desc);
                }
                Widgets.EndScrollView();
            }
        }

        Vector2 modScrollLeft;
        Vector2 modScrollRight;

        const float scrollbarWidth = 16f;

        [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
        public void DrawModList(Rect inRect)
        {
            const float modLabelHeight = 21f;
            const float selectorWidth = 160f;
            const float selectorSpacing = 20f;
            const float selectorHeight = 300f;

            inRect.yMin += 15f;

            var mods1Rect = inRect.
                Right(inRect.center.x - (selectorWidth * 3 + selectorSpacing * 3) / 2f).
                LeftPartPixels(selectorWidth).
                TopPartPixels(selectorHeight + 50f);

            void DrawModListItem(Vector2 topLeft, string name, string tip, ContentSource source, Color color, Vector2 scrollMinHeight)
            {
                if (scrollMinHeight.x > topLeft.x || scrollMinHeight.x + scrollMinHeight.y < topLeft.x)
                    return;

                var itemRect = new Rect(topLeft, new(selectorWidth, modLabelHeight));
                Widgets.DrawHighlightIfMouseover(itemRect);
                TooltipHandler.TipRegion(itemRect, tip);

                GUI.DrawTexture(new Rect(topLeft + new Vector2(2, 2), new(16, 16)), source.GetIcon());
                MpUI.Label(new Rect(topLeft + new Vector2(21, 0), new(selectorWidth - 21f - 16f, modLabelHeight + 2)), name, color: color);
            }

            GUI.BeginGroup(mods1Rect);
            {
                MpUI.Label(new Rect(0, 0, selectorWidth, 20f), "MpMismatchServerMods".Translate(), GameFont.Tiny, TextAnchor.MiddleCenter);

                var selectorRect = new Rect(0, 20, selectorWidth, selectorHeight);
                using (MpStyle.Set(new Color(1, 1, 1, 0.1f)))
                    Widgets.DrawBox(selectorRect);

                Widgets.BeginScrollView(selectorRect, ref modScrollLeft, new Rect(0, 0, selectorRect.width - scrollbarWidth, remote.remoteMods.Count * 1 * modLabelHeight));
                {
                    int i = 0;
                    for (int j = 0; j < 1; j++)
                        foreach (var m in remote.remoteMods)
                        {
                            DrawModListItem(
                                new(0, i * modLabelHeight),
                                m.name,
                                m.packageId,
                                m.source,
                                m.Installed ? Color.white : Red,
                                new Vector2(modScrollLeft.x, selectorHeight + modLabelHeight)
                            );
                            i++;
                        }
                }
                Widgets.EndScrollView();
            }
            GUI.EndGroup();

            var mods2Rect = mods1Rect.
                Right(mods1Rect.width + selectorSpacing).
                TopPartPixels(selectorHeight + 25f);

            GUI.BeginGroup(mods2Rect);
            {
                MpUI.Label(new Rect(0, 0, selectorWidth, 20f), "MpMismatchLocalMods".Translate(), GameFont.Tiny, TextAnchor.MiddleCenter);

                var selectorRect = new Rect(0, 20, selectorWidth, selectorHeight);
                using (MpStyle.Set(new Color(1, 1, 1, 0.1f)))
                    Widgets.DrawBox(selectorRect);

                Widgets.BeginScrollView(selectorRect, ref modScrollRight, new Rect(0, 0, selectorRect.width - scrollbarWidth, ModsConfig.ActiveModsInLoadOrder.Count() * 1 * modLabelHeight));
                {
                    int i = 0;
                    for (int j = 0; j < 1; j++)
                        foreach (var m in ModsConfig.ActiveModsInLoadOrder)
                        {
                            DrawModListItem(
                                new(0, i * modLabelHeight),
                                m.Name,
                                m.PackageIdNonUnique,
                                m.Source,
                                Color.white,
                                new Vector2(modScrollRight.x, selectorHeight + modLabelHeight)
                            );
                            i++;
                        }
                }
                Widgets.EndScrollView();
            }
            GUI.EndGroup();

            const float btnsWidth = selectorWidth + 20f;

            var mods3Rect = mods2Rect.
                Right(mods1Rect.width + selectorSpacing).
                TopPartPixels(selectorHeight)
                .Width(btnsWidth);

            GUI.BeginGroup(mods3Rect);
            {
                var notInstalled = remote.remoteMods.Where(m => !m.Installed);
                var notInstalledNotOnSteam = notInstalled.Where(m => !m.CanSubscribe);
                var btns = new Rect(0, 0, btnsWidth, 35f * 2 + 10f).CenterOn(new Rect(0, 0, btnsWidth, selectorHeight));

                if (Widgets.ButtonText(btns.TopPartPixels(35f), "MpMismatchModListRefresh".Translate()))
                {
                    JoinData.ClearInstalledCache();
                    ModLister.RebuildModList();
                }

                var subscribeTip = "MpMismatchSubscribeNoSteam".Translate();
                if (SteamManager.Initialized)
                {
                    subscribeTip = "MpMismatchSubscribeDesc1".Translate();
                    if (notInstalledNotOnSteam.Any())
                        subscribeTip += "\n\n" + "MpMismatchSubscribeDesc2".Translate();
                }

                if (!notInstalled.Any())
                    subscribeTip = "MpMismatchSubscribeNoMissing".Translate();

                var subscribeBtn = "MpMismatchSubscribe".Translate();
                if (notInstalledNotOnSteam.Any())
                    subscribeBtn += $" ({notInstalled.Count(m => m.CanSubscribe)})";

                var doSubscribe = MpUI.ButtonTextWithTip(
                    btns.BottomPartPixels(35f),
                    subscribeBtn,
                    subscribeTip,
                    !SteamManager.Initialized || !notInstalled.Any()
                );

                if (doSubscribe)
                {
                    foreach (var m in notInstalled.Where(m => m.CanSubscribe))
                        SteamUGC.SubscribeItem(new PublishedFileId_t(m.steamId));

                    Messages.Message($"MpMismatchSubscribeMsg".Translate(notInstalled.Count(m => m.CanSubscribe)), MessageTypeDefOf.PositiveEvent, false);
                }

                var infoStr = "MpMismatchModListsMatch".Translate();
                var infoColor = Color.green;

                if (modListDiff == ModListDiff.NoMatchAsSets)
                {
                    infoStr = "MpMismatchModListsMismatch".Translate();
                    infoColor = Red;
                }
                else if (modListDiff == ModListDiff.WrongOrder)
                {
                    infoStr = "MpMismatchWrongOrder".Translate();
                    infoColor = Yellow;
                }

                if (notInstalled.Any())
                {
                    infoStr = "MpMismatchNotInstalled".Translate(notInstalled.Count());
                    infoColor = Red;
                }

                using (MpStyle.Set(GameFont.Tiny))
                using (MpStyle.Set(TextAnchor.UpperCenter))
                using (MpStyle.Set(infoColor))
                    Widgets.Label(btns.Under(60f).Down(10f), infoStr);
            }
            GUI.EndGroup();
        }
    }

    public class FixAndRestartWindow : Window
    {
        private enum Stage
        {
            Choose, Downloading, CannotFix
        }

        private RemoteData data;
        private IConnector connector;
        private bool applyModList = true;
        private bool applyConfigs;

        private Stage stage;
        private List<ModInfo> missing = new();
        private Dictionary<ulong, string> downloadStatus = new();
        private float nextPoll;
        private float nextRequest;
        private Vector2 listScroll;

        private const float PollInterval = 0.5f;    // Seconds between Steam state polls
        private const float RequestInterval = 5f;   // Seconds before asking Steam again for an item that isn't moving
        private const float RowHeight = 24f;

        public override Vector2 InitialSize => stage == Stage.Choose
            ? new(400, 200)
            : new(480, Mathf.Clamp(160 + missing.Count * RowHeight, 220, 480));

        public FixAndRestartWindow(RemoteData data, IConnector connector)
        {
            this.data = data;
            this.connector = connector;
            applyConfigs = data.hasConfigs;

            closeOnAccept = false;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
        }

        private void SetStage(Stage newStage)
        {
            stage = newStage;
            SetInitialSizeAndPosition();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (stage == Stage.Downloading)
            {
                DrawDownloading(inRect);
                return;
            }

            if (stage == Stage.CannotFix)
            {
                DrawCannotFix(inRect);
                return;
            }

            var line = inRect.Height(30f);
            MpUI.CheckboxLabeledWithTip(
                line,
                "MpApplyModList".Translate(),
                "MpApplyModListTip".Translate(),
                ref applyModList
            );

            line = line.Down(30);
            MpUI.CheckboxLabeledWithTip(
                line,
                "MpApplyConfigs".Translate(),
                data.hasConfigs ? "MpApplyConfigsTip".Translate() : "MpConfigSyncDisabled".Translate(),
                ref applyConfigs,
                !data.hasConfigs
            );

            const float btnWidth = 110;
            const float btnHeight = 35;

            var center = new Vector2(inRect.width / 2f, inRect.yMax - btnHeight);
            var size = new Vector2(btnWidth, btnHeight);
            var restartBtn = new Rect(center + new Vector2(-btnWidth - 5, 0), size);

            if (MpUI.ButtonTextWithTip(restartBtn, "MpMismatchRestart".Translate(), "MpMismatchRestartDesc".Translate()))
                DoRestart();

            if (Widgets.ButtonText(new Rect(center + new Vector2(5, 0), size), "MpMismatchBack".Translate()))
                Close();
        }

        private void DoRestart()
        {
            if (applyModList)
            {
                // Restarting into a mod list with uninstalled mods only fails the join again,
                // so fetch them from the Workshop first or tell the player where to get them
                JoinData.ClearInstalledCache();
                missing = data.remoteMods.Where(m => !m.Installed).ToList();

                if (missing.Any())
                {
                    if (SteamManager.Initialized && missing.All(m => m.CanSubscribe))
                        StartDownloads();
                    else
                    {
                        Log.Message($"Multiplayer: Fix and restart can't download {string.Join(", ", missing.Select(Row))}" +
                                    (SteamManager.Initialized ? "" : " (Steam not running)"));
                        SetStage(Stage.CannotFix);
                    }

                    return;
                }
            }

            Restart();
        }

        private static ModDiffRow Row(ModInfo m) => new(m.name, m.packageId, m.steamId);

        private void StartDownloads()
        {
            Log.Message($"Multiplayer: Fix and restart is subscribing to {string.Join(", ", missing.Select(Row))}");

            foreach (var m in missing)
            {
                var id = new PublishedFileId_t(m.steamId);
                SteamUGC.SubscribeItem(id);
                SteamUGC.DownloadItem(id, true);
            }

            downloadStatus.Clear();
            nextPoll = 0f;
            nextRequest = Time.realtimeSinceStartup + RequestInterval;
            SetStage(Stage.Downloading);
        }

        private void PollDownloads()
        {
            var now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollInterval;

            var request = now >= nextRequest;
            if (request)
                nextRequest = now + RequestInterval;

            const EItemState busy =
                EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending | EItemState.k_EItemStateNeedsUpdate;

            var allDone = true;
            foreach (var m in missing)
            {
                var id = new PublishedFileId_t(m.steamId);
                var state = (EItemState)SteamUGC.GetItemState(id);
                var subscribed = state.HasFlag(EItemState.k_EItemStateSubscribed);
                var done = subscribed && state.HasFlag(EItemState.k_EItemStateInstalled) && (state & busy) == 0;

                string status;
                if (done)
                    status = "Installed";
                else if (state.HasFlag(EItemState.k_EItemStateDownloading) &&
                         SteamUGC.GetItemDownloadInfo(id, out var bytesDone, out var bytesTotal) && bytesTotal > 0)
                    status = $"Downloading {(float)bytesDone / bytesTotal:P0}";
                else if ((state & (EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0)
                    status = "Queued";
                else if (!subscribed)
                    status = "Subscribing";
                else
                    status = "Waiting for Steam";

                downloadStatus[m.steamId] = status;

                if (done) continue;
                allDone = false;

                // Not moving: ask again in case Steam dropped the first request
                if (request && (state & busy) == 0)
                {
                    if (!subscribed)
                        SteamUGC.SubscribeItem(id);
                    SteamUGC.DownloadItem(id, true);
                }
            }

            if (!allDone) return;

            // Steam can report an item installed before the ItemInstalled callback that rebuilds
            // RimWorld's Workshop list has run, so rebuild that list here before the mod list
            WorkshopItems.RebuildItemsList();
            JoinData.ClearInstalledCache();
            ModLister.RebuildModList();

            if (missing.All(m => m.Installed))
            {
                Log.Message($"Multiplayer: Fix and restart downloaded {string.Join(", ", missing.Select(Row))}, restarting");
                Restart();
            }
            else
            {
                // Not listed by RimWorld yet; the mod list rebuild is heavy, so wait longer before the next try
                nextPoll = now + 2f;
            }
        }

        private void DrawModRows(Rect rect, Func<ModInfo, string> status)
        {
            var viewRect = new Rect(0, 0, rect.width - 16f, missing.Count * RowHeight);
            Widgets.BeginScrollView(rect, ref listScroll, viewRect);
            {
                for (int i = 0; i < missing.Count; i++)
                {
                    var m = missing[i];
                    var row = new Rect(0, i * RowHeight, viewRect.width, RowHeight);
                    if (i % 2 == 0)
                        Widgets.DrawAltRect(row);
                    TooltipHandler.TipRegion(row, Row(m).ToString());

                    var statusStr = status(m);

                    using (MpStyle.Set(TextAnchor.MiddleLeft))
                    using (MpStyle.Set(WordWrap.NoWrap))
                    {
                        var statusWidth = Text.CalcSize(statusStr).x + 10f;
                        Widgets.Label(row.MinX(5f).Width(row.width - statusWidth - 5f), m.name.Truncate(row.width - statusWidth - 5f));
                        using (MpStyle.Set(new Color(0.7f, 0.7f, 0.7f)))
                            Widgets.Label(row.MinX(row.xMax - statusWidth), statusStr);
                    }
                }
            }
            Widgets.EndScrollView();
        }

        private void DrawDownloading(Rect inRect)
        {
            PollDownloads();

            const float headerHeight = 50f;
            const float btnHeight = 35f;

            Widgets.Label(inRect.Height(headerHeight),
                "Downloading the host's mods from the Steam Workshop. RimWorld restarts and rejoins when they are installed.");

            var list = inRect.MinY(inRect.y + headerHeight).MaxY(inRect.yMax - btnHeight - 10f);
            DrawModRows(list, m => downloadStatus.TryGetValue(m.steamId, out var s) ? s : "Subscribing");

            var cancel = new Rect(0, 0, 110f, btnHeight).CenterOn(inRect.BottomPartPixels(btnHeight));
            if (Widgets.ButtonText(cancel, "CancelButton".Translate()))
            {
                Log.Message("Multiplayer: Fix and restart download cancelled");
                SetStage(Stage.Choose);
            }
        }

        private void DrawCannotFix(Rect inRect)
        {
            const float headerHeight = 80f;
            const float btnHeight = 35f;

            var why = SteamManager.Initialized
                ? "These mods are not installed here and not all of them are on the Steam Workshop."
                : "These mods are not installed here and Steam is not running, so they can't be downloaded.";

            using (MpStyle.Set(new Color(1f, 0.6f, 0.6f)))
                Widgets.Label(inRect.Height(headerHeight),
                    why + " Get them from the host, install them and try again. Nothing was changed.");

            var list = inRect.MinY(inRect.y + headerHeight).MaxY(inRect.yMax - btnHeight - 10f);
            DrawModRows(list, m => m.CanSubscribe ? $"workshop {m.steamId}" : m.packageId);

            var back = new Rect(0, 0, 110f, btnHeight).CenterOn(inRect.BottomPartPixels(btnHeight));
            if (Widgets.ButtonText(back, "MpMismatchBack".Translate()))
                SetStage(Stage.Choose);
        }

        private void Restart()
        {
            if (applyModList)
            {
                ModsConfig.data.activeMods = data.remoteMods.Select(JoinData.ResolveInstalledId).ToList();
                ModsConfig.Save();
            }

            if (applyConfigs)
            {
                SyncConfigs.SaveConfigs(data.remoteModConfigs);
                SyncConfigs.MarkApplicableForChildProcess();
            }

            AutoJoinHandler.SetForChildProcess(connector);
            GenCommandLine.Restart();
        }
    }
}
