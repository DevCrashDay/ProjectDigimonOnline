using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace DigimonNetplay
{
    /// <summary>
    /// Clock used by the game's scripts during a match instead of wall-clock based Unity timers, so every
    /// peer sees exactly the same numbers on the same frame.
    /// </summary>
    public static class NetClock
    {
        public static bool Active;
        private static float _time = 100f, _real = 100f, _unscaledDelta = MatchSession.FrameTime;

        internal static void Reset() { Active = false; _time = 100f; _real = 100f; _unscaledDelta = MatchSession.FrameTime; }
        internal static void BeginSegment() { Active = true; }

        internal static void Step(float scaledDelta, float unscaledDelta)
        {
            _time += scaledDelta;
            _real += unscaledDelta;
            _unscaledDelta = unscaledDelta;
        }

        public static float GetTime() => Active ? _time : Time.time;
        public static float GetRealtimeSinceStartup() => Active ? _real : Time.realtimeSinceStartup;
        public static float GetUnscaledTime() => Active ? _real : Time.unscaledTime;
        public static float GetUnscaledDeltaTime() => Active ? _unscaledDelta : Time.unscaledDeltaTime;
    }

    /// <summary>Rewrites game methods that read wall-clock time so they read <see cref="NetClock"/> instead.</summary>
    internal static class TimePatches
    {
        private static readonly Dictionary<MethodInfo, MethodInfo> Swaps = new Dictionary<MethodInfo, MethodInfo>
        {
            { AccessTools.PropertyGetter(typeof(Time), nameof(Time.time)), AccessTools.Method(typeof(NetClock), nameof(NetClock.GetTime)) },
            { AccessTools.PropertyGetter(typeof(Time), nameof(Time.realtimeSinceStartup)), AccessTools.Method(typeof(NetClock), nameof(NetClock.GetRealtimeSinceStartup)) },
            { AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledTime)), AccessTools.Method(typeof(NetClock), nameof(NetClock.GetUnscaledTime)) },
            { AccessTools.PropertyGetter(typeof(Time), nameof(Time.unscaledDeltaTime)), AccessTools.Method(typeof(NetClock), nameof(NetClock.GetUnscaledDeltaTime)) },
        };

        // Menus, saving and pure visuals may keep real time.
        private static bool Excluded(string typeName) =>
            typeName.StartsWith("Menu_") || typeName == "Savegame" || typeName.StartsWith("SavingSpinner") || typeName == "StageLoader" ||
            typeName == "SoundManager" || typeName.Contains("UVAnimatedTexture") || typeName == "UnscaledTimeParticleSystem";

        public static void Apply(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(TimePatches), nameof(Transpiler));
            int patched = 0;
            foreach (var method in FindCallers())
            {
                try { harmony.Patch(method, transpiler: transpiler); patched++; }
                catch (Exception e) { Plugin.Log.LogWarning("Clock patch skipped for " + method.DeclaringType + "." + method.Name + ": " + e.Message); }
            }
            Plugin.Log.LogInfo("Clock patches applied to " + patched + " game methods.");
        }

        /// <summary>Scans Assembly-CSharp's IL with Cecil for methods calling the timers we replace.</summary>
        private static IEnumerable<MethodBase> FindCallers()
        {
            var asm = typeof(GameData).Assembly;
            var names = new HashSet<string>(Swaps.Keys.Select(m => m.Name));
            var found = new List<MethodBase>();
            using (var def = AssemblyDefinition.ReadAssembly(asm.Location))
            {
                var module = asm.ManifestModule;
                foreach (var type in def.MainModule.GetTypes())
                {
                    var outer = type;
                    while (outer.DeclaringType != null) outer = outer.DeclaringType;
                    if (Excluded(outer.Name)) continue;
                    foreach (var m in type.Methods)
                    {
                        if (!m.HasBody || m.HasGenericParameters || type.HasGenericParameters) continue;
                        bool hit = false;
                        foreach (var ins in m.Body.Instructions)
                        {
                            if ((ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) && ins.Operand is MethodReference mr &&
                                mr.DeclaringType.FullName == "UnityEngine.Time" && names.Contains(mr.Name))
                            { hit = true; break; }
                        }
                        if (!hit) continue;
                        try
                        {
                            var mb = module.ResolveMethod(m.MetadataToken.ToInt32());
                            if (mb != null) found.Add(mb);
                        }
                        catch (Exception) { }
                    }
                }
            }
            return found;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand is MethodInfo mi && Swaps.TryGetValue(mi, out var repl))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = repl;
                }
                yield return ins;
            }
        }
    }

    /// <summary>
    /// Unity runs FixedUpdate a varying number of times per frame depending on real elapsed time. During a match
    /// the game's own FixedUpdate methods are suppressed and run exactly once per frame from the gate, followed by
    /// exactly one physics step.
    /// </summary>
    internal static class FixedStepDriver
    {
        public static bool Suppress;
        private static bool _running;
        private static readonly List<Type> Types = new List<Type>();
        private static readonly Dictionary<Type, MethodInfo> Resolved = new Dictionary<Type, MethodInfo>();
        private static readonly List<MonoBehaviour> Batch = new List<MonoBehaviour>();

        public static void Apply(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(FixedStepDriver), nameof(Prefix));
            foreach (var t in AccessTools.GetTypesFromAssembly(typeof(GameData).Assembly))
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(t) || t.IsGenericTypeDefinition) continue;
                var m = t.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m == null || m.IsAbstract) continue;
                try
                {
                    harmony.Patch(m, prefix: prefix);
                    Types.Add(t);
                }
                catch (Exception e) { Plugin.Log.LogWarning("FixedUpdate patch skipped for " + t + ": " + e.Message); }
            }
            Plugin.Log.LogInfo("Fixed-step control installed on " + Types.Count + " scripts.");
        }

        private static bool Prefix() => !Suppress || _running;

        private static MethodInfo MostDerivedFixedUpdate(Type t)
        {
            if (Resolved.TryGetValue(t, out var m)) return m;
            for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
            {
                m = c.GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null) break;
            }
            Resolved[t] = m;
            return m;
        }

        public static void RunOneStep(float dt)
        {
            if (!Suppress) return;
            _running = true;
            try
            {
                Batch.Clear();
                foreach (var t in Types)
                    foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType(t))
                        if (b != null && b.isActiveAndEnabled && MostDerivedFixedUpdate(b.GetType())?.DeclaringType == t) Batch.Add(b);
                // Creation order is the same on every peer; instance ids follow it.
                Batch.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                foreach (var b in Batch)
                {
                    if (b == null || !b.isActiveAndEnabled) continue;
                    try { MostDerivedFixedUpdate(b.GetType()).Invoke(b, null); }
                    catch (TargetInvocationException e) { Debug.LogException(e.InnerException ?? e); }
                }
                if (dt > 0f) Physics.Simulate(dt);
            }
            finally { _running = false; }
        }
    }

    /// <summary>Hooks the start of Unity's Update phase so the match can hold a frame until everyone's input is in.</summary>
    internal static class FrameGate
    {
        private struct NetplayGate { }

        /// <summary>Time.frameCount of the last tick, so the manager can notice if something rebuilt the player loop.</summary>
        public static int LastTickFrame = -1;

        public static void Install()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var systems = root.subSystemList;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(Update)) continue;
                var list = systems[i].subSystemList.ToList();
                if (list.Any(s => s.type == typeof(NetplayGate))) return;
                list.Insert(0, new PlayerLoopSystem { type = typeof(NetplayGate), updateDelegate = Tick });
                systems[i].subSystemList = list.ToArray();
                root.subSystemList = systems;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
            Plugin.Log.LogError("Could not find Unity's Update phase; online play will not work.");
        }

        private static void Tick()
        {
            LastTickFrame = Time.frameCount;
            try
            {
                NetplayManager.Instance?.PumpNetwork();
                MatchSession.Current?.OnFrame();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Netplay frame error: " + e);
                MatchSession.Current?.End("Netplay error: " + e.Message);
            }
        }
    }
}
