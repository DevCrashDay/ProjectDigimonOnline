using System;
using System.Collections.Generic;
using System.Linq;
using DigimonNetplay.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DigimonNetplay
{
    /// <summary>Connection, lobby and the in-game netplay window.</summary>
    internal sealed class NetplayManager : MonoBehaviour
    {
        public static NetplayManager Instance { get; private set; }

        private RelayClient _client;
        private RelayServer _localServer;
        private string _hostField, _portField, _nameField, _codeField = "";

        private bool _inRoom;
        private string _roomCode;
        private int _mySlot = -1, _hostSlot, _maxPlayers;
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private LobbyInfo _lobby = new LobbyInfo();
        private readonly List<KeyValuePair<int, byte[]>> _early = new List<KeyValuePair<int, byte[]>>();

        private List<GameBridge.CharOption> _chars = new List<GameBridge.CharOption>();
        private List<int> _stages = new List<int>();
        private int _charIdx, _colorIdx, _evoIdx, _stageIdx;
        private string _charFilter = "";

        private bool _show;
        private Vector2 _scroll;
        private string _notice;
        private float _noticeUntil;
        private double _rtt = -1;
        private float _nextPing;
        private Key _toggleKey = Key.F8;
        private bool _gameInputDisabled;

        private bool IsHost => _inRoom && _mySlot == _hostSlot;

        public bool ScreenOpen => _show;
        public bool InRoom => _inRoom;

        /// <summary>Opens the online screen (from the main menu's ONLINE entry or after an online match).</summary>
        public void OpenScreen() => _show = true;

        private void CloseScreen()
        {
            _show = false;
            GUIUtility.keyboardControl = 0;
        }

        private void Awake()
        {
            Instance = this;
            _hostField = Plugin.ServerHost.Value;
            _portField = Plugin.ServerPort.Value.ToString();
            _nameField = Plugin.PlayerName.Value;
            if (!Enum.TryParse(Plugin.ToggleKey.Value, true, out _toggleKey)) _toggleKey = Key.F8;
            _lobby.InputDelay = Mathf.Clamp(Plugin.DefaultInputDelay.Value, 0, 12);
        }

        private void OnDestroy()
        {
            _client?.Dispose();
            _localServer?.Stop();
        }

        public void Notify(string text)
        {
            _notice = text;
            _noticeUntil = Time.unscaledTime + 8f;
            _show = true;
            Plugin.Log.LogInfo(text);
        }

        // ------------------------------------------------------------------ network

        public void WaitForNetwork(int ms) => _client?.Arrived.WaitOne(ms);

        public void PumpNetwork()
        {
            if (_client == null) return;
            while (_client.TryPoll(out RelayEvent ev))
            {
                switch (ev)
                {
                    case EvRoomJoined j:
                        _inRoom = true; _roomCode = j.Code; _mySlot = j.MySlot; _hostSlot = j.HostSlot; _maxPlayers = j.MaxPlayers;
                        _names.Clear(); _names[_mySlot] = _nameField;
                        _lobby = new LobbyInfo { InputDelay = Mathf.Clamp(Plugin.DefaultInputDelay.Value, 0, 12), Stage = (int)GameData.STAGES._RANDOM_ };
                        _early.Clear();
                        SendPick();
                        break;
                    case EvPeerJoined pj:
                        _names[pj.Slot] = pj.Name;
                        SendPick();
                        if (IsHost) BroadcastLobby();
                        break;
                    case EvPeerLeft pl:
                        _names.Remove(pl.Slot);
                        _lobby.Picks.Remove(pl.Slot);
                        MatchSession.Current?.Core.OnPeerLeft(pl.Slot);
                        if (IsHost) BroadcastLobby();
                        if (pl.Slot == _hostSlot) LeaveRoomState();
                        break;
                    case EvError e:
                        Notify(e.Text);
                        if (e.Text.Contains("host closed")) LeaveRoomState();
                        break;
                    case EvPong p:
                        _rtt = p.RttMs;
                        break;
                    case EvData d:
                        HandleData(d.FromSlot, d.Payload);
                        break;
                    case EvDisconnected dc:
                        Notify("Disconnected from server: " + dc.Reason);
                        MatchSession.Current?.End("Disconnected from the server.");
                        _client = null;
                        LeaveRoomState();
                        return;
                }
            }
        }

        private void HandleData(int from, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            var session = MatchSession.Current;
            if (session != null && session.Core.Handle(from, payload)) return;
            using (var r = GameMsg.Open(payload, out byte type))
            {
                switch (type)
                {
                    case GameMsg.Pick:
                        _lobby.Picks[from] = PlayerPick.Read(r);
                        if (IsHost) BroadcastLobby();
                        break;
                    case GameMsg.Lobby:
                        if (from != _hostSlot) break;
                        var mine = MyPick();
                        _lobby = LobbyInfo.Read(r);
                        _lobby.Picks[_mySlot] = mine;
                        break;
                    case GameMsg.Start:
                        if (from != _hostSlot) break;
                        StartLocal(MatchConfig.Read(r));
                        break;
                    case GameMsg.Ready:
                    case GameMsg.Input:
                    case GameMsg.StateCheck:
                        // A fast peer can talk lockstep before our Start arrives; keep it for the session.
                        _early.Add(new KeyValuePair<int, byte[]>(from, payload));
                        break;
                }
            }
        }

        private void Broadcast(byte[] payload) => _client?.Broadcast(payload);

        private void SendPick()
        {
            var pick = MyPick();
            if (pick == null) return;
            _lobby.Picks[_mySlot] = pick;
            Broadcast(GameMsg.Build(GameMsg.Pick, w => pick.Write(w)));
        }

        private void BroadcastLobby()
        {
            var l = _lobby;
            Broadcast(GameMsg.Build(GameMsg.Lobby, w => l.Write(w)));
        }

        private PlayerPick MyPick()
        {
            if (_chars.Count == 0) return null;
            var c = _chars[Mathf.Clamp(_charIdx, 0, _chars.Count - 1)];
            return new PlayerPick
            {
                Character = c.Name,
                Color = c.Colors[Mathf.Clamp(_colorIdx, 0, c.Colors.Count - 1)],
                AltForm = c.Evolutions.Count > 0 ? c.Evolutions[Mathf.Clamp(_evoIdx, 0, c.Evolutions.Count - 1)].Key : 1,
            };
        }

        private void Connect(bool hostLocally)
        {
            if (!int.TryParse(_portField, out int port)) { Notify("Port must be a number."); return; }
            Plugin.ServerHost.Value = _hostField;
            Plugin.ServerPort.Value = port;
            Plugin.PlayerName.Value = _nameField;
            string host = _hostField;
            try
            {
                if (hostLocally)
                {
                    if (_localServer == null)
                    {
                        _localServer = new RelayServer { Log = m => Plugin.Log.LogInfo("[relay] " + m) };
                        _localServer.Start(port);
                    }
                    host = "127.0.0.1";
                }
                _client = new RelayClient();
                _client.Connect(host, port, _nameField);
                _nextPing = 0;
                Notify(hostLocally ? "Hosting on this PC, port " + port + ". Friends connect to your public IP (forward TCP " + port + ")." : "Connected to " + host + ":" + port + ".");
            }
            catch (Exception e)
            {
                _client = null;
                Notify("Could not connect: " + e.Message);
            }
        }

        private void Disconnect()
        {
            MatchSession.Current?.End("You left the match.");
            _client?.Dispose();
            _client = null;
            _localServer?.Stop();
            _localServer = null;
            LeaveRoomState();
        }

        private void LeaveRoomState()
        {
            _inRoom = false; _roomCode = null; _mySlot = -1; _names.Clear(); _lobby.Picks.Clear(); _early.Clear();
        }

        // ------------------------------------------------------------------ starting

        private void HostStart()
        {
            var slots = _names.Keys.OrderBy(s => s).ToList();
            if (slots.Count < 2) { Notify("Wait for at least one more player."); return; }
            foreach (int s in slots)
            {
                if (!_lobby.Picks.TryGetValue(s, out var p) || string.IsNullOrEmpty(p.Character)) { Notify(_names[s] + " has not picked a Digimon yet."); return; }
                if (!GameBridge.CharacterExists(p.Character)) { Notify(_names[s] + " picked " + p.Character + ", which this game version does not have."); return; }
            }
            var rng = new System.Random();
            int stage = _lobby.Stage;
            if (stage == (int)GameData.STAGES._RANDOM_)
            {
                var pool = GameBridge.UnlockedStages();
                stage = pool.Count > 0 ? (int)pool[rng.Next(pool.Count)] : (int)GameData.STAGES.DANGEROUS_FOREST;
            }
            var settings = GameBridge.CaptureSettings();
            settings["BattleRounds"] = Mathf.Clamp(_lobby.Rounds - 1, 0, 3).ToString();
            var cfg = new MatchConfig
            {
                Seed = rng.Next(),
                InputDelay = _lobby.InputDelay,
                Stage = stage,
                Song = SoundManager.SONG_DEFAULT_STAGE,
                BattleRoyale = slots.Count > 2,
                Slots = slots.ToArray(),
                Names = slots.Select(s => _names[s]).ToArray(),
                Picks = slots.Select(s => _lobby.Picks[s].Clone()).ToArray(),
                Settings = settings,
            };
            Broadcast(GameMsg.Build(GameMsg.Start, w => cfg.Write(w)));
            StartLocal(cfg);
        }

        private void StartLocal(MatchConfig cfg)
        {
            int me = cfg.PlayerOfSlot(_mySlot);
            if (me < 0) { Notify("A match started without you (the room was full)."); return; }
            var controls = Plugin.LocalControls.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            EnableGameInput(true);
            MatchSession.Begin(cfg, me, IsHost, Broadcast, controls);
            foreach (var e in _early) MatchSession.Current?.Core.Handle(e.Key, e.Value);
            _early.Clear();
            _show = false;
        }

        // ------------------------------------------------------------------ UI

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[_toggleKey].wasPressedThisFrame) { if (_show) CloseScreen(); else OpenScreen(); }
            else if (_show && MatchSession.Current == null &&
                     ((kb != null && kb.escapeKey.wasPressedThisFrame) || (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame)))
                CloseScreen();
            if (_client != null && Time.unscaledTime >= _nextPing)
            {
                _client.Ping();
                _nextPing = Time.unscaledTime + 2f;
            }
            if (_chars.Count == 0 && GameBridge.GameReady)
            {
                RefreshLists();
                if (_inRoom) SendPick();
            }
            // Something (a plugin, the input system) may rebuild Unity's player loop; put the frame gate back.
            if (FrameGate.LastTickFrame >= 0 && Time.frameCount - FrameGate.LastTickFrame > 2)
            {
                Plugin.Log.LogWarning("Frame gate went missing from the player loop; reinstalling.");
                FrameGate.Install();
                FrameGate.LastTickFrame = Time.frameCount;
            }
            if (MatchSession.Current == null) PumpNetwork();
            // Typing a room code must not drive the menus behind the window.
            EnableGameInput(!(_show && GUIUtility.keyboardControl != 0 && MatchSession.Current == null));
        }

        private void EnableGameInput(bool on)
        {
            var im = InputController.inputManager;
            if (im == null || on != _gameInputDisabled) return;
            if (on) im.Enable(); else im.Disable();
            _gameInputDisabled = !on;
        }

        private void RefreshLists()
        {
            _chars = GameBridge.UnlockedCharacters().OrderBy(c => c.Name).ToList();
            _stages = new List<int> { (int)GameData.STAGES._RANDOM_ };
            _stages.AddRange(GameBridge.UnlockedStages().Select(s => (int)s));
        }

        private void OnGUI()
        {
            // Everything is laid out on a 720-pixel-high canvas and scaled to the window, like the game's menus.
            float scale = Screen.height / OnlineSkin.CanvasHeight;
            float width = Screen.width / scale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            GUI.skin = OnlineSkin.Get();

            var session = MatchSession.Current;
            if (session != null) DrawHud(session, width);
            if (!_show)
            {
                DrawNotice(width);
                return;
            }

            if (session == null) GUI.DrawTexture(new Rect(0, 0, width, OnlineSkin.CanvasHeight), OnlineSkin.Dim);
            var panel = new Rect(width / 2 - 400, 50, 800, OnlineSkin.CanvasHeight - 100);
            GUI.Box(panel, GUIContent.none, OnlineSkin.Panel);
            GUILayout.BeginArea(new Rect(panel.x + 28, panel.y + 18, panel.width - 56, panel.height - 36));
            GUILayout.Label("ONLINE VERSUS", OnlineSkin.Title);
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (!GameBridge.GameReady) GUILayout.Label("Waiting for the game to finish loading...");
            else if (_client == null) DrawConnect();
            else if (!_inRoom) DrawRoomChoice();
            else DrawLobby();
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            GUILayout.Label(session == null ? "Esc / B: back      " + _toggleKey + ": open or close anywhere" : _toggleKey + ": hide", OnlineSkin.Hint);
            if (GUILayout.Button("Back", GUILayout.Width(140))) CloseScreen();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            DrawNotice(width);
        }

        private void DrawNotice(float width)
        {
            if (_notice != null && Time.unscaledTime < _noticeUntil)
                GUI.Box(new Rect(width / 2 - 360, 8, 720, 34), _notice, OnlineSkin.Notice);
        }

        private void DrawHud(MatchSession s, float width)
        {
            var c = s.Core;
            string text = s.Status + "   ping " + (_rtt < 0 ? "?" : _rtt.ToString("0")) + " ms   delay " + c.Delay + "f";
            if (c.DesyncCount > 0) text += "   desyncs fixed " + c.DesyncCount;
            if (s.StallMs > 50) text += "   waiting " + s.StallMs.ToString("0") + " ms";
            GUI.Label(new Rect(10, OnlineSkin.CanvasHeight - 30, width - 20, 26), text, OnlineSkin.Hud);
        }

        private void DrawConnect()
        {
            GUILayout.Label("Your name");
            _nameField = GUILayout.TextField(_nameField, 20);
            GUILayout.Label("Server address and port");
            GUILayout.BeginHorizontal();
            _hostField = GUILayout.TextField(_hostField, GUILayout.Width(420));
            _portField = GUILayout.TextField(_portField, 6);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Connect to server")) Connect(false);
            GUILayout.Space(8);
            GUILayout.Label("No server? Host on this PC (friends need your public IP and TCP " + _portField + " forwarded):");
            if (GUILayout.Button("Host on this PC")) Connect(true);
        }

        private void DrawRoomChoice()
        {
            GUILayout.Label("Connected" + (_rtt >= 0 ? " (" + _rtt.ToString("0") + " ms)" : "") + ".");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Create 1v1 room")) _client.CreateRoom(2);
            if (GUILayout.Button("Create 4-player room")) _client.CreateRoom(4);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            GUILayout.Label("Join with a room code");
            GUILayout.BeginHorizontal();
            _codeField = GUILayout.TextField(_codeField.ToUpperInvariant(), 5, GUILayout.Width(160));
            if (GUILayout.Button("Join") && _codeField.Length > 0) _client.JoinRoom(_codeField);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
            if (GUILayout.Button("Disconnect")) Disconnect();
        }

        private void DrawLobby()
        {
            bool inMatch = MatchSession.Current != null;
            GUILayout.Label("Room code: " + _roomCode + "   (" + _names.Count + "/" + _maxPlayers + ")   ping " + (_rtt < 0 ? "?" : _rtt.ToString("0")) + " ms");
            foreach (var kv in _names.OrderBy(k => k.Key))
            {
                _lobby.Picks.TryGetValue(kv.Key, out var p);
                string pick = p == null ? "choosing..." : p.Character + " (color " + p.Color + (p.AltForm > 1 ? ", evo " + p.AltForm : "") + ")";
                GUILayout.Label((kv.Key == _hostSlot ? "[Host] " : "") + kv.Value + (kv.Key == _mySlot ? " (you)" : "") + ": " + pick);
            }
            if (inMatch)
            {
                GUILayout.Label("Match in progress.");
                if (GUILayout.Button("Leave match")) MatchSession.Current.End("You left the match.");
                return;
            }

            GUILayout.Space(6);
            DrawPicker();

            GUILayout.Space(6);
            if (IsHost)
            {
                GUILayout.Label("Stage: " + GameBridge.StageLabel(_lobby.Stage));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("<")) { _stageIdx = (_stageIdx + _stages.Count - 1) % _stages.Count; _lobby.Stage = _stages[_stageIdx]; BroadcastLobby(); }
                if (GUILayout.Button(">")) { _stageIdx = (_stageIdx + 1) % _stages.Count; _lobby.Stage = _stages[_stageIdx]; BroadcastLobby(); }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Rounds to win: " + _lobby.Rounds, GUILayout.Width(260));
                if (GUILayout.Button("-")) { _lobby.Rounds = Mathf.Max(1, _lobby.Rounds - 1); BroadcastLobby(); }
                if (GUILayout.Button("+")) { _lobby.Rounds = Mathf.Min(4, _lobby.Rounds + 1); BroadcastLobby(); }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Input delay: " + _lobby.InputDelay + " frames", GUILayout.Width(260));
                if (GUILayout.Button("-")) { _lobby.InputDelay = Mathf.Max(0, _lobby.InputDelay - 1); BroadcastLobby(); }
                if (GUILayout.Button("+")) { _lobby.InputDelay = Mathf.Min(12, _lobby.InputDelay + 1); BroadcastLobby(); }
                GUILayout.EndHorizontal();
                if (_rtt >= 0) GUILayout.Label("Suggested delay for your ping: " + SuggestedDelay(_rtt) + " (raise it if the game stutters)");
                GUI.enabled = GameBridge.InMenus;
                if (GUILayout.Button(GameBridge.InMenus ? "Start match" : "Go to a menu screen to start")) HostStart();
                GUI.enabled = true;
            }
            else
            {
                GUILayout.Label("Stage: " + GameBridge.StageLabel(_lobby.Stage) + "   rounds to win: " + _lobby.Rounds + "   delay: " + _lobby.InputDelay + "f");
                GUILayout.Label("Waiting for the host to start.");
            }
            GUILayout.Space(6);
            if (GUILayout.Button("Leave room")) { _client.LeaveRoom(); LeaveRoomState(); }
        }

        private static int SuggestedDelay(double rttMs) => Mathf.Clamp(Mathf.CeilToInt((float)rttMs / 16.7f) + 1, 1, 12);

        private void DrawPicker()
        {
            if (_chars.Count == 0) { GUILayout.Label("No unlocked Digimon found in your save."); return; }
            GUILayout.Label("Your Digimon");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(90));
            string filter = GUILayout.TextField(_charFilter, 20);
            GUILayout.EndHorizontal();
            if (filter != _charFilter)
            {
                _charFilter = filter;
                int hit = _chars.FindIndex(c => c.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase));
                if (hit < 0) hit = _chars.FindIndex(c => c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit >= 0 && hit != _charIdx) { _charIdx = hit; _colorIdx = 0; _evoIdx = 0; SendPick(); }
            }
            var ch = _chars[Mathf.Clamp(_charIdx, 0, _chars.Count - 1)];
            if (Stepper(ch.Name, ref _charIdx, _chars.Count)) { _colorIdx = 0; _evoIdx = 0; SendPick(); }
            if (Stepper("Color " + ch.Colors[Mathf.Clamp(_colorIdx, 0, ch.Colors.Count - 1)], ref _colorIdx, ch.Colors.Count)) SendPick();
            if (ch.Evolutions.Count > 0 && Stepper("Evolves to " + ch.Evolutions[Mathf.Clamp(_evoIdx, 0, ch.Evolutions.Count - 1)].Value, ref _evoIdx, ch.Evolutions.Count)) SendPick();
        }

        private static bool Stepper(string label, ref int idx, int count)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(50)) && count > 0) { idx = (idx + count - 1) % count; changed = true; }
            GUILayout.Label(label, GUILayout.Width(420));
            if (GUILayout.Button(">", GUILayout.Width(50)) && count > 0) { idx = (idx + 1) % count; changed = true; }
            GUILayout.EndHorizontal();
            return changed;
        }
    }
}
