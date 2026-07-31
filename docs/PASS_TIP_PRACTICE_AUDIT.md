# Pass Service & Tip Practice Audit

Cross-checked against `Puck Assets/Data Dump/puck_arena_runtime_arena_manual.json`, `multirink_catalog_autoload_client.json`, and `_vendor/puckchasers/RinkGeometry.cs`. PuckModToolkit has no coordinate constants — the arena dump is the source of truth.

**Audit date:** 2026-07-30

---

## Coordinate Reference (Verified from Arena Dump)

| Axis | Arena dump | Code (`RinkGeometry` / drills) | Verdict |
|------|------------|--------------------------------|---------|
| **Ice half-width (X)** | ±22.5 | 22.5 | Match |
| **Ice half-length (Z)** | ±45.75 | 45.75 | Match |
| **Ice surface Y** | ~0.030 | `VanillaRinkCloner.IceSurfaceY = 0.03` | Match |
| **Practice puck Y** | — | `IceSurfaceY + 0.05` ≈ 0.08 | Match spawn height |
| **Blue goal root** | z = **+40.95** | — | Prefab pivot |
| **Goal frame center** | z = **±40.776** | — | Mesh anchor |
| **Gameplay net Z** | — | **40.2** (`NetZ`) | Goal-line constant (not prefab root) |
| **Blue line Z** | not in dump | **±15** | Code-only; used consistently |
| **Barrier half** | ~22.57 × 45.82 | 23.1 × 46.35 | Code slightly padded (probe margin) |

**Convention:** Blue net **+Z**, Red net **−Z**, origin at center ice. All drill coords are **rink-local** (`worldPos - slot.Origin`).

### Source files

| Role | Path |
|------|------|
| Primary arena dump | `Puck Assets/Data Dump/puck_arena_runtime_arena_manual.json` |
| Mesh/catalog dump | `Puck Assets/Data Dump/multirink_catalog_autoload_client.json` |
| PuckModToolkit | API index + dump README only — **no rink constants** |
| MultiSheet baseline | `_vendor/puckchasers/RinkGeometry.cs` |
| Pass service | `StretchPassPractice.cs` |
| Tip / save practice | `RinkPracticeDrills.cs`, `GoalieShotPhysics.cs` |

---

## Pass Service Audit (`StretchPassPractice.cs`)

### Intended Design — Matches Code

| Requirement | Implementation | Status |
|-------------|----------------|--------|
| Max 2 pucks on ice | `MaxPassPucksOnRink = 2`, `EnforceMaxPucksOnRink` | OK |
| Staggered loop (not same-tick spawn+launch) | `PassLoop`: spawn → settle → launch → delay → spawn other dot | OK |
| Stretch alternates ±15 dots | `useLeftDot` toggles after each pass | OK |
| Point alternates rim/point | `pointPassNextIsPoint` toggles | OK |
| Low cycle rotates 4 variants | `LowCyclePassSequence`: Indirect→Rim→Air→Hard | OK |
| Clear pucks on mode entry | `Apply()` + `RinkPracticeDrills.ClearLoosePucksOnRink` | OK |
| R-spawn capped | `PuckSpawnSync` → `OnPlayerPuckSpawned` → enforce cap | OK |
| Look puck: active then upcoming | `ResolveLookPuck` (flying) + `ResolveQueuedLookPuck` (holder) | OK |
| Look broadcast to clients | `GoaliePracticeLookTarget` + `GoalieTrackPuckPatch` | OK |

### Spawn Positions (Rink-Local)

| Mode | Spawn logic | Z range (local) | Notes |
|------|-------------|-----------------|-------|
| **Stretch** | `(±15, endSign × 22)` | ±22 | `BlueLineZ + 7`; alternates X |
| **Point (wall)** | `NetZ+1.2 … HalfLength` on skater's wall | ~41–45 | End zone, board lane |
| **Point (rim)** | slightly more inboard + jitter | ~41–45 | Hard rim speeds 58–76 |
| **Low cycle** | `BlueLineZ+4 … +10` on wall | 19–25 | Blue-line depth |

Board X uses `RinkGeometry.BoardHalfWidthAtZ()` — matches dump barrier geometry formula.

### Pass Loop State Machine

```
spawn holder (kinematic)
  → settle (~0.65s)
  → launch pass (flying)
  → holder spawn delay (1.25–1.75s)
  → spawn holder on other dot/wall
  → pass gap (2–4s)
  → repeat
```

Never more than **2 pucks**: one flying + one queued (or one of each after cap enforcement).

### Bugs Found & Fixed (Audit)

1. **Stretch dots always z = −22** — ignored which end zone the skater was in. Point/low cycle already used `EndSign`; stretch did not.  
   **Fixed:** `z = sides.EndSign × 22` (±22 by end zone).

2. **Stale skater position at launch** — `playerLocal` captured at loop start, not refreshed before `LaunchPassPuck`.  
   **Fixed:** re-read blade position + velocity immediately before launch.

### Known Limitations (Not Bugs)

- **z = ±22 is not a painted faceoff dot** — dump has no faceoff transforms. NHL end-zone dots would be ~**±34** (6.1 m inside goal line). ±22 is the SSPT “blue line + 7” slot, same as the non-stretch fallback formula.
- **Join cap = 1 player** on pass rinks (`GetJoinCapacity`) — separate from the 2-puck cap.
- **`IceCornerRadius = 7.5`** in clamp vs `RinkGeometry.CornerRadius = 12.75` — spawn clamp is tighter than board formula; intentional to keep pucks in playable ice.

---

## Tip Practice Audit (`RinkPracticeDrills.cs` + `GoalieShotPhysics.cs`)

### Intended Design — Mostly Matches

| Requirement | Implementation | Status |
|-------------|----------------|--------|
| Max 2 pucks (1 held + 1 flying) | `TipMaxTotalPucks = 2`, `TipHoldQueueDepth = 2` | OK |
| Staggered queue + fire | `TipPracticeLoop` with `FireAt` / `TipArrivalAt` | OK |
| Retarget at fire | `FireTipShot` → `RetargetTipShot` + rebuild velocity | OK |
| Look puck on active/upcoming | Same publish path as pass; tip-specific threat filter | OK |
| Arrival time from actual fire | `TrackTipPuck(Time.time + travelTime)` | OK |
| OnNet/HighLooper targets tipper plane | No longer aim at net Z during sim | OK |

### Feed Geometry (Relative, Not Fixed Dots)

Tip spawns are **dynamic** — 12–50 m behind tipper along `awayFromNet`:

```
spawn = tipperPos + awayFromNet × backDistance
awayFromNet = toward the end the target net is in
```

Sim crosses **tipper's Z plane** (`tipPlaneZ`), not the goal plane — correct for tippable feeds.

### Tip Feed Mix

| Feed kind | Weight | Style | Target |
|-----------|--------|-------|--------|
| LongStraight | 24% | Direct/SoftLift | Stick/chest |
| AtTipper | 16% | Direct/SoftLift | Stick/chest |
| WideTipper | 14% | Direct/SoftLift | Wide stick |
| HighLooperTipper | 16% | HighArc | Elevated tipper |
| HighLooperNet | 15% | SoftLift (capped arc) | Tipper + allowNet |
| OnNet | 15% | Direct/SoftLift | Tipper + allowNet |

### Bugs Found & Fixed (Audit)

**Tip practice always targeted Blue net** (`PlayerTeam.Blue`) regardless of tipper team.

- Blue tipper should attack **Red net** (−Z)
- Red tipper should attack **Blue net** (+Z)

**Fixed:** `ResolveTipAttackNetTeam(tipper)` — Red→Blue net, Blue→Red net.

### Earlier Fixes (Same Session)

- Skater look puck on tip practice (was goalie-only path)
- OnNet/HighLooper sim targets tipper plane, not net coordinates
- Rainbow/high arc capped to avoid sailing over crossbar
- `TrackTipPuck` uses `Time.time + travelTime` at fire (not scheduled `FireAt`)

---

## Look Puck Chain (Pass + Tip)

```
PassLoop/TipLoop → RefreshLook()
  → ResolveLookPuck (flying/active)
  → ResolveQueuedLookPuck (upcoming holder)
  → GoaliePracticeLookTarget.Publish (server, waits for NetworkObjectId)
  → GoalieTrackPuckPatch → GoalieThreatPuckSelector.TryGetPracticeTrackPuck
```

Pass/tip modes route **all skater roles** through practice look (not goalie-only). Pass feeds skip the “behind player” filter.

---

## Code Changes from This Audit

| File | Change |
|------|--------|
| `RinkPracticeDrills.cs` | Tip net targets tipper's attacking end |
| `StretchPassPractice.cs` | Stretch dots mirror Z by end zone; refresh skater pos before launch |

---

## Optional Follow-Ups (Not Changed)

1. **True faceoff dots (~±34 Z)** if painted-dot accuracy is preferred over SSPT ±22 slot.
2. **F10 faceoff dump** via PuckModToolkit in-game to confirm dot positions if Puck adds scene markers later.
3. **Low cycle `ToggleVariantAfterPass`** — if a launched variant isn't in the sequence (shouldn't happen), index won't advance; low risk.
4. **Debug chat command** (`/passdebug`) to print current look puck, queued puck, and local spawn coords for in-game verification.

---

## Net Z Layering Reference

Use the right constant for the job:

| Use case | Z value | Source |
|----------|---------|--------|
| Gameplay pathing / net footprint | 40.2 | `RinkGeometry.NetZ` |
| Crease / goalie AI (MaxPractice) | 40.23 | `TrainingLayoutConfig` |
| Goal prefab root transform | 40.95 | Arena dump `Goal Blue` |
| Goal frame mesh center | 40.776 | Arena dump |
