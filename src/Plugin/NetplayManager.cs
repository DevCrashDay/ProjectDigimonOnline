using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DigimonNetplay.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DigimonNetplay
{
    /// <summary>Connection, lobby browser, room lobby and the in-game online screen.</summary>
    internal sealed class NetplayManager : MonoBehaviour
    {
        public static NetplayManager Instance { get; private set; }

        private RelayClient _client;
        private RelayServer _localServer;
        private P2PMux _mux;                                                   // direct links, only in rooms with P2P on
        private readonly Dictionary<int, byte[]> _hellos = new Dictionary<int, byte[]>(); // peers' P2P announcements
        private string _serverField, _hostPortField, _nameField, _codeField = "";

        // connecting happens on a worker thread so the game never freezes on a slow or dead server
        private volatile bool _connectDone;
        private volatile RelayClient _connected;
        private volatile string _connectError;
        private bool _connecting, _autoConnectTried, _showAdvanced;
        private string _status;

        // lobby browser
        private enum Page { Browser, Create }
        private Page _page;
        private string _serverName;
        private int _playersOnline;
        private RoomInfo[] _rooms = new RoomInfo[0];
        private float _nextListAt;
        private string _joinTarget, _joinPassword = "", _codePassword = "";
        private string _createName = "", _createPassword = "";
        private int _createPlayers, _createVisibility; // toolbar indexes
        private static readonly string[] PlayerChoices = { "1 vs 1", "4 players" };
        private static readonly string[] VisibilityChoices = { "Public", "Password", "Private (code)" };

        private bool _inRoom;
        private string _roomCode, _roomName;
        private byte _roomVisibility;
        private bool _roomHasPassword, _inMatchFlag;
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
        public void OpenScreen()
        {
            _show = true;
            if (_client == null && !_connecting && !_autoConnectTried && GameBridge.GameReady && EffectiveServer.Length > 0)
            {
                _autoConnectTried = true;
                BeginConnect(EffectiveServer);
            }
        }

        private static string EffectiveServer
        {
            get
            {
                string s = (Plugin.LobbyServer.Value ?? "").Trim();
                return s.Length > 0 ? s : Plugin.OfficialServer;
            }
        }

        private void CloseScreen()
        {
            _show = false;
            GUIUtility.keyboardControl = 0;
        }

        private void Awake()
        {
            Instance = this;
            _serverField = EffectiveServer;
            _hostPortField = Plugin.HostPort.Value.ToString();
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
            DrainDirect();
            if (_client == null) return;
            while (_client.TryPoll(out RelayEvent ev))
            {
                switch (ev)
                {
                    case EvRoomJoined j:
                        _inRoom = true; _roomCode = j.Code; _mySlot = j.MySlot; _hostSlot = j.HostSlot; _maxPlayers = j.MaxPlayers;
                        _roomName = j.Name; _roomVisibility = j.Visibility; _roomHasPassword = j.HasPassword;
                        _joinTarget = null; _joinPassword = ""; _page = Page.Browser; _inMatchFlag = false;
                        _names.Clear(); _names[_mySlot] = _nameField;
                        _lobby = new LobbyInfo { InputDelay = Mathf.Clamp(Plugin.DefaultInputDelay.Value, 0, 12), Stage = (int)GameData.STAGES._RANDOM_ };
                        _early.Clear();
                        SendPick();
                        break;
                    case EvPeerJoined pj:
                        _names[pj.Slot] = pj.Name;
                        _mux?.Announce();
                        SendPick();
                        if (IsHost) BroadcastLobby();
                        break;
                    case EvPeerLeft pl:
                        _names.Remove(pl.Slot);
                        _hellos.Remove(pl.Slot);
                        _mux?.RemovePeer(pl.Slot);
                        _lobby.Picks.Remove(pl.Slot);
                        MatchSession.Current?.Core.OnPeerLeft(pl.Slot);
                        if (IsHost) BroadcastLobby();
                        if (pl.Slot == _hostSlot) LeaveRoomState();
                        break;
                    case EvError e:
                        Notify(e.Text);
                        if (e.Text.Contains("host closed")) LeaveRoomState();
                        if (!e.Text.Contains("password")) _joinTarget = null; // keep the password box open to retry
                        break;
                    case EvWelcome w:
                        _serverName = w.ServerName;
                        break;
                    case EvRoomList l:
                        _rooms = l.Rooms;
                        _playersOnline = l.PlayersOnline;
                        break;
                    case EvPong p:
                        _rtt = p.RttMs;
                        break;
                    case EvData d:
                        HandleData(d.FromSlot, d.Payload);
                        break;
                    case EvDisconnected dc:
                        Notify("Disconnected from the lobby server: " + dc.Reason);
                        _status = "Disconnected: " + dc.Reason;
                        MatchSession.Current?.End("Disconnected from the server.");
                        _client = null;
                        LeaveRoomState();
                        return;
                }
            }
            DrainDirect();
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
                    case GameMsg.P2PHello:
                        _hellos[from] = payload;
                        if (_mux != null) _mux.OnHello(from, r);
                        break;
                    case GameMsg.Seq:
                        // A P2P match message that came through the server (fallback, or a player without direct links).
                        EnsureMux().OnRelayed(from, r);
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

        private void BeginConnect(string address)
        {
            if (_connecting) return;
            Plugin.PlayerName.Value = _nameField;
            _connecting = true;
            _connectDone = false;
            _connected = null;
            _connectError = null;
            _status = "Connecting to " + address + " ...";
            string name = _nameField;
            new Thread(() =>
            {
                try
                {
                    var c = new RelayClient();
                    c.Connect(address, name);
                    _connected = c;
                }
                catch (Exception e)
                {
                    _connectError = e.Message;
                }
                _connectDone = true;
            }) { IsBackground = true, Name = "netplay-connect" }.Start();
        }

        private void FinishConnect()
        {
            if (!_connectDone) return;
            _connectDone = false;
            _connecting = false;
            if (_connected != null)
            {
                _client = _connected;
                _connected = null;
                _status = null;
                _nextPing = 0;
                _nextListAt = 0;
                _page = Page.Browser;
            }
            else
            {
                _status = "Could not reach the lobby server: " + _connectError;
                Plugin.Log.LogWarning(_status);
            }
        }

        private void HostOnThisPc()
        {
            if (!int.TryParse(_hostPortField, out int port) || port <= 0 || port > 65535) { Notify("Port must be a number."); return; }
            Plugin.HostPort.Value = port;
            try
            {
                if (_localServer == null)
                {
                    _localServer = new RelayServer { Log = m => Plugin.Log.LogInfo("[relay] " + m), ServerName = _nameField + "'s PC" };
                    _localServer.Start(port);
                }
            }
            catch (Exception e)
            {
                Notify("Could not open port " + port + ": " + e.Message);
                return;
            }
            Notify("Hosting on this PC, port " + port + ". Friends connect to " + "your public IP:" + port + " (forward TCP " + port + ").");
            BeginConnect("127.0.0.1:" + port);
        }

        private void Disconnect()
        {
            MatchSession.Current?.End("You left the match.");
            _client?.Dispose();
            _client = null;
            _localServer?.Stop();
            _localServer = null;
            _status = null;
            _rooms = new RoomInfo[0];
            LeaveRoomState();
        }

        // ------------------------------------------------------------------ P2P

        private P2PMux EnsureMux()
        {
            if (_mux != null) return _mux;
            _mux = new P2PMux(_mySlot, Plugin.AllowP2P.Value, Broadcast, _client?.Arrived) { Log = m => Plugin.Log.LogInfo(m) };
            _mux.StartDiscovery();
            foreach (var kv in _hellos)
                using (var r = GameMsg.Open(kv.Value, out _)) _mux.OnHello(kv.Key, r);
            return _mux;
        }

        private void DropMux()
        {
            _mux?.Dispose();
            _mux = null;
        }

        /// <summary>Direct links are set up while players wait in a P2P room, so they are ready when the match starts.</summary>
        private void UpdateP2P()
        {
            bool want = _client != null && _inRoom && _lobby.P2P;
            if (want && _mux == null) EnsureMux();
            else if (!want && _mux != null && MatchSession.Current == null) DropMux();
        }

        private void DrainDirect()
        {
            while (_mux != null && _mux.TryReceive(out int from, out byte[] payload)) HandleData(from, payload);
        }

        private void LeaveRoomState()
        {
            DropMux();
            _hellos.Clear();
            _inRoom = false; _roomCode = null; _roomName = null; _mySlot = -1; _nextListAt = 0; _names.Clear(); _lobby.Picks.Clear(); _early.Clear();
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
                P2P = _lobby.P2P,
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
            Action<byte[]> send = Broadcast;
            if (cfg.P2P)
            {
                var mux = EnsureMux();
                mux.BeginMatch(cfg.Seed, cfg.Slots);
                send = mux.Send;
            }
            MatchSession.Begin(cfg, me, IsHost, send, controls);
            if (IsHost) { _client?.SetInMatch(true); _inMatchFlag = true; } // the lobby browser shows the room as busy
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
            FinishConnect();
            UpdateP2P();
            if (MatchSession.Current == null) PumpNetwork();
            if (_inMatchFlag && MatchSession.Current == null) { _client?.SetInMatch(false); _inMatchFlag = false; }
            if (_show && _client != null && !_inRoom && _page == Page.Browser && Time.unscaledTime >= _nextListAt)
            {
                _client.ListRooms();
                _nextListAt = Time.unscaledTime + 3f;
            }
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
            else if (_client == null) DrawOffline();
            else if (_inRoom) DrawLobby();
            else if (_page == Page.Create) DrawCreate();
            else DrawBrowser();
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
            if (s.Core.Config.P2P && _mux != null)
            {
                var others = s.Core.Config.Slots.Where(x => x != _mySlot).ToList();
                text += "   direct " + others.Count(_mux.IsDirect) + "/" + others.Count;
            }
            GUI.Label(new Rect(10, OnlineSkin.CanvasHeight - 30, width - 20, 26), text, OnlineSkin.Hud);
        }

        // ------------------------------------------------------------------ screens

        private void DrawOffline()
        {
            bool hasServer = EffectiveServer.Length > 0;
            if (_connecting) GUILayout.Label(_status);
            else if (_status != null) GUILayout.Label(_status, OnlineSkin.Warn);
            else if (!hasServer) GUILayout.Label("No lobby server is set yet. Enter one under Server settings.", OnlineSkin.Warn);

            GUILayout.Label("Your name");
            _nameField = GUILayout.TextField(_nameField, 20);
            GUI.enabled = !_connecting && hasServer;
            if (GUILayout.Button(_status == null ? "Connect to the lobby server" : "Try again")) BeginConnect(EffectiveServer);
            GUI.enabled = true;

            GUILayout.Space(10);
            _showAdvanced = GUILayout.Toggle(_showAdvanced, _showAdvanced ? "Server settings  (hide)" : "Server settings", OnlineSkin.Link);
            if (!_showAdvanced) return;
            GUILayout.Label("Lobby server address (wss://your.domain, ws://host:port or host:port). Leave empty for the official server.", OnlineSkin.Small);
            GUILayout.BeginHorizontal();
            _serverField = GUILayout.TextField(_serverField, 120, GUILayout.Width(500));
            GUI.enabled = !_connecting;
            if (GUILayout.Button("Connect"))
            {
                Plugin.LobbyServer.Value = _serverField.Trim() == Plugin.OfficialServer ? "" : _serverField.Trim();
                if (EffectiveServer.Length > 0) BeginConnect(EffectiveServer);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (Plugin.OfficialServer.Length > 0 && GUILayout.Button("Use the official server", GUILayout.Width(300)))
            {
                _serverField = Plugin.OfficialServer;
                Plugin.LobbyServer.Value = "";
            }
            GUILayout.Space(8);
            GUILayout.Label("Or host a private server on this PC. Friends connect to your public IP and port; forward that TCP port on your router.", OnlineSkin.Small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(60));
            _hostPortField = GUILayout.TextField(_hostPortField, 6, GUILayout.Width(100));
            GUI.enabled = !_connecting;
            if (GUILayout.Button("Host on this PC")) HostOnThisPc();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawBrowser()
        {
            GUILayout.Label((_serverName ?? "Lobby server") + "   |   " + _playersOnline + " online   |   ping " + (_rtt < 0 ? "?" : _rtt.ToString("0")) + " ms   |   you are " + _nameField, OnlineSkin.Small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Create room", GUILayout.Width(200)))
            {
                _page = Page.Create;
                if (_createName.Length == 0) _createName = _nameField + "'s room";
            }
            if (GUILayout.Button("Refresh", GUILayout.Width(130))) _nextListAt = 0;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Disconnect", GUILayout.Width(160))) Disconnect();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Room code", GUILayout.Width(130));
            _codeField = GUILayout.TextField(_codeField.ToUpperInvariant(), 5, GUILayout.Width(110));
            GUILayout.Label("Password", GUILayout.Width(110));
            _codePassword = GUILayout.PasswordField(_codePassword, '*', 32, GUILayout.Width(160));
            if (GUILayout.Button("Join") && _codeField.Length > 0) _client.JoinRoom(_codeField, _codePassword);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("OPEN ROOMS", OnlineSkin.Heading);
            if (_rooms.Length == 0)
            {
                GUILayout.Label("No public rooms right now. Create one, or join a private room with its code.", OnlineSkin.Small);
                return;
            }
            foreach (var r in _rooms)
            {
                bool full = r.Players >= r.MaxPlayers;
                GUILayout.BeginHorizontal(OnlineSkin.Row);
                GUILayout.Label((r.Locked ? "[PW]  " : "") + r.Name, GUILayout.Width(300));
                GUILayout.Label(r.HostName, OnlineSkin.Small, GUILayout.Width(170));
                GUILayout.Label(r.Players + "/" + r.MaxPlayers + (r.MaxPlayers == 2 ? "  1v1" : "  FFA"), GUILayout.Width(110));
                GUI.enabled = !full && !r.InMatch;
                if (GUILayout.Button(r.InMatch ? "In match" : full ? "Full" : "Join", GUILayout.Width(110)))
                {
                    if (r.Locked) { _joinTarget = r.Code; _joinPassword = ""; }
                    else _client.JoinRoom(r.Code);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                if (_joinTarget == r.Code)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Password for " + r.Name, GUILayout.Width(300));
                    _joinPassword = GUILayout.PasswordField(_joinPassword, '*', 32, GUILayout.Width(200));
                    if (GUILayout.Button("Join", GUILayout.Width(90))) _client.JoinRoom(r.Code, _joinPassword);
                    if (GUILayout.Button("Cancel", GUILayout.Width(110))) { _joinTarget = null; _joinPassword = ""; }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void DrawCreate()
        {
            GUILayout.Label("CREATE A ROOM", OnlineSkin.Heading);
            GUILayout.Label("Room name");
            _createName = GUILayout.TextField(_createName, 32);
            GUILayout.Label("Players");
            _createPlayers = GUILayout.Toolbar(_createPlayers, PlayerChoices);
            GUILayout.Label("Who can join");
            _createVisibility = GUILayout.Toolbar(_createVisibility, VisibilityChoices);
            switch (_createVisibility)
            {
                case 0:
                    GUILayout.Label("Listed in the lobby. Anyone can join.", OnlineSkin.Small);
                    break;
                case 1:
                    GUILayout.Label("Listed in the lobby with a lock. Players need the password.", OnlineSkin.Small);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Password", GUILayout.Width(130));
                    _createPassword = GUILayout.TextField(_createPassword, 32, GUILayout.Width(260));
                    GUILayout.EndHorizontal();
                    break;
                default:
                    GUILayout.Label("Not listed. Share the room code with your friends.", OnlineSkin.Small);
                    break;
            }
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            bool needsPassword = _createVisibility == 1 && _createPassword.Trim().Length == 0;
            GUI.enabled = !needsPassword;
            if (GUILayout.Button("Create", GUILayout.Width(200)))
                _client.CreateRoom(_createPlayers == 0 ? 2 : 4, _createName,
                    _createVisibility == 2 ? RoomVisibility.Private : RoomVisibility.Public,
                    _createVisibility == 1 ? _createPassword.Trim() : "");
            GUI.enabled = true;
            if (GUILayout.Button("Cancel", GUILayout.Width(160))) _page = Page.Browser;
            GUILayout.EndHorizontal();
            if (needsPassword) GUILayout.Label("Enter a password.", OnlineSkin.Small);
        }

        private void DrawLobby()
        {
            bool inMatch = MatchSession.Current != null;
            GUILayout.Label((_roomName ?? "Room").ToUpperInvariant(), OnlineSkin.Heading);
            string access = _roomVisibility == RoomVisibility.Private ? "private, share the code" : _roomHasPassword ? "public with password" : "public";
            GUILayout.Label("Code " + _roomCode + "   (" + access + ")   |   " + _names.Count + "/" + _maxPlayers + " players   |   ping " + (_rtt < 0 ? "?" : _rtt.ToString("0")) + " ms", OnlineSkin.Small);
            foreach (var kv in _names.OrderBy(k => k.Key))
            {
                _lobby.Picks.TryGetValue(kv.Key, out var p);
                string pick = p == null ? "choosing..." : p.Character + " (color " + p.Color + (p.AltForm > 1 ? ", evo " + p.AltForm : "") + ")";
                string link = !_lobby.P2P || kv.Key == _mySlot ? "" : _mux != null && _mux.IsDirect(kv.Key) ? "   [direct]" : "   [via server]";
                GUILayout.Label((kv.Key == _hostSlot ? "[Host] " : "") + kv.Value + (kv.Key == _mySlot ? " (you)" : "") + ": " + pick + link);
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
            DrawConnectionChoice();
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

        private static readonly string[] ConnectionChoices = { "Through the server", "Direct (P2P)" };

        private void DrawConnectionChoice()
        {
            if (IsHost)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Connection", GUILayout.Width(140));
                int pick = GUILayout.Toolbar(_lobby.P2P ? 1 : 0, ConnectionChoices);
                GUILayout.EndHorizontal();
                if ((pick == 1) != _lobby.P2P) { _lobby.P2P = pick == 1; BroadcastLobby(); }
            }
            else GUILayout.Label("Connection: " + (_lobby.P2P ? "direct (P2P)" : "through the server"));
            if (!_lobby.P2P) return;
            GUILayout.Label("Players connect directly where their networks allow it and use the server otherwise. Direct links show each player's IP address to the others in this room.", OnlineSkin.Small);
            bool allow = GUILayout.Toggle(Plugin.AllowP2P.Value, "  Allow a direct connection for me");
            if (allow != Plugin.AllowP2P.Value)
            {
                Plugin.AllowP2P.Value = allow;
                DropMux(); // rebuilt next frame with the new setting
            }
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
