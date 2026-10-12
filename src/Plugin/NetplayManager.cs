using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DigimonNetplay.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DigimonNetplay
{
    /// <summary>Connection, lobby browser, rooms, ranked play and the flow into and out of online matches. The screens are in NetplayManager.Screens.cs.</summary>
    internal sealed partial class NetplayManager : MonoBehaviour
    {
        public static NetplayManager Instance { get; private set; }

        private sealed class PingReport
        {
            public double Server;
            public readonly Dictionary<int, double> Direct = new Dictionary<int, double>();
        }

        private RelayClient _client;
        private RelayServer _localServer;
        private P2PMux _mux;                                                   // direct links, only in rooms with P2P on
        private readonly Dictionary<int, byte[]> _hellos = new Dictionary<int, byte[]>(); // peers' P2P announcements
        private string _serverField, _hostPortField, _nameField, _codeField = "";
        private string _token = "";

        // connecting happens on a worker thread so the game never freezes on a slow or dead server
        private volatile bool _connectDone;
        private volatile RelayClient _connected;
        private volatile string _connectError;
        private bool _connecting, _autoConnectTried, _showAdvanced;
        private string _status;

        // staying connected
        private static readonly int[] ReconnectDelays = { 2, 4, 8, 8, 8 };
        private string _address;           // the server we are (or were last) connected to
        private bool _wantConnection;      // false after the player disconnects on purpose
        private int _reconnectTries;
        private float _reconnectAt = -1;
        private string _rejoinCode, _rejoinPassword = "";
        private bool _rejoinSpectate;
        private int _rejoinTries;
        private float _rejoinAt = -1;

        // server
        private string _serverName, _latestVersion = "", _downloadUrl = "";
        private bool _versionRefused;
        private int _playersOnline;
        private RoomInfo[] _rooms = new RoomInfo[0];
        private float _nextListAt;

        // ranked account
        private string _accountName;
        private int _rating, _wins, _losses, _rank;
        private bool _searching;
        private int _searchSecs, _searchPlayers;
        private LeaderRow[] _leaders = new LeaderRow[0];
        private float _nextLeadersAt;
        private string _ratingNote;

        // pages outside a room
        private enum Page { Browser, Create, Ranked, Leaderboard, Report, RoomSettings }
        private Page _page;
        private string _joinTarget, _joinPassword = "", _codePassword = "";
        private bool _joinSpectate;
        private string _pendingPassword = "";      // password of the room we are joining, kept for reconnecting
        private string _createName = "", _createPassword = "";
        private int _createPlayers, _createVisibility; // toolbar indexes
        private string _setName = "", _setPassword = "";
        private int _setPlayers, _setVisibility;
        private string _reportText = "", _reportStatus, _reportBody, _reportFile;
        private bool _reportWaiting;
        private float _reportDeadline;

        // room
        private bool _inRoom, _ranked;
        private string _roomCode, _roomName, _roomPassword = "";
        private byte _roomVisibility;
        private bool _roomHasPassword, _inMatchFlag;
        private int _mySlot = -1, _hostSlot, _maxPlayers;
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _peerRatings = new Dictionary<int, int>();
        private LobbyInfo _lobby = new LobbyInfo();
        private readonly List<KeyValuePair<int, byte[]>> _early = new List<KeyValuePair<int, byte[]>>();
        private float _earlySince;
        private bool _myReady;
        private readonly List<string> _chat = new List<string>();
        private string _chatField = "";
        private readonly Dictionary<int, PingReport> _pings = new Dictionary<int, PingReport>();
        private float _nextPingInfo;
        private string _lastMatchNote;
        private bool _reopenWhenMenus;

        // picker
        private List<GameBridge.CharOption> _chars = new List<GameBridge.CharOption>();
        private List<int> _stages = new List<int>();
        private int _charIdx, _colorIdx, _evoIdx, _stageIdx;
        private string _charFilter = "";
        private int _gridTop;

        private bool _show;
        private string _notice;
        private float _noticeUntil;
        private double _rtt = -1;
        private float _nextPing;
        private Key _toggleKey = Key.F8;
        private bool _gameInputDisabled;
        private int _enableInputAtFrame;
        private readonly OnlineNav _nav = new OnlineNav();

        private bool IsHost => _inRoom && _mySlot == _hostSlot;
        private bool IsSpectator => _mySlot >= Msg.FirstSpectatorSlot;
        private IEnumerable<int> PlayerSlots => _names.Keys.Where(s => s < Msg.FirstSpectatorSlot).OrderBy(s => s);
        private IEnumerable<int> SpectatorSlots => _names.Keys.Where(s => s >= Msg.FirstSpectatorSlot).OrderBy(s => s);
        private bool UpdateAvailable => _latestVersion.Length > 0 && BuildInfo.IsNewer(_latestVersion, BuildInfo.Version);

        public bool ScreenOpen => _show;
        public bool InRoom => _inRoom;

        /// <summary>Opens the online screen (from the main menu's ONLINE entry or after an online match).</summary>
        public void OpenScreen()
        {
            if (!_show) _nav.Block(); // the press that opened the screen must not also press a button on it
            _show = true;
            if (_client == null && !_connecting && !_autoConnectTried && _reconnectAt < 0 && GameBridge.GameReady && EffectiveServer.Length > 0)
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
            _token = Account.LoadOrCreateToken();
        }

        private void OnDestroy()
        {
            _client?.Dispose();
            _connected?.Dispose();
            _localServer?.Stop();
        }

        public void Notify(string text)
        {
            _notice = text;
            _noticeUntil = Time.unscaledTime + 8f;
            if (MatchSession.Current == null) _show = true;
            Plugin.Log.LogInfo(text);
        }

        private string NameOf(int slot) => _names.TryGetValue(slot, out var n) ? n : "A player";

        private void AddChat(string line)
        {
            _chat.Add(line);
            if (_chat.Count > 60) _chat.RemoveAt(0);
        }

        private static string CleanText(string text, int max)
        {
            var sb = new StringBuilder();
            foreach (char ch in (text ?? "").Trim())
                if (!char.IsControl(ch) && sb.Length < max) sb.Append(ch);
            return sb.ToString().Trim();
        }

        private static void OpenUrl(string url)
        {
            if (url != null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                Application.OpenURL(url);
        }

        /// <summary>A web page on the lobby server (the leaderboard), reachable the same way the game connects.</summary>
        private string WebUrl(string path)
        {
            try
            {
                var a = ServerAddress.Parse(_address);
                bool defaultPort = (a.Tls && a.Port == 443) || (!a.Tls && a.Port == 80);
                return (a.Tls ? "https://" : "http://") + a.Host + (defaultPort ? "" : ":" + a.Port) + path;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ network

        public void WaitForNetwork(int ms) => _client?.Arrived.WaitOne(ms);

        public void PumpNetwork()
        {
            DrainDirect();
            if (_client == null) return;
            while (_client != null && _client.TryPoll(out RelayEvent ev))
            {
                switch (ev)
                {
                    case EvWelcome w:
                        _serverName = w.ServerName;
                        _latestVersion = w.LatestVersion ?? "";
                        _downloadUrl = w.DownloadUrl ?? "";
                        if (_reconnectTries > 0) Notify("Reconnected to the lobby server.");
                        _reconnectTries = 0; // only a server that answers counts as a working connection
                        break;
                    case EvAccount a:
                        _accountName = a.Display; _rating = a.Rating; _wins = a.Wins; _losses = a.Losses; _rank = a.Rank;
                        break;
                    case EvRoomJoined j:
                        OnRoomJoined(j);
                        break;
                    case EvPeerJoined pj:
                        _names[pj.Slot] = pj.Name;
                        _peerRatings[pj.Slot] = pj.Rating;
                        if (pj.Slot >= Msg.FirstSpectatorSlot)
                        {
                            AddChat("* " + pj.Name + " is watching.");
                            if (_mux != null) _mux.SpectatorBroadcast = SendToSpectators;
                            if (IsHost) BroadcastLobby(); // so they see the picks and rules
                        }
                        else
                        {
                            AddChat("* " + pj.Name + " joined.");
                            _mux?.Announce();
                            SendPick();
                            if (IsHost) BroadcastLobby();
                        }
                        break;
                    case EvPeerLeft pl:
                        if (_names.TryGetValue(pl.Slot, out var leftName)) AddChat("* " + leftName + " left.");
                        _names.Remove(pl.Slot);
                        _peerRatings.Remove(pl.Slot);
                        _hellos.Remove(pl.Slot);
                        _pings.Remove(pl.Slot);
                        _mux?.RemovePeer(pl.Slot);
                        _lobby.Picks.Remove(pl.Slot);
                        _lobby.Ready.Remove(pl.Slot);
                        if (pl.Slot < Msg.FirstSpectatorSlot && _lobby.Matches > 0)
                        {
                            // The score is kept by seat; a new player in that seat starts a new score.
                            _lobby.Wins.Clear();
                            _lobby.Matches = 0;
                        }
                        MatchSession.Current?.Core.OnPeerLeft(pl.Slot);
                        if (IsHost) BroadcastLobby();
                        break;
                    case EvHostChanged h:
                        _hostSlot = h.HostSlot;
                        if (IsHost)
                        {
                            AddChat("* You are the host now.");
                            BroadcastLobby();
                        }
                        else AddChat("* " + NameOf(h.HostSlot) + " is the host now.");
                        break;
                    case EvRoomUpdated u:
                        _maxPlayers = u.MaxPlayers; _roomName = u.Name; _roomVisibility = u.Visibility; _roomHasPassword = u.HasPassword;
                        if (!u.HasPassword) _roomPassword = "";
                        AddChat("* Room settings changed.");
                        break;
                    case EvRoomLeft left:
                        MatchSession.Current?.End(left.Reason);
                        Notify(left.Reason);
                        _rejoinCode = null;
                        LeaveRoomState();
                        break;
                    case EvError e:
                        OnServerError(e.Text);
                        break;
                    case EvRoomList l:
                        _rooms = l.Rooms;
                        _playersOnline = l.PlayersOnline;
                        break;
                    case EvQueue q:
                        _searching = q.Searching; _searchSecs = q.Seconds; _searchPlayers = q.PlayersSearching;
                        break;
                    case EvRatingChanged rc:
                        int delta = rc.NewRating - rc.OldRating;
                        _ratingNote = (rc.Result == 1 ? "You beat " : rc.Result == 0 ? "You lost to " : "Draw with ") + rc.Opponent +
                                      ". Rating " + rc.OldRating + " -> " + rc.NewRating + " (" + (delta >= 0 ? "+" : "") + delta + ")";
                        AddChat("* " + _ratingNote);
                        Notify(_ratingNote);
                        break;
                    case EvLeaderboard lb:
                        _leaders = lb.Rows;
                        break;
                    case EvReportSaved rs:
                        _reportWaiting = false;
                        _reportStatus = "Sent. Your report id is " + rs.Id + ". Mention it when you ask for help.";
                        break;
                    case EvPong p:
                        _rtt = p.RttMs;
                        break;
                    case EvData d:
                        HandleData(d.FromSlot, d.Payload);
                        break;
                    case EvDisconnected dc:
                        OnDisconnected(dc.Reason);
                        return;
                }
            }
            DrainDirect();
        }

        private void OnRoomJoined(EvRoomJoined j)
        {
            bool sameRoom = _inRoom && _roomCode == j.Code; // moved to another seat when the room got smaller
            if (!sameRoom) DropMux();
            _inRoom = true; _roomCode = j.Code; _mySlot = j.MySlot; _hostSlot = j.HostSlot; _maxPlayers = j.MaxPlayers;
            _roomName = j.Name; _roomVisibility = j.Visibility; _roomHasPassword = j.HasPassword; _ranked = j.Ranked;
            _roomPassword = _pendingPassword ?? "";
            _joinTarget = null; _joinPassword = ""; _page = Page.Browser; _inMatchFlag = false; _searching = false;
            _rejoinCode = null; _rejoinAt = -1;
            _names.Clear(); _peerRatings.Clear(); _hellos.Clear(); _pings.Clear();
            _names[_mySlot] = _nameField;
            _early.Clear();
            if (!sameRoom)
            {
                _lobby = NewLobby();
                _chat.Clear();
                _myReady = false;
                _lastMatchNote = null;
                _ratingNote = null;
                AddChat(_ranked ? "* Ranked match found! Pick your Digimon and press Ready." :
                        IsSpectator ? "* You are watching " + _roomName + "." : "* You joined " + _roomName + ".");
            }
            else
            {
                _lobby.Picks.Clear();
                _lobby.Ready.Clear();
                _lobby.Wins.Clear(); // seats changed, so the score by seat no longer fits
                _lobby.Matches = 0;
                _myReady = false;
            }
            SendPick();
            if (!_show) OpenScreen();
        }

        private LobbyInfo NewLobby() => new LobbyInfo
        {
            InputDelay = Mathf.Clamp(Plugin.DefaultInputDelay.Value, 0, 12),
            Stage = (int)GameData.STAGES._RANDOM_,
            Rounds = _ranked ? 2 : 1,
            AutoDelay = true,
        };

        private void OnServerError(string text)
        {
            if (_rejoinCode != null && _rejoinTries > 0 && !_inRoom)
            {
                if ((text.Contains("in a match") || text.Contains("is full")) && _rejoinTries < 6) { _rejoinAt = Time.unscaledTime + 3f; return; }
                Notify("Could not get back into the room: " + text);
                _rejoinCode = null;
                return;
            }
            if (_reportWaiting && (text.Contains("report") || text.Contains("minute")))
            {
                _reportWaiting = false;
                SaveReportLocally(_reportBody, "The server did not take the report (" + text + ")");
                return;
            }
            Notify(text);
            if (text.StartsWith("Version mismatch"))
            {
                _versionRefused = true;
                _wantConnection = false;
                _client?.Dispose(); // the server hangs up too; show the offline page with the update notice
                _client = null;
                _status = "This lobby server runs a different version of the online mod.";
            }
            if (!text.Contains("password")) _joinTarget = null; // keep the password box open to retry
        }

        private void OnDisconnected(string reason)
        {
            if (_inRoom && _wantConnection)
            {
                _rejoinCode = _roomCode;
                _rejoinPassword = _roomPassword;
                _rejoinSpectate = IsSpectator;
                _rejoinTries = 0;
            }
            MatchSession.Current?.End("Lost the connection to the lobby server.");
            _client = null;
            _searching = false;
            LeaveRoomState();
            if (_wantConnection && !_versionRefused)
            {
                ScheduleReconnect();
                if (_reconnectTries == 0) Notify("Lost the connection to the lobby server. Reconnecting...");
            }
            else
            {
                _status = "Disconnected: " + reason;
                if (!_versionRefused) Notify("Disconnected from the lobby server: " + reason);
            }
        }

        private void ScheduleReconnect()
        {
            if (_reconnectTries >= ReconnectDelays.Length)
            {
                _reconnectAt = -1;
                _wantConnection = false;
                _rejoinCode = null;
                _status = "Could not reconnect to the lobby server. Check your internet connection and try again.";
                return;
            }
            float wait = ReconnectDelays[_reconnectTries];
            _reconnectAt = Time.unscaledTime + wait;
            _status = "Connection lost. Reconnecting in " + wait + " s (try " + (_reconnectTries + 1) + " of " + ReconnectDelays.Length + ")...";
        }

        private void StopReconnecting()
        {
            _reconnectAt = -1;
            _wantConnection = false;
            _rejoinCode = null;
            _status = null;
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
                        if (from >= Msg.FirstSpectatorSlot) break;
                        _lobby.Picks[from] = PlayerPick.Read(r);
                        if (IsHost) BroadcastLobby();
                        break;
                    case GameMsg.Lobby:
                        if (from != _hostSlot) break;
                        var mine = MyPick();
                        _lobby = LobbyInfo.Read(r);
                        if (mine != null) _lobby.Picks[_mySlot] = mine;
                        break;
                    case GameMsg.Start:
                        if (from != _hostSlot) break;
                        StartLocal(MatchConfig.Read(r));
                        break;
                    case GameMsg.LobbyReady:
                    {
                        bool ready = r.ReadBoolean();
                        if (!IsHost || from >= Msg.FirstSpectatorSlot) break;
                        if (ready) _lobby.Ready.Add(from); else _lobby.Ready.Remove(from);
                        BroadcastLobby();
                        break;
                    }
                    case GameMsg.Chat:
                    {
                        string text = CleanText(r.ReadString(), GameMsg.MaxChat);
                        if (text.Length > 0) AddChat(NameOf(from) + ": " + text);
                        break;
                    }
                    case GameMsg.PingInfo:
                    {
                        var p = new PingReport { Server = r.ReadInt16() };
                        int n = r.ReadByte();
                        for (int i = 0; i < n; i++) { int slot = r.ReadByte(); p.Direct[slot] = r.ReadInt16(); }
                        _pings[from] = p;
                        break;
                    }
                    case GameMsg.P2PHello:
                        _hellos[from] = payload;
                        if (_mux != null) _mux.OnHello(from, r);
                        break;
                    case GameMsg.Seq:
                        // A P2P match message that came through the server (fallback, a player without direct links, or for spectators).
                        EnsureMux().OnRelayed(from, r);
                        break;
                    case GameMsg.Ready:
                    case GameMsg.Input:
                    case GameMsg.StateCheck:
                        // A fast peer can talk lockstep before our Start arrives; keep it for the session.
                        // (A spectator who joined mid-match also hears the running match; that is dropped as stale.)
                        if (_early.Count > 0 && Time.unscaledTime - _earlySince > 3f) _early.Clear(); // stale: a match we only walked in on
                        if (_early.Count == 0) _earlySince = Time.unscaledTime;
                        if (_early.Count < 4000) _early.Add(new KeyValuePair<int, byte[]>(from, payload));
                        break;
                }
            }
        }

        private void Broadcast(byte[] payload) => _client?.Broadcast(payload);
        private void SendToSpectators(byte[] payload) => _client?.SendToSpectators(payload);

        private void SendPick()
        {
            if (IsSpectator || !_inRoom) return;
            var pick = MyPick();
            if (pick == null) return;
            _lobby.Picks[_mySlot] = pick;
            Broadcast(GameMsg.Build(GameMsg.Pick, w => pick.Write(w)));
        }

        private void BroadcastLobby()
        {
            if (!IsHost) return;
            var l = _lobby;
            Broadcast(GameMsg.Build(GameMsg.Lobby, w => l.Write(w)));
        }

        private PlayerPick MyPick()
        {
            if (_chars.Count == 0 || IsSpectator) return null;
            var c = _chars[Mathf.Clamp(_charIdx, 0, _chars.Count - 1)];
            return new PlayerPick
            {
                Character = c.Name,
                Color = c.Colors[Mathf.Clamp(_colorIdx, 0, c.Colors.Count - 1)],
                AltForm = c.Evolutions.Count > 0 ? c.Evolutions[Mathf.Clamp(_evoIdx, 0, c.Evolutions.Count - 1)].Key : 1,
            };
        }

        private void SetReady(bool ready)
        {
            _myReady = ready;
            if (IsHost)
            {
                if (ready) _lobby.Ready.Add(_mySlot); else _lobby.Ready.Remove(_mySlot);
                BroadcastLobby();
            }
            else Broadcast(GameMsg.Build(GameMsg.LobbyReady, w => w.Write(ready)));
        }

        private void SendChat()
        {
            string text = CleanText(_chatField, GameMsg.MaxChat);
            _chatField = "";
            if (text.Length == 0 || !_inRoom) return;
            Broadcast(GameMsg.Build(GameMsg.Chat, w => w.Write(text)));
            AddChat(_nameField + ": " + text);
        }

        // ------------------------------------------------------------------ connection

        private void BeginConnect(string address)
        {
            if (_connecting) return;
            Plugin.PlayerName.Value = _nameField;
            _connecting = true;
            _connectDone = false;
            _connected = null;
            _connectError = null;
            _address = address;
            _wantConnection = true;
            _versionRefused = false;
            _reconnectAt = -1;
            if (_reconnectTries == 0) _status = "Connecting to " + address + " ...";
            string name = _nameField, token = _token;
            new Thread(() =>
            {
                try
                {
                    var c = new RelayClient { AccountToken = token };
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
            if (_connected != null && !_wantConnection)
            {
                _connected.Dispose(); // the player stopped reconnecting while this attempt was running
                _connected = null;
                return;
            }
            if (_connected != null)
            {
                _client = _connected;
                _connected = null;
                _status = null;
                _nextPing = 0;
                _nextListAt = 0;
                if (_rejoinCode != null)
                {
                    _rejoinTries = 1;
                    _pendingPassword = _rejoinPassword;
                    _client.JoinRoom(_rejoinCode, _rejoinPassword, _rejoinSpectate);
                }
                else if (!_inRoom) _page = Page.Browser;
            }
            else if (_wantConnection && _reconnectTries > 0)
            {
                Plugin.Log.LogWarning("Reconnect failed: " + _connectError);
                ScheduleReconnect();
            }
            else
            {
                _wantConnection = false;
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
            _wantConnection = false;
            _rejoinCode = null;
            _reconnectAt = -1;
            MatchSession.Current?.End("You left the match.");
            _client?.Dispose();
            _client = null;
            _localServer?.Stop();
            _localServer = null;
            _status = null;
            _rooms = new RoomInfo[0];
            _searching = false;
            LeaveRoomState();
        }

        private void JoinRoom(string code, string password, bool spectate)
        {
            if (_client == null || string.IsNullOrEmpty(code)) return;
            _pendingPassword = password ?? "";
            _client.JoinRoom(code, password, spectate);
        }

        private void QuickMatch()
        {
            _pendingPassword = "";
            _client?.QuickMatch();
        }

        private void QueueRanked()
        {
            _pendingPassword = "";
            _client?.QueueRanked();
        }

        private void LeaveRoom()
        {
            _client?.LeaveRoom();
            LeaveRoomState();
        }

        // ------------------------------------------------------------------ P2P

        private P2PMux EnsureMux()
        {
            if (_mux != null) return _mux;
            // Spectators never open direct links: they only listen through the server.
            _mux = new P2PMux(_mySlot, Plugin.AllowP2P.Value && !IsSpectator, Broadcast, _client?.Arrived) { Log = m => Plugin.Log.LogInfo(m) };
            if (SpectatorSlots.Any()) _mux.SpectatorBroadcast = SendToSpectators;
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
            _inRoom = false; _ranked = false; _roomCode = null; _roomName = null; _mySlot = -1; _nextListAt = 0;
            _names.Clear(); _peerRatings.Clear(); _lobby.Picks.Clear(); _lobby.Ready.Clear(); _early.Clear(); _pings.Clear();
            _myReady = false;
            if (_page == Page.RoomSettings) _page = Page.Browser;
        }

        // ------------------------------------------------------------------ automatic input delay

        /// <summary>Every player shares its ping to the server and over each direct link, so the host can pick the delay.</summary>
        private void SendPingInfo()
        {
            if (!_inRoom || IsSpectator || _rtt < 0) return;
            var direct = new List<KeyValuePair<int, double>>();
            if (_mux != null)
                foreach (int s in PlayerSlots)
                {
                    double d = s == _mySlot ? -1 : _mux.DirectRtt(s);
                    if (d > 0) direct.Add(new KeyValuePair<int, double>(s, d));
                }
            short server = (short)Mathf.Clamp((float)_rtt, 0, 30000);
            Broadcast(GameMsg.Build(GameMsg.PingInfo, w =>
            {
                w.Write(server);
                w.Write((byte)direct.Count);
                foreach (var kv in direct) { w.Write((byte)kv.Key); w.Write((short)Mathf.Clamp((float)kv.Value, 0, 30000)); }
            }));
        }

        private int ComputeAutoDelay()
        {
            var slots = PlayerSlots.ToList();
            var server = new Dictionary<int, double>();
            foreach (int s in slots)
            {
                if (s == _mySlot) server[s] = Math.Max(0, _rtt);
                else if (_pings.TryGetValue(s, out var p)) server[s] = p.Server;
                else server[s] = Math.Max(0, _rtt); // not heard yet: assume a ping like ours
            }
            double Direct(int a, int b)
            {
                if (!_lobby.P2P) return -1;
                if (a == _mySlot && _mux != null) return _mux.DirectRtt(b);
                if (b == _mySlot && _mux != null) return _mux.DirectRtt(a);
                if (_pings.TryGetValue(a, out var pa) && pa.Direct.TryGetValue(b, out double x)) return x;
                if (_pings.TryGetValue(b, out var pb) && pb.Direct.TryGetValue(a, out double y)) return y;
                return -1;
            }
            return LobbyInfo.AutoDelayFor(slots, server, Direct);
        }

        private void UpdateAutoDelay()
        {
            if (!IsHost || !_lobby.AutoDelay) return;
            int d = ComputeAutoDelay();
            if (d != _lobby.InputDelay)
            {
                _lobby.InputDelay = d;
                BroadcastLobby();
            }
        }

        // ------------------------------------------------------------------ starting and ending matches

        private string NotReadyReason()
        {
            var slots = PlayerSlots.ToList();
            if (slots.Count < 2) return "Waiting for another player.";
            var waiting = slots.Where(s => (s != _mySlot || _ranked) && !_lobby.Ready.Contains(s)).Select(NameOf).ToList();
            if (waiting.Count > 0) return "Waiting for " + string.Join(", ", waiting.ToArray()) + " to press Ready.";
            foreach (int s in slots)
            {
                if (!_lobby.Picks.TryGetValue(s, out var p) || string.IsNullOrEmpty(p.Character)) return NameOf(s) + " has not picked a Digimon yet.";
                if (!GameBridge.CharacterExists(p.Character)) return NameOf(s) + " picked " + p.Character + ", which your game version does not have.";
            }
            if (!GameBridge.InMenus) return "Go back to a menu screen to start.";
            return null;
        }

        private void HostStart()
        {
            string why = NotReadyReason();
            if (why != null) { Notify(why); return; }
            var slots = PlayerSlots.ToList();
            var rng = new System.Random();
            int stage = _lobby.Stage;
            if (stage == (int)GameData.STAGES._RANDOM_)
            {
                var pool = GameBridge.UnlockedStages();
                stage = pool.Count > 0 ? (int)pool[rng.Next(pool.Count)] : (int)GameData.STAGES.DANGEROUS_FOREST;
            }
            var settings = GameBridge.CaptureSettings();
            settings["BattleRounds"] = Mathf.Clamp(_lobby.Rounds - 1, 0, 3).ToString();
            if (_ranked)
            {
                // Same rules for every ranked game: best of three, no items, no stage hazards.
                settings["BattleRounds"] = "1";
                settings["ItemsEnabled"] = "False";
                settings["HazardsEnabled"] = "False";
            }
            if (_lobby.AutoDelay) _lobby.InputDelay = ComputeAutoDelay();
            var cfg = new MatchConfig
            {
                Seed = rng.Next(1, int.MaxValue),
                InputDelay = _lobby.InputDelay,
                Stage = stage,
                Song = SoundManager.SONG_DEFAULT_STAGE,
                BattleRoyale = slots.Count > 2,
                P2P = _lobby.P2P,
                Slots = slots.ToArray(),
                Names = slots.Select(NameOf).ToArray(),
                Picks = slots.Select(s => _lobby.Picks[s].Clone()).ToArray(),
                Settings = settings,
            };
            _lobby.Ready.Clear();
            BroadcastLobby();
            Broadcast(GameMsg.Build(GameMsg.Start, w => cfg.Write(w)));
            StartLocal(cfg);
        }

        private void StartLocal(MatchConfig cfg)
        {
            int me = cfg.PlayerOfSlot(_mySlot);
            if (me < 0 && !IsSpectator) { Notify("A match started without you (the room was full)."); return; }
            _myReady = false;
            _lastMatchNote = null;
            _ratingNote = null;
            var controls = Plugin.LocalControls.Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            EnableGameInput(true);
            Action<byte[]> send = Broadcast;
            if (cfg.P2P)
            {
                var mux = EnsureMux();
                mux.SpectatorBroadcast = SpectatorSlots.Any() ? (Action<byte[]>)SendToSpectators : null;
                if (!IsSpectator)
                {
                    mux.BeginMatch(cfg.Seed, cfg.Slots);
                    send = mux.Send;
                }
            }
            MatchSession.Begin(cfg, me, IsHost, _hostSlot, send, controls);
            if (IsHost) { _client?.SetInMatch(true, cfg.Seed); _inMatchFlag = true; } // the lobby browser shows the room as busy
            // Messages that arrived just before Start belong to this match; anything older is from a match we only walked in on.
            if (Time.unscaledTime - _earlySince < 3f)
                foreach (var e in _early) MatchSession.Current?.Core.Handle(e.Key, e.Value);
            _early.Clear();
            _show = false;
        }

        /// <summary>Called by MatchSession when a match ends, normally (abortReason null) or not.</summary>
        public void OnMatchEnded(MatchSession session, string abortReason)
        {
            var cfg = session.Core.Config;
            _early.Clear();
            _reopenWhenMenus = _inRoom;
            if (abortReason == null)
            {
                int winner = GameBridge.ReadWinner(cfg);
                string text = winner >= 0 ? NameOf(winner) + " wins!" : "Draw.";
                _lastMatchNote = text;
                AddChat("* " + text);
                if (IsHost)
                {
                    if (winner >= 0) _lobby.Wins[winner] = (_lobby.Wins.TryGetValue(winner, out int w) ? w : 0) + 1;
                    _lobby.Matches++;
                    BroadcastLobby();
                }
                if (_ranked && !session.IsSpectator) _client?.ReportResult(cfg.Seed, winner >= 0 ? winner : Msg.NoWinner);
            }
            else
            {
                _lastMatchNote = "The last match ended early: " + abortReason;
                AddChat("* " + _lastMatchNote);
                Notify(abortReason);
            }
        }

        private void TryAutoStartRanked()
        {
            if (!_ranked || !IsHost || MatchSession.Current != null || PlayerSlots.Count() != 2) return;
            if (NotReadyReason() == null) HostStart();
        }

        // ------------------------------------------------------------------ problem reports

        private void SendReport()
        {
            string body = ProblemReport.Build(CleanText(_reportText, 300), _client != null ? _address : "not connected");
            _reportFile = null;
            byte[] gz = ProblemReport.Gzip(body);
            if (_client == null || gz.Length > 240 * 1024)
            {
                SaveReportLocally(body, _client == null ? "You are not connected" : "The log is too big to send");
                return;
            }
            _client.SendReport(CleanText(_reportText, 300), gz);
            _reportWaiting = true;
            _reportBody = body;
            _reportDeadline = Time.unscaledTime + 15f;
            _reportStatus = "Sending...";
        }

        private void SaveReportLocally(string body, string why)
        {
            try
            {
                _reportFile = ProblemReport.SaveLocally(body ?? ProblemReport.Build(CleanText(_reportText, 300), _address ?? "not connected"));
                _reportStatus = why + ", so the report was saved to " + _reportFile + ". Attach that file when you ask for help.";
            }
            catch (Exception e)
            {
                _reportStatus = why + ", and saving the report failed: " + e.Message;
            }
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            var kb = Keyboard.current;
            bool toggled = kb != null && kb[_toggleKey].wasPressedThisFrame;
            if (toggled) { if (_show) CloseScreen(); else OpenScreen(); }
            bool menuInput = _show && MatchSession.Current == null;
            if (menuInput && !toggled && _nav.Poll())
            {
                var back = _nav.BackAction;
                if (back != null) back(); else CloseScreen();
            }
            _nav.RunDeferred();

            float now = Time.unscaledTime;
            if (_client != null && now >= _nextPing)
            {
                _client.Ping();
                _nextPing = now + 2f;
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
            if (_reconnectAt > 0 && now >= _reconnectAt && _client == null && !_connecting)
            {
                _reconnectTries++;
                BeginConnect(_address);
                _status = "Reconnecting to the lobby server (try " + _reconnectTries + " of " + ReconnectDelays.Length + ")...";
            }
            if (_rejoinAt > 0 && now >= _rejoinAt && _client != null && !_inRoom && _rejoinCode != null)
            {
                _rejoinAt = -1;
                _rejoinTries++;
                _client.JoinRoom(_rejoinCode, _rejoinPassword, _rejoinSpectate);
            }
            UpdateP2P();
            bool inMatch = MatchSession.Current != null;
            if (!inMatch) PumpNetwork();
            if (_inMatchFlag && !inMatch) { _client?.SetInMatch(false); _inMatchFlag = false; }

            if (_show && _client != null && !_inRoom && _page == Page.Browser && now >= _nextListAt)
            {
                _client.ListRooms();
                _nextListAt = now + 3f;
            }
            if (_show && _client != null && _page == Page.Leaderboard && now >= _nextLeadersAt)
            {
                _client.GetLeaderboard();
                _nextLeadersAt = now + 15f;
            }
            if (_inRoom && !inMatch && now >= _nextPingInfo)
            {
                _nextPingInfo = now + 2f;
                SendPingInfo();
                UpdateAutoDelay();
            }
            TryAutoStartRanked();
            if (_reportWaiting && now > _reportDeadline)
            {
                _reportWaiting = false;
                SaveReportLocally(_reportBody, "The server did not answer");
            }
            if (_reopenWhenMenus && !inMatch && GameBridge.InMenus)
            {
                _reopenWhenMenus = false;
                if (_inRoom) OpenScreen();
            }

            // While the online screen is up, the menus behind it must not react to the same buttons.
            if (_show && !inMatch)
            {
                EnableGameInput(false);
                _enableInputAtFrame = Time.frameCount + 2;
            }
            else if (Time.frameCount >= _enableInputAtFrame) EnableGameInput(true);
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
    }
}
