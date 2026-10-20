using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Logs every beard roll in a session with what decides it.
    ///
    /// In the 2026-10-19 session the host's and the joiner's join point saves had different beards on the same
    /// pawns (a trader caravan's men got beards on Stephen's game and none on Blake's) while the random states
    /// stayed equal, so the roll drew the same number from a different candidate list or different weights.
    /// Each line names the pawn, the random state before the roll, whether it can have a beard, and every candidate
    /// with its weight; diffing the two players' lines for one pawn shows the input that differs.
    /// </summary>
    [HarmonyPatch(typeof(PawnStyleItemChooser), nameof(PawnStyleItemChooser.RandomBeardFor))]
    static class StyleRolls
    {
        private const int MaxLogged = 300;
        private static int logged;

        static void Prefix(Pawn pawn, ref ulong __state) => __state = Rand.StateCompressed;

        static void Postfix(Pawn pawn, BeardDef __result, ulong __state)
        {
            if (Multiplayer.Client == null || pawn?.style == null || logged >= MaxLogged) return;
            logged++;

            var candidates = DefDatabase<BeardDef>.AllDefsListForReading
                .Where(b => PawnStyleItemChooser.WantsToUseStyle(pawn, b))
                .Select(b => $"{b.defName}={PawnStyleItemChooser.TotalStyleItemLikelihood(b, pawn):0.###}");

            MpLog.Log($"[beard] roll pawn={pawn.thingIDNumber} kind={pawn.kindDef?.defName} gender={pawn.gender} " +
                      $"stage={pawn.DevelopmentalStage} canWant={pawn.style.CanWantBeard} ideo={pawn.Ideo?.id} " +
                      $"rand={__state:X16}->{Rand.StateCompressed:X16} result={__result?.defName} | {string.Join(" ", candidates)}");
        }
    }
}
