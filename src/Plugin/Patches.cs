using System.Collections.Generic;
using HarmonyLib;

namespace DigimonNetplay
{
    /// <summary>Fighters read their buttons through these; online players use the virtual controls "N0".."N3".</summary>
    [HarmonyPatch(typeof(Controller), "GetAxis")]
    internal static class ControllerGetAxisPatch
    {
        private static bool Prefix(string nameWithControl, ref float __result)
        {
            if (!MatchSession.TryAnswerAxis(nameWithControl, out float v)) return true;
            __result = v;
            return false;
        }
    }

    [HarmonyPatch(typeof(Controller), "GetButton")]
    internal static class ControllerGetButtonPatch
    {
        private static bool Prefix(Controller __instance, string nameWithControl, bool isGetButtonDown, bool isGetButtonUp, ref bool __result)
        {
            if (!MatchSession.TryAnswerButton(nameWithControl, isGetButtonDown, isGetButtonUp, out bool v)) return true;
            // Same rule as the original: specials are ignored while the fighter is barred from using them.
            if (!__instance.CAN_DO_SPECIALS && nameWithControl.StartsWith("Special")) v = false;
            __result = v;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputController), nameof(InputController.GetAxis))]
    internal static class InputControllerGetAxisPatch
    {
        private static bool Prefix(string nameWithControl, ref float __result)
        {
            if (!MatchSession.TryAnswerAxis(nameWithControl, out float v)) return true;
            __result = v;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputController), nameof(InputController.GetButton))]
    internal static class InputControllerGetButtonPatch
    {
        private static bool Prefix(string nameWithControl, bool isGetButtonDown, bool isGetButtonUp, ref bool __result)
        {
            if (!MatchSession.TryAnswerButton(nameWithControl, isGetButtonDown, isGetButtonUp, out bool v)) return true;
            __result = v;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputController), nameof(InputController.GetIsControlTypeAvailable))]
    internal static class ControlAvailablePatch
    {
        private static bool Prefix(string controlType, ref bool __result)
        {
            if (controlType == null || controlType.Length != 2 || controlType[0] != 'N') return true;
            __result = true;
            return false;
        }
    }

    /// <summary>During an online match, stage loads (first round and every new round) are coordinated across peers.</summary>
    [HarmonyPatch(typeof(StageLoader), nameof(StageLoader.LoadingStage))]
    internal static class StageLoaderPatch
    {
        private static bool Prefix(GameData.STAGES stageNum)
        {
            var s = MatchSession.Current;
            if (s == null) return true;
            s.LoadStage(stageNum);
            return false;
        }
    }

    /// <summary>The host's battle settings are borrowed for the match; never write them into this player's save.</summary>
    [HarmonyPatch(typeof(Savegame), nameof(Savegame.SaveToFile))]
    internal static class SavegamePatch
    {
        private static void Prefix(out Dictionary<string, string> __state)
        {
            __state = null;
            if (MatchSession.Current == null || GameBridge.SavedSettings == null) return;
            __state = GameBridge.CaptureSettings();
            GameBridge.ApplySettings(GameBridge.SavedSettings);
        }

        // SaveToFile serialises the data before its first await, so by the time it returns the copy is taken.
        private static void Postfix(Dictionary<string, string> __state)
        {
            if (__state != null) GameBridge.ApplySettings(__state);
        }
    }
}
