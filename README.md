# Digimon Netplay (online versus for Project Digimon)
<img width="1920" height="750" alt="Digimon2" src="https://github.com/user-attachments/assets/428d3c7a-5c81-4a17-8c04-b1af4fe50051" />
<img width="1920" height="1080" alt="Digimon" src="https://github.com/user-attachments/assets/bba708dd-e0d9-4f31-8576-1a9e1818a4f7" />


A client mod plus a lobby server that let 2 to 4 people play Project Digimon versus battles over the internet.

The Original Game Thread and Files can be found here: https://withthewill.net/threads/digimon-rumble-arena-fangame-read-the-first-post-before-posting.21232/
All credits for this amazing fan game goes to Draco74 https://withthewill.net/members/draco74.11579/

## How it works

Project Digimon is a Unity 2021.3 (Mono) platform fighter with up to 4 local players. The mod keeps that design and swaps the local controllers for networked ones:

- **Delay-based lockstep.** Every player's game runs the full battle. Each frame, every player's buttons are sent to everyone else and used a few frames later (the "input delay"), so all games see exactly the same inputs on the same frame. If someone's input is late, the game holds that frame until it arrives.
- **Deterministic frames during a match.** Fixed 60 fps game time, the game's clocks replaced by a match clock, the random number generator reseeded every frame from a shared seed, and exactly one physics step and one `FixedUpdate` per frame.
- **Rounds stay in sync.** Each round reloads the stage. Everyone loads it in the background and all players switch to it on the same simulation frame.
- **Desync safety net.** Every 30 frames the host shares fighter positions and life/energy bars. If a guest's game has drifted, it is nudged back toward the host's and the HUD counts it ("desyncs fixed N").
- **Lobby server.** Players browse the open rooms on a lobby server, or join a private room by its 5-letter code, so nobody needs to forward ports except whoever runs the server. The server can sit behind Cloudflare (WebSocket). A player can also run the server inside the game ("Host on this PC").

Pause is disabled online. Battles use the host's settings (rounds, items, hazards, timer, damage handicap, special forms). Each player picks from the Digimon unlocked in their own save. The host's settings are never written into a guest's save file.

## Install (each player)

1. Close the game.
2. Download `DigimonNetplay-Client.zip` from the [Releases](../../releases) page and extract it into the game folder, the one containing `ProjectDigimon.exe`. It adds BepInEx 5.4.23.2 (`winhttp.dll`, `doorstop_config.ini`, `BepInEx/`) and `BepInEx/plugins/DigimonNetplay/DigimonNetplay.dll`. It does not change any of the game's own files.
3. Start the game once. `BepInEx/LogOutput.log` should contain `Digimon Netplay 0.3.0 loaded`, and the main menu has an **ONLINE** entry.

Everyone must use the same game version (1.16) and the same mod version.

To uninstall, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder.

## Run a lobby server

The server is one small program. It keeps the list of open rooms and relays messages between the players in each room, so it uses very little CPU or bandwidth. Players see every public room on the server in the game's **ONLINE** menu.

One port (7777 by default) accepts three kinds of connections: the game over WebSocket (used behind Cloudflare), the game over plain TCP (direct connections), and a status page at `/status` that shows the number of players and rooms.

### On a Linux VPS behind Cloudflare (recommended)

Cloudflare only forwards web traffic, so the game talks to the server over a secure WebSocket (`wss://`). The simplest setup is a Cloudflare Tunnel, which needs no open ports on the VPS:

1. Copy `DigimonNetplayServer` from `DigimonNetplay-Server-Linux.zip` to `/opt/digimon-netplay/` and run `chmod +x /opt/digimon-netplay/DigimonNetplayServer`.
2. Install the service: copy `deploy/digimon-netplay.service` to `/etc/systemd/system/`, then run `sudo systemctl daemon-reload && sudo systemctl enable --now digimon-netplay`. Change `--name` in the file to name your server.
3. Install `cloudflared` ([Cloudflare's guide](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/get-started/create-local-tunnel/)), then run `cloudflared tunnel login`, `cloudflared tunnel create digimon-lobby` and `cloudflared tunnel route dns digimon-lobby lobby.example.com`.
4. Put `deploy/cloudflared-config.yml` in `/etc/cloudflared/config.yml` with your hostname and tunnel id, then run `sudo cloudflared service install`.
5. Open `https://lobby.example.com/status` in a browser. It should show `{"server":...,"players":0,"rooms":0}`.
6. Players use `wss://lobby.example.com` as the lobby server. A release built for your server has it as the default, so nobody has to type it.

Without a tunnel: point a proxied DNS record at the VPS, start the server with `--port 80`, and set Cloudflare's SSL mode to Flexible (or add an Origin Rule that sends the hostname to port 7777).

Server options: `--port 7777`, `--name "My server"`, `--behind-proxy` (use the player address that Cloudflare forwards for the per-address connection limit), `--max-clients 1000`, `--max-rooms 300`.

### On Windows or without Cloudflare

- Windows: `DigimonNetplayServer.exe --port 7777`
- Linux: `./DigimonNetplayServer --port 7777`

Forward TCP 7777 to that machine. Players set the lobby server to `your-ip:7777` (direct TCP) or `ws://your-ip:7777`.

### Without any server

In the online screen, open **Server settings** and click **Host on this PC**. The game runs a private server; friends set their lobby server to your public IP and port. Forward that TCP port on your router.

## Play

1. In the main menu, pick **ONLINE** (under VS COMPUTER). **F8** also opens the online screen from anywhere; **Esc** or **B** goes back.
2. The game connects to the lobby server and lists the open rooms. The first time, enter your name. If no server is set yet, enter one under **Server settings**.
3. Join a room from the list, or click **Create room** and choose:
   - **Public**: listed, anyone can join.
   - **Password**: listed with `[PW]`, players need the password.
   - **Private (code)**: not listed, friends join with the 5-letter room code (box at the top of the list).
4. In the room, everyone picks a Digimon, color and evolution.
5. The host picks the stage, rounds to win and input delay, then clicks **Start match**. Everyone goes to the VS screen together. While the match runs, the room shows "In match" and nobody else can join.
6. After the battle, everyone is back at the main menu with the online screen open and still in the room; the host can start a rematch.

The bottom-left HUD shows ping, input delay, how long the game waited for someone, and how many desyncs were corrected.

**Input delay:** 2 to 3 frames is fine for players in the same region, 4 to 6 for long distances. If the game keeps pausing briefly, the host should raise it. The online screen suggests a value from your ping.

## Settings

`BepInEx/config/community.projectdigimon.netplay.cfg` (created on first run):

| Setting | Default | Meaning |
|---|---|---|
| LobbyServer | (empty) | Lobby server address; empty uses the official server built into the release |
| HostPort | 7777 | Port for Host on this PC |
| PlayerName | Tamer | Name shown to others |
| LocalControls | K1,G1 | Which of the game's control sets drive your fighter online (K1/K2 keyboard layouts, G1..G6 gamepads), merged. Uses your rebinds from the game's options. |
| DefaultInputDelay | 3 | Delay a new room starts with |
| ToggleKey | F8 | Opens/closes the online screen from anywhere |
| StallTimeoutSeconds | 15 | How long to wait for a silent player before ending the match |

## Known limits

- Early version: 1v1 has been played successfully in the real game; 3 to 4 player rooms are less tested. Send `BepInEx/LogOutput.log` from each player when something goes wrong.
- Lockstep is only as deterministic as the game. Rare moves that depend on things outside the synced state may still drift. The desync check corrects positions and life/energy bars, but not animations or projectiles.
- Supported modes: 1v1 versus (2 players) and battle royale free-for-all (3 to 4 players). Team battles, arcade, story and special battles stay offline.
- The online screen is used with the mouse (Esc or B goes back); the main menu entry itself works with keyboard and gamepad.
- The lobby over WebSocket is covered by the automated tests, but the encrypted `wss://` connection through Cloudflare has not run inside the game yet. If it fails, the log names the TLS error, and `ws://` (unencrypted) through Cloudflare is the fallback.
- No rollback. A bad connection shows up as brief pauses, not teleports.
- A player who closes the game or loses connection ends the match for everyone (back to the main menu).

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

- `src/Shared`: wire protocol, relay server, relay client, lockstep core (no Unity dependency; used by mod, server and tests)
- `src/Plugin`: BepInEx plugin (ONLINE main menu entry, online screen, match setup, frame gate, Harmony patches)
- `src/Server`: standalone lobby and relay server
- `deploy`: systemd service and Cloudflare Tunnel config for a Linux VPS
- `tests/NetcodeTests`: end-to-end netcode tests
- `tests/HookCheck`: verifies the game DLL still has everything the mod patches
