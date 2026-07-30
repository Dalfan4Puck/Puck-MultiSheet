#!/bin/bash
# Safe single-instance restart for /srv/puck-download PuckServer.service.
# Used by watch_workshop_mods.py and manual deploy scripts.
set -euo pipefail

INSTALL="${PUCK_SERVER_ROOT:-/srv/puck-download}"
PORT="${PUCK_SERVER_PORT:-30609}"

cd "${INSTALL}"

systemctl stop PuckServer.service || true

while read -r pid; do
  [ -z "${pid}" ] && continue
  exe=$(readlink -f "/proc/${pid}/exe" 2>/dev/null || true)
  cmd=$(tr '\0' ' ' < "/proc/${pid}/cmdline" 2>/dev/null || true)
  if [ "${exe}" = "${INSTALL}/Puck" ] || echo "${cmd}" | grep -q "${INSTALL}/start_server.sh"; then
    kill -9 "${pid}" 2>/dev/null || true
  fi
done < <(ps -eo pid= 2>/dev/null || true)

fuser -k "${PORT}/udp" "${PORT}/tcp" 2>/dev/null || true

for _ in $(seq 1 40); do
  if ! pgrep -f "${INSTALL}/Puck" >/dev/null 2>&1 && ! ss -ulpn 2>/dev/null | grep -q ":${PORT}"; then
    break
  fi
  sleep 0.5
done
sleep 2

if [ -f "${INSTALL}/Logs/Puck.log" ]; then
  : > "${INSTALL}/Logs/Puck.log"
fi

systemctl start PuckServer.service
sleep 12

if ! systemctl is-active --quiet PuckServer.service; then
  echo "ERROR: PuckServer.service not active" >&2
  systemctl status PuckServer.service --no-pager || true
  exit 1
fi

puck_count=$(pgrep -c -f "${INSTALL}/Puck" 2>/dev/null || echo 0)
if [ "${puck_count}" != "1" ]; then
  echo "ERROR: expected 1 Puck process, found ${puck_count}" >&2
  exit 1
fi

if ! ss -ulpn 2>/dev/null | grep -q ":${PORT}"; then
  echo "ERROR: not listening on ${PORT}" >&2
  exit 1
fi

echo "Restart OK (single Puck instance on ${PORT})."
