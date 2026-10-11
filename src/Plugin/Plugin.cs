using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DigimonNetplay
{
    [BepInPlugin(Guid, "Digimon Netplay", Version)]
    [BepInProcess("ProjectDigimon.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "community.projectdigimon.netplay";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> ServerHost;
        internal static ConfigEntry<int> ServerPort;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<string> LocalControls;
        internal static ConfigEntry<int> DefaultInputDelay;
        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<int> StallTimeoutSeconds;

        private void Awake()
        {
            Log = Logger;
            ServerHost = Config.Bind("Connection", "ServerHost", "127.0.0.1", "Address of the netplay relay server (or the host's public IP when they host on their PC).");
            ServerPort = Config.Bind("Connection", "ServerPort", 7777, "TCP port of the relay server.");
            PlayerName = Config.Bind("Connection", "PlayerName", "Tamer", "Name shown to other players.");
            LocalControls = Config.Bind("Input", "LocalControls", "K1,G1", "Which of the game's control sets drive your fighter online, comma separated (K1, K2 = keyboard layouts, G1..G6 = gamepads). They are merged.");
            DefaultInputDelay = Config.Bind("Netplay", "DefaultInputDelay", 3, "Frames of input delay a new room starts with (host can change it in the lobby). 2-3 for nearby players, 4-6 for long distance.");
            ToggleKey = Config.Bind("UI", "ToggleKey", "F8", "Key that shows or hides the netplay window (Unity Input System key name, e.g. F8, F9, Backquote).");
            StallTimeoutSeconds = Config.Bind("Netplay", "StallTimeoutSeconds", 15, "How long to wait for a silent opponent before ending the match.");

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            TimePatches.Apply(new Harmony(Guid + ".time"));
            FixedStepDriver.Apply(new Harmony(Guid + ".fixed"));

            var go = new GameObject("DigimonNetplay");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<NetplayManager>();
            FrameGate.Install();
            Log.LogInfo("Digimon Netplay " + Version + " loaded. Pick ONLINE in the main menu (or press " + ToggleKey.Value + ") to play online.");
        }
    }
}
