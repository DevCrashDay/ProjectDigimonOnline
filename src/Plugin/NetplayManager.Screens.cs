using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using DigimonNetplay.Shared;
using UnityEngine;

namespace DigimonNetplay
{
    /// <summary>The online screen. Every button goes through OnlineNav so it works with a gamepad, the keyboard and the mouse.</summary>
    internal sealed partial class NetplayManager
    {
        private static readonly string[] PlayerChoices = { "1 vs 1", "4 players" };
        private static readonly string[] VisibilityChoices = { "Public", "Password", "Private (code)" };
        private static readonly string[] ConnectionChoices = { "Through the server", "Direct (P2P)" };
        private static readonly string[] DelayChoices = { "Automatic", "Manual" };

        private const float GridTile = 78f;
        private const int GridRows = 3;
        private int _gridSnappedFor = -1;

        private string PageKey()
        {
            if (!GameBridge.GameReady) return "loading";
            if (_page == Page.Report) return "report";
            if (_client == null) return "offline";
            if (_inRoom) return _page == Page.RoomSettings && IsHost ? "roomsettings" : "room";
            return _page.ToString();
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
            var panel = new Rect(width / 2 - 410, 40, 820, OnlineSkin.CanvasHeight - 80);
            GUI.Box(panel, GUIContent.none, OnlineSkin.Panel);
            _nav.Begin(PageKey());
            GUILayout.BeginArea(new Rect(panel.x + 26, panel.y + 14, panel.width - 52, panel.height - 28));
            GUILayout.Label("ONLINE VERSUS", OnlineSkin.Title);
            _nav.BeginScroll();
            string key = PageKey();
            switch (key)
            {
                case "loading": GUILayout.Label("Waiting for the game to finish loading..."); break;
                case "report": DrawReport(); break;
                case "offline": DrawOffline(); break;
                case "room": DrawLobby(); break;
                case "roomsettings": DrawRoomSettings(); break;
                case "Create": DrawCreate(); break;
                case "Ranked": DrawRanked(); break;
                case "Leaderboard": DrawLeaderboard(); break;
                default: DrawBrowser(); break;
            }
            _nav.EndScroll();
            GUILayout.BeginHorizontal();
            GUILayout.Label(session == null ? "A / Enter: select     B / Esc: back     " + _toggleKey + ": open or close" : _toggleKey + ": hide", OnlineSkin.Hint);
            _nav.Button("Back", () => (_nav.BackAction ?? CloseScreen)(), true, GUILayout.Width(140));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            _nav.End();
            DrawNotice(width);
        }

        private void DrawNotice(float width)
        {
            if (_notice != null && Time.unscaledTime < _noticeUntil)
                GUI.Box(new Rect(width / 2 - 380, 6, 760, 32), _notice, OnlineSkin.Notice);
        }

        private void DrawHud(MatchSession s, float width)
        {
            var c = s.Core;
            string text = (s.IsSpectator ? "Watching   " : "") + s.Status + "   ping " + (_rtt < 0 ? "?" : _rtt.ToString("0")) + " ms   delay " + c.Delay + "f";
            if (c.DesyncCount > 0) text += "   desyncs fixed " + c.DesyncCount;
            if (s.StallMs > 50) text += "   waiting " + s.StallMs.ToString("0") + " ms";
            if (s.Core.Config.P2P && _mux != null && !s.IsSpectator)
            {
                var others = s.Core.Config.Slots.Where(x => x != _mySlot).ToList();
                text += "   direct " + others.Count(_mux.IsDirect) + "/" + others.Count;
            }
            GUI.Label(new Rect(10, OnlineSkin.CanvasHeight - 30, width - 20, 26), text, OnlineSkin.Hud);
        }

        private string Ping => _rtt < 0 ? "?" : _rtt.ToString("0");

        private void DrawUpdateBanner()
        {
            if (!UpdateAvailable && !_versionRefused) return;
            GUILayout.BeginVertical(OnlineSkin.Row);
            if (_versionRefused && !UpdateAvailable)
            {
                // The server is the old one: nothing to download on our side.
                GUILayout.Label("This lobby server runs an older version of the online mod than yours (" + BuildInfo.Version + "). Ask its owner to update the server.", OnlineSkin.Warn);
                GUILayout.EndVertical();
                return;
            }
            GUILayout.Label(_versionRefused
                ? "This lobby server needs version " + _latestVersion + " of the online mod. You have " + BuildInfo.Version + "."
                : "Version " + _latestVersion + " of the online mod is out. You have " + BuildInfo.Version + ".", OnlineSkin.Warn);
            string url = _downloadUrl.Length > 0 ? _downloadUrl : BuildInfo.ReleasesUrl;
            _nav.Button("Download the update", () => OpenUrl(url), true, GUILayout.Width(300));
            GUILayout.EndVertical();
        }

        // ------------------------------------------------------------------ not connected

        private void DrawOffline()
        {
            _nav.Back(null);
            DrawUpdateBanner();
            bool hasServer = EffectiveServer.Length > 0;
            bool reconnecting = _reconnectAt > 0 || (_connecting && _reconnectTries > 0);
            if (_connecting || reconnecting) GUILayout.Label(_status);
            else if (_status != null) GUILayout.Label(_status, OnlineSkin.Warn);
            else if (!hasServer) GUILayout.Label("No lobby server is set yet. Enter one under Server settings.", OnlineSkin.Warn);

            GUILayout.Label("Your name");
            _nameField = _nav.Text("name", _nameField, 20, false, null, GUILayout.Width(360));
            if (reconnecting) _nav.Button("Stop reconnecting", StopReconnecting, true, GUILayout.Width(360));
            else _nav.Button(_status == null ? "Connect to the lobby server" : "Try again", () => BeginConnect(EffectiveServer), !_connecting && hasServer, GUILayout.Width(360));

            GUILayout.Space(10);
            _nav.Toggle(_showAdvanced, _showAdvanced ? "Server settings  (hide)" : "Server settings", v => _showAdvanced = v, true, OnlineSkin.Link);
            if (_showAdvanced)
            {
                GUILayout.Label("Lobby server address (wss://your.domain, ws://host:port or host:port). Leave empty for the official server.", OnlineSkin.Small);
                GUILayout.BeginHorizontal();
                _serverField = _nav.Text("server", _serverField, 120, false, null, GUILayout.Width(500));
                _nav.Button("Connect", () =>
                {
                    Plugin.LobbyServer.Value = _serverField.Trim() == Plugin.OfficialServer ? "" : _serverField.Trim();
                    if (EffectiveServer.Length > 0) BeginConnect(EffectiveServer);
                }, !_connecting);
                GUILayout.EndHorizontal();
                if (Plugin.OfficialServer.Length > 0)
                    _nav.Button("Use the official server", () => { _serverField = Plugin.OfficialServer; Plugin.LobbyServer.Value = ""; }, true, GUILayout.Width(300));
                GUILayout.Space(8);
                GUILayout.Label("Or host a private server on this PC. Friends connect to your public IP and port; forward that TCP port on your router.", OnlineSkin.Small);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(60));
                _hostPortField = _nav.Text("port", _hostPortField, 6, false, null, GUILayout.Width(100));
                _nav.Button("Host on this PC", HostOnThisPc, !_connecting);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(10);
            _nav.Button("Report a problem", OpenReport, true, GUILayout.Width(260));
        }

        private void OpenReport()
        {
            _page = Page.Report;
            _reportStatus = null;
            _reportFile = null;
        }

        // ------------------------------------------------------------------ lobby browser

        private void DrawBrowser()
        {
            _nav.Back(null);
            DrawUpdateBanner();
            string me = _accountName != null ? _accountName + (_rank > 0 ? "  (" + _rating + ")" : "") : _nameField;
            GUILayout.Label((_serverName ?? "Lobby server") + "   |   " + _playersOnline + " online   |   ping " + Ping + " ms   |   you are " + me, OnlineSkin.Small);
            if (_searching)
            {
                GUILayout.BeginHorizontal(OnlineSkin.Row);
                GUILayout.Label("Searching for a ranked opponent... " + _searchSecs / 60 + ":" + (_searchSecs % 60).ToString("00") + "   (" + _searchPlayers + " searching)", GUILayout.Width(560));
                _nav.Button("Cancel", () => _client?.CancelQueue(), true, GUILayout.Width(140));
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            _nav.Button("Quick match", QuickMatch, true, GUILayout.Width(250), GUILayout.Height(46));
            _nav.Button("Ranked", () => { _page = Page.Ranked; _client?.GetLeaderboard(); }, true, GUILayout.Width(230), GUILayout.Height(46));
            _nav.Button("Create room", () =>
            {
                _page = Page.Create;
                if (_createName.Length == 0) _createName = _nameField + "'s room";
            }, true, GUILayout.Width(250), GUILayout.Height(46));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            _nav.Button("Leaderboard", () => { _page = Page.Leaderboard; _nextLeadersAt = 0; }, true, GUILayout.Width(190));
            _nav.Button("Refresh", () => _nextListAt = 0, true, GUILayout.Width(130));
            _nav.Button("Report a problem", OpenReport, true, GUILayout.Width(230));
            GUILayout.FlexibleSpace();
            _nav.Button("Disconnect", Disconnect, true, GUILayout.Width(160));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Room code", GUILayout.Width(120));
            _codeField = _nav.Text("code", _codeField.ToUpperInvariant(), 5, false, () => JoinRoom(_codeField, _codePassword, false), GUILayout.Width(100)).ToUpperInvariant();
            GUILayout.Label("Password", GUILayout.Width(100));
            _codePassword = _nav.Text("codepw", _codePassword, 32, true, null, GUILayout.Width(140));
            _nav.Button("Join", () => JoinRoom(_codeField, _codePassword, false), _codeField.Length > 0);
            _nav.Button("Watch", () => JoinRoom(_codeField, _codePassword, true), _codeField.Length > 0);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("OPEN ROOMS", OnlineSkin.Heading);
            if (_rooms.Length == 0)
            {
                GUILayout.Label("No public rooms right now. Try Quick match, create a room, or join a private room with its code.", OnlineSkin.Small);
                return;
            }
            foreach (var r in _rooms)
            {
                var room = r;
                bool full = r.Players >= r.MaxPlayers;
                GUILayout.BeginHorizontal(OnlineSkin.Row);
                GUILayout.Label((r.Ranked ? "[RANKED]  " : r.Locked ? "[PW]  " : "") + r.Name, GUILayout.Width(290));
                GUILayout.Label(r.Ranked ? "" : r.HostName, OnlineSkin.Small, GUILayout.Width(140));
                GUILayout.Label(r.Players + "/" + r.MaxPlayers + (r.Spectators > 0 ? "  +" + r.Spectators + " watching" : ""), OnlineSkin.Small, GUILayout.Width(130));
                bool canJoin = !full && !r.InMatch && !r.Ranked;
                _nav.Button(r.Ranked ? "Ranked" : r.InMatch ? "In match" : full ? "Full" : "Join", () => PickRoom(room, false), canJoin, GUILayout.Width(100));
                _nav.Button("Watch", () => PickRoom(room, true), r.Spectators < Msg.MaxSpectators, GUILayout.Width(90));
                GUILayout.EndHorizontal();
                if (_joinTarget == r.Code)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Password for " + r.Name, GUILayout.Width(290));
                    _joinPassword = _nav.Text("rowpw", _joinPassword, 32, true, () => JoinRoom(room.Code, _joinPassword, _joinSpectate), GUILayout.Width(190));
                    _nav.Button(_joinSpectate ? "Watch" : "Join", () => JoinRoom(room.Code, _joinPassword, _joinSpectate), true, GUILayout.Width(90));
                    _nav.Button("Cancel", () => { _joinTarget = null; _joinPassword = ""; }, true, GUILayout.Width(110));
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void PickRoom(RoomInfo r, bool spectate)
        {
            if (r.Locked) { _joinTarget = r.Code; _joinPassword = ""; _joinSpectate = spectate; }
            else JoinRoom(r.Code, "", spectate);
        }

        private void DrawCreate()
        {
            _nav.Back(() => _page = Page.Browser);
            GUILayout.Label("CREATE A ROOM", OnlineSkin.Heading);
            GUILayout.Label("Room name");
            _createName = _nav.Text("roomname", _createName, 32, false, null, GUILayout.Width(500));
            GUILayout.Label("Players");
            _nav.Choice(_createPlayers, PlayerChoices, v => _createPlayers = v);
            GUILayout.Label("Who can join");
            _nav.Choice(_createVisibility, VisibilityChoices, v => _createVisibility = v);
            // Decided before the password box draws, so typing cannot add or remove a label mid-frame.
            bool needsPassword = _createVisibility == 1 && _createPassword.Trim().Length == 0;
            DrawVisibilityHelp(_createVisibility, ref _createPassword, "createpw");
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            _nav.Button("Create", () =>
            {
                _pendingPassword = _createVisibility == 1 ? _createPassword.Trim() : "";
                _client?.CreateRoom(_createPlayers == 0 ? 2 : 4, _createName,
                    _createVisibility == 2 ? RoomVisibility.Private : RoomVisibility.Public, _pendingPassword);
            }, !needsPassword, GUILayout.Width(200));
            _nav.Button("Cancel", () => _page = Page.Browser, true, GUILayout.Width(160));
            GUILayout.EndHorizontal();
            if (needsPassword) GUILayout.Label("Enter a password.", OnlineSkin.Small);
        }

        private void DrawVisibilityHelp(int visibility, ref string password, string fieldName)
        {
            switch (visibility)
            {
                case 0:
                    GUILayout.Label("Listed in the lobby. Anyone can join.", OnlineSkin.Small);
                    break;
                case 1:
                    GUILayout.Label("Listed in the lobby with a lock. Players need the password.", OnlineSkin.Small);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Password", GUILayout.Width(130));
                    password = _nav.Text(fieldName, password, 32, false, null, GUILayout.Width(260));
                    GUILayout.EndHorizontal();
                    break;
                default:
                    GUILayout.Label("Not listed. Share the room code with your friends.", OnlineSkin.Small);
                    break;
            }
        }

        // ------------------------------------------------------------------ ranked and leaderboard

        private void DrawRanked()
        {
            _nav.Back(() => _page = Page.Browser);
            GUILayout.Label("RANKED", OnlineSkin.Heading);
            if (_accountName == null)
            {
                GUILayout.Label("This server does not support ranked play, or your ranked account key could not be created.", OnlineSkin.Warn);
                _nav.Button("Back to the rooms", () => _page = Page.Browser, true, GUILayout.Width(300));
                return;
            }
            GUILayout.Label(_accountName, OnlineSkin.Heading);
            GUILayout.Label("Rating " + _rating + "     " + _wins + " wins, " + _losses + " losses     " +
                            (_rank > 0 ? "rank #" + _rank : "not on the leaderboard until your first ranked game"));
            GUILayout.Label("1 vs 1 against a player near your rating. Best of three rounds, no items, no stage hazards, random stage, " +
                            "played through the server. Winning raises your rating, losing lowers it. Leaving a ranked match counts as a loss.", OnlineSkin.Small);
            GUILayout.Space(6);
            if (_searching)
            {
                GUILayout.Label("Searching... " + _searchSecs / 60 + ":" + (_searchSecs % 60).ToString("00") + "   (" + _searchPlayers + " searching). The search widens the longer you wait.");
                _nav.Button("Cancel search", () => _client?.CancelQueue(), true, GUILayout.Width(300), GUILayout.Height(46));
            }
            else _nav.Button("Find a ranked match", QueueRanked, true, GUILayout.Width(360), GUILayout.Height(46));
            GUILayout.BeginHorizontal();
            _nav.Button("Leaderboard", () => { _page = Page.Leaderboard; _nextLeadersAt = 0; }, true, GUILayout.Width(220));
            _nav.Button("Back to the rooms", () => _page = Page.Browser, true, GUILayout.Width(260));
            GUILayout.EndHorizontal();
            GUILayout.Label("Your rating is tied to the file BepInEx/config/DigimonNetplay.account. Keep it to keep your rating.", OnlineSkin.Small);
        }

        private void DrawLeaderboard()
        {
            _nav.Back(() => _page = Page.Browser);
            GUILayout.Label("LEADERBOARD", OnlineSkin.Heading);
            if (_accountName != null)
                GUILayout.Label("You: " + _accountName + "   rating " + _rating + "   " + (_rank > 0 ? "rank #" + _rank : "unranked"), OnlineSkin.Small);
            GUILayout.BeginHorizontal();
            string web = WebUrl("/leaderboard");
            _nav.Button("Open in the browser", () => OpenUrl(web), web != null, GUILayout.Width(280));
            _nav.Button("Refresh", () => _nextLeadersAt = 0, true, GUILayout.Width(140));
            _nav.Button("Ranked", () => _page = Page.Ranked, true, GUILayout.Width(140));
            GUILayout.EndHorizontal();
            if (_leaders.Length == 0)
            {
                GUILayout.Label("No ranked games played yet.", OnlineSkin.Small);
                return;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("#", OnlineSkin.Small, GUILayout.Width(60));
            GUILayout.Label("Player", OnlineSkin.Small, GUILayout.Width(360));
            GUILayout.Label("Rating", OnlineSkin.Small, GUILayout.Width(110));
            GUILayout.Label("Won - lost", OnlineSkin.Small, GUILayout.Width(140));
            GUILayout.EndHorizontal();
            foreach (var row in _leaders)
            {
                bool mine = row.Name == _accountName;
                GUILayout.BeginHorizontal(OnlineSkin.Row);
                var style = mine ? OnlineSkin.Heading : GUI.skin.label;
                GUILayout.Label(row.Rank.ToString(), style, GUILayout.Width(60));
                GUILayout.Label(row.Name, style, GUILayout.Width(360));
                GUILayout.Label(row.Rating.ToString(), style, GUILayout.Width(110));
                GUILayout.Label(row.Wins + " - " + row.Losses, style, GUILayout.Width(140));
                GUILayout.EndHorizontal();
                _nav.Custom(null, null, null); // rows take the highlight so a gamepad can scroll the list
            }
        }

        // ------------------------------------------------------------------ problem report

        private void DrawReport()
        {
            _nav.Back(() => _page = Page.Browser);
            GUILayout.Label("REPORT A PROBLEM", OnlineSkin.Heading);
            GUILayout.Label("Sends the end of your game log and basic system info (Windows version, graphics card, mod settings) to the lobby server's owner. " +
                            "Your Windows user name, PC name and IP addresses are removed first.", OnlineSkin.Small);
            GUILayout.Label("What went wrong?");
            _reportText = _nav.Text("report", _reportText, 300, false, null, GUILayout.Width(740));
            GUILayout.BeginHorizontal();
            _nav.Button(_client != null ? "Send report" : "Save report", SendReport, !_reportWaiting, GUILayout.Width(250));
            if (_client != null) _nav.Button("Save to a file instead", () => SaveReportLocally(null, "Saved on request"), !_reportWaiting, GUILayout.Width(300));
            GUILayout.EndHorizontal();
            if (_reportStatus != null) GUILayout.Label(_reportStatus, _reportFile != null ? OnlineSkin.Warn : GUI.skin.label);
            if (_reportFile != null) _nav.Button("Open the game folder", () => Application.OpenURL("file:///" + Paths.GameRootPath.Replace('\\', '/')), true, GUILayout.Width(300));
            GUILayout.Space(6);
            _nav.Button("Done", () => _page = Page.Browser, true, GUILayout.Width(160));
        }

        // ------------------------------------------------------------------ room

        private void DrawLobby()
        {
            _nav.Back(null);
            bool inMatch = MatchSession.Current != null;
            GUILayout.Label((_ranked ? "RANKED  " : "") + (_roomName ?? "Room").ToUpperInvariant(), OnlineSkin.Heading);
            string access = _ranked ? "matched by the server" : _roomVisibility == RoomVisibility.Private ? "private, share the code" : _roomHasPassword ? "public with password" : "public";
            int players = PlayerSlots.Count(), watching = SpectatorSlots.Count();
            GUILayout.Label((_ranked ? "" : "Code " + _roomCode + "   ") + "(" + access + ")   |   " + players + "/" + _maxPlayers + " players" +
                            (watching > 0 ? ", " + watching + " watching" : "") + "   |   ping " + Ping + " ms", OnlineSkin.Small);
            if (_lastMatchNote != null) GUILayout.Label(_lastMatchNote, OnlineSkin.Heading);
            if (_ratingNote != null) GUILayout.Label(_ratingNote);
            string score = ScoreLine();
            if (score != null) GUILayout.Label("Score: " + score);

            foreach (int slot in PlayerSlots.ToList()) DrawPlayerRow(slot);
            if (watching > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Watching: " + string.Join(", ", SpectatorSlots.Select(NameOf).ToArray()), OnlineSkin.Small, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
                if (IsHost && !_ranked)
                {
                    GUILayout.BeginHorizontal();
                    foreach (int s in SpectatorSlots.ToList())
                    {
                        int slot = s;
                        _nav.Button("Remove " + NameOf(s), () => _client?.Kick(slot), true);
                    }
                    GUILayout.EndHorizontal();
                }
            }

            if (inMatch)
            {
                GUILayout.Label(MatchSession.Current.IsSpectator ? "You are watching the match." : "Match in progress.");
                _nav.Button(MatchSession.Current.IsSpectator ? "Stop watching" : "Leave match",
                    () => MatchSession.Current?.End(MatchSession.Current.IsSpectator ? "You stopped watching." : "You left the match."), true, GUILayout.Width(300));
                return;
            }

            if (IsSpectator)
            {
                GUILayout.Space(6);
                GUILayout.Label("You are watching this room. The next match plays on your screen too.", OnlineSkin.Small);
                GUILayout.Label("Stage: " + GameBridge.StageLabel(_lobby.Stage) + "   rounds to win: " + _lobby.Rounds, OnlineSkin.Small);
            }
            else
            {
                GUILayout.Space(4);
                DrawPicker();
                GUILayout.Space(4);
                if (_ranked) DrawRankedRules();
                else DrawRoomRules();
                GUILayout.Space(4);
                DrawStartControls();
            }

            DrawChat();
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (_ranked && !IsSpectator) _nav.Button("Find a new opponent", () => { LeaveRoom(); QueueRanked(); _page = Page.Ranked; }, true, GUILayout.Width(300));
            _nav.Button(IsSpectator ? "Stop watching" : "Leave room", LeaveRoom, true, GUILayout.Width(220));
            GUILayout.EndHorizontal();
        }

        private string ScoreLine()
        {
            if (_lobby.Matches == 0) return null;
            var slots = PlayerSlots.ToList();
            int Wins(int s) => _lobby.Wins.TryGetValue(s, out int w) ? w : 0;
            string line = slots.Count == 2
                ? NameOf(slots[0]) + "  " + Wins(slots[0]) + " : " + Wins(slots[1]) + "  " + NameOf(slots[1])
                : string.Join(",  ", slots.Select(s => NameOf(s) + " " + Wins(s)).ToArray());
            int draws = _lobby.Matches - _lobby.Wins.Values.Sum();
            if (draws > 0) line += "   (" + draws + (draws == 1 ? " draw" : " draws") + ")";
            return line + "   after " + _lobby.Matches + (_lobby.Matches == 1 ? " match" : " matches");
        }

        private void DrawPlayerRow(int slot)
        {
            _lobby.Picks.TryGetValue(slot, out var p);
            GUILayout.BeginHorizontal(OnlineSkin.Row);
            var face = GUILayoutUtility.GetRect(44, 44, GUILayout.Width(44), GUILayout.Height(44));
            if (p != null) Portraits.Draw(face, Portraits.Face(p.Character));
            string name = (slot == _hostSlot && !_ranked ? "[Host] " : "") + NameOf(slot) + (slot == _mySlot ? " (you)" : "") +
                          (_peerRatings.TryGetValue(slot, out int rating) && rating > 0 ? "  " + rating : "");
            GUILayout.Label(name, GUILayout.Width(250));
            string pick = p == null ? "choosing..." : p.Character + (p.Color > 1 ? " color " + p.Color : "") + (p.AltForm > 1 ? ", evo " + p.AltForm : "");
            GUILayout.Label(pick, OnlineSkin.Small, GUILayout.Width(230));
            bool ready = slot == _mySlot ? _myReady : _lobby.Ready.Contains(slot);
            string state = slot == _hostSlot && !_ranked ? "" : ready ? "READY" : "";
            if (_lobby.P2P && slot != _mySlot) state += _mux != null && _mux.IsDirect(slot) ? "  direct" : "  via server";
            GUILayout.Label(state, OnlineSkin.Small, GUILayout.Width(120));
            if (IsHost && !_ranked && slot != _mySlot)
            {
                int s = slot;
                _nav.Button("Kick", () => _client?.Kick(s), true, GUILayout.Width(80));
            }
            GUILayout.EndHorizontal();
        }

        private void DrawRankedRules()
        {
            GUILayout.Label("Ranked rules: best of three rounds, no items, no stage hazards, random stage, input delay set from your pings (" + _lobby.InputDelay + "f).", OnlineSkin.Small);
        }

        private void DrawRoomRules()
        {
            if (!IsHost)
            {
                GUILayout.Label("Stage: " + GameBridge.StageLabel(_lobby.Stage) + "   rounds to win: " + _lobby.Rounds + "   delay: " + _lobby.InputDelay + "f" +
                                (_lobby.AutoDelay ? " (automatic)" : "") + "   connection: " + (_lobby.P2P ? "direct (P2P)" : "through the server"));
                DrawP2PNote();
                return;
            }
            GUILayout.Label("ROOM RULES", OnlineSkin.Heading);
            _nav.Stepper("Stage: " + GameBridge.StageLabel(_lobby.Stage), d =>
            {
                if (_stages.Count == 0) return;
                _stageIdx = (_stageIdx + d + _stages.Count) % _stages.Count;
                _lobby.Stage = _stages[_stageIdx];
                BroadcastLobby();
            });
            _nav.Stepper("Rounds to win: " + _lobby.Rounds, d => { _lobby.Rounds = Mathf.Clamp(_lobby.Rounds + d, 1, 4); BroadcastLobby(); });
            GUILayout.BeginHorizontal();
            GUILayout.Label("Input delay", GUILayout.Width(140));
            _nav.Choice(_lobby.AutoDelay ? 0 : 1, DelayChoices, v =>
            {
                _lobby.AutoDelay = v == 0;
                if (_lobby.AutoDelay) _lobby.InputDelay = ComputeAutoDelay();
                BroadcastLobby();
            });
            GUILayout.EndHorizontal();
            if (_lobby.AutoDelay)
                GUILayout.Label("Automatic: " + _lobby.InputDelay + " frames, from everyone's ping. It updates while you wait.", OnlineSkin.Small);
            else
                _nav.Stepper("Input delay: " + _lobby.InputDelay + " frames", d => { _lobby.InputDelay = Mathf.Clamp(_lobby.InputDelay + d, 0, 12); BroadcastLobby(); });
            GUILayout.BeginHorizontal();
            GUILayout.Label("Connection", GUILayout.Width(140));
            _nav.Choice(_lobby.P2P ? 1 : 0, ConnectionChoices, v => { _lobby.P2P = v == 1; BroadcastLobby(); });
            GUILayout.EndHorizontal();
            DrawP2PNote();
            _nav.Button("Room settings (name, size, password)", OpenRoomSettings, true, GUILayout.Width(440));
        }

        private void DrawP2PNote()
        {
            if (!_lobby.P2P) return;
            GUILayout.Label("Players connect directly where their networks allow it and use the server otherwise. Direct links show each player's IP address to the others in this room.", OnlineSkin.Small);
            _nav.Toggle(Plugin.AllowP2P.Value, "  Allow a direct connection for me", allow =>
            {
                Plugin.AllowP2P.Value = allow;
                DropMux(); // rebuilt next frame with the new setting
            });
        }

        private void DrawStartControls()
        {
            string why = NotReadyReason();
            if (_ranked)
            {
                _nav.Button(_myReady ? "Ready! (press to cancel)" : "Ready", () => SetReady(!_myReady), true, GUILayout.Width(360), GUILayout.Height(46));
                GUILayout.Label(why ?? "Starting...", OnlineSkin.Small);
                return;
            }
            if (IsHost)
            {
                _nav.Button(why == null ? "Start match" : "Start match (not everyone is ready)", HostStart, why == null, GUILayout.Width(440), GUILayout.Height(46));
                if (why != null) GUILayout.Label(why, OnlineSkin.Small);
            }
            else
            {
                _nav.Button(_myReady ? "Ready! (press to cancel)" : "Ready", () => SetReady(!_myReady), true, GUILayout.Width(360), GUILayout.Height(46));
                GUILayout.Label(_myReady ? "Waiting for the host to start." : "Press Ready when you have picked your Digimon.", OnlineSkin.Small);
            }
        }

        private void DrawChat()
        {
            GUILayout.Label("CHAT", OnlineSkin.Heading);
            GUILayout.BeginVertical(OnlineSkin.Row);
            int from = Math.Max(0, _chat.Count - 8);
            if (_chat.Count == 0) GUILayout.Label("No messages yet.", OnlineSkin.Small);
            for (int i = from; i < _chat.Count; i++) GUILayout.Label(_chat[i], OnlineSkin.Small);
            GUILayout.EndVertical();
            GUILayout.BeginHorizontal();
            _chatField = _nav.Text("chat", _chatField, GameMsg.MaxChat, false, SendChat, GUILayout.Width(560));
            _nav.Button("Send", SendChat, _chatField.Trim().Length > 0, GUILayout.Width(120));
            GUILayout.EndHorizontal();
        }

        private void OpenRoomSettings()
        {
            _setName = _roomName ?? "";
            _setPlayers = _maxPlayers <= 2 ? 0 : 1;
            _setVisibility = _roomVisibility == RoomVisibility.Private ? 2 : _roomHasPassword ? 1 : 0;
            _setPassword = _roomPassword ?? "";
            _page = Page.RoomSettings;
        }

        private void DrawRoomSettings()
        {
            _nav.Back(() => _page = Page.Browser);
            GUILayout.Label("ROOM SETTINGS", OnlineSkin.Heading);
            GUILayout.Label("Room name");
            _setName = _nav.Text("setname", _setName, 32, false, null, GUILayout.Width(500));
            GUILayout.Label("Players");
            _nav.Choice(_setPlayers, PlayerChoices, v => _setPlayers = v);
            GUILayout.Label("Who can join");
            _nav.Choice(_setVisibility, VisibilityChoices, v => _setVisibility = v);
            bool keepHint = _setVisibility == 1 && _roomHasPassword && _setPassword.Length == 0;
            bool needsPassword = _setVisibility == 1 && _setPassword.Trim().Length == 0 && !_roomHasPassword;
            DrawVisibilityHelp(_setVisibility, ref _setPassword, "setpw");
            if (keepHint) GUILayout.Label("Leave the password empty to keep the current one.", OnlineSkin.Small);
            GUILayout.BeginHorizontal();
            _nav.Button("Save", () =>
            {
                string pw = _setPassword.Trim();
                bool change = _setVisibility != 1 || pw.Length > 0;
                if (_setVisibility != 1) pw = "";
                _client?.UpdateRoom(_setPlayers == 0 ? 2 : 4, _setName, _setVisibility == 2 ? RoomVisibility.Private : RoomVisibility.Public, change, pw);
                if (change) _roomPassword = pw;
                _page = Page.Browser;
            }, !needsPassword, GUILayout.Width(200));
            _nav.Button("Cancel", () => _page = Page.Browser, true, GUILayout.Width(160));
            GUILayout.EndHorizontal();
            if (needsPassword) GUILayout.Label("Enter a password.", OnlineSkin.Small);
        }

        // ------------------------------------------------------------------ character picker

        private List<int> FilteredChars()
        {
            var list = new List<int>();
            for (int i = 0; i < _chars.Count; i++)
                if (_charFilter.Length == 0 || _chars[i].Name.IndexOf(_charFilter, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(i);
            return list;
        }

        private void SelectChar(int index)
        {
            if (index == _charIdx) return;
            _charIdx = index;
            _colorIdx = 0;
            _evoIdx = 0;
            SendPick();
        }

        private void DrawPicker()
        {
            if (_chars.Count == 0) { GUILayout.Label("No unlocked Digimon found in your save."); return; }
            _charIdx = Mathf.Clamp(_charIdx, 0, _chars.Count - 1);
            var ch = _chars[_charIdx];
            int color = ch.Colors[Mathf.Clamp(_colorIdx, 0, ch.Colors.Count - 1)];

            GUILayout.Label("YOUR DIGIMON", OnlineSkin.Heading);
            GUILayout.BeginHorizontal();
            var art = GUILayoutUtility.GetRect(210, 210, GUILayout.Width(210), GUILayout.Height(210));
            GUI.Box(art, GUIContent.none, OnlineSkin.Row);
            if (!Portraits.Draw(art, Portraits.Art(ch.Name, color)) && !Portraits.Draw(art, Portraits.Face(ch.Name)))
                GUI.Label(art, ch.Name, OnlineSkin.Heading);
            GUILayout.BeginVertical();
            GUILayout.Label(ch.Name, OnlineSkin.Title);
            _nav.Stepper("Color " + (Mathf.Clamp(_colorIdx, 0, ch.Colors.Count - 1) + 1) + " of " + ch.Colors.Count, d =>
            {
                _colorIdx = (Mathf.Clamp(_colorIdx, 0, ch.Colors.Count - 1) + d + ch.Colors.Count) % ch.Colors.Count;
                SendPick();
            }, ch.Colors.Count > 1, 300);
            if (ch.Evolutions.Count > 0)
                _nav.Stepper("Evolves to " + ch.Evolutions[Mathf.Clamp(_evoIdx, 0, ch.Evolutions.Count - 1)].Value, d =>
                {
                    _evoIdx = (Mathf.Clamp(_evoIdx, 0, ch.Evolutions.Count - 1) + d + ch.Evolutions.Count) % ch.Evolutions.Count;
                    SendPick();
                }, ch.Evolutions.Count > 1, 300);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(80));
            string filter = _nav.Text("search", _charFilter, 20, false, null, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            if (filter != _charFilter)
            {
                _charFilter = filter;
                var hits = FilteredChars();
                if (hits.Count > 0 && !hits.Contains(_charIdx)) _nav.Defer(() => SelectChar(hits[0]));
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            DrawCharGrid();
        }

        /// <summary>The game's face icons in a grid. One gamepad stop: left/right and up/down move the selection, leaving at the edges.</summary>
        private void DrawCharGrid()
        {
            var shown = FilteredChars();
            float width = 740f;
            int perRow = Mathf.Max(1, (int)(width / GridTile));
            int rows = Mathf.Max(1, (shown.Count + perRow - 1) / perRow);
            var area = GUILayoutUtility.GetRect(width, GridRows * GridTile, GUILayout.Width(width), GUILayout.Height(GridRows * GridTile));
            int pos = shown.IndexOf(_charIdx);
            int selRow = pos < 0 ? 0 : pos / perRow;
            _gridTop = Mathf.Clamp(_gridTop, 0, Mathf.Max(0, rows - GridRows));
            if (_charIdx != _gridSnappedFor)
            {
                // Bring a newly picked Digimon into view; otherwise leave the scroll to the mouse wheel.
                _gridSnappedFor = _charIdx;
                if (selRow < _gridTop) _gridTop = selRow;
                if (selRow >= _gridTop + GridRows) _gridTop = selRow - GridRows + 1;
            }

            var e = Event.current;
            if (e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
            {
                int d = e.delta.y > 0 ? 1 : -1;
                _nav.Defer(() => _gridTop = Mathf.Clamp(_gridTop + d, 0, Mathf.Max(0, rows - GridRows)));
                e.Use();
            }

            for (int row = _gridTop; row < Math.Min(rows, _gridTop + GridRows); row++)
                for (int col = 0; col < perRow; col++)
                {
                    int i = row * perRow + col;
                    if (i >= shown.Count) break;
                    int charIndex = shown[i];
                    var tile = new Rect(area.x + col * GridTile, area.y + (row - _gridTop) * GridTile, GridTile - 4, GridTile - 4);
                    bool selected = charIndex == _charIdx;
                    GUI.Box(tile, GUIContent.none, selected ? OnlineSkin.Selected : OnlineSkin.Row);
                    var inner = new Rect(tile.x + 3, tile.y + 3, tile.width - 6, tile.height - 6);
                    if (!Portraits.Draw(inner, Portraits.Face(_chars[charIndex].Name), selected ? 1f : 0.8f))
                        GUI.Label(inner, _chars[charIndex].Name, OnlineSkin.Tiny);
                    if (GUI.Button(tile, GUIContent.none, GUIStyle.none)) _nav.Defer(() => SelectChar(charIndex));
                }
            if (rows > GridRows)
            {
                string more = (_gridTop > 0 ? "more above   " : "") + (_gridTop + GridRows < rows ? "more below" : "");
                GUI.Label(new Rect(area.xMax - 220, area.yMax - 22, 220, 22), more, OnlineSkin.Tiny);
            }

            // One stop for the gamepad: the whole grid.
            _nav.Custom(null,
                d =>
                {
                    int p = shown.IndexOf(_charIdx);
                    int next = Mathf.Clamp((p < 0 ? 0 : p) + d, 0, shown.Count - 1);
                    if (shown.Count > 0) SelectChar(shown[next]);
                },
                d =>
                {
                    int p = shown.IndexOf(_charIdx);
                    if (p < 0) return null;
                    int next = p + d * perRow;
                    if (next < 0 || next / perRow >= rows) return null; // leave the grid
                    next = Math.Min(next, shown.Count - 1);
                    int target = shown[next];
                    return () => SelectChar(target);
                });
            GUILayout.Label(shown.Count + " of " + _chars.Count + " Digimon" + (_charFilter.Length > 0 ? " match \"" + _charFilter + "\"" : "") +
                            ". Arrows or the stick move the cursor; click a face to pick it.", OnlineSkin.Small);
        }
    }
}
