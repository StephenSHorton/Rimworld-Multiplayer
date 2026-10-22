using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Multiplayer.Client.Desyncs;
using Verse;

namespace Multiplayer.Client
{
    public abstract class StackTraceLogItem : IDisposable
    {
        public int tick;
        public int hash;

        public abstract string AdditionalInfo { get; }

        public abstract string StackTraceString { get; }

        public override string ToString()
        {
            return $"Tick:{tick} Hash:{hash} '{AdditionalInfo}'\n{StackTraceString}";
        }

        public virtual void Dispose()
        {
        }
    }

    public class StackTraceLogItemObj : StackTraceLogItem
    {
        public string info1;
        public string info2;

        public override string AdditionalInfo => $"{info1} {info2}";

        public override string StackTraceString => "";
    }

    public class StackTraceLogItemRaw : StackTraceLogItem
    {
        public const int MaxDepth = 32;
        public long[] raw = new long[MaxDepth];
        public int depth;

        public int ticksGame;
        public ulong rngState;
        public ThingDef thingDef;
        public int thingId;
        public string factionName;
        public string moreInfo;
        // What made the trace: Rand, UniqueIds, Spawn or DeSpawn (see DeferredStackTracing); shown first as [kind]
        public string kind;

        public override string AdditionalInfo => $"{(kind == null ? "" : $"[{kind}] ")}{ticksGame} {thingDef}{thingId} {factionName} {depth} {rngState} {moreInfo}";

        private static Dictionary<long, string> methodNameCache = new();

        public override string StackTraceString
        {
            get
            {
                var builder = new StringBuilder();
                for (int i = 0; i < depth; i++)
                {
                    var addr = raw[i];

                    if (!methodNameCache.TryGetValue(addr, out string method))
                        methodNameCache[addr] = method = Native.MethodNameFromAddr(raw[i], false);

                    builder.AppendLine(method != null ? SyncCoordinator.MethodNameWithIL(method) : "Null");
                }

                return builder.ToString();
            }
        }

        public static StackTraceLogItemRaw GetFromPool()
        {
            return SimplePool<StackTraceLogItemRaw>.Get();
        }

        public override void Dispose()
        {
            Clear();
            SimplePool<StackTraceLogItemRaw>.Return(this);
        }

        protected void Clear()
        {
            depth = 0;
            ticksGame = 0;
            rngState = 0;
            thingId = 0;
            thingDef = null;
            factionName = null;
            moreInfo = null;
            kind = null;
        }
    }

    // A trace from System.Diagnostics.StackTrace, for CoreCLR (RimWorld 1.7) where the Mono stack walk can't run
    public class StackTraceLogItemManaged : StackTraceLogItemRaw
    {
        public MethodBase[] methods = new MethodBase[MaxDepth];

        public override string StackTraceString
        {
            get
            {
                var builder = new StringBuilder();
                for (int i = 0; i < depth; i++)
                    builder.AppendLine(methods[i]?.FullDescription() ?? "Null");
                return builder.ToString();
            }
        }

        public new static StackTraceLogItemManaged GetFromPool()
        {
            return SimplePool<StackTraceLogItemManaged>.Get();
        }

        public override void Dispose()
        {
            Array.Clear(methods, 0, methods.Length);
            Clear();
            SimplePool<StackTraceLogItemManaged>.Return(this);
        }
    }
}
