using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;

namespace Multiplayer.Client.Patches
{
    [HarmonyPatch(typeof(WindManager), nameof(WindManager.BaseWindSpeedAt))]
    static class BaseWindSpeetGetHashCodeChange
    {
        static readonly MethodInfo OriginalGetHash = AccessTools.Method(typeof(object), nameof(GetHashCode));

        static readonly MethodInfo ChangedGetHash = AccessTools.Method(typeof(BaseWindSpeetGetHashCodeChange), nameof(GetMapHash));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var ci in insts)
            {
                if (ci.Calls(OriginalGetHash))
                    yield return new CodeInstruction(OpCodes.Call, ChangedGetHash);
                else
                    yield return ci;
            }
        }

        static int GetMapHash(Map map)
        {
            if (Multiplayer.Client == null)
                return map.GetHashCode();

            return map.Tile;
        }
    }

    [HarmonyPatch(typeof(GlowGrid), MethodType.Constructor, typeof(Map))]
    static class GlowGridCtorPatch
    {
        private static AccessTools.FieldRef<GlowGrid, HashSet<CompGlower>> litGlowers =
            AccessTools.FieldRefAccess<GlowGrid, HashSet<CompGlower>>(nameof(GlowGrid.litGlowers));

        static void Postfix(GlowGrid __instance)
        {
            litGlowers(__instance) = new HashSet<CompGlower>(new CompGlowerEquality());
        }

        class CompGlowerEquality : IEqualityComparer<CompGlower>
        {
            public bool Equals(CompGlower x, CompGlower y) => x == y;
            public int GetHashCode(CompGlower obj) => obj.parent.thingIDNumber;
        }
    }

    [HarmonyPatch]
    static class PatchTargetInfoHashCodes
    {
        static MethodInfo Combine = AccessTools.Method(typeof(Gen), nameof(Gen.HashCombine)).MakeGenericMethod(typeof(Map));

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GlobalTargetInfo), nameof(GlobalTargetInfo.GetHashCode));
            yield return AccessTools.Method(typeof(TargetInfo), nameof(TargetInfo.GetHashCode));
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand as MethodInfo == Combine)
                    inst.operand = AccessTools.Method(typeof(PatchTargetInfoHashCodes), nameof(CombineHashes));

                yield return inst;
            }
        }

        static int CombineHashes(int seed, Map map) => Gen.HashCombineInt(seed, map?.uniqueID ?? -1);
    }

    // todo does this cause issues?
    [HarmonyPatch(typeof(Tradeable), nameof(Tradeable.GetHashCode))]
    static class TradeableHashCode
    {
        static bool Prefix() => false;

        static void Postfix(Tradeable __instance, ref int __result)
        {
            __result = RuntimeHelpers.GetHashCode(__instance);
        }
    }

    // System.HashCode is seeded per process, so game code that seeds Rand with HashCode.Combine would roll
    // differently for each player. Only those call sites are rewritten. Patching HashCode.Combine itself is not
    // safe on CoreCLR: callers that were precompiled with it inlined (ValueTuple.GetHashCode in CoreLib) or that
    // the JIT inlined it into keep the original, so one key hashed two ways and tuple-keyed dictionaries lost
    // entries. Save loading's LoadIDsWantedBank is one, which broke thousands of references on every
    // Multiplayer load (designation targets, jobs, owned beds) and left pawns standing at their work.
    [HarmonyPatch]
    static class DeterministicHashCodeSeeds
    {
        private static readonly Type HashCodeType = typeof(HashCode);

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Map), nameof(Map.NextGenSeed));
            yield return AccessTools.PropertyGetter(typeof(Gravship), nameof(Gravship.ConstantRandSeed));
            yield return AccessTools.Method(typeof(LayoutWorker), nameof(LayoutWorker.FillAllRooms));
            // Raiders decide which traps they know of with Rand.ValueAsync(HashCode.Combine(faction or lord, trap id)):
            // the 2026-10-22 map 1 desyncs (wander and path choices around traps differing per player).
            yield return AccessTools.Method(typeof(Building_Trap), nameof(Building_Trap.KnowsOfTrap));
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand is MethodInfo { Name: nameof(HashCode.Combine) } method && method.DeclaringType == HashCodeType)
                {
                    var parameters = method.GetParameters();
                    if (parameters.All(p => p.ParameterType == typeof(int)))
                    {
                        var replacement = parameters.Length switch
                        {
                            2 => AccessTools.Method(typeof(DeterministicHashCodeSeeds), nameof(Combine2)),
                            3 => AccessTools.Method(typeof(DeterministicHashCodeSeeds), nameof(Combine3)),
                            _ => null
                        };

                        if (replacement != null)
                            inst.operand = replacement;
                    }
                }

                yield return inst;
            }
        }

        static int Combine2(int value1, int value2) =>
            Multiplayer.Client == null ? HashCode.Combine(value1, value2) : Gen.HashCombineInt(value1, value2);

        static int Combine3(int value1, int value2, int value3) =>
            Multiplayer.Client == null ? HashCode.Combine(value1, value2, value3) : DeterministicHash.HashCombineInt(value1, value2, value3);
    }
}
