# Digimon Netplay (online versus for Project Digimon)

A client mod plus a relay server that let 2 to 4 people play Project Digimon versus battles over the internet.

The Original Game Thread and Files can be found here: https://withthewill.net/threads/digimon-rumble-arena-fangame-read-the-first-post-before-posting.21232/
All credits for this amazing fan game goes to Draco74 https://withthewill.net/members/draco74.11579/

## How it works

Project Digimon is a Unity 2021.3 (Mono) platform fighter with up to 4 local players. The mod keeps that design and swaps the local controllers for networked ones:

- **Delay-based lockstep.** Every player's game runs the full battle. Each frame, every player's buttons are sent to everyone else and used a few frames later (the "input delay"), so all games see exactly the same inputs on the same frame. If someone's input is late, the game holds that frame until it arrives.
- **Deterministic frames during a match.** Fixed 60 fps game time, the game's clocks replaced by a match clock, the random number generator reseeded every frame from a shared seed, and exactly one physics step and one `FixedUpdate` per frame.
- **Rounds stay in sync.** Each round reloads the stage. Everyone loads it in the background and all players switch to it on the same simulation frame.
- **Desync safety net.** Every 30 frames the host shares fighter positions and life/energy bars. If a guest's game has drifted, it is nudged back toward the host's and the HUD counts it ("desyncs fixed N").
- **Relay server.** Players join a room by a 5-letter code, so nobody needs to forward ports except whoever runs the server. The host can also run the relay inside the game ("Host on this PC").

Pause is disabled online. Battles use the host's settings (rounds, items, hazards, timer, damage handicap, special forms). Each player picks from the Digimon unlocked in their own save. The host's settings are never written into a guest's save file.

## Install (each player)

1. Close the game.
2. Download `DigimonNetplay-Client.zip` from the [Releases](../../releases) page and extract it into the game folder, the one containing `ProjectDigimon.exe`. It adds BepInEx 5.4.23.2 (`winhttp.dll`, `doorstop_config.ini`, `BepInEx/`) and `BepInEx/plugins/DigimonNetplay/DigimonNetplay.dll`. It does not change any of the game's own files.
3. Start the game once. `BepInEx/LogOutput.log` should contain `Digimon Netplay 0.1.1 loaded`.

Everyone must use the same game version (1.16) and the same mod version.

To uninstall, delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder.

## Run a server (one person, or a cheap VPS)

Download the server zip for your system from the [Releases](../../releases) page, extract it, and run:

- Windows: `DigimonNetplayServer.exe --port 7777`
- Linux: `./DigimonNetplayServer --port 7777`

Players on other networks need TCP port 7777 forwarded to that machine (or the VPS firewall opened). The server only relays messages, so it uses very little CPU or bandwidth.

Alternative without a separate server: the host clicks **Host on this PC** in the mod's window, forwards TCP 7777 on their router, and gives friends their public IP.

## Play

1. Go to the main menu and press **F8** to open the Online Versus window.
2. Enter your name and the server address, then **Connect to server**.
3. The host clicks **Create 1v1 room** or **Create 4-player room** and shares the room code. Others type it and **Join**.
4. Everyone picks a Digimon, color and evolution in the window.
5. The host picks the stage, rounds to win and input delay, then clicks **Start match**. Everyone goes to the VS screen together.
6. After the battle, everyone is back at the menu and still in the room; the host can start a rematch.

The bottom-left HUD shows ping, input delay, how long the game waited for someone, and how many desyncs were corrected.

**Input delay:** 2 to 3 frames is fine for players in the same region, 4 to 6 for long distances. If the game keeps pausing briefly, the host should raise it. The window suggests a value from your ping.

## Settings

`BepInEx/config/community.projectdigimon.netplay.cfg` (created on first run):

| Setting | Default | Meaning |
|---|---|---|
| ServerHost / ServerPort | 127.0.0.1 / 7777 | Last server used |
| PlayerName | Tamer | Name shown to others |
| LocalControls | K1,G1 | Which of the game's control sets drive your fighter online (K1/K2 keyboard layouts, G1..G6 gamepads), merged. Uses your rebinds from the game's options. |
| DefaultInputDelay | 3 | Delay a new room starts with |
| ToggleKey | F8 | Opens/closes the window |
| StallTimeoutSeconds | 15 | How long to wait for a silent player before ending the match |

## Known limits

- Early version: 1v1 has been played successfully in the real game; 3 to 4 player rooms are less tested. Send `BepInEx/LogOutput.log` from each player when something goes wrong.
- Lockstep is only as deterministic as the game. Rare moves that depend on things outside the synced state may still drift. The desync check corrects positions and life/energy bars, but not animations or projectiles.
- Supported modes: 1v1 versus (2 players) and battle royale free-for-all (3 to 4 players). Team battles, arcade, story and special battles stay offline.
- No rollback. A bad connection shows up as brief pauses, not teleports.
- A player who closes the game or loses connection ends the match for everyone (back to the main menu).

## Build from source

Requires the .NET 8 SDK.

```
# client mod (needs the game's DLLs and BepInEx installed in the game folder)
dotnet build src/Plugin -c Release -p:GameDir="C:\Games\ProjectDIGIMON"

# server
dotnet publish src/Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# tests: real relay server + simulated players using the mod's lockstep core
dotnet run --project tests/NetcodeTests -c Release

# after a game update: check every method/field the mod hooks still exists
dotnet run --project tests/HookCheck -c Release -- "C:\Games\ProjectDIGIMON\ProjectDigimon_Data\Managed\Assembly-CSharp.dll"
```

Layout:

- `src/Shared`: wire protocol, relay server, relay client, lockstep core (no Unity dependency; used by mod, server and tests)
- `src/Plugin`: BepInEx plugin (lobby window, match setup, frame gate, Harmony patches)
- `src/Server`: standalone relay server
- `tests/NetcodeTests`: end-to-end netcode tests
- `tests/HookCheck`: verifies the game DLL still has everything the mod patches
