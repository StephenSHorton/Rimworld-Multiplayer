using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Multiplayer.Client.Util;
using Multiplayer.Common.Desyncs;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// What a desync costs the player. The first one in a while resyncs on its own: a small overlay names the cause,
    /// the report is written, and the game reloads from the host's join point with no button to press. If the same
    /// thing desyncs again soon after, the game restarts in a fresh process and joins again by itself, since a cause
    /// that survives a reload in the same process is usually process state (a static cache). Only after that does
    /// the old blocking window come up. Debug freeze keeps the blocking window from the start, so nothing is
    /// reloaded before the state has been looked at.
    /// </summary>
    public static class DesyncRecovery
    {
        private const string EscalationVariable = "MultiplayerDesyncEscalation";
        private const float RepeatWindowSeconds = 15 * 60;

        // This process's desyncs: (realtime, signature)
        private static readonly List<(float time, string signature)> history = new();
        private static int inheritedEscalation = -1;

        /// <summary>Runs before an automatic resync reloads the game; mods clear their static caches here.</summary>
        public static event Action BeforeResync;

        public enum Step { Resync, RestartProcess, Block }

        public static Step Decide(string signature)
        {
            if (inheritedEscalation == -1)
            {
                // A restart for a repeated desync leaves the count for the new process
                inheritedEscalation = int.TryParse(Environment.GetEnvironmentVariable(EscalationVariable), out var e) ? e : 0;
                Environment.SetEnvironmentVariable(EscalationVariable, "");
            }

            var now = Time.realtimeSinceStartup;
            history.RemoveAll(h => now - h.time > RepeatWindowSeconds);
            history.Add((now, signature));

            int recent = history.Count + inheritedEscalation;
            if (Multiplayer.GameComp?.desyncDebugFreeze == true || recent >= 3) return Step.Block;
            if (recent == 2) return Step.RestartProcess;
            return Step.Resync;
        }

        public static void OnDesync(string message, SaveableDesyncInfo info)
        {
            var signature = FaultSignature.Normalize(message);
            var step = Decide(signature);
            MpLog.Warn($"Desync recovery: {step} after \"{message}\"");

            MpUI.ClearWindowStack();
            if (step == Step.Block)
            {
                Find.WindowStack.Add(new DesyncedWindow(message, info));
                return;
            }

            Find.WindowStack.Add(new DesyncOverlayWindow(message, info, step));
        }

        public static void Resync()
        {
            try
            {
                BeforeResync?.Invoke();
            }
            catch (Exception e)
            {
                MpLog.Error($"A mod's resync hook failed: {e}");
            }

            Rejoiner.DoRejoin();
        }

        public static void RestartProcess()
        {
            var connector = Multiplayer.session?.connector;
            if (connector == null)
            {
                Resync();
                return;
            }

            Environment.SetEnvironmentVariable(EscalationVariable, (history.Count + Math.Max(0, inheritedEscalation)).ToString());
            AutoJoinHandler.SetForChildProcess(connector);
            GenCommandLine.Restart();
        }
    }

    /// <summary>
    /// The desync overlay: no buttons, doesn't block the screen. It waits for the report (and the host's part of it,
    /// at most a few seconds), then resyncs or restarts.
    /// </summary>
    public class DesyncOverlayWindow : Window
    {
        private const float MaxWaitForHost = 5f;

        private readonly string message;
        private readonly SaveableDesyncInfo info;
        private readonly DesyncRecovery.Step step;
        private readonly float openedAt;
        [CanBeNull] private SaveableDesyncInfo.HostInfo hostInfo;
        private bool done;

        public override Vector2 InitialSize => new(560, 80);

        public DesyncOverlayWindow(string message, SaveableDesyncInfo info, DesyncRecovery.Step step)
        {
            this.message = message;
            this.info = info;
            this.step = step;
            openedAt = Time.realtimeSinceStartup;

            layer = WindowLayer.Super;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            doCloseX = false;
        }

        public override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect((UI.screenWidth - InitialSize.x) / 2f, 40f, InitialSize.x, InitialSize.y);
        }

        public void HandleHostDesyncInfo(SaveableDesyncInfo.HostInfo hostInfo) => this.hostInfo = hostInfo;

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            var what = step == DesyncRecovery.Step.RestartProcess
                ? "Out of sync again. Restarting RimWorld and rejoining"
                : "Out of sync with the host. Re-syncing";
            Widgets.Label(inRect.TopHalf(), what + MpUI.FixedEllipsis());
            Text.Font = GameFont.Tiny;
            Widgets.Label(inRect.BottomHalf(), message.Length > 110 ? message.Substring(0, 110) + "..." : message);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        public override void WindowUpdate()
        {
            if (done) return;

            var waited = Time.realtimeSinceStartup - openedAt;
            if ((hostInfo == null && waited < MaxWaitForHost) || !info.ReadyToSave) return;

            done = true;
            info.Save(hostInfo);

            if (step == DesyncRecovery.Step.RestartProcess)
                DesyncRecovery.RestartProcess();
            else
                DesyncRecovery.Resync();
        }
    }
}
