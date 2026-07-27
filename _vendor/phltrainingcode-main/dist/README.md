# PHL Training Mod (FlamiePrac)

Standalone practice tools for Puck **blank dedicated servers** (no MultiSheet).

**Deploy guide:** [SERVER_DEPLOY.md](SERVER_DEPLOY.md)  
**Full fix write-up:** [docs/PHASE1_FIXES.md](docs/PHASE1_FIXES.md)

**Mod required on all peers:** server + every client need the same `FlamieTraining` package.

**Disable cleanly:** toggling the mod off runs full teardown (Harmony unpatch, despawn props, remove HUD).

## Quick deploy to blank dedicated

```powershell
# Build + local Plugins\FlamiePrac + scp to /srv/puck-download/Plugins/FlamiePrac
.\deploy-server.ps1
```

Or zip only: `.\pack-server.ps1` → extract to `Plugins/FlamiePrac` on **server and clients**.

SSH/host config lives in `Server & SQL Files\tools\flamietraining-deploy.json` (not in this repo).

## Deploy folder contents

Build copies to Steam `Plugins\FlamieTraining\` and `dist\`:

- `MyMod.dll`
- `trainingprefabs` (asset bundle)
- `training_layout.json` (auto-seeded from example on first boot if missing)
- `training_layout.example.json`
- `training_prefab_names.json`
- `RadioSongs/` (optional `.mp3` files)
- `SERVER_DEPLOY.md`

## Blank dedicated QA (no MultiSheet)

1. Dedicated process with **only** FlamieTraining (plus stock game).
2. Two clients with the **same** package join.
3. Log shows `FlamiePrac 1.0.0 protocol=1 target=blank-dedicated` and `IsServer=True Dedicated=True`.
4. Hive + neon passers visible; late join gets snapshot.
5. Stick-push speakers/beam; bumper center returns puck; `/nextsong` works.

## Features (Phase 1)

| Feature | Notes |
|---------|--------|
| **Pass-back bumpers** | Neon green 5 m boards at the goal line; lead pass between feet and stick blade |
| **MaxPractice goalie** | Real AI `Player` at the hive net (decorative prefab goalie hidden) |
| **Slidable beams** | Long prefab rectangles in the hive — push with stick (all beams, server-synced) |
| **Test puck** | Press **R** to spawn puck above stick (QA) |
| **Radio** | 3D speaker audio; shuffle next; single-prev restart, double-prev history |

## Chat commands

| Command | Description |
|---------|-------------|
| `/speed 200` | Rotation speed (alias: `/lu54bdhrtjr`) |
| `/trainhere [prefab]` | Spawn prefab at your feet + append to `training_layout.json` |
| `/traindump` | Log all spawn positions to server log |
| `/trainreload` | Reload layout JSON from disk |
| `/passer` | Spawn pass-back box pair |
| `/targetpractise` | Spawn moving circular target |

## Radio

Songs go in `RadioSongs\` next to `MyMod.dll` — any `.mp3` filename (e.g. `WatermelonCrawl.mp3`).

| Command | Description |
|---------|-------------|
| `/nextsong` | Next track (alias: `/radioskip`) |
| `/prevsong` | Previous track — **tap once** restarts current; **double-tap within 0.4 s** goes back in history (alias: `/radioprev`) |

**Next/auto-advance** picks randomly without repeating until every song has played once.

**Do not use `/skip` or `/prev`** — those are reserved Puck admin commands and will show “You do not have permissions.”

After joining a rink, wait a few seconds for MP3s to load. **Radio controls are optional** — click the small **♪ Radio** chip at the **bottom-left** to open the panel (close with **✕**). It does not cover the team/role select screen. Chat commands `/nextsong` and `/prevsong` still work. **Audio plays from the Speaker prop in 3D** — walk toward the training hive to hear it clearly.

### Radio log lines (Player.log / Puck.log)

```
[FlamiePrac] Radio looking for songs in: ...\FlamieTraining\RadioSongs
[FlamiePrac] Radio found 1 track(s).
[FlamiePrac] Radio loaded: WatermelonCrawl
[FlamiePrac] Radio playing: WatermelonCrawl
```

If loading fails, check for `Failed to load` or `RadioSongs folder not found`.

### Host vs dedicated

| Mode | Radio |
|------|--------|
| **Local host** (you start the server) | Works — radio attaches to the hive on host |
| **Dedicated server + remote client** | Works on the joining client |
| **Dedicated headless (no client)** | No radio (expected — no listener) |

## Architecture

- **Server**: spawns authority objects with colliders + gameplay scripts.
- **Clients**: receive spawns via Custom Messaging (`TrainingSync`) and instantiate visuals locally.
- **Radio**: `RadioController` on the hive **Speaker** — 3D MP3 + screen UI; track changes synced via `FlamiePrac_Radio` / `FlamiePrac_RadioRequest`.
- **training_layout.json** in the plugin folder overrides built-in pass-back defaults — update or delete after layout changes.
- Joining clients request a full snapshot from the server.

See [docs/PHASE1_FIXES.md](docs/PHASE1_FIXES.md) for root causes, fix details, QA checklist, and changelog.
