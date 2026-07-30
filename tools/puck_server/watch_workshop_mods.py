#!/usr/bin/env python3
"""
Poll Steam Workshop for updates to mods listed in the Puck server config.
When a watched mod's time_updated advances, download it via steamcmd and restart
PuckServer.service.

Adaptive polling:
  - Fast: every FAST_POLL_SEC (default 75s / 1m15s) while a workshop update was applied recently
  - Slow: every SLOW_POLL_SEC (default 10 min) after SLOW_AFTER_SEC (default 3 h) idle

The systemd timer fires every FAST_POLL_SEC; this script self-throttles in slow mode.

Install: tools/puck_server/install_workshop_monitor.sh

Environment (optional file: /etc/puck/workshop-monitor.env):
  PUCK_SERVER_ROOT=/srv/puck-download
  PUCK_SERVER_CONFIG=/srv/puck-download/server_config.json
  WORKSHOP_CONTENT_APP_ID=2994020
  WORKSHOP_DOWNLOAD_APP_ID=3481440
  STEAMCMD=steamcmd
  STEAM_WEB_API_KEY=          # optional; public items work without a key
  STATE_FILE=/var/lib/puck-workshop-monitor/state.json
  LOG_FILE=/var/log/puck-workshop-monitor.log
  FAST_POLL_SEC=75
  SLOW_POLL_SEC=600
  SLOW_AFTER_SEC=10800
  DRY_RUN=0                   # set 1 to log only, no download/restart
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

DEFAULT_SERVER_ROOT = Path("/srv/puck-download")
DEFAULT_CONFIG = DEFAULT_SERVER_ROOT / "server_config.json"
DEFAULT_STATE = Path("/var/lib/puck-workshop-monitor/state.json")
DEFAULT_LOG = Path("/var/log/puck-workshop-monitor.log")
STEAM_DETAILS_URL = (
    "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/"
)


def log(msg: str, log_path: Path) -> None:
    line = f"[{datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')}Z] {msg}"
    print(line, flush=True)
    try:
        log_path.parent.mkdir(parents=True, exist_ok=True)
        with log_path.open("a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError:
        pass


def load_env_file(path: Path) -> None:
    if not path.is_file():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        os.environ.setdefault(key, value)


def env_int(name: str, default: int) -> int:
    raw = os.environ.get(name, str(default)).strip()
    try:
        return max(1, int(raw))
    except ValueError:
        return default


def last_activity_ts(state: dict[str, Any]) -> int:
    applied = state.get("last_update_applied_at")
    if applied is not None:
        try:
            return int(applied)
        except (TypeError, ValueError):
            pass

    initialized = state.get("initialized_at")
    if initialized is not None:
        try:
            return int(initialized)
        except (TypeError, ValueError):
            pass

    checked: list[int] = []
    mods = state.get("mods") or {}
    if isinstance(mods, dict):
        for entry in mods.values():
            if not isinstance(entry, dict):
                continue
            try:
                checked.append(int(entry.get("checked_at") or 0))
            except (TypeError, ValueError):
                continue
    if checked:
        return max(checked)

    return int(time.time())


def poll_schedule(state: dict[str, Any]) -> tuple[int, str, int]:
    """Return (interval_sec, mode_label, idle_sec_since_last_update)."""
    slow_after = env_int("SLOW_AFTER_SEC", 3 * 3600)
    fast_poll = env_int("FAST_POLL_SEC", 75)
    slow_poll = env_int("SLOW_POLL_SEC", 600)

    now = int(time.time())
    last_activity = last_activity_ts(state)
    idle = max(0, now - last_activity)

    if idle >= slow_after:
        return slow_poll, "slow", idle
    return fast_poll, "fast", idle


def should_poll_now(state: dict[str, Any], interval_sec: int) -> bool:
    try:
        last_poll = int(state.get("last_poll_at") or 0)
    except (TypeError, ValueError):
        last_poll = 0
    return int(time.time()) - last_poll >= interval_sec


def read_enabled_mod_ids(config_path: Path) -> list[str]:
    if not config_path.is_file():
        raise FileNotFoundError(f"Server config not found: {config_path}")

    cfg = json.loads(config_path.read_text(encoding="utf-8"))
    mods = cfg.get("mods") or []
    ids: list[str] = []
    seen: set[str] = set()

    for entry in mods:
        if not isinstance(entry, dict):
            continue
        enabled = entry.get("isEnabled", entry.get("enabled", True))
        if enabled is False:
            continue
        mod_id = entry.get("id")
        if mod_id is None:
            continue
        sid = str(mod_id).strip()
        if not sid or sid in seen:
            continue
        seen.add(sid)
        ids.append(sid)

    return ids


def fetch_workshop_details(
    mod_ids: list[str], api_key: str | None
) -> dict[str, dict[str, Any]]:
    if not mod_ids:
        return {}

    payload: dict[str, str] = {"itemcount": str(len(mod_ids))}
    for i, mod_id in enumerate(mod_ids):
        payload[f"publishedfileids[{i}]"] = mod_id
    if api_key:
        payload["key"] = api_key

    body = urllib.parse.urlencode(payload).encode("utf-8")
    req = urllib.request.Request(
        STEAM_DETAILS_URL,
        data=body,
        method="POST",
        headers={"Content-Type": "application/x-www-form-urlencoded"},
    )

    with urllib.request.urlopen(req, timeout=45) as resp:
        data = json.loads(resp.read().decode("utf-8"))

    out: dict[str, dict[str, Any]] = {}
    details = (
        data.get("response", {}).get("publishedfiledetails")
        or data.get("publishedfiledetails")
        or []
    )
    if not isinstance(details, list):
        return out

    for item in details:
        if not isinstance(item, dict):
            continue
        if item.get("result") not in (None, 1):
            continue
        mod_id = str(item.get("publishedfileid", "")).strip()
        if not mod_id:
            continue
        out[mod_id] = item
    return out


def folder_fingerprint(workshop_dir: Path) -> str | None:
    if not workshop_dir.is_dir():
        return None
    total_size = 0
    newest = 0.0
    file_count = 0
    for path in workshop_dir.rglob("*"):
        if not path.is_file():
            continue
        try:
            st = path.stat()
        except OSError:
            continue
        total_size += st.st_size
        newest = max(newest, st.st_mtime)
        file_count += 1
    if file_count == 0:
        return None
    return f"{file_count}:{total_size}:{int(newest)}"


def load_state(path: Path) -> dict[str, Any]:
    if not path.is_file():
        return {"mods": {}, "initialized": False}
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return {"mods": {}, "initialized": False}
    if "mods" not in raw:
        raw["mods"] = {}
    return raw


def save_state(path: Path, state: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(state, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    tmp.replace(path)


def download_workshop_item(
    steamcmd: str,
    install_dir: Path,
    app_id: str,
    mod_id: str,
    log_path: Path,
) -> None:
    cmd = [
        steamcmd,
        "+@sSteamCmdForcePlatformType",
        "linux",
        "+force_install_dir",
        str(install_dir),
        "+login",
        "anonymous",
        "+workshop_download_item",
        app_id,
        mod_id,
        "+quit",
    ]
    log(f"steamcmd download app={app_id} item={mod_id}", log_path)
    subprocess.run(cmd, check=True, timeout=600)


def restart_puck_server(restart_script: Path, log_path: Path) -> None:
    if restart_script.is_file():
        log(f"restart via {restart_script}", log_path)
        subprocess.run(["/bin/bash", str(restart_script)], check=True, timeout=180)
        return
    log("restart via systemctl restart PuckServer.service", log_path)
    subprocess.run(["systemctl", "restart", "PuckServer.service"], check=True)


def main() -> int:
    load_env_file(Path("/etc/puck/workshop-monitor.env"))

    server_root = Path(os.environ.get("PUCK_SERVER_ROOT", str(DEFAULT_SERVER_ROOT)))
    config_path = Path(
        os.environ.get("PUCK_SERVER_CONFIG", str(DEFAULT_CONFIG))
    )
    content_app_id = os.environ.get("WORKSHOP_CONTENT_APP_ID", "2994020")
    download_app_id = os.environ.get("WORKSHOP_DOWNLOAD_APP_ID", "3481440")
    steamcmd = os.environ.get("STEAMCMD", "steamcmd")
    api_key = os.environ.get("STEAM_WEB_API_KEY") or None
    state_path = Path(os.environ.get("STATE_FILE", str(DEFAULT_STATE)))
    log_path = Path(os.environ.get("LOG_FILE", str(DEFAULT_LOG)))
    dry_run = os.environ.get("DRY_RUN", "0").strip() in ("1", "true", "yes")
    restart_script = Path(
        os.environ.get(
            "PUCK_RESTART_SCRIPT",
            str(Path(__file__).resolve().parent / "restart_puck_server.sh"),
        )
    )

    state = load_state(state_path)
    interval_sec, poll_mode, idle_sec = poll_schedule(state)

    if state.get("initialized") and not should_poll_now(state, interval_sec):
        return 0

    prev_mode = state.get("poll_mode")
    state["last_poll_at"] = int(time.time())
    state["poll_mode"] = poll_mode

    if prev_mode != poll_mode:
        log(
            f"Poll mode -> {poll_mode} (every {interval_sec}s; "
            f"{idle_sec // 60} min since last workshop update)",
            log_path,
        )

    mod_ids = read_enabled_mod_ids(config_path)
    if not mod_ids:
        log(f"No enabled workshop mods in {config_path}", log_path)
        save_state(state_path, state)
        return 0

    log(
        f"Watching {len(mod_ids)} mod(s) [{poll_mode}, {interval_sec}s]: "
        + ", ".join(mod_ids),
        log_path,
    )

    try:
        details = fetch_workshop_details(mod_ids, api_key)
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        log(f"Steam API error: {exc}", log_path)
        save_state(state_path, state)
        return 1

    changed: list[str] = []
    workshop_root = (
        server_root / "steamapps" / "workshop" / "content" / content_app_id
    )

    for mod_id in mod_ids:
        item = details.get(mod_id)
        prev = state["mods"].get(mod_id, {})
        title = (item or {}).get("title") or prev.get("title") or mod_id

        time_updated = None
        if item is not None:
            try:
                time_updated = int(item.get("time_updated") or 0)
            except (TypeError, ValueError):
                time_updated = 0

        fingerprint = folder_fingerprint(workshop_root / mod_id)

        record = {
            "title": title,
            "time_updated": time_updated,
            "fingerprint": fingerprint,
            "checked_at": int(time.time()),
        }

        if not state.get("initialized"):
            state["mods"][mod_id] = record
            log(f"init baseline: {mod_id} ({title}) time_updated={time_updated}", log_path)
            continue

        prev_updated = prev.get("time_updated")
        prev_fp = prev.get("fingerprint")

        updated = (
            time_updated is not None
            and prev_updated is not None
            and time_updated > int(prev_updated)
        )
        fp_changed = (
            fingerprint is not None
            and prev_fp is not None
            and fingerprint != prev_fp
        )

        if updated or fp_changed:
            reason = "steam time_updated" if updated else "local fingerprint"
            log(f"UPDATE detected for {mod_id} ({title}) via {reason}", log_path)
            changed.append(mod_id)

        state["mods"][mod_id] = record

    if not state.get("initialized"):
        now = int(time.time())
        state["initialized"] = True
        state["initialized_at"] = now
        state["last_update_applied_at"] = now
        save_state(state_path, state)
        log("First run complete — baselines stored, no restart.", log_path)
        return 0

    save_state(state_path, state)

    if not changed:
        log("No workshop updates.", log_path)
        return 0

    log(f"Changed mod(s): {', '.join(changed)}", log_path)

    if dry_run:
        log("DRY_RUN=1 — skipping download and restart.", log_path)
        return 0

    for mod_id in changed:
        try:
            download_workshop_item(
                steamcmd, server_root, download_app_id, mod_id, log_path
            )
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, OSError) as exc:
            log(f"Download failed for {mod_id}: {exc}", log_path)
            return 1

    try:
        restart_puck_server(restart_script, log_path)
    except subprocess.CalledProcessError as exc:
        log(f"Restart failed: {exc}", log_path)
        return 1

    now = int(time.time())
    state["last_update_applied_at"] = now
    state["poll_mode"] = "fast"
    save_state(state_path, state)
    log(
        "Workshop update applied and PuckServer restarted — fast polling for "
        f"{env_int('SLOW_AFTER_SEC', 3 * 3600) // 3600}h.",
        log_path,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
