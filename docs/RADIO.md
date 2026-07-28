# Practice radio (mod client)

Clients stream practice-rink radio from phlstats. **No AWS keys in the DLL.** Signed GET URLs come from the API; audio never ships in Workshop.

## Sync model

The **server owns the radio clock** (dedicated included — no audio on dedicated, state only):

| Field | Meaning |
|---|---|
| `trackId` | Shared phlstats id for everyone |
| `trackStartServerTime` | `NetworkManager.ServerTime.Time` when the track started |
| skip votes | Majority of connected clients |

Clients each call `/track?id=` and seek to:

`seek = ServerTime.Time - trackStartServerTime`

Late joiners get a snapshot on connect and hear the current song mid-track. Drift is corrected ~1 Hz if off by &gt;0.35s.

### Skip vote

HUD **Skip** casts a vote. Need `⌊n/2⌋ + 1` of connected clients (2/2, 2/3, 3/4, …). **Restart** resets the current track to t=0 for everyone. **On/Off** tunes streaming in/out (server clock keeps running; tune-in seeks to live position). **Volume** mutes locally while staying tuned in.

### Messaging

Existing channels `FlamiePrac_Radio` / `FlamiePrac_RadioRequest` carry state snapshots.

## API contract

Base URL: `https://phlstats.com/radio/api`  
Auth: none

| Method | Path | Response |
|---|---|---|
| GET | `/playlist` | `{ "tracks": [ { "id", "title" } ] }` — **no URLs** |
| GET | `/track?id=` | `{ "id", "title", "url", "expiresIn" }` |
| GET | `/health` | optional `{ "ok", "bucketConfigured", "trackCount" }` |

Audio is **API/S3 only** — no `RadioSongs\` MP3 pack.

Optional `config/radio_playlist.json` is **id/title metadata only** (dedicated sync if `/playlist` is unreachable). Clients still need `/track?id=` for signed audio URLs.

## Client config

Optional `config/radio_client.json`:

```json
{
  "ApiBase": "https://phlstats.com/radio/api"
}
```

## Workshop package

`tools/assemble-workshop-dist.ps1` does **not** ship MP3s; it removes any stale `dist/RadioSongs`.
