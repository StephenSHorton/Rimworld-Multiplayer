using System;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Saving
{
    /// <summary>
    /// A joining player's game loaded on a worker thread (Root_Play queues the load asynchronously), while the
    /// host and every join point reload on the main thread. Load-time code that needs the main thread (materials,
    /// textures, Unity objects) then behaved differently on the joiner (Adaptive Storage's atlas materials came back
    /// null there). The Multiplayer load now runs on the main thread for everyone (upstream #854).
    /// </summary>
    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
        typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action))]
    static class JoinLoadOnMainThread
    {
        static void Prefix(string textKey, ref bool doAsynchronously)
        {
            if (doAsynchronously && LoadPatch.gameToLoad != null && textKey == "LoadingLongEvent")
                doAsynchronously = false;
        }
    }
}
