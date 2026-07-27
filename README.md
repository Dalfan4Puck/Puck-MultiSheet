# PHLPracticeModPack

Experimental practice-server mod: **three copies of the base-game hockey rink** on one server, with `/rink1` `/rink2` `/rink3` teleports.

Uses PuckLargeLevel **chunk sync only** (from `_vendor/PuckLargeLevel`) so positions beyond ~50 m stay networked. **Does not** use Jake-Porter's open-world `TestLevel` bundle by default.

```
PHLPracticeModPack/
├── _vendor/PuckLargeLevel/   ← chunk-sync C# (compiled in)
├── VanillaRinkCloner.cs      ← clones base-game Rink + goals
├── config/multi_rink.json
└── dist/
```

---

## How rinks are built

| Slot | Geometry |
|---|---|
| **Rink 1** (z=0) | Original `level_default` objects — `Rink`, `Goal Blue`, `Goal Red` |
| **Rink 2+** | Server clones (colliders) + client clones (visuals with ice materials copied from rink 1) |

Clone templates (from arena dump): `Rink`, `Goal Blue`, `Goal Red`, `Lights/Goal Blue`, `Lights/Goal Red`. Spawn Y auto-resolves to ice surface (~0.03) + 1 m when config `Spawn.y` is 0.

No Unity map file in this repo. Clones happen at level load via `Instantiate`.

**Not isolated yet:** no walls between sheets; shared hangar hidden (`HideHangar: true`). Players can still skate from one sheet to another in the empty space between origins.

**Known limits (to test on server):**
- Extra `Goal Blue` / `Goal Red` copies may confuse scoring if the game assumes one pair globally
- Cloned nets include cloth sim — extra CPU cost
- Scoreboards / spectator booths stay at rink 1 only

Legacy optional path: `UseAssetBundle: true` + `assets/puckobjects` for the open-world TestLevel (not the hockey rink).

---

## Commands

| Command | Action |
|---|---|
| `/rink1` `/rink2` `/rink3` | Teleport to that rink's spawn |
| `/rinks` | List rinks |

Chunk sync + optional R-key practice pucks (PuckLargeLevel) when enabled.

---

## Config (`config/multi_rink.json`)

```json
{
  "EnableMultiRink": true,
  "UseAssetBundle": false,
  "HideHangar": true,
  "CloneTemplates": ["Rink", "Goal Blue", "Goal Red"],
  "Rinks": [ … ]
}
```

---

## Build

```powershell
dotnet build PHLPracticeModPack.csproj -c Release
```

Or use the deploy script (builds, commits, and pushes to GitHub by default):

```powershell
.\deploy-qa.ps1
.\deploy-qa.ps1 -SkipGit          # build only — then Workshop-upload dist/MultiSheet.dll
.\tools\sync-git.ps1              # commit + push without rebuilding
```

Output (Workshop upload this file):

`dist/MultiSheet.dll`

Workshop item: `3771130437`

Vendor refresh: `.\setup-vendor.ps1` (if present)

Build expects `../libs/Puck.dll` (copy from your Puck install: `Puck_Data/Managed/Puck.dll`).

**Git remote:** `git@github.com:Dalfan4Puck/Puck-MultiSheet.git` (repo root is this folder).

---

## Deployment (Steam Workshop)

1. Run `.\deploy-qa.ps1` (or `dotnet build`) to produce `dist/MultiSheet.dll`.
2. GitHub sync runs automatically after each successful build unless you pass `-SkipGit`.
3. Upload `dist/MultiSheet.dll` to Workshop item **`3771130437`**.
4. Steam delivers the same build to subscribed clients and dedicated servers.
5. Fully restart Puck (client) and restart the dedicated server if needed so Workshop content refreshes.

Optional dev path: `.\deploy-qa.ps1 -DeployLocal` copies into a local `Puck\Plugins\` folder (bypasses Workshop). Fully quit Puck before testing.

Server **config** (`config/multi_rink.json`) is edited on the host separately from Workshop DLL updates. Clients sync rink layout from the server MOTD list when running a matching build.

### Client FPS A/B (`config/multisheet_client.json`)

See `config/multisheet_client.example.json`. Defaults: `renderAllRinks: false` (just my rink). Optional kill switches for isolating the remaining ~100 FPS at one rink:

| Flag | Effect |
|---|---|
| `skipChunkClient` | Do not arm client chunk decode |
| `skipArenaLighting` | Skip `ArenaLighting.Apply` / enforcer tick |
| `skipClientBuild` | Skip clone / proxy / ground build |
| `skipPracticeHud` | Skip practice clock `LateTick` |
| `hideStockPucks` | Hide all puck meshes except local R-spawn |
| `skipScoreboardUi` | Skip Rinks-tab inject + Tab hold-open (vanilla Tab scoreboard) |
| `skipMotdUi` | Skip join/F9 MOTD overlay **and** preview camera rig (scoreboard tiles lose live/static RT feeds) |

**Path:** `C:\Program Files (x86)\Steam\steamapps\common\Puck\config\multisheet_client.json`  
**Edit only while Puck is fully quit.** Lighting/UI toggles call `Flush()` and rewrite the whole file from memory — hand-edits made while the game is open get clobbered (flags snap back to `false`).

On launch, Player.log should contain one line like  
`[PHLPractice] Client settings from ... skipMotdUi=true skipScoreboardUi=true ...`.  
If that line shows `false`, the JSON never applied (wrong file, clobber, or old Workshop DLL).

---

## Related mods

- **AIGoaliesStandalone** — bots (separate DLL)
- **MaxPractice** — save prac, cones, etc.

PuckLargeLevel upstream: https://github.com/Jake-Porter/PuckLargeLevel
