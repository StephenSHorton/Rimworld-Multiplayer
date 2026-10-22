using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client.Patches
{
    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
        typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action))]
    static class MarkLongEvents
    {
        private static MethodInfo MarkerMethod = AccessTools.Method(typeof(MarkLongEvents), nameof(Marker));

        static void Prefix(ref Action action, string textKey)
        {
            if (Multiplayer.Client is { State: ConnectionStateEnum.ClientPlaying } && (Multiplayer.Ticking || Multiplayer.ExecutingCmds || textKey == "MpSaving"))
            {
                action += Marker;
            }
        }

        private static void Marker()
        {
        }

        public static bool IsTickMarked(Action action)
        {
            return action?.GetInvocationList()?.Any(d => d.Method == MarkerMethod) ?? false;
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsUpdate))]
    static class NewLongEvent
    {
        public static bool currentEventWasMarked;

        /// <summary>
        /// Whether the current long event is a synchronous one with the standard window that hasn't run yet. Such
        /// an event waits one drawn frame before it runs (ShouldWaitUntilDisplayed), and in that frame the whole
        /// interface is drawn and takes input as in any other frame (Root.OnGUI only stops for ShouldWaitForEvent).
        /// Multiplayer.InInterface used to be false for that frame because currentEvent was set, so every interface
        /// guard was off: the join point's frame let the melee attack gizmo store a melee verb on one game's drafted
        /// pawns (2026-10-21), and a click in it would have run unsynced. False while LongEventsUpdate runs: a
        /// synchronous event runs to its end inside it, so one still current after it hasn't run.
        /// </summary>
        public static bool pendingWithInterface;

        [HarmonyPriority(Priority.First)]
        static void Prefix(ref bool __state)
        {
            pendingWithInterface = false;
            __state = LongEventHandler.currentEvent == null;
            currentEventWasMarked = MarkLongEvents.IsTickMarked(LongEventHandler.currentEvent?.eventAction);
        }

        [HarmonyPriority(Priority.Last)]
        static void Postfix(bool __state)
        {
            currentEventWasMarked = false;
            pendingWithInterface = LongEventHandler.currentEvent is { UseStandardWindow: true };

            if (Multiplayer.Client == null) return;

            if (__state && MarkLongEvents.IsTickMarked(LongEventHandler.currentEvent?.eventAction))
                Multiplayer.Client.Send(ClientFreezePacket.Freeze());
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteToExecuteWhenFinished))]
    static class LongEventEnd
    {
        static void Postfix()
        {
            if (Multiplayer.Client != null && NewLongEvent.currentEventWasMarked)
                Multiplayer.Client.Send(ClientFreezePacket.Unfreeze());
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent), [typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action)])]
    static class LongEventAlwaysSync
    {
        static void Prefix(ref bool doAsynchronously, ref Action action, Action callback)
        {
            if (Multiplayer.ExecutingCmds)
            {
                doAsynchronously = false;

                // Callback is only called in asynchronous long events and will be skipped in
                // a synchronous ones. Make sure they are called after the actual action.
                if (callback != null)
                    action += callback;
            }
        }
    }

    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.UpdateCurrentAsynchronousEvent))]
    public static class PatchUpdateCurrentAsynchronousEventCleanUpOrder
    {
        readonly static MethodInfo OrginalMethod = AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteToExecuteWhenFinished));
        readonly static MethodInfo ReplacementMethod = AccessTools.Method(typeof(PatchUpdateCurrentAsynchronousEventCleanUpOrder), nameof(ReorderCleanupCode));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(OrginalMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, ReplacementMethod);
                    yield return new CodeInstruction(OpCodes.Ret);
                }
                else
                    yield return instruction;
            }
        }

        public static void ReorderCleanupCode()
        {
            Action callback = LongEventHandler.currentEvent.callback;

            LongEventHandler.currentEvent = null;
            LongEventHandler.eventThread = null;
            LongEventHandler.levelLoadOp = null;

            LongEventHandler.ExecuteToExecuteWhenFinished();

            if (callback != null)
                callback();
        }
    }
}
