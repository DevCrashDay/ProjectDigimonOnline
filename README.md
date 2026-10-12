# Digimon Netplay (online versus for Project Digimon)
<img width="1920" height="750" alt="Digimon2" src="https://github.com/user-attachments/assets/428d3c7a-5c81-4a17-8c04-b1af4fe50051" />
<img width="1920" height="1080" alt="Digimon" src="https://github.com/user-attachments/assets/bba708dd-e0d9-4f31-8576-1a9e1818a4f7" />


A client mod plus a lobby server that let 2 to 4 people play Project Digimon versus battles over the internet.

The Original Game Thread and Files can be found here: https://withthewill.net/threads/digimon-rumble-arena-fangame-read-the-first-post-before-posting.21232/
All credits for this amazing fan game goes to Draco74 https://withthewill.net/members/draco74.11579/

## How it works

Project Digimon is a Unity 2021.3 (Mono) platform fighter with up to 4 local players. The mod keeps that design and swaps the local controllers for networked ones:

- **Server relay or direct P2P.** Matches go through the lobby server by default; the host can switch a room to direct UDP connections with automatic fallback to the server.
- **Delay-based lockstep.** Every player's game runs the full battle. Each frame, every player's buttons are sent to everyone else and used a few frames later (the "input delay"), so all games see exactly the same inputs on the same frame. If someone's input is late, the game holds that frame until it arrives.
- **Deterministic frames during a match.** Fixed 60 fps game time, the game's clocks replaced by a match clock, the random number generator reseeded every frame from a shared seed, and exactly one physics step and one `FixedUpdate` per frame.
- **Rounds stay in sync.** Each round reloads the stage. Everyone loads it in the background and all players switch to it on the same simulation frame.
- **Desync safety net.** Every 30 frames the host shares fighter positions and life/energy bars. If a guest's game has drifted, it is nudged back toward the host's and the HUD counts it ("desyncs fixed N").
- **Lobby server.** Players browse the open rooms on a lobby server, use Quick match, or join a private room by its 5-letter code, so nobody needs to forward ports except whoever runs the server. The server can sit behind Cloudflare (WebSocket). A player can also run the server inside the game ("Host on this PC").
- **Ranked play.** The server pairs players of similar Elo rating for 1v1 ranked matches and keeps a public leaderboard, in the game and as a web page. Accounts are anonymous: the game makes up a random key on first run and the server only stores a hash of it.
- **Spectators.** Up to 8 people can watch a room. They receive the same inputs as the players and replay the battle on their own game.

Pause is disabled online. Battles use the host's settings (rounds, items, hazards, timer, damage handicap, special forms). Each player picks from the Digimon unlocked in their own save. The host's settings are never written into a guest's save file.

## Install (each player)

1. Close the game.
2. Download `DigimonNetplay-Client.zip` from the [Releases](../../releases) page and extract it into the game folder, the one containing `ProjectDigimon.exe`. It adds BepInEx 5.4.23.2 (`winhttp.dll`, `doorstop_config.ini`, `BepInEx/`) and `BepInEx/plugins/DigimonNetplay/DigimonNetplay.dll`. It does not change any of the game's own files.
3. Start the game once. `BepInEx/LogOutput.log` should contain `Digimon Netplay 0.5.1 loaded`, and the main menu has an **ONLINE** entry.

Everyone must use the same game version (1.16) and the same mod version.

To uninstall, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder.

## Run a lobby server

The server is one small program. It keeps the list of open rooms and relays messages between the players in each room, so it uses very little CPU or bandwidth. Players see every public room on the server in the game's **ONLINE** menu.

One port (7777 by default) accepts the game over WebSocket (used behind Cloudflare) and over plain TCP (direct connections), and serves three web pages:

- `/leaderboard` (also `/`): the public ranked leaderboard, top 100.
- `/leaderboard.json`: the same as JSON, for a website or a Discord bot.
- `/status`: number of players, rooms and players searching for a ranked match.

The server keeps ranked ratings (`ratings.tsv`) and problem reports from players (`reports/`, at most 500 files) in its data folder (`--data-dir`, default `./data`).

### On a Linux VPS behind Cloudflare (recommended)

Cloudflare only forwards web traffic, so the game talks to the server over a secure WebSocket (`wss://`). The simplest setup is a Cloudflare Tunnel, which needs no open ports on the VPS:

1. Copy `DigimonNetplayServer` from `DigimonNetplay-Server-Linux.zip` to `/opt/digimon-netplay/` and run `chmod +x /opt/digimon-netplay/DigimonNetplayServer`.
2. Install the service: copy `deploy/digimon-netplay.service` to `/etc/systemd/system/`, then run `sudo systemctl daemon-reload && sudo systemctl enable --now digimon-netplay`. Change `--name` in the file to name your server. Ratings and problem reports are kept in `/var/lib/digimon-netplay`; back up `ratings.tsv` from there.
3. Install `cloudflared` ([Cloudflare's guide](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/get-started/create-local-tunnel/)), then run `cloudflared tunnel login`, `cloudflared tunnel create digimon-lobby` and `cloudflared tunnel route dns digimon-lobby lobby.example.com`.
4. Put `deploy/cloudflared-config.yml` in `/etc/cloudflared/config.yml` with your hostname and tunnel id, then run `sudo cloudflared service install`.
5. Open `https://lobby.example.com/status` in a browser. It should show `{"server":...,"players":0,"rooms":0,...}`. The leaderboard is at `https://lobby.example.com/leaderboard`.
6. Players use `wss://lobby.example.com` as the lobby server. A release built for your server has it as the default, so nobody has to type it.

Without a tunnel: point a proxied DNS record at the VPS, start the server with `--port 80`, and set Cloudflare's SSL mode to Flexible (or add an Origin Rule that sends the hostname to port 7777).

Server options:

| Option | Default | Meaning |
|---|---|---|
| `--port` | 7777 | Port for the game and the web pages |
| `--name` | Digimon Netplay server | Shown in the game and on the leaderboard page |
| `--behind-proxy` | off | Use the player address Cloudflare forwards for the per-address connection limit |
| `--data-dir` | `./data` | Where ratings and problem reports are kept |
| `--latest-version` | the server's own version | Players with an older mod see an update notice |
| `--min-games` | 5 | Ranked games a player needs before they appear on the leaderboard |
| `--daily-pair-limit` | 3 | Rated games per day between the same two players (0 = no limit) |
| `--download-url` | this repository's latest release | Where the update notice sends players |
| `--max-clients`, `--max-rooms` | 1000, 300 | Limits |

Update the server together with the mod: a new mod version usually needs the matching server. Players on an older mod get a message with the download link.

### On Windows or without Cloudflare

- Windows: `DigimonNetplayServer.exe --port 7777`
- Linux: `./DigimonNetplayServer --port 7777`

Forward TCP 7777 to that machine. Players set the lobby server to `your-ip:7777` (direct TCP) or `ws://your-ip:7777`.

### Without any server

In the online screen, open **Server settings** and click **Host on this PC**. The game runs a private server; friends set their lobby server to your public IP and port. Forward that TCP port on your router.

## Play

The whole online screen works with a gamepad (stick or d-pad to move, **A** to select, **B** to go back), with the keyboard (arrow keys or WASD, **Enter**, **Esc**) or with the mouse.

1. In the main menu, pick **ONLINE** (under VS COMPUTER). **F8** also opens the online screen from anywhere.
2. The game connects to the lobby server and lists the open rooms. The first time, enter your name. If no server is set yet, enter one under **Server settings**. When a newer version of the mod is out, the screen says so and **Download the update** opens the release page.
3. Pick one of:
   - **Quick match**: joins a player who is waiting for a 1v1, or opens a new public 1v1 room and waits for one.
   - **Ranked**: the server finds an opponent near your rating (below).
   - **Create room**: **Public** (listed, anyone can join), **Password** (listed with `[PW]`) or **Private (code)** (not listed, friends join with the 5-letter code in the box at the top of the list).
   - **Join** or **Watch** a room from the list.
4. In the room, everyone picks a Digimon from the game's own character art, then a color and evolution, and presses **Ready**. The room has a chat.
5. The host picks the stage, rounds to win, input delay and connection, and starts the match once everyone is ready. **Room settings** changes the room's name, size and password; **Kick** removes a player (they cannot rejoin that room). While the match runs, the room shows "In match"; new players can only watch.
6. After the battle everyone is back in the room with the score (for example `Ash 3 : 2 Bob`). Press Ready again for a rematch.

**If the connection drops**, the game reconnects by itself (up to 5 tries) and puts you back in your room. If the host leaves, the room stays open and the next player becomes the host. A match that ends early says why and returns everyone to the room.

**Ranked:** 1v1, best of three rounds, no items, no stage hazards, random stage, always through the server. Everyone starts at 1000 Elo; the search starts within 100 rating points and widens the longer you wait. Both games report the winner and the rating only changes when they agree. Leaving a ranked match counts as a loss. Players appear on the leaderboard after 5 ranked games, and only the first 3 games per day (UTC) against the same opponent are rated; later ones are played but change nothing. The rating belongs to the file `BepInEx/config/DigimonNetplay.account`; keep it (it is not in the shared config file) to keep your rating on a new PC. The **Leaderboard** button shows the top 100 and opens the web version.

**Watching:** up to 8 spectators per room, including ranked rooms. A spectator who joins during a match watches from the next one. Spectators replay the inputs on their own game, a moment behind the players, and never slow the players down.

**Report a problem** (in the room list) sends the end of your game log and basic system info to the server's owner, with your Windows user name, PC name and IP addresses removed. Without a connection it saves the report as `NetplayReport-....txt` in the game folder instead.

**Connection:** by default every match runs through the lobby server, which hides players' IP addresses from each other and works on every network. The host can switch a room to **Direct (P2P)**: players then send their inputs straight to each other, which saves the trip through the server. Each player finds their public address through a public STUN server (Google or Cloudflare), the addresses are exchanged through the lobby server, and the games connect over UDP. Where a direct connection fails (some routers and mobile networks block it) or drops during a match, those players automatically use the server, and the match keeps going. The room shows `direct` or `via server` next to each player. Direct links show each player's IP address to the others in the room. Players who don't want that untick **Allow a direct connection for me** and always go through the server.

**Input delay:** **Automatic** (the default) measures everyone's ping to the server and over direct links and sets the delay that covers the slowest pair of players; it keeps updating while you wait in the room. The host can switch to **Manual**: 2 to 3 frames is fine for players in the same region, 4 to 6 for long distances. If the game keeps pausing briefly, raise it.

The bottom-left HUD shows ping, input delay, how long the game waited for someone, how many desyncs were corrected and, in P2P rooms, how many players are connected directly.

## Settings

`BepInEx/config/community.projectdigimon.netplay.cfg` (created on first run):

| Setting | Default | Meaning |
|---|---|---|
| LobbyServer | (empty) | Lobby server address; empty uses the official server built into the release |
| HostPort | 7777 | Port for Host on this PC |
| AllowP2P | true | Use direct connections in P2P rooms (shares your IP with that room) |
| PlayerName | Tamer | Name shown to others |
| LocalControls | K1,G1 | Which of the game's control sets drive your fighter online (K1/K2 keyboard layouts, G1..G6 gamepads), merged. Uses your rebinds from the game's options. |
| DefaultInputDelay | 3 | Delay a new room starts with when the host switches it to Manual |
| ToggleKey | F8 | Opens/closes the online screen from anywhere |
| StallTimeoutSeconds | 15 | How long to wait for a silent player before ending the match |

## Known limits

- Early version: 1v1 has been played successfully in the real game; 3 to 4 player rooms, spectators and ranked are less tested. Use **Report a problem** when something goes wrong.
- Lockstep is only as deterministic as the game. Rare moves that depend on things outside the synced state may still drift. The desync check corrects positions and life/energy bars, but not animations or projectiles.
- Supported modes: 1v1 versus (2 players) and battle royale free-for-all (3 to 4 players). Team battles, arcade, story and special battles stay offline.
- Text boxes (name, chat, room code) need a keyboard.
- Ranked results rely on both games agreeing; a modified game could refuse to report a loss, but then the game is simply not counted.
- Ranked accounts are anonymous key files, so the server cannot tell that two accounts belong to the same person. The 5-game minimum and the 3-games-a-day limit per opponent make farming a rating with a second account slower, not impossible.
- The lobby over WebSocket is covered by the automated tests, but the encrypted `wss://` connection through Cloudflare has not run inside the game yet. If it fails, the log names the TLS error, and `ws://` (unencrypted) through Cloudflare is the fallback.
- No rollback. A bad connection shows up as brief pauses, not teleports.
- A player who closes the game or loses connection ends the current match for everyone; the others go back to the room.

## Build from source

Requires the .NET 8 SDK.

```
# client mod (needs the game's DLLs and BepInEx installed in the game folder)
dotnet build src/Plugin -c Release -p:GameDir="C:\Games\ProjectDIGIMON"

# server
dotnet publish src/Server -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true

# tests: real relay server + simulated players using the mod's lockstep core
dotnet run --project tests/NetcodeTests -c Release

# after a game update: check every method/field the mod hooks still exists
dotnet run --project tests/HookCheck -c Release -- "C:\Games\ProjectDIGIMON\ProjectDigimon_Data\Managed\Assembly-CSharp.dll"
```

Layout:

- `src/Shared`: wire protocol, relay server, relay client, lockstep core, P2P links (no Unity dependency; used by mod, server and tests)
- `src/Plugin`: BepInEx plugin (ONLINE main menu entry, online screen, match setup, frame gate, Harmony patches)
- `src/Server`: standalone lobby and relay server
- `deploy`: systemd service and Cloudflare Tunnel config for a Linux VPS
- `tests/NetcodeTests`: end-to-end netcode tests
- `tests/HookCheck`: verifies the game DLL still has everything the mod patches
