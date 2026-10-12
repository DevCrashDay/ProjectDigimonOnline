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
        public const string Version = Shared.BuildInfo.Version;

        /// <summary>Lobby server used when the config leaves LobbyServer empty.</summary>
        public const string OfficialServer = "";

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> LobbyServer;
        internal static ConfigEntry<int> HostPort;
        internal static ConfigEntry<bool> AllowP2P;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<string> LocalControls;
        internal static ConfigEntry<int> DefaultInputDelay;
        internal static ConfigEntry<string> ToggleKey;
        internal static ConfigEntry<int> StallTimeoutSeconds;

        private void Awake()
        {
            Log = Logger;
            LobbyServer = Config.Bind("Connection", "LobbyServer", "", "Lobby server. Empty = the official server. wss://your.domain for a server behind Cloudflare/HTTPS, ws://host:port for plain WebSocket, host:port for a direct TCP connection.");
            AllowP2P = Config.Bind("Connection", "AllowP2P", true, "In rooms set to Direct (P2P), connect straight to the other players (they see your IP address). Off = always go through the server.");
            HostPort = Config.Bind("Connection", "HostPort", 7777, "TCP port used by \"Host on this PC\".");
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
