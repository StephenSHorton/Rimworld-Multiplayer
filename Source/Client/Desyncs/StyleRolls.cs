using System.Collections.Generic;
using System.Globalization;
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
    /// 2026-10-21: the same random state, and NoBeard weighed 1200 on the host's game and 600 on the joiner's for
    /// mercenary, town and villager kinds. Each line names the pawn, the random state before the roll, whether it
    /// can have a beard, every candidate with its weight and the factors of that weight (gender x identity), the
    /// kind's style tags and NoBeard's tags, all computed before the roll; diffing the two players' lines for one
    /// pawn shows the input that differs. The fingerprint's "Style tags" rows compare the defs themselves.
    /// </summary>
    [HarmonyPatch(typeof(PawnStyleItemChooser), nameof(PawnStyleItemChooser.RandomBeardFor))]
    static class StyleRolls
    {
        private const int MaxLogged = 300;
        private static int logged;

        private static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        public static string Tags(List<StyleItemTagWeighted> tags) =>
            tags == null ? "none" : string.Join(",", tags.Select(t => $"{t.Tag}:{F(t.baseWeight)}x{F(t.weightFactor)}"));

        static void Prefix(Pawn pawn, ref (ulong rand, string inputs) __state)
        {
            // The random state first: the breakdown below draws no random numbers, and must not move it
            __state = (Rand.StateCompressed, null);
            if (Multiplayer.Client == null || pawn?.style == null || logged >= MaxLogged) return;

            try
            {
                var ideo = pawn.Ideo;
                var candidates = pawn.style.CanWantBeard
                    ? DefDatabase<BeardDef>.AllDefsListForReading
                        .Where(b => PawnStyleItemChooser.WantsToUseStyle(pawn, b))
                        .Select(b =>
                        {
                            var g = PawnStyleItemChooser.FrequencyFromGender(b, pawn);
                            var i = PawnStyleItemChooser.FrequencyFromPawnIdentity(pawn, b);
                            return $"{b.defName}={F(g * i)}({F(g)}x{F(i)})";
                        })
                    : Enumerable.Empty<string>();

                var noBeard = BeardDefOf.NoBeard;
                var identity = !pawn.kindDef.styleItemTags.NullOrEmpty() ? "kindTags"
                    : !ModsConfig.IdeologyActive || Find.IdeoManager.classicMode ? "culture"
                    : ideo == null ? "none" : "ideo";
                var ideoPart = ideo == null ? "ideo=none"
                    : $"ideo={ideo.id} classic={Find.IdeoManager.classicMode} noBeardFreq={ideo.style.GetFrequency(noBeard)} noBeardGender={ideo.style.GetGender(noBeard)}";

                __state.inputs =
                    $"identity={identity} kindTags=[{Tags(pawn.kindDef.styleItemTags)}] noBeardTags=[{string.Join(",", noBeard.styleTags ?? new List<string>())}] " +
                    $"{ideoPart} faction={pawn.HomeFaction?.def?.defName} | {string.Join(" ", candidates)}";
            }
            catch (System.Exception e)
            {
                __state.inputs = $"breakdown failed: {e.GetType().Name}: {e.Message}";
            }
        }

        static void Postfix(Pawn pawn, BeardDef __result, (ulong rand, string inputs) __state)
        {
            if (__state.inputs == null) return;
            logged++;

            MpLog.Log($"[beard] roll pawn={pawn.thingIDNumber} kind={pawn.kindDef?.defName} gender={pawn.gender} " +
                      $"stage={pawn.DevelopmentalStage} canWant={pawn.style.CanWantBeard} " +
                      $"rand={__state.rand:X16}->{Rand.StateCompressed:X16} result={__result?.defName} {__state.inputs}");
        }
    }
}
