# Practice radio (standalone Flamie / MyMod)

Clients stream practice-rink radio from phlstats. **No AWS keys in the DLL.** Signed GET URLs come from the API; audio never ships with the mod package.

## API

Base URL: `https://phlstats.com/radio/api`

| Method | Path | Response |
|---|---|---|
| GET | `/playlist` | `{ "tracks": [ { "id", "title" } ] }` — **no URLs** |
| GET | `/track?id=` | `{ "id", "title", "url", "expiresIn" }` |

Audio is **API/S3 only** — no `RadioSongs\` MP3 pack.

Optional `config/radio_client.json` next to `MyMod.dll` (or under the game cwd `config/`):

```json
{
  "ApiBase": "https://phlstats.com/radio/api"
}
```

## Sync

Server owns track id + `ServerTime` start; clients seek to the shared clock. Skip is majority vote; Restart resets for everyone; personal pause is local.
