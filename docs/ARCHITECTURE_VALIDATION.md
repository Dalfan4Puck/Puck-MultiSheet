# MultiSheet architecture validation matrix

Run after role-split patch install (Phase 2+) on a 6-rink practice server. Use NetworkPerf client scopes when available.

## Sessions

| ID | Config | Measure | Pass |
|----|--------|---------|------|
| A | MultiSheet off | Baseline FPS | Reference only |
| B | All kill switches false | Feature parity, patch count in enable log | Client patches install; dedicated log shows `role=dedicated` with no client UI |
| C | `"skipChunkClient": true` | FPS delta vs B | Chunk decode isolated |
| D | `"skipArenaLighting": true` | FPS delta vs B | Lighting isolated |
| E | `"skipClientBuild": true` | Join hitch vs B | Clone build isolated |
| F | `"skipMinimap": true` | Minimap prefix ms/s vs B | Minimap cost isolated |
| G | Dedicated server log | Enable line | No minimap/TRL/scoreboard install messages |

## NetworkPerf targets

- `PHLPracticeModPack.CloneVisualProxy.LateUpdate`
- `UnityEngine.Graphics.DrawMesh`
- Minimap prefix (if wrapped)

## Dedicated server acceptance

Enable log must include:

```
[PHLPractice] Enabled role=dedicated patches=N
[PHLPractice] Patch install: role=dedicated dedicated=True count=...
```

Enable log must **not** include:

- `Minimap: rink-local view patch installed`
- `TRL compatibility: hooked`
- Scoreboard tab install on dedicated
