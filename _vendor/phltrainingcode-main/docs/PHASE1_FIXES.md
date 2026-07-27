# PHL Training Mod — Phase 1 Fixes

What was broken, why it looked fine locally, and what we changed before MultiSheet integration.

**Mod log prefix:** `[FlamiePrac]`  
**Build output:** `_vendor/phltrainingcode-main/dist/`  
**QA scope:** Vanilla dedicated server, MultiSheet **disabled**, at least two clients (one must be a remote join, not host-only).

---

## Executive summary

The commissioned training mod worked when the person running it was **also the server** (local practice / host). It failed on a **dedicated server** because:

1. Training props were spawned with plain `GameObject.Instantiate` — **never replicated to clients**.
2. Several server-side scripts **fought normal player spawning** (forced teleport to center ice).
3. Presentation code (radio UI, audio) ran on the **headless server** where it cannot work.
4. Layout was **hardcoded for a single rink at world origin** with no config or authoring tools.

Phase 1 fixes add **server → client replication**, remove spawn conflicts, split server vs client responsibilities, introduce a **JSON layout + in-game placement commands**, and iterate on **radio** (commands, UI, client networking, 3D speaker audio).

Follow-up sessions (documented in [Changelog](#changelog-vendor-fix-sessions)) fixed radio UI, remote-client `/nextsong`, spatial audio, **MaxPractice AI goalie**, **pass-back bumper layout/AI**, **slidable prefab beams**, **R-key test puck spawn**, **radio shuffle/prev behavior**, **ServerTime-locked rotator/mover sync**, **tick-rate slidable sync**, **client snapshot rejoin**, **passer DDOL parenting**, **snapshot reliability**, **workshop join-enable vs app-start bootstrap**, and **single-process VPS restart** — see sections below, especially **Workshop enable modes**, **How we got moving items synced**, and **Client snapshot one-time pass**.

---

## Why it “worked on client” but not on server

| Scenario | What happens | Looks like |
|----------|--------------|------------|
| **Local host / practice** | Server and client run in the same process. Server-side `Instantiate` objects exist in the same scene the player renders. | “It works!” |
| **Dedicated server + remote client** | Objects exist only in the server process. Netcode does **not** auto-sync plain `GameObject`s. | Empty rink — no hive, no passers, no targets |
| **Dedicated server log only** | `[FlamiePrac] Spawned 'trainingprefab'` appears in `Puck.log` | Owner thinks it works; clients see nothing |

This is the #1 reported symptom: *“works locally, broken on the VPS.”*

---

## Root causes (original vendor code)

### 1. No network replication

`TrainingObjectManager.SpawnTrainingObject()` did:

```csharp
GameObject obj = GameObject.Instantiate(prefab, position, rotation);
```

In Unity Netcode, that creates a **server-local** object. Unless the prefab is a registered `NetworkObject` (or you manually sync it), **joining clients never receive it**.

There was no:

- `NetworkObject.Spawn()`
- ClientRpc / ServerRpc
- Custom Messaging spawn list
- Snapshot on client connect

### 2. Manager only partially gated to server

`MyMod.OnEnable` created `TrainingObjectManager` only when:

```csharp
Application.isBatchMode || NetworkManager.Singleton.IsServer
```

That part is correct for **authority**, but without client-side mirroring it meant clients had bundles loaded and nothing to show.

`TrainingObjectManager.Start()` also bailed if `NetworkManager.Singleton` was null at startup — timing could fail silently on some boot orders.

### 3. Forced player teleport (MultiSheet conflict)

In `Update()`, when collider count exceeded 50 (“arena loaded”):

```csharp
Invoke(nameof(ForceAllPlayersToSpawn), 0.2f);
// ForceAllPlayersToSpawn → every player to (0, 1.2, 0)
```

That overrides normal spawn flow. On a MultiSheet server it would yank players off their chosen rink back to rink-1 center. Even on a vanilla server it is hostile to normal join/spawn.

### 4. Single puck / single player assumptions

These all used `FindFirstObjectByType<Puck>()` / `FindFirstObjectByType<Player>()`:

- `GoalieController`
- `GoalieStickController`
- `PuckPasser`
- `PuckSpawner`
- `ReactiveTarget`

On a busy server, drills track the **first** puck/player in the scene, not the one on your rink. Acceptable for Phase 1 single-rink QA; must be scoped per-rink before MultiSheet integration.

### 5. Radio ran on dedicated server

`RadioController` on the `Speaker` child:

- Created a `ScreenSpaceOverlay` canvas
- Loaded MP3s from disk
- Ran `Update()` UI animation

On a **batch-mode / headless** dedicated server there is no display and no listener. `CollisionHelper` disabled audio on headless, but UI creation still ran server-side.

### 6. Hardcoded coordinates

All auto-spawns assumed **one rink at origin**:

| Object | Position |
|--------|----------|
| Training hive | `(0.8, 0, 21)` |
| Pass-back boxes | `(±6, 0, 38.46)` — neon green, 5 m long, angled 45° at goal line |
| Circular target (commented cmd) | `(0, 1.4, 40)` |

MultiSheet rinks use a grid with 64m X / 128m Z spacing — these offsets only make sense on **rink 1** even after replication is fixed.

### 7. Chat / messaging bugs

- `GetSteamIdFromPlayer()` returned `OwnerClientId`, but `SendMessage` passed it to `ChatManager.Server_SendChatMessage` as if it were a Steam ID — replies often went nowhere.
- `/skip` and `/prev` are **stock Puck admin commands** (phase skip) — mods never receive them; players see “You do not have permissions.”
- Original code called `RadioController.Instance` on the **server**, where no radio exists after headless stripping.
- Most useful chat commands were **commented out** in the switch; only an obfuscated `/lu54bdhrtjr` rotation command was active.

### 8. Empty Harmony patch

`ChatPatch.cs` patched `UIChat.Server_ProcessPlayerChatMessage` with an **empty** body while `harmony.PatchAll()` ran at plugin enable — dead weight, no functional benefit.

### 9. Passers not tracked or synced

`SpawnOnePasser()` created primitive cubes but did **not** register them in `spawnedObjects` or replicate them — same visibility problem as prefabs.

### 11. Radio UI was non-functional (follow-up QA)

Original `RadioController.CreateUI()` built text and a progress bar but **no interactive controls**:

- No `Button` components — “controls” were visual only.
- `songText` rect stretched over the full panel, covering the volume label.
- Volume showed as `🔊 NN%` text with **no way to change it**.
- Runtime uGUI needs an **`EventSystem`**; none was created, so clicks would fail even if buttons existed.

### 12. Client radio commands did not reach the mod (follow-up QA)

Radio skip was wired only through **`Event_Server_OnChatCommand`** on `TrainingObjectManager` (server-only component). Remote clients typing `/nextsong` appeared to do nothing because:

- The mod assumed the server chat event would always fire for client-originated commands.
- There was no **client → server** radio request channel.
- UI buttons called `NextSong()` locally but did not sync to other players until `RequestRadioCommand` was added.

Fix pattern came from **PUBLIC MOD LIBRARY** (`ToastersReskinLoader` client chat prefix + CPT server listener).

### 13. Host would have gotten duplicates (latent bug)

Once replication was added naïvely, a **host** (IsServer + IsClient) would spawn authority objects **and** receive client mirror spawns. We explicitly skip client mirrors when `NetworkManager.IsServer`.

Same rule applied later to **radio**: `OnRadioReceived` skips when `nm.IsServer` because `BroadcastRadioCommand` already calls `ApplyRadioCommandLocal` on the host.

---

## What we fixed

### New: `TrainingSync.cs`

Custom Messaging layer (same pattern as MultiSheet’s `PuckSpawnSync` / `RinkMotdService`):

| Channel | Direction | Purpose |
|---------|-----------|---------|
| `FlamiePrac_Spawn` | Server → all clients | One new object (prefab, passer, or target) |
| `FlamiePrac_Despawn` | Server → all clients | Remove by sync ID |
| `FlamiePrac_Snapshot` | Server → one client | Full spawn list on join |
| `FlamiePrac_RequestSync` | Client → server | Joiner asks for snapshot |
| `FlamiePrac_Radio` | Server → all clients | Apply track change on pure clients |
| `FlamiePrac_RadioRequest` | Client → server | Pure client asks host to change track |
| `FlamiePrac_Slidable` | Server → all clients | Slidable beam/speaker pose + velocity (network tick while moving) |
| `FlamiePrac_Mover` | Server → all clients | Mover **params** (`/speed`, decoy rates) + **circular-target poses**; rotators/decoy do **not** need per-tick poses (they clock-lock) |
| `FlamiePrac_TestPuckSpawn` | Client → server | QA request to spawn puck above stick |

**Boot flow:**

1. `MyMod.OnEnable` creates `FlamiePrac_Bootstrap` + `TrainingSync` on **every** peer (loads bundles everywhere). This may run at **app start** or at **workshop server-join**.
2. `TrainingSync` → `WaitForNetwork` → `CatchUpAfterNetworkReady` (idempotent): register handlers, server ice-safe AutoStart nudge, client snapshot request. Also listens for `Event_OnClientStarted` / `Stopped`.
3. Server auto-starts training when rink ice is ready (`StartTrainingModeWhenReady` → `StartTrainingMode`). Catch-up must **not** spawn before ice.
4. Each spawn calls `BroadcastSpawn`; joiners get coalesced/deferred snapshots (scene sync / level spawn / retry — see snapshot + workshop sections).
5. Pure clients instantiate **local visual copies** under `FlamiePrac_ClientVisuals` (DDOL); host skips mirrors to avoid duplicates.

### New: `TrainingObjectFactory.cs`

Single place to build props with a clear split:

| Role | Colliders | Gameplay scripts | Radio |
|------|-----------|------------------|-------|
| **ServerAuthority** | Yes (`CollisionHelper`) | Rotators, passers, slidable beams | No |
| **ClientVisual** | No (puck physics is server-side) | Rotators, slidable visuals | Yes on `Speaker` |

MaxPractice AI goalie spawns separately via `FlamiePracTrainingGoalie` (real networked `Player`, not a prefab child script).

### New: `TrainingLayoutConfig.cs`

- Reads `training_layout.json` next to the DLL.
- Falls back to built-in defaults (hive + two passers).
- Example file: `training_layout.example.json` → rename to `training_layout.json` on deploy.

### Refactored: `TrainingObjectManager.cs`

**Removed:**

- `ForceAllPlayersToSpawn` and arena collider-count detection in `Update`
- `DisableEverythingExceptPlayers` / world-disable / teleport-lock loops
- Verbose per-renderer debug spam on every spawn
- ~700 lines of commented/WIP dead paths kept for reference elsewhere

**Added / kept:**

- Layout-driven auto-start from JSON
- Placement chat commands (see below)
- `GetSpawnRecords()` for snapshot sync
- Passers and targets registered and replicated like prefabs
- `SendMessageToClient(clientId, …)` using correct client ID

### Updated: `MyMod.cs`

- Always creates `TrainingSync` bootstrap (not only on server).
- `ChatPatch.cs` excluded from build (empty patch — no benefit).
- Enables Harmony for **`TrainingClientChat`** only (client radio commands).

### New: `TrainingClientChat.cs`

Harmony prefix on `ChatManagerController.Event_OnChatSubmitMessage` (pattern copied from **PUBLIC MOD LIBRARY** → `ToastersReskinLoader-main/PatchClientChat.cs`).

**Why this exists:** Server-side radio commands were wired to `Event_Server_OnChatCommand` (pattern from **CompetitivePuckTweaks** / **BlackMirror**). That works when the **host** types in chat, but **pure remote clients** often never get mod commands through that pipeline reliably. Guessing at a fix without reference mods wasted time.

**Flow for a pure client typing `/nextsong`:**

1. Client chat prefix intercepts before default handling (`return false`).
2. `TrainingSync.RequestRadioCommand(CmdNext)` sends `FlamiePrac_RadioRequest` to server.
3. Server `OnRadioRequest` → `BroadcastRadioCommand` → `FlamiePrac_Radio` to all clients.
4. Pure clients: `OnRadioReceived` → `ApplyRadioCommandLocal` → `RadioController.NextSong()`.
5. Host/server: `BroadcastRadioCommand` already calls `ApplyRadioCommandLocal` (host does **not** also process `OnRadioReceived` — see below).

**Host typing `/nextsong`:** Prefix returns `true` (unchanged); `TrainingObjectManager` handles via `Event_Server_OnChatCommand` → `BroadcastRadioCommand`.

### Updated: `TrainingSync.cs` (radio follow-ups)

- **`RequestRadioCommand(byte)`** — unified entry for UI buttons and client chat; server broadcasts, client sends request.
- **`OnRadioReceived`** — skips when `nm.IsServer` so the host does not apply track changes twice (same rule as spawn mirrors).
- Server registers handler for **`FlamiePrac_RadioRequest`**.

### Updated: `RadioController.cs`

Initial radio pass (headless skip, host attach, MP3 URI, command rename):

- Skips UI/audio on batch-mode dedicated server (headless).
- **`/nextsong` / `/prevsong`** (not `/skip` — reserved Puck admin command).
- **Host fix:** radio attaches on the authority hive when you are server+client (host skips client mirror spawns).
- **Host fix:** `/nextsong` applies locally on host after server broadcast.
- **MP3 path fix:** `file://` URLs use proper `Uri` encoding (fixes `Program Files (x86)` paths with spaces).
- Log prefix `[FlamiePrac]` on load/play/errors for easier QA.

**UI pass** (buttons appeared broken, no usable volume control):

| Problem | Cause | Fix |
|---------|--------|-----|
| Buttons did nothing | No `Button` components existed — only decorative text | Added Prev / Play-Pause / Next `Button`s wired to `RequestTrackChange` |
| Clicks still ignored | No `EventSystem` in scene for runtime-built uGUI | `EnsureEventSystem()` creates one if missing |
| Volume % invisible / useless | `songText` rect filled entire panel; no slider | Fixed layout zones; added `Slider` + `%` label |
| Annoying UI motion | Pulsing scale / rainbow outline on every frame | Removed; kept progress bar + time |

**UI details:**

- Screen-space overlay panel (top-right): title, track name, “Next: …”, progress bar, time, control row, volume slider.
- Volume persisted via `PlayerPrefs` key `FlamiePrac_RadioVolume` (default 75%).
- Play/Pause toggles `userPaused` so auto-advance does not fight manual pause.
- Auto-advance at end of track uses `advancingTrack` guard and detects the **playing→stopped** edge (Unity resets `AudioSource.time` to 0 at end-of-clip, so comparing `time` to `length` was unreliable).
- **Auto-advance is local-only** (`NextSong` on each client) so multiple joined clients don't all send `RadioRequest` to the server when a track ends.
- Manual `/nextsong` / UI skip still syncs via `FlamiePrac_Radio`.

**Radio pass 5 — shuffle pool + smarter prev:**

| Behavior | Detail |
|----------|--------|
| **Next / auto-advance** | Random pick from an **unplayed pool**; no track repeats until every song has played once, then pool resets |
| **Single Prev** | Restarts the current track from 0:00 (0.4 s double-tap window) |
| **Double Prev** | Second press within 0.4 s steps back through **play history** instead of restarting |

**Audio pass** (2D → 3D at Speaker):

- `AudioSource` is added to the **`Speaker`** child transform (same as the prop in the hive prefab).
- **3D spatial** (`spatialBlend = 1`, logarithmic rolloff, ~4 m full level, fades by ~48 m).

**UI pass 2 — removed broken uGUI, replaced with UITK (Radio pass 4):**

| Problem | Cause | Fix |
|---------|--------|-----|
| Blocked team/role select | Full-screen uGUI `Canvas` sort order 999 + panel `raycastTarget` | Removed all runtime uGUI |
| Buttons not clickable | Custom `EventSystem` fought Puck's UITK input | No EventSystem; use UITK on `UIManager` |
| Could not close panel | Always visible top-right overlay | **Collapsed by default** — bottom-left **♪ Radio** chip; **✕** closes |
| Orphan UI on disable | `RadioCanvas` not parented to Speaker | `RadioHudUI.TearDown()` + legacy canvas cleanup |

Pattern: same as **MultiSheet** `RinkMotdUI` / **ToasterStatsOverlay** — attach to `UIManager.RootVisualElement`, `pickingMode.Ignore` on host, interactive elements only on chip/panel. Audio stays in `RadioController` on the Speaker.

### Updated: `MyMod.csproj` deploy

- `dotnet build -c Release` deploys directly to `Puck\Plugins\FlamieTraining\` (archive copy in `dist/`).

### Updated: `PuckPasser.cs` / `CircularMovingTarget.cs`

- Puck detection and pass logic run **server-side only**.
- Clients still see visuals; puck motion comes from game netcode.

**Pass-back AI pass 2 (`PuckPasser.cs`):**

| Problem | Fix |
|---------|-----|
| Wrong shooter on busy ice | Raycast along **incoming puck velocity** (+ hit-face forward + scored fallback) to find the passer |
| Pass felt late / behind player | **Intercept lead** — quadratic solve for puck travel time using bumper distance + player body velocity |
| Pass always to same spot | Random blend **0–1** between **feet** (body at ice Y = 0.08) and **stick blade** (`BladeHandlePosition`), both endpoints led |

Server log on each pass: `[PuckPasser] Pass to … blend=… (0=feet,1=blade)`.

### Updated: Pass-back bumper layout (`TrainingLayoutConfig.cs`, `TrainingObjectFactory.cs`, `training_layout.example.json`)

| Problem | Fix |
|---------|-----|
| 12 m boards extended **behind the goal line** into the end zone | Shortened to **5 m** (`DefaultPasserLength`); `PasserCenterZ()` places center so the goal-side edge sits on the blue goal line (`BlueGoalLineZ = 40.23`) |
| Hard to see procedural passers vs prefab beams | **Neon green** material (`PasserNeonGreen`) on server + client visual copies |
| Stale deploy overrides new defaults | **`training_layout.json` in the plugin folder wins** over built-in defaults — delete or update it after layout changes |

Default positions: **x = ±6**, **z ≈ 38.46**, **rotation Y = ±45°**, scale **(5, 0.55, 0.5)**.

### New: MaxPractice AI goalie (replaces decorative prefab goalie)

The original hive used static `GoalieModel` / `GoalieStickController` meshes — they looked like a goalie but did not play like one.

| Change | Detail |
|--------|--------|
| **Vendored** | `MaxPractice/` — `GoalieAI.cs`, `GoalieAIManager.cs`, `PracticePatches.cs`, `PracticeHelpers.cs`, `ModConfig.cs` |
| **Integration** | `FlamiePracMaxPracticeShim.cs`, `FlamiePracGoaliePlacement.cs`, `FlamiePracTrainingGoalie.cs`, `FlamiePracGoalieBootstrap.cs` |
| **Removed from build** | `GoalieController.cs`, `GoalieStickController.cs`, `TrainingGoalieLogic.cs` |
| **Spawn** | One real networked **Player** AI goalie at the hive net (`Goaltarp` / `GoalieModel` anchor) after hive loads |
| **Visuals** | Decorative `GoalieModel` / `GoalieModelStick` hidden; MaxPractice drives crease + save logic |
| **Phase 1 scope** | **Single net only** — `FlamiePracGoaliePlacement` overrides vanilla ±40.23 crease for the training hive side |

Port source: `Training/MaxPractice-main/src` (not the empty `PUBLIC MOD LIBRARY/MaxPractice-main` folder). Build stubs: `YoyoManager` no-op, TCP player-count JSON patched via regex (no Newtonsoft dependency).

Log: `[FlamiePrac] MaxPractice AI goalie spawn team=… success=…`

### New: R-key test puck spawn (`FlamiePracTestPuckSpawn.cs`)

- Press **R** (local input) → `TrainingSync.RequestTestPuckSpawn()` → server spawns puck **0.5 m above stick blade** via `PuckManager.Server_SpawnPuck`.
- Server-authoritative; 0.35 s cooldown; skipped on headless batch mode.
- Channel: `FlamiePrac_TestPuckSpawn`.

### New: Slidable prefab beams (`SlidableObstacle*.cs`, `SlidableObstacleSetup.cs`)

Long dark rectangular meshes in the training hive prefab can be pushed along the ice with stick contact.

| Piece | Role |
|-------|------|
| **`SlidableObstacleSetup`** | Finds beam renderers in the hive, adds dynamic `Rigidbody` + collider, registers per-beam `RelativePath` |
| **`SlidableObstacle`** | Server physics — mass 120, low friction, Y + rotation frozen |
| **`SlidableObstacleSync`** | Replicates transform (+ velocity) on network tick via `FlamiePrac_Slidable` |
| **`SlidableObstacleVisual`** | Client-side mirror with SmoothDamp + short extrapolation |
| **`TrainingMotionSync`** | Params broadcast + circular-target poses (`FlamiePrac_Mover`); rotators/decoy use `ServerTime` local sim |

**Multi-beam fix:** early builds only configured the **single best-scoring** beam (`FindSlidableBeam`). A second long beam stayed on the parent **kinematic** rigidbody and could not move. Now **`FindAllSlidableBeams`** configures every mesh that passes the heuristic (length ≥ 3.5 m, aspect ≥ 2:1, height 0.2–2.5 m). Skips goalie, speaker, passer, spinner, and other named gameplay objects.

Log: `[FlamiePrac] Configured N slidable beam(s) on 'trainingprefab'.`

### Updated: `CollisionHelper.cs`

- `AddHitboxes(obj, serverAuthority: true/false)` — headless visual strip only when building server authority meshes.

### Build / project fixes (`MyMod.csproj`)

- References `Puck.dll` from `DALF MOD LIBRARY/libs` (same as MultiSheet) — **not** `Assembly-CSharp.dll` (Puck does not ship that name).
- Unity modules from `C:\Program Files (x86)\Steam\steamapps\common\Puck\Puck_Data\Managed`.
- Auto-deploy to `Plugins\FlamieTraining\` on build; archive in `dist/`.

---

## New chat commands (QA / placement)

| Command | Description |
|---------|-------------|
| `/speed 200` | Set spinner rotation speed (alias: `/lu54bdhrtjr`) |
| `/nextsong` / `/prevsong` | Radio — server broadcasts (aliases: `/radioskip`, `/radioprev`) |
| `/trainhere [prefab]` | Spawn at your feet; append entry to `training_layout.json` |
| `/traindump` | Log all spawn records to server log |
| `/trainreload` | Reload `training_layout.json` from disk |
| `/passer` | Spawn pass-back box pair at default positions |
| `/targetpractise` | Spawn moving circular target |
| `/cleartargetpractise` | Clear circular targets |

---

## Radio setup and troubleshooting

### Folder layout

```
Plugins\FlamieTraining\
├── MyMod.dll
├── trainingprefabs
└── RadioSongs\
    └── AnyNameYouWant.mp3    ← not required to be track1.mp3
```

### Commands

| Use | Avoid |
|-----|--------|
| `/nextsong`, `/radioskip` | `/skip` (Puck admin — permission denied) |
| `/prevsong`, `/radioprev` | `/prev` if Puck reserves it |

### Expected log lines

```
[FlamiePrac] Radio looking for songs in: ...\FlamieTraining\RadioSongs
[FlamiePrac] Radio found 1 track(s).
[FlamiePrac] Radio loaded: WatermelonCrawl
[FlamiePrac] Radio playing: WatermelonCrawl
```

### Common failures (fixed in current build)

| Symptom | Cause | Fix |
|---------|--------|-----|
| “No permissions” on `/skip` | Stock Puck admin command | Use `/nextsong` |
| No panel, no audio on **host** | Radio only on client mirrors; host skips mirrors | Host now gets `RadioController` on authority hive |
| `/nextsong` silent on host | Broadcast skipped local apply on server | Host applies radio command locally after broadcast |
| **`/nextsong` does nothing on remote client** | Only server chat listener; client command never reached mod | `TrainingClientChat` + `FlamiePrac_RadioRequest` |
| UI buttons dead | No EventSystem / no Button components | EventSystem bootstrap + wired buttons |
| MP3 never loads, no error | `file://C:\Program Files\...` broken URI | Proper `Uri.AbsoluteUri` encoding |
| Music everywhere, not from speaker | 2D audio (`spatialBlend = 0`) | 3D audio on Speaker transform |
| Host skips two tracks per command | Host received broadcast + `OnRadioReceived` | `OnRadioReceived` skips when `nm.IsServer` |
| `Radio command ignored — no RadioController` | Client joined before snapshot / mod not on client | Enable mod on **all** peers; wait for hive spawn |

### Radio command paths (who handles what)

```
Pure client types /nextsong
  → TrainingClientChat (Harmony prefix)
  → TrainingSync.RequestRadioCommand
  → Server OnRadioRequest → BroadcastRadioCommand
  → All peers: ApplyRadioCommandLocal (host once; clients via OnRadioReceived)

Host types /nextsong
  → Event_Server_OnChatCommand → TrainingObjectManager
  → BroadcastRadioCommand → ApplyRadioCommandLocal

UI Prev/Next button (any peer)
  → RadioController.RequestTrackChange
  → TrainingSync.RequestRadioCommand (same as above)
```

### Expected log lines (additions)

**Client requesting skip:**

```
[FlamiePrac] Client requested radio command: /nextsong
```

**Server handling skip:**

```
[FlamiePrac] Server radio command: next
```

**3D audio enabled:**

```
[FlamiePrac] Radio using 3D audio at Speaker: (x, y, z)
```

### Mode matrix

| Mode | Radio |
|------|--------|
| Local host | Yes — after hive spawns (~2s) |
| Dedicated + remote client | Yes — on client visual copy |
| Dedicated headless only | No — no listener (expected) |

---

## QA checklist

Use this to confirm Phase 1 before MultiSheet integration.

### Server setup

- [ ] MultiSheet **not** loaded on server or clients
- [ ] **FlamieTraining enabled on server AND every client** (client runs radio + visuals locally)
- [ ] Deploy `dist/` contents: `MyMod.dll`, `trainingprefabs`, `training_layout.json`
- [ ] Optional: `RadioSongs/*.mp3` for radio

### Dedicated + remote client

- [ ] Start **dedicated** server (not listen/host-only test)
- [ ] Join from a **second machine** or second Steam account
- [ ] Client sees training hive at ~(0.8, 0, 21)
- [ ] Client sees two **neon green** pass-back boards at x = ±6, z ≈ 38.46 (touching goal line, not behind it)
- [ ] Puck collides with spinners / **MaxPractice AI goalie** (server physics)
- [ ] Pass-back boxes fire when puck enters trigger (server log: `[PuckPasser] Pass to …`)
- [ ] **Slidable beams** in hive move when pushed with stick (server log: `Configured N slidable beam(s)`)
- [ ] **R key** spawns test puck above stick blade (server log: test puck spawn)
- [ ] `/speed 100` changes spinner rate on both sides (visual)
- [ ] **`/prevsong` once** restarts current track; **double-tap within 0.4 s** goes to previous track in history
- [ ] **`/nextsong`** picks random track without repeating until all songs played
- [ ] **`/nextsong` from remote client** changes track (client log: `Client requested radio command`)
- [ ] Log shows `[FlamiePrac] Radio playing: ...`
- [ ] **3D audio:** loud near hive speaker, quieter far away
- [ ] **UI:** Prev/Next buttons and volume slider work on client

### Local host smoke test (optional)

- [ ] Top-right radio panel appears after spawn (~2s)
- [ ] `/nextsong` works without permission error
- [ ] UI buttons skip tracks; volume slider changes loudness
- [ ] Walk away from hive — music gets quieter

### Logs to verify

**Server (`Puck.log`):**

```
[FlamiePrac] TrainingSync bootstrap created.
[FlamiePrac] TrainingObjectManager attached on server.
[FlamiePrac] Starting training mode
[FlamiePrac] Spawned 'trainingprefab' (#1) at ...
[FlamiePrac] Configured 2 slidable beam(s) on 'trainingprefab'.
[FlamiePrac] MaxPractice AI goalie spawn team=Blue success=True
[FlamiePrac] Sent snapshot (N object(s)) to client ...
```

**Client (`Player.log`):**

```
[FlamiePrac] TrainingSync bootstrap created.
[FlamiePrac] Requested training snapshot from server.
[FlamiePrac] Applied snapshot with N object(s).
[FlamiePrac] Radio using 3D audio at Speaker: ...
[FlamiePrac] Radio playing: ...
[FlamiePrac] Client requested radio command: /nextsong   ← remote client skip
```

If server logs spawns but client never logs snapshot/spawn received → replication or firewall/message issue.

### Known Phase 1 limitations (not bugs)

- Single-rink coordinates only — MultiSheet offsets come in Phase 3.
- One global puck/player for **passer AI** target lookup — fine for one player on one rink; MaxPractice goalie is scoped to the training hive net only.
- Rotators/decoy stay phase-locked via NGO `ServerTime` (not wall-clock `Time.time`). Circular targets still use pose snapshots (random on hit).
- `PuckSpawner` (middle-mouse) is still an orphan debug script, not wired into the manager.
- **`training_layout.json` on deploy** overrides shipped defaults — update or delete after layout/pass-back changes.

---

## Architecture after fixes

```
MyMod.OnEnable
  └─ Load trainingprefabs bundle (all peers)
  └─ Create FlamiePrac_Bootstrap + TrainingSync (all peers)
  └─ Harmony patch: TrainingClientChat (client radio chat)

TrainingSync (waits for NetworkManager)
  ├─ Server: add TrainingObjectManager
  │     └─ Register Event_Server_OnChatCommand
  │     └─ StartTrainingMode → read training_layout.json
  │           └─ SpawnTrainingObject / SpawnOnePasser (authority + colliders)
  │                 └─ BroadcastSpawn → all clients
  │                 └─ SlidableObstacleSetup.ConfigureServer (all prefab beams)
  │                 └─ FlamiePracTrainingGoalie.SpawnForHive (MaxPractice AI)
  ├─ Server: OnClientConnected → SendSnapshotToClient
  ├─ Server: Event_Server_OnClientSceneSynchronizeComplete → SendSnapshotToClient
  ├─ Server: OnRadioRequest → BroadcastRadioCommand
  ├─ Server: OnTestPuckSpawnRequest → spawn puck above requester's stick
  ├─ Server: NetworkTickSystem.Tick → SlidableObstacleSync + TrainingMotionSync
  ├─ Everyone: Event_Everyone_OnLevelSpawned
  │     ├─ Server: EnsureTrainingRunningAfterLevelSpawn (respawn if hive wiped)
  │     └─ Pure client: ScheduleSnapshotRequest (rejoin / practice leave-return)
  ├─ Everyone: Event_Everyone_OnLevelDespawned → client ClearClientObjects
  └─ Client: RequestSync / snapshot → ApplyClientSpawn under FlamiePrac_ClientVisuals
        └─ SlidableObstacleSetup.ConfigureClientMirror + TrainingMotionSync visuals

Radio (per listening client)
  └─ RadioController on Speaker transform
        ├─ 3D AudioSource (MP3 from RadioSongs/)
        ├─ UITK HUD (♪ Radio chip + panel)
        ├─ Shuffle pool + play history for next/prev
        └─ RequestTrackChange → TrainingSync.RequestRadioCommand

Slidable beams / speakers
  └─ Server: dynamic Rigidbody, stick/body push; unparented; ice-seated world pose
  └─ Client: SlidableObstacleVisual follows FlamiePrac_Slidable **world** pose + velocity

Moving hive props (rotators / decoy)
  └─ Server + client: simulateLocally=true, driven by NetworkManager.ServerTime
  └─ FlamiePrac_Mover: params only for these (globalSpeed / decoy rates); no pose stream
  └─ Circular targets: server sim + TrainingMotionVisual pose follow (non-deterministic)

Client ownership roots (DDOL FlamiePrac_ClientVisuals)
  └─ Hive prefab root → parented as-is
  └─ Passers → parent PassBackAnchor_* (NOT the PassBackBox child)
  └─ Clear/despawn destroys ownership root

Test puck (QA)
  └─ R key → FlamiePrac_TestPuckSpawn → server spawns puck at stick blade

Dedicated server: authority objects, no renderers, no radio
Remote client:   visual copies + 3D radio at speaker + UI
Host:            authority + radio on authority hive (no duplicate mirror)
```

### Sync rate cheat sheet (blank dedicated, Jul 2026)

| Channel | When clients are present | Idle / notes |
|---------|--------------------------|--------------|
| `FlamiePrac_Slidable` | Every network tick while prop moving | Every ~20 ticks when settled |
| `FlamiePrac_Mover` params | Every ~15 network ticks + on `/speed` | Tiny unreliable packet |
| `FlamiePrac_Mover` poses | Every network tick **only while circular targets exist** | Rotators/decoy: zero pose traffic |
| Network tick on this VPS | **30 Hz** (`NetworkConfig.TickRate`) | Raise in `server_config.json` if you want puck-like 100 |

Log proof of tick lock: `[FlamiePrac] Slidable sync locked to NetworkTickSystem (30 Hz).`  
Log proof of clock-driven sticks: `[FlamiePrac] Rotators/movers use ServerTime clock sync (local sim both sides).`

---

## How we find Puck APIs (do not guess)

When fixing vendor code, check **PUBLIC MOD LIBRARY** (`../../PUBLIC MOD LIBRARY/` from this repo) before inventing patterns:

| Problem we hit | Reference mod | What we copied |
|----------------|---------------|----------------|
| Server chat commands | `CompetitivePuckTweaks-main` | `Event_Server_OnChatCommand`, message dict shape |
| Client chat intercept | `ToastersReskinLoader-main` | Harmony prefix on `ChatManagerController.Event_OnChatSubmitMessage` |
| Server mod + chat lifecycle | `BlackMirror-main` | `EventManager.AddEventListener` + **same delegate instance** for remove |
| Custom Messaging | MultiSheet / `_vendor/PuckLargeLevel` | Named channels, snapshot on join |
| Event name verification | `DALF MOD LIBRARY/libs/Puck.dll` | `rg -a "Event_.*Chat"` for exact strings |

Project rule for this repo: `.cursor/rules/puck-playground-reference.mdc`

---

## Mod enable / disable lifecycle (mod health)

A Puck plugin that only cleans up in theory will **leak** when toggled in the mod list, hot-reloaded, or disabled before quit. Ghost listeners and scene objects cause confusing bugs that look like “the mod is still running.”

### Why this matters

| Leak type | Symptom after disable |
|-----------|------------------------|
| Harmony patch left active | Client `/nextsong` still intercepted; conflicts with other mods |
| `Event_Server_OnChatCommand` listener | `/speed`, `/nextsong` still handled; duplicate replies |
| Custom Messaging handlers | Stale handlers on re-enable; duplicate spawns or radio skips |
| Spawned hive / passers in scene | Colliders and props remain with no mod to manage them |
| UITK radio chip on HUD | Bottom-left “♪ Radio” persists; clicks do nothing or crash |
| Legacy uGUI `RadioCanvas` / `EventSystem` | Blocks team/role UI even after “disabling” mod |
| `ConstantRotator.globalSpeed` | `/speed 999` persists globally after mod off |
| `Class1.Instance` / static singletons | Re-enable sees stale plugin state |

**Goal:** After `OnDisable`, the process should behave as if FlamieTraining was never loaded (until `OnEnable` runs again).

### Leaks that existed before the lifecycle pass

1. **`RemoveEventListener` with a new delegate** — `AddEventListener(..., _onChatCommand)` but remove used `new Action<>(OnChatCommand)` → listener **never unregistered** (BlackMirror stores the field; we copied that fix).
2. **No despawn on disable** — bootstrap destroyed but server/client **training objects stayed in the world**.
3. **`ClearClientObjects` not called on shutdown** — client visual mirrors orphaned.
4. **Harmony patched before bundle validation** — failed enable could leave chat patch active.
5. **`Class1.Instance` not cleared** — stale plugin reference after disable.
6. **Radio HUD / legacy canvas** — not torn down reliably; EventSystem from old uGUI could survive.
7. **`Invoke(StartTrainingMode, 2f)`** — could fire after disable without `CancelInvoke`.
8. **AssetBundle.Unload(false)** on disable while instances still referenced — now destroy objects first, then `Unload(true)`.

### What we implemented

**New: `FlamiePracLifecycle.Shutdown()`** — single entry called from `MyMod.OnDisable` **before** destroying bootstrap:

```
OnDisable
  └─ FlamiePracLifecycle.Shutdown()
        ├─ RadioHudUI.TearDown() + legacy canvas/EventSystem cleanup
        ├─ TrainingSync.PerformShutdown()
        │     ├─ StopAllCoroutines / cancel WaitForNetwork
        │     ├─ TrainingObjectManager.Shutdown() — despawn all, CancelInvoke, remove chat listener
        │     ├─ ClearClientObjects()
        │     └─ UnregisterHandlers() — all Custom Messaging channels
        ├─ ConstantRotator.globalSpeed reset to default (200)
        └─ Destroy orphan bootstrap + stop RadioController audio
  └─ harmony.UnpatchSelf() — TrainingClientChat only
  └─ Destroy bootstrap GameObject
  └─ Class1.Instance = null
  └─ AssetBundle.Unload(true)
```

**`TrainingObjectManager.Shutdown()`** — idempotent; stores `_onChatCommand` field; destroys every spawned authority object.

**`TrainingSync.PerformShutdown()`** — idempotent; stops coroutines; clears client dictionary; unregisters netcode handlers.

**`MyMod.OnEnable`** — Harmony patch runs **after** bundles/bootstrap succeed; `RollbackEnable()` unpatches + destroys bootstrap if anything fails.

### How to verify disable is clean

1. Enable mod, join server, confirm hive + radio chip appear.
2. Disable mod in Puck mod list (do not quit).
3. Confirm:
   - No training hive / passers in scene
   - No **♪ Radio** chip on HUD
   - `/nextsong` does **not** change music (no Harmony intercept)
   - Server log: `[FlamiePrac] Disabled — patches removed, scene and UI cleaned up`
4. Re-enable mod — fresh bootstrap, no duplicate hive from old spawns.

**Note:** Disabling mid-session is now supported for QA; production servers should still prefer enable-at-boot for simplicity.

---

## What comes next (Phase 2–3)

Not in this build — documented for context:

1. **Phase 2** — Author layouts with `/trainhere`, tune `training_layout.json`, no coordinate log ping-pong.
2. **Phase 3** — Merge into MultiSheet; offset spawns by each rink’s `WorldOrigin`; scope puck/player lookup per rink; unified Workshop deploy.

---

## File reference

| File | Role |
|------|------|
| `MyMod.cs` | Plugin entry; enable rollback; `OnDisable` full teardown |
| `FlamiePracLifecycle.cs` | Central `Shutdown()` orchestrator |
| `TrainingSync.cs` | Netcode custom messaging / replication + `PerformShutdown()` |
| `TrainingClientChat.cs` | Client-side Harmony chat hook for `/nextsong` / `/prevsong` |
| `TrainingObjectFactory.cs` | Server vs client object construction; attaches `RadioController` to Speaker |
| `TrainingObjectManager.cs` | Server spawn authority + server chat commands |
| `RadioController.cs` | 3D audio on Speaker only (no screen UI) |
| `RadioHudUI.cs` | Collapsible UITK panel on `UIManager.RootVisualElement` |
| `RadioHudDriver.cs` | Tick attach/refresh; tear down on mod disable |
| `TrainingLayoutConfig.cs` | JSON layout load/save; pass-back defaults + `PasserCenterZ()` |
| `PuckPasser.cs` | Pass-back bumper AI (intercept lead, feet/blade blend) |
| `FlamiePracMaxPracticeShim.cs` | MaxPractice static fields + fake-client cleanup |
| `FlamiePracGoaliePlacement.cs` | Training-net crease override (single net, Phase 1) |
| `FlamiePracTrainingGoalie.cs` | Spawn/despawn MaxPractice AI goalie for hive |
| `FlamiePracGoalieBootstrap.cs` | Waits for hive spawn, triggers goalie setup |
| `FlamiePracTestPuckSpawn.cs` | R-key QA puck spawn above stick |
| `SlidableObstacleSetup.cs` | Detect + configure all slidable prefab beams |
| `SlidableObstacle.cs` | Server physics for pushable beams + velocity write for sync |
| `SlidableObstacleSync.cs` | Beam/speaker transform replication on network tick |
| `TrainingMotionSync.cs` | Params + circular poses on `FlamiePrac_Mover`; rotators/decoy clock-lock |
| `ConstantRotator.cs` / `ConstantMover.cs` | Absolute pose from `ServerTime` (both sides `simulateLocally=true`) |
| `deploy-server.ps1` | Build + local + VPS deploy; `-RestartServer` single-process + log verify |
| `MaxPractice/*.cs` | Vendored real Player AI goalie stack |
| `CollisionHelper.cs` | Mesh colliders + headless visual strip; static layer 21 vs Ice slidables |
| `ChatPatch.cs` | **Excluded from build** — empty stub, do not enable |
| `training_layout.json` | Spawn definitions (user deploy) |
| `training_layout.example.json` | Shipped default layout |

---

## How we got moving items synced (dedicated server)

Plain `GameObject`s are **not** Netcode `NetworkObject`s. Spawning the hive on the server creates authoritative physics; clients only see what Custom Messaging rebuilds. Spawn/snapshot covers **existence**. Continuous motion needs a second layer — and the **right kind** of second layer depends on whether the motion is deterministic.

**Working end state (Jul 2026 QA):** rotating sticks + decoy defender stay visually locked to server hitboxes at a solid refresh feel; speakers/beam use tick-rate pose sync; passers are pose-locked static boards.

---

### Final design — two sync strategies

| Prop | Motion type | What syncs | Why |
|------|-------------|------------|-----|
| **Rotating sticks** (`ConstantRotator`) | Deterministic spin | **Nothing per frame** — both sides simulate from NGO `ServerTime` | Pose packets always lag a fast spin (e.g. 200°/s × RTT = visible offset) |
| **Decoy defender** (`ConstantMover`) | Deterministic sine path | Same — `Sin(ServerTime * speed) * distance` | Same phase on every peer |
| **`/speed` + decoy rates** | Shared constants | Tiny `FlamiePrac_Mover` **params** packet (~0.5 s + on change) | Clients must share `globalSpeed` or clock-lock diverges |
| **Circular targets** | Non-deterministic (random on hit) | Pose snapshots on `FlamiePrac_Mover` | Cannot reconstruct from a clock alone |
| **Speakers / push beam** | Physics-driven | `FlamiePrac_Slidable` pose + velocity on network tick | True rigidbody motion — must replicate |

```
ConstantRotator / ConstantMover (server + every client)
  restPose captured once at Awake/Start
  each FixedUpdate:
    t = NetworkManager.Singleton.ServerTime.Time   // NGO-synced, NOT Time.time
    rotator: localRot = rest * Euler(0, direction * globalSpeed * t, 0)   // mod 360° carefully
    mover:   position = start + right * Sin(t * globalSpeed) * globalDistance
    Physics.SyncTransforms()   // hitboxes follow the mesh

TrainingMotionSync
  MsgParams (byte 1): rotator/mover global rates → clients
  MsgPoses  (byte 2): circular targets only
```

Log lines that prove the happy path:

```
[FlamiePrac] Rotators/movers use ServerTime clock sync (local sim both sides).
[FlamiePrac] Slidable sync locked to NetworkTickSystem (30 Hz).
```

**Why `ServerTime` works where `Time.time` failed:** Unity’s `Time.time` is per-process (starts at load / differs after join). NGO `NetworkManager.ServerTime.Time` is the **same synchronized simulation clock** on dedicated server and remote clients, including late joiners. Absolute angle/offset from that clock + a rest pose captured from the prefab/spawn transform → identical phase without streaming transforms.

---

### Problem A — First attempt: local `Time.time` on both sides (broken)

| Piece | What went wrong |
|-------|-----------------|
| `ConstantRotator` | Incremental `Rotate(...)` / wall-clock on server **and** client — start times diverge |
| `ConstantMover` | `Sin(Time.time)` — phase mismatch |
| Symptom | Visual stick on one side of the path; server hitbox on the other → phantom knockdowns |

### Problem A1 — Second attempt: server pose stream, client follow (mostly worked, then didn’t)

We mirrored slidables: server simulates, client `simulateLocally = false`, Custom Messaging poses on `FlamiePrac_Mover`.

That **fixed phantom knockdowns** (visual roughly tracked authority) but had follow-on issues:

| Step | What we did | Result |
|------|-------------|--------|
| A1a | Pose snap at ~20 Hz | Correct-ish place, **steppy** look |
| A1b | Every network tick + lin/ang velocity extrapolation | Smoother, still **latency offset** vs hitboxes on a fast spin |
| A1c | Larger payload (paths + velocity) in a **non-growable** `FastBufferWriter` | Writes overflowed → broadcast caught/failed → **visuals froze** while server kept spinning |
| A1d | Growable writer + MTU-safe batches | Visuals moved again, but **not locked to collisions** (pose lag) |

Lesson: for **deterministic** spins, pose replication is the wrong primary tool. Use it for physics props and random movers; use a **shared clock** for formula motion.

### Problem A2 — Final fix: `ServerTime` clock lock (working)

1. `ConstantRotator` / `ConstantMover` always `simulateLocally = true` on server **and** clients.
2. Drive absolute pose from `NetworkManager.ServerTime.Time` (fallback `Time.timeAsDouble` only if Netcode isn’t up).
3. `TrainingMotionSync.RegisterFromRoot` no longer attaches `TrainingMotionVisual` to rotators/decoy — nothing overrides the local sim.
4. Broadcast **params** (`ConstantRotator.globalSpeed`, mover speed/distance) on an interval and immediately from `/speed` via `BroadcastParamsNow()`.
5. Keep pose batches **only** for `CircularMovingTarget`.

Client + server must run the **same** `Plugins/FlamiePrac` build (params message layout). Restart the client after deploy.

### Problem B — Speakers / pushable beam felt choppy on dedicated

| Piece | What went wrong |
|-------|-----------------|
| `SlidableObstacleSync` | Hard-coded **10 Hz** (`0.1f`) broadcast |
| `SlidableObstacleVisual` | Soft `Lerp(..., 0.4f)` trailed behind sparse packets |
| Local/host session | Physics runs in-process — no network lag, so it felt “fast and reactive” |

**Fix — match Netcode tick + velocity extrapolation (still the right model for rigidbodies):**

1. Subscribe server slidable ticks to `NetworkManager.NetworkTickSystem.Tick` (falls back to `Update` if unavailable).
2. While a slidable is moving (`IsActivelyMoving`), broadcast **every network tick**. Idle drops to every ~20 ticks (~5 Hz).
3. Packet payload: local pose **plus** linear/angular velocity (`WriteState`).
4. Client `SlidableObstacleVisual`: short SmoothDamp + brief extrapolation.
5. Use a **growable** `FastBufferWriter(..., maxSize)` so long `RelativePath` strings cannot silently kill the channel.

**Note:** This server’s `NetworkConfig.TickRate` logged as **30 Hz**. That is whatever `server_config.json` / Puck sets — we follow the tick system. Raising tick rate improves slidable/puck feel; rotators/decoy already update every `FixedUpdate` from the shared clock, so they stay smooth independently of mover pose rate.

### Problem C — Pass bumpers at center ice after snapshot rejoin work

Speakers looked correct; neon pass boards appeared at **center ice**; beam sometimes looked offset.

| Piece | What went wrong |
|-------|-----------------|
| Snapshot rejoin DDOL | `ApplyClientSpawn` parented the **passer GameObject** under `FlamiePrac_ClientVisuals` |
| Passer slidable sync | Server wrote poses **local to `PassBackAnchor_*`** (rest pose ≈ `0,0,0`) |
| After bad reparent | Child’s `localPosition = 0` under DDOL root at world origin → **center ice** |
| Speakers / beam | Still under hive hierarchy → hive-relative sync kept working |

**Fix (and current passer policy):**

1. `GetClientOwnershipRoot(obj)` — if parent is `PassBackAnchor_*`, parent **that** to DDOL (worldPositionStays).
2. `DestroyClientOwnedObject` / `ClearClientObjects` destroy the ownership root (anchor + child).
3. Passers are now **pose-locked** at spawn (no slide sync) — kinematic freeze + HitFace trigger on the front; body-push collider on the back half only (removes center-face dead zone).

### Related deploy pitfall (two Pucks on one port)

`-RestartServer` once left an old `/srv/puck-download/Puck` alive and started a second process → `Failed to bind` on 30609, log overwritten, clients saw “Missing mods” / invisible props while MaxPractice dummy goalie still appeared.

**Restart script now:** kill all Puck + `start_server.sh` wrappers, wait until UDP 30609 is free, truncate `Puck.log`, start **one** process, require exactly one Puck + `Adding plugin FlamiePrac` in the new log.

```powershell
.\deploy-server.ps1 -RestartServer
```

**Layout note:** if the log shows `training_layout.json empty — using built-in defaults`, the file on disk may be `{}` / invalid JSON array. Built-in defaults still spawn hive + two passers; fix by copying `training_layout.example.json` → `training_layout.json` on the server (deploy copies example only when the target file is missing).

---

## Client snapshot one-time pass — bug and fix

### Symptom

- First join (or first practice session): hive appears, sometimes after a short delay.
- Leave practice / rink and come back: **no hive visuals**, only MaxPractice dummy goalie.
- On dedicated: server still has colliders (you bump invisible stuff); client mesh mirrors are gone.

Dummy goalie is a real networked `Player` (MaxPractice). FlamiePrac hive/passers/speakers are **Custom Messaging mirrors only**.

### Root cause (one-time snapshot)

```
Client boot
  └─ WaitForNetwork once
        └─ RequestSnapshot()   ← ONLY HERE
              └─ ApplyClientSpawn → GameObjects parented in the rink scene

Leave practice / level unload
  └─ Unity destroys scene objects
  └─ clientObjects dictionary still “has” entries (or empty dead refs)
  └─ WaitForNetwork already finished → NO second RequestSnapshot

Rejoin
  └─ Empty ice (server physics still there)
```

Early `OnClientConnected` snapshot could also arrive **before** the client rink/scene was ready, so the first pack was easy to miss or lose on scene load.

### Fix (what we shipped)

| Trigger | Who | Action |
|---------|-----|--------|
| First network ready | Pure client | `ScheduleSnapshotRequest(0.25f)` (unchanged intent, slightly delayed) |
| `Event_Everyone_OnLevelSpawned` | Pure client | `ScheduleSnapshotRequest(0.35f)` — **rejoin / practice return** |
| `Event_Everyone_OnLevelDespawned` | Pure client | `ClearClientObjects()` |
| `Event_Server_OnClientSceneSynchronizeComplete` | Server | `SendSnapshotToClient(clientId)` — after client scene is ready |
| `OnClientConnected` | Server | Still sends snapshot (early path; scene-sync covers the reliable path) |
| Client `Update` safety | Pure client | Every ~2s, if no live visuals → `RequestSnapshot()` |
| Apply spawn | Client | Parent **ownership root** under DDOL `FlamiePrac_ClientVisuals` |
| Passer ownership | Client | Parent `PassBackAnchor_*`, never the `PassBackBox` child alone |
| Clear / despawn | Client | Destroy ownership root (anchor + passer, or hive root) |
| `Event_Everyone_OnLevelSpawned` | Server | `EnsureTrainingRunningAfterLevelSpawn()` — if hive was destroyed with the old level, reset `modEnabled` and AutoStart again |

### Why DDOL + wrong parent broke passers

```
Correct
  FlamiePrac_ClientVisuals (DDOL)
    └─ PassBackAnchor_2          world = goal-line spawn
          └─ PassBackBox_2       local ≈ (0,0,0)  ← FlamiePrac_Slidable writes here

Broken (first DDOL attempt)
  FlamiePrac_ClientVisuals (DDOL at origin)
    └─ PassBackBox_2             local (0,0,0) from sync → world center ice
  PassBackAnchor_2               orphaned in scene (or destroyed inconsistently)
```

### Client log lines that prove it worked

```
[FlamiePrac] Level spawned — requesting training snapshot.
[FlamiePrac] Requested training snapshot from server.
[FlamiePrac] Applied snapshot with N object(s).
```

Server side on join/rejoin:

```
[FlamiePrac] Client scene sync complete — sending snapshot to <id>
[FlamiePrac] Sent snapshot (N object(s)) to client <id>
```

If server logs spawns but client never logs `Applied snapshot` → wrong/missing client plugin folder (`FlamiePrac` vs old `FlamieTraining`), or connection rejected (`Missing mods`).

### QA checklist (dedicated + remote client)

- [ ] Server log: exactly one Puck process; `Adding plugin FlamiePrac`; `Starting training mode`; `Spawned 'trainingprefab'`
- [ ] Server log: `Slidable sync locked to NetworkTickSystem (... Hz)`
- [ ] Client join: `Applied snapshot with N object(s)` (hive + passers)
- [ ] Leave practice and rejoin: snapshot requested again; hive still visible
- [ ] Pass bumpers at goal line (not center ice)
- [ ] Speakers / beam push smoothly (tick-rate slidable)
- [ ] Server log: `Rotators/movers use ServerTime clock sync`
- [ ] Rotating sticks + decoy: smooth spin **and** knockovers line up with the mesh
- [ ] Client folder is `Plugins/FlamiePrac` (disable old `FlamieTraining`)

---

## Changelog (vendor fix sessions)

| Session | Focus |
|---------|--------|
| **Replication pass** | `TrainingSync`, `TrainingObjectFactory`, remove teleport, layout JSON, server→client spawns |
| **Radio pass 1** | Rename commands (`/nextsong`), host radio attach, MP3 URI, headless skip |
| **Radio pass 2** | UI buttons + EventSystem + volume slider; client chat via `TrainingClientChat` |
| **Radio pass 3** | 3D spatial audio at Speaker; `RadioRequest` channel; double-skip guard on host |
| **Radio pass 4** | Removed uGUI overlay (blocked team select); UITK collapsible HUD on `UIManager` |
| **Lifecycle pass** | Full `OnDisable` teardown — unpatch, despawn, unregister listeners, UI cleanup |
| **Goalie pass** | MaxPractice real Player AI goalie; hide decorative `GoalieModel`; single-net placement |
| **Pass-back pass 1** | Neon green 5 m boards at goal line; `PasserCenterZ()`; layout JSON defaults |
| **Pass-back pass 2** | `PuckPasser` intercept lead + random feet/blade blend; raycast shooter detection |
| **Radio pass 5** | Shuffle pool (no repeat until all played); single-prev restart; double-prev history |
| **Slidable pass 1** | Stick-pushable prefab beams + `FlamiePrac_Slidable` sync |
| **Slidable pass 2** | `FindAllSlidableBeams` — configure **every** matching beam, not just one |
| **QA pass** | R-key test puck spawn via `FlamiePrac_TestPuckSpawn` |
| **Motion sync pass** | `TrainingMotionSync` / `FlamiePrac_Mover` — server pose for rotators + decoy (fix phantom knockdowns) |
| **Slidable rate pass** | Tick-locked slidable sync + velocity extrapolation (replace 10 Hz chop) |
| **Snapshot rejoin pass** | LevelSpawned / scene-sync resnapshot; DDOL client visual root; dual-Puck restart guard |
| **Passer DDOL pass** | Parent `PassBackAnchor_*` under DDOL (fix center-ice bumpers after rejoin) |
| **Motion rate pass** | Movers leave 20 Hz timer → every network tick + ang/lin vel extrapolation |
| **Snapshot reliability pass** | Coalesce/defer snapshots until records exist; atomic parse-before-clear; client retry until live visuals; push-all after AutoStart |
| **Workshop join-enable pass** | Catch-up works when mod enables on server join (not only app startup); `Event_OnClientStarted` / `Stopped` reconnect |
| **Passer lock pass** | Pass-back boards frozen at spawn (no slide sync); body-push collider on back half; HitFace covers full front (center dead zone) |
| **Motion visual freeze pass** | Growable `FastBufferWriter` + MTU-safe batches for `FlamiePrac_Mover` (overflow after tick-rate/vel payload froze client visuals); local visual fallback if sync silent |
| **Motion phase lock pass** | Rotators/movers drive from NGO `ServerTime` on server + client (pose replication lagged spins vs hitboxes); `/speed` params broadcast; circular targets still pose-synced |
| **Slidable world-pose pass** | Beam/speakers sync **world** pose (hive-local left client mesh at prefab rest while server hitbox was ice-seated); kinematic props keep tick broadcast; rename JSON empty → BuiltInDefaults |

### Workshop enable modes (must both work)

Puck workshop mods are often **enabled when joining a server**, not when the client app starts. Local/dev installs (and some players) enable the mod at menu boot. **Steam Workshop deploy must work for both** — if we only listen for `LevelSpawned` after `OnEnable`, workshop clients miss the hive forever because that event already fired.

| Mode | When `IPuckPlugin.OnEnable` runs | Typical failure if we only wait on LevelSpawned |
|------|----------------------------------|--------------------------------------------------|
| **App-start enable** | Main menu / before connect | Usually OK — we are listening before the rink loads |
| **Workshop server-join enable** | Already in session (`ClientStarted` + `LevelSpawned` may have **already fired**) | Silent empty ice — no snapshot request, handlers never “catch up” |

#### What `TrainingSync` does now

| Mode | Bootstrap behavior |
|------|--------------------|
| **App-start** | `Start` → `WaitForNetwork` until `IsClient`/`IsServer` + `CustomMessagingManager` exist → `CatchUpAfterNetworkReady`. Disconnect: `Event_OnClientStopped` clears mirrors and drops client handlers. Reconnect: `Event_OnClientStarted` restarts `WaitForNetwork`. |
| **Workshop join-enable** | Same `WaitForNetwork`, but it succeeds **immediately** (session already up). `CatchUpAfterNetworkReady` registers handlers and **requests a snapshot without waiting for LevelSpawned**. |

#### `CatchUpAfterNetworkReady` (idempotent)

```
RegisterHandlers (server/client flags — no double-subscribe)
SlidableBoardCollision.Ensure()
if server:
  EnsureServerManager()
  EnsureTrainingRunningIfIceReady()   // only AutoStart if rink ice exists
  QueueSnapshotToAllClients()         // deferred if records empty
if pure client:
  BeginClientSnapshotWait()
  RequestSnapshot()                   // now
  ScheduleSnapshotRequest(0.5s)       // second chance after handlers settle
```

Log line:  
`[FlamiePrac] Network ready — … (catch-up: app-start or workshop join-enable)`

#### Catch-up entry points (any one can succeed)

1. `WaitForNetwork` → `CatchUpAfterNetworkReady` (first time session is up, including workshop mid-join)
2. `Event_OnClientStarted` → restarts network wait (app-start reconnect; also covers late enable if the event re-fires)
3. `Event_Everyone_OnLevelSpawned` → client snapshot request when we are already listening
4. `Event_Server_OnClientSceneSynchronizeComplete` → server queues snapshot for that client
5. Client retry loop until `live=True`

#### Server pitfall: do not AutoStart before ice

An early catch-up that called `StartTrainingMode` **before** rink ice existed spawned the hive into a void; `LevelSpawned` then wiped those objects and respawned (double hive / flaky clients).

**Fix:** `EnsureTrainingRunningIfIceReady()`:

- If live props exist → just `QueueSnapshotToAllClients`
- If ice **not** ready → log and return; `StartTrainingModeWhenReady` coroutine owns first spawn
- If ice ready and nothing live → restart AutoStart (level reload / workshop mid-session server enable)

Log: `[FlamiePrac] Catch-up: rink ice not ready yet — AutoStart coroutine will spawn.`

#### Handler lifecycle

| Flag | Purpose |
|------|---------|
| `serverHandlersRegistered` | Named messages + `OnClientConnected` + network tick — register once |
| `clientHandlersRegistered` | Spawn/Despawn/Snapshot/Radio + slidable/mover handlers — register once |

`Event_OnClientStopped` (pure client): unregister client channels, clear mirrors, reset `clientHandlersRegistered` so the **next** join rebinds cleanly.

### Intermittent “sometimes no hive on client” (snapshot reliability)

**Symptoms:** Close game / leave / rejoin — sometimes props appear, sometimes only dummy goalie. Server log still shows hive spawned. Worse under workshop join-enable (mod late).

**Causes we hit:**

1. Client `RequestSync` / `OnClientConnected` ran **before** AutoStart filled `spawnRecords` → server `SendSnapshotToClient` returned silently (empty).
2. Burst of 3–4 snapshot sends on join (connect + scene sync + client request) racing.
3. `OnSnapshotReceived` called `ClearClientObjects()` **before** finishing the read — a parse failure left the rink empty.
4. `OnLevelSpawned` used stale `isClient` (still false before `WaitForNetwork`) and skipped the request.
5. Workshop join-enable missed LevelSpawned entirely (see above).
6. Catch-up AutoStart before ice → spawn wiped on level load.

**Fixes:**

| Change | Behavior |
|--------|----------|
| `QueueSnapshotToClient` + 0.35s flush | Coalesce join bursts into one send |
| Empty records → re-queue | Deferred until hive exists (`Snapshot deferred…`) |
| `QueueSnapshotToAllClients` after AutoStart / level-with-live-props | Push to anyone who asked too early |
| Parse-all then clear then spawn | Failed reads no longer wipe a good hive |
| `clientAwaitingSnapshot` retry loop | Keeps requesting until `live=true` (up to ~24 tries) |
| Live `NetworkManager.IsClient` on LevelSpawned | Don’t miss rejoin when role flags lag |
| `CatchUpAfterNetworkReady` | Workshop join-enable + app-start share one path |
| `EnsureTrainingRunningIfIceReady` | No pre-ice AutoStart from catch-up |

**Client success log:** `Applied snapshot with N record(s), built=N, live=True.`  
**Retry log:** `Client visuals missing — snapshot retry #N`  
**Workshop/boot log:** `Network ready — … (catch-up: app-start or workshop join-enable)`

### QA — both enable modes

- [ ] **App-start:** enable FlamiePrac in menu → join dedicated → hive visible; leave server → rejoin → hive visible again
- [ ] **Workshop-style:** disable mod at menu → join server that requires/enables mod on connect → hive appears without restarting the game
- [ ] Client log shows `catch-up: app-start or workshop join-enable` and `live=True`
- [ ] Server log shows single AutoStart (not spawn → wipe → spawn again)
- [ ] Pass bumpers at goal line; movers/slidables sync smoothly

---

*Document version: Phase 1 QA — blank-dedicated replication, ServerTime-locked rotators/decoy, tick-locked slidables, reliable snapshot rejoin, workshop join-enable + app-start catch-up, locked passers, radio, MaxPractice goalie, deploy restart guard.*
