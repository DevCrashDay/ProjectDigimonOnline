using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DigimonNetplay
{
    /// <summary>
    /// Adds an ONLINE entry to the game's main menu, under VS COMPUTER. The game's own entries keep their option
    /// numbers 0..6 (its LoadLevel switch depends on them); ONLINE is option 7 and only its place on screen and in the
    /// up/down order puts it in the middle.
    /// </summary>
    [HarmonyPatch(typeof(Menu_Main))]
    internal static class MainMenuPatch
    {
        public const int OnlineOption = 7;
        private const float Spacing = 35f; // the game's own gap between entries
        private static readonly int[] ScreenOrder = { 0, 1, 2, OnlineOption, 3, 4, 5, 6 };

        private static readonly AccessTools.FieldRef<Menu_Main, float[][]> SelectorPosition = AccessTools.FieldRefAccess<Menu_Main, float[][]>("SelectorPosition");
        private static readonly AccessTools.FieldRef<Menu_Main, RectTransform[]> Buttons = AccessTools.FieldRefAccess<Menu_Main, RectTransform[]>("Buttons");
        private static readonly AccessTools.FieldRef<Menu_Main, float[]> ButtonsFinalPosY = AccessTools.FieldRefAccess<Menu_Main, float[]>("ButtonsFinalPosY");
        private static readonly AccessTools.FieldRef<Menu_Main, float[]> ButtonsInitialPosY = AccessTools.FieldRefAccess<Menu_Main, float[]>("ButtonsInitialPosY");
        private static readonly AccessTools.FieldRef<Menu_Main, float[]> TimeBetweenButtonsCount = AccessTools.FieldRefAccess<Menu_Main, float[]>("TimeBetweenButtonsCount");
        private static readonly AccessTools.FieldRef<Menu_Main, string> NavigationControlV = AccessTools.FieldRefAccess<Menu_Main, string>("NavigationControlV");
        private static readonly AccessTools.FieldRef<Menu_Main, bool> IsVerticalMovementDone = AccessTools.FieldRefAccess<Menu_Main, bool>("isVerticalMovementDone");
        private static readonly AccessTools.FieldRef<MenuBase, AudioSource> AudioSourceEffects = AccessTools.FieldRefAccess<MenuBase, AudioSource>("AudioSource_Effects");
        private static readonly AccessTools.FieldRef<MenuBase, AudioClip> AudioButton1 = AccessTools.FieldRefAccess<MenuBase, AudioClip>("Audio_Button1");
        private static readonly AccessTools.FieldRef<MenuBase, AudioClip> AudioButton2 = AccessTools.FieldRefAccess<MenuBase, AudioClip>("Audio_Button2");
        private static readonly Traverse RemarkedOption = Traverse.Create(typeof(Menu_Main)).Field("RemarkedOption");
        private static readonly Action<Menu_Main, int> MoveSelector =
            AccessTools.MethodDelegate<Action<Menu_Main, int>>(AccessTools.Method(typeof(Menu_Main), "MoveSelector"));

        private static bool _reselectOnline;

        private static int Remarked => RemarkedOption.GetValue<int>();

        [HarmonyPrefix, HarmonyPatch("Start")]
        private static void StartPrefix()
        {
            // The game re-selects the remembered option before it knows about ONLINE; do that part ourselves.
            _reselectOnline = Remarked >= OnlineOption;
            if (_reselectOnline) RemarkedOption.SetValue(0);
        }

        [HarmonyPostfix, HarmonyPatch("Start")]
        private static void StartPostfix(Menu_Main __instance)
        {
            try
            {
                AddOnlineButton(__instance);
                if (_reselectOnline) MoveSelector(__instance, OnlineOption);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not add ONLINE to the main menu (F8 still opens the online screen): " + e);
            }
            _reselectOnline = false;
            // Back from an online match: show the room again.
            if (NetplayManager.Instance != null && NetplayManager.Instance.InRoom) NetplayManager.Instance.OpenScreen();
        }

        private static void AddOnlineButton(Menu_Main menu)
        {
            var buttons = Buttons(menu);
            if (buttons == null || buttons.Length != 7) return; // a game update changed the menu; leave it alone

            var template = buttons[1]; // VS PLAYER: same size, has a text and a "new" star
            var go = UnityEngine.Object.Instantiate(template.gameObject, template.parent, false);
            go.name = "Button_Online";
            foreach (Transform child in go.transform) child.name = child.name.Replace("2Player", "Online").Replace("Versus", "Online");
            go.GetComponentInChildren<Text>().text = "ONLINE";
            foreach (var img in go.GetComponentsInChildren<Image>(true))
                if (img.gameObject != go && img.name.Contains("Star")) img.enabled = false;

            int count = ScreenOrder.Length;
            var finalY = new float[count];
            var initialY = new float[count];
            var selector = new float[count][];
            float top = ButtonsFinalPosY(menu)[0];
            float[] sel0 = SelectorPosition(menu)[0];
            for (int slot = 0; slot < count; slot++)
            {
                int option = ScreenOrder[slot];
                finalY[option] = top - slot * Spacing;
                initialY[option] = finalY[option] - 200f;
                selector[option] = new[] { sel0[0], sel0[1] - slot * Spacing };
            }

            var newButtons = new RectTransform[count];
            Array.Copy(buttons, newButtons, buttons.Length);
            newButtons[OnlineOption] = go.GetComponent<RectTransform>();
            var timers = new float[count];
            Array.Copy(TimeBetweenButtonsCount(menu), timers, buttons.Length);

            Buttons(menu) = newButtons;
            ButtonsFinalPosY(menu) = finalY;
            ButtonsInitialPosY(menu) = initialY;
            SelectorPosition(menu) = selector;
            TimeBetweenButtonsCount(menu) = timers;
            for (int i = 0; i < count; i++)
            {
                var p = newButtons[i].localPosition;
                p.y = initialY[i];
                newButtons[i].localPosition = p;
            }
            MoveSelector(menu, Remarked); // the lower entries moved down one row
        }

        /// <summary>Up/down follows the on-screen order, and confirming ONLINE opens the online screen.</summary>
        [HarmonyPrefix, HarmonyPatch("Navigation")]
        private static bool NavigationPrefix(Menu_Main __instance)
        {
            var mgr = NetplayManager.Instance;
            if (mgr != null && mgr.ScreenOpen) return false; // the online screen has the input
            if (Buttons(__instance).Length <= OnlineOption) return true;

            float axis = InputController.GetAxis(NavigationControlV(__instance), GameData.CONTROL_GAMEPAD[0]);
            ref bool done = ref IsVerticalMovementDone(__instance);
            if (done && axis < MenuBase.DeadZone && axis > -MenuBase.DeadZone) done = false;
            if (!done && (axis > MenuBase.DeadZone || axis < -MenuBase.DeadZone))
            {
                // Marking the move as done here also stops the game's own up/down handling for this press.
                done = true;
                int slot = Array.IndexOf(ScreenOrder, Remarked) + (axis > 0f ? -1 : 1);
                if (slot >= 0 && slot < ScreenOrder.Length)
                {
                    MoveSelector(__instance, ScreenOrder[slot]);
                    Play(__instance, AudioButton1(__instance));
                }
            }

            if (Remarked != OnlineOption) return true;
            if (mgr != null && InputController.GetButton("Attack" + GameData.CONTROL[0], isGetButtonDown: true, isGetButtonUp: false, GameData.CONTROL_GAMEPAD[0]))
            {
                Play(__instance, AudioButton2(__instance));
                mgr.OpenScreen();
            }
            return false;
        }

        private static void Play(MenuBase menu, AudioClip clip)
        {
            var src = AudioSourceEffects(menu);
            if (src != null && clip != null) src.PlayOneShot(clip);
        }
    }
}
