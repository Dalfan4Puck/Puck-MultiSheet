#!/bin/bash
# Install workshop update monitor on the Puck VPS (run as root on the server).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_ROOT="${PUCK_SERVER_ROOT:-/srv/puck-download}"
TARGET_DIR="${INSTALL_ROOT}/tools/puck_server"
ENV_FILE="/etc/puck/workshop-monitor.env"
STATE_DIR="/var/lib/puck-workshop-monitor"

echo "==> Installing workshop monitor to ${TARGET_DIR}"
mkdir -p "${TARGET_DIR}" "${TARGET_DIR}/systemd" "${STATE_DIR}" /etc/puck

install_file() {
  local src="$1"
  local dst="$2"
  local mode="$3"
  if [ "$(readlink -f "${src}")" = "$(readlink -f "${dst}")" ]; then
    chmod "${mode}" "${dst}"
    return
  fi
  install -m "${mode}" "${src}" "${dst}"
}

install_file "${SCRIPT_DIR}/watch_workshop_mods.py" "${TARGET_DIR}/watch_workshop_mods.py" 755
install_file "${SCRIPT_DIR}/restart_puck_server.sh" "${TARGET_DIR}/restart_puck_server.sh" 755

if [ ! -f "${ENV_FILE}" ]; then
  install -m 600 "${SCRIPT_DIR}/workshop-monitor.env.example" "${ENV_FILE}"
  echo "Created ${ENV_FILE} — review before relying on auto-restart."
else
  echo "Keeping existing ${ENV_FILE}"
fi

install -m 644 "${SCRIPT_DIR}/systemd/puck-workshop-monitor.service" /etc/systemd/system/puck-workshop-monitor.service
install -m 644 "${SCRIPT_DIR}/systemd/puck-workshop-monitor.timer" /etc/systemd/system/puck-workshop-monitor.timer

systemctl daemon-reload
systemctl enable --now puck-workshop-monitor.timer

echo "==> Timer status"
systemctl status puck-workshop-monitor.timer --no-pager || true

echo ""
echo "Adaptive polling: 1m15s fast for 3h after each applied update, then 10min slow."
echo "First poll stores baselines (no restart). Test manually:"
echo "  python3 ${TARGET_DIR}/watch_workshop_mods.py"
echo "  tail -f /var/log/puck-workshop-monitor.log"
echo ""
echo "Dry run:"
echo "  DRY_RUN=1 python3 ${TARGET_DIR}/watch_workshop_mods.py"
