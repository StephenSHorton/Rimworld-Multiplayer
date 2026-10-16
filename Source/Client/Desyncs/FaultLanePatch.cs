using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Feeds errors logged while simulating into the sync opinion's fault lane. Ticks and commands catch their
    /// exceptions and carry on, so an exception on one machine used to fork the game without a word; now both
    /// games list their errors and the comparison names the one only one side logged.
    /// </summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
    static class FaultLanePatch
    {
        [ThreadStatic] private static bool recording;

        static void Prefix(string text)
        {
            if (recording || Multiplayer.Client == null || Multiplayer.game == null) return;
            if (!UnityData.IsInMainThread) return;
            if (!Multiplayer.Ticking && !Multiplayer.ExecutingCmds) return;

            recording = true;
            try
            {
                // MpLog puts the local username in front of its messages
                var username = Multiplayer.username;
                if (text != null && !username.NullOrEmpty())
                    text = text.Replace(username, "<player>");

                Multiplayer.game.sync.TryAddFault(Multiplayer.MapContext?.uniqueID ?? -1, text ?? "");
            }
            finally
            {
                recording = false;
            }
        }
    }
}
