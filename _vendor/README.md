# Vendored third-party code (compile-time only)

| Path | Purpose |
|---|---|
| `_vendor/PuckLargeLevel/` | [Jake-Porter/PuckLargeLevel](https://github.com/Jake-Porter/PuckLargeLevel) — chunk sync + custom level loading |

Refresh from upstream:

```powershell
.\setup-vendor.ps1
```

PHLPracticeModPack integrates this via `CustomLevelBridge.cs` (we do **not** compile upstream
`CustomLevelPlugin.cs` — that entry point is replaced by our plugin).
