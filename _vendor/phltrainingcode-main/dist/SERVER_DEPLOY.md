# FlamiePrac — blank dedicated server deploy (no MultiSheet)

Standalone training mod for a vanilla Puck dedicated server. **Same package on server and every client.**

## Package contents

Copy this entire folder into:

`…/Puck/Plugins/FlamiePrac/`

**Blank dedicated (this VPS layout):** `/srv/puck-download/Plugins/FlamiePrac`

| File | Role |
|------|------|
| `MyMod.dll` | Plugin |
| `trainingprefabs` | Asset bundle (required) |
| `training_layout.json` | Auto-start hive + passers (seeded on first boot if missing) |
| `training_layout.example.json` | Template |
| `training_prefab_names.json` | Prefab rename map |
| `RadioSongs/` | Optional `.mp3` tracks (clients + listen-server) |
| `SERVER_DEPLOY.md` | This file |

Do **not** install MultiSheet for this target.

## Server steps

1. Stop the dedicated process.
2. Drop `FlamieTraining` into the server `Plugins` folder (create it if needed).
3. Enable the mod in the server mod list / config the same way you enable any other `IPuckPlugin`.
4. Start dedicated. In the server log, confirm:
   - `FlamiePrac 1.0.0 protocol=1 target=blank-dedicated`
   - `Network ready — … IsServer=True Dedicated=True`
   - `Starting training mode` / layout spawn lines
   - Goalie / slidable ready lines (no bundle errors)

## Client steps

1. Install the **same** `FlamieTraining` folder under the game `Plugins` directory.
2. Enable the mod locally.
3. Join the dedicated server.
4. You should see the hive, neon pass bumpers, radio chip, and synced slidables.

Clients without the mod will not see training props (server still runs them).

## Version lock

Server and clients should show the same banner:

```text
[FlamiePrac] … FlamiePrac 1.0.0 protocol=1 target=blank-dedicated
```

Mismatch = update both sides from the same `dist` zip.

## Smoke test (dedicated + 2 clients)

1. Hive + passers visible to both clients  
2. Late join still gets a full snapshot  
3. Stick-push speaker / beam — motion on all clients  
4. Bumper center shot returns the puck  
5. `/nextsong` from a client changes audio for peers with songs installed  

## Build / pack / auto-deploy (dev machine)

```powershell
# Build + copy local Plugins\FlamiePrac + scp to VPS /srv/puck-download/Plugins/FlamiePrac
.\deploy-server.ps1

# Upload current dist only (no rebuild)
.\deploy-server.ps1 -SkipBuild

# Zip for manual copy
.\pack-server.ps1
```

Config (SSH key/host — keep outside public git):  
`%USERPROFILE%\OneDrive\Desktop\Puck Mod\Server & SQL Files\tools\flamietraining-deploy.json`

After a remote deploy, **restart the dedicated Puck process** so it reloads `MyMod.dll`.
