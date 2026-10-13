using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonoMod.Utils;
using Multiplayer.API;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace MultiplayerLoader;

// Syncs the role assignment methods of PawnRoleSelectionWidgetBase<RoleType> for the ritual and psychic ritual widgets.
//
// RimWorld 1.7 runs on CoreCLR, where every reference-type instantiation of a generic class shares one compiled
// method body. A Harmony patch on PawnRoleSelectionWidgetBase<RitualRole>.TryAssign is a patch on that shared body,
// so its prefix runs for both widgets, but the replacement Harmony builds is specific to RitualRole and throws an
// InvalidCastException when the original runs for a PsychicRitualRoleDef widget. So the prefixes here always skip
// the patched body: they sync the call when it should be synced, and otherwise run an unpatched copy of the original
// for the instance's own instantiation. The prefixes take the generic arguments as object, never __args, because
// Harmony writes __args back with a cast to the RitualRole types.
//
// Before 1.7 this was done with a Prepatcher prefix, which does not run on CoreCLR.
public static class SyncRituals
{
    private static readonly MethodInfo syncMethod = AccessTools.Method("Multiplayer.Client.Sync:Method");

    // (widget type, method name) -> synced method; only exact widget types are synced
    private static readonly Dictionary<(Type, string), ISyncMethod> syncMethods = new();

    // (closed PawnRoleSelectionWidgetBase<> type, method name) -> unpatched copy taking the instance first
    private static readonly Dictionary<(Type, string), MethodInfo> originals = new();

    public static void Init(Harmony harmony)
    {
        void Register(string method, Type widgetType, Action<ISyncMethod> postProcess = null)
        {
            // Sync.Method doesn't apply a Harmony patch and doesn't require the DeclaringType to be the target type
            var newSyncMethod = (ISyncMethod)syncMethod.Invoke(null, [widgetType, method, null]);
            postProcess?.Invoke(newSyncMethod);
            syncMethods[(widgetType, method)] = newSyncMethod;
        }

        void Patch(string method, string prefix)
        {
            // Patching one instantiation patches the body every reference-type instantiation shares
            harmony.Patch(
                AccessTools.DeclaredMethod(typeof(PawnRoleSelectionWidgetBase<RitualRole>), method),
                prefix: new HarmonyMethod(typeof(SyncRituals), prefix));
        }

        var ritualRolesSerializer = Serializer.New<IEnumerable<RitualRole>, (PawnRitualRoleSelectionWidget roleSelectionWidget, string Key)>(
            RitualRoleWriter<PawnRitualRoleSelectionWidget, RitualRole>,
            RitualRoleReader<PawnRitualRoleSelectionWidget, RitualRole>
        );
        var psychicRitualRolesSerializer = Serializer.New<IEnumerable<PsychicRitualRoleDef>, (PawnPsychicRitualRoleSelectionWidget roleSelectionWidget, string Key)>(
            RitualRoleWriter<PawnPsychicRitualRoleSelectionWidget, PsychicRitualRoleDef>,
            RitualRoleReader<PawnPsychicRitualRoleSelectionWidget, PsychicRitualRoleDef>
        );

        const string tryAssignReplace = nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssignReplace);
        Register(tryAssignReplace, typeof(PawnRitualRoleSelectionWidget), m => m.TransformArgument(1, ritualRolesSerializer));
        Register(tryAssignReplace, typeof(PawnPsychicRitualRoleSelectionWidget), m => m.TransformArgument(1, psychicRitualRolesSerializer));
        Patch(tryAssignReplace, nameof(TryAssignReplacePrefix));

        const string tryAssignAnyRole = nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssignAnyRole);
        Register(tryAssignAnyRole, typeof(PawnRitualRoleSelectionWidget));
        Register(tryAssignAnyRole, typeof(PawnPsychicRitualRoleSelectionWidget));
        Patch(tryAssignAnyRole, nameof(TryAssignAnyRolePrefix));

        const string tryAssign = nameof(PawnRoleSelectionWidgetBase<RitualRole>.TryAssign);
        Register(tryAssign, typeof(PawnRitualRoleSelectionWidget), m => m.TransformArgument(1, ritualRolesSerializer));
        Register(tryAssign, typeof(PawnPsychicRitualRoleSelectionWidget), m => m.TransformArgument(1, psychicRitualRolesSerializer));
        Patch(tryAssign, nameof(TryAssignPrefix));

        // Ordinal 7: roles right click delegate (try assign spectate)
        // Ordinal 2: not participating left click delegate (try assign any role or spectate)
        foreach (var ordinal in new[] { 7, 2 })
        {
            var lambda = MpMethodUtil.GetLambda(
                typeof(PawnRoleSelectionWidgetBase<ILordJobRole>),
                nameof(PawnRoleSelectionWidgetBase<RitualRole>.DrawPawnListInternal),
                lambdaOrdinal: ordinal).Name;
            Register(lambda, typeof(PawnRitualRoleSelectionWidget));
            Register(lambda, typeof(PawnPsychicRitualRoleSelectionWidget));
            Patch(lambda, nameof(PawnLambdaPrefix));
        }
    }

    private static bool TryAssignReplacePrefix(object __instance, MethodBase __originalMethod,
        Pawn pawn, object roleGroup, Pawn replacing, ref bool __result)
    {
        __result = Run(__instance, __originalMethod, [pawn, roleGroup, replacing]) is true;
        return false;
    }

    private static bool TryAssignAnyRolePrefix(object __instance, MethodBase __originalMethod, Pawn p, ref bool __result)
    {
        __result = Run(__instance, __originalMethod, [p]) is true;
        return false;
    }

    private static bool TryAssignPrefix(object __instance, MethodBase __originalMethod,
        Pawn pawn, object roleGroup, bool sendMessage, Pawn insertBefore, bool doSound, bool insertLast, ref bool __result)
    {
        __result = Run(__instance, __originalMethod, [pawn, roleGroup, sendMessage, insertBefore, doSound, insertLast]) is true;
        return false;
    }

    private static bool PawnLambdaPrefix(object __instance, MethodBase __originalMethod, Pawn p)
    {
        Run(__instance, __originalMethod, [p]);
        return false;
    }

    // Returns the original's return value, or null when the call was synced (a synced bool method returns false,
    // as it did under Prepatcher)
    private static object Run(object instance, MethodBase patched, object[] args)
    {
        if (syncMethods.TryGetValue((instance.GetType(), patched.Name), out var sync) && sync.DoSync(instance, args))
            return null;

        return Original(instance.GetType(), patched)
            .Invoke(null, BindingFlags.DoNotWrapExceptions, null, [instance, ..args], null);
    }

    private static MethodInfo Original(Type instanceType, MethodBase patched)
    {
        var baseType = instanceType;
        while (!baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(PawnRoleSelectionWidgetBase<>))
            baseType = baseType.BaseType;

        lock (originals)
        {
            if (originals.TryGetValue((baseType, patched.Name), out var copy))
                return copy;

            // A closed generic type's methods keep the generic definition's metadata token
            var closed = AccessTools.GetDeclaredMethods(baseType)
                .First(m => m.MetadataToken == patched.MetadataToken && m.Module == patched.Module);

            // DynamicMethodDefinition reads the IL from metadata, so the copy is the unpatched original
            copy = new DynamicMethodDefinition(closed).Generate();
            originals[(baseType, patched.Name)] = copy;
            return copy;
        }
    }

    private static (WidgetType roleSelectionWidget, string Key) RitualRoleWriter<WidgetType, RoleType>(IEnumerable<RoleType> roles, object target, object[] _)
        where WidgetType : PawnRoleSelectionWidgetBase<RoleType>
        where RoleType : class, ILordJobRole
    {
        var roleSelectionWidget = (WidgetType)target;
        return (roleSelectionWidget, roleSelectionWidget.assignments.RoleGroups().FirstOrDefault(g => g.SequenceEqual(roles))?.Key);
    }

    private static IEnumerable<RoleType> RitualRoleReader<WidgetType, RoleType>((WidgetType roleSelectionWidget, string Key) data)
        where WidgetType : PawnRoleSelectionWidgetBase<RoleType>
        where RoleType : class, ILordJobRole
    {
        return data.roleSelectionWidget.assignments.RoleGroups().FirstOrDefault(g => g.Key == data.Key);
    }
}
