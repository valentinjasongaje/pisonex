#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
#  Pisonex Orange Pi installer — sets up the SAME layout as the production Pi:
#
#    /opt/pisonex/                  git clone of the pisonex repo
#    /opt/pisonex/venv/             shared Python virtualenv
#    /opt/pisonex/server-orangepi/  server code, .env, pisonet.db, backups/
#    pisonex.service                systemd unit (runs main.py as root)
#
#  Usage (on the Pi, as root or with sudo):
#    sudo git clone <repo-url> /opt/pisonex
#    sudo bash /opt/pisonex/server-orangepi/deploy/install.sh
#
#  Safe to re-run: it updates packages, the venv and the service, and never
#  touches an existing .env or pisonet.db.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

INSTALL_ROOT="/opt/pisonex"
SERVER_DIR="$INSTALL_ROOT/server-orangepi"
VENV="$INSTALL_ROOT/venv"
SERVICE="pisonex"
UNIT_DST="/etc/systemd/system/$SERVICE.service"

say() { printf '\n\033[1m[%s] %s\033[0m\n' "$1" "$2"; }
die() { printf '\n\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "Run as root:  sudo bash $0"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ "$HERE" = "$SERVER_DIR" ] || die "The repo must be cloned to $INSTALL_ROOT (this script is in $HERE).
       sudo git clone <repo-url> $INSTALL_ROOT"
[ -f "$SERVER_DIR/main.py" ] || die "$SERVER_DIR/main.py not found."

say 1/6 "System packages"
apt-get update -qq
# (No network-manager: installing it can take over an /etc/network/interfaces
#  setup and drop the connection. The dashboard's Network card handles both.)
# python3-dev + build-essential: some wheels (e.g. httptools) may need compiling on ARM.
apt-get install -y -qq python3 python3-venv python3-pip python3-dev build-essential \
                        git sqlite3 curl avahi-daemon >/dev/null
python3 -c 'import sys; assert sys.version_info >= (3, 10), sys.version' \
    || die "Python 3.10+ is required (found $(python3 --version))."

say 2/6 "Python environment ($VENV)"
[ -d "$VENV" ] || python3 -m venv "$VENV"
# 32-bit ARM (Orange Pi One = armv7l): PyPI has no ready-made packages for
# several of our dependencies on this CPU, and bcrypt would need a Rust compiler
# to build (too much for 512 MB RAM). piwheels.org (the Raspberry Pi project's
# package mirror) serves prebuilt armv7 wheels for Debian; pip still prefers PyPI.
PIP_EXTRA=()
if [ "$(uname -m)" = "armv7l" ]; then
    PIP_EXTRA=(--extra-index-url https://www.piwheels.org/simple)
    echo "    32-bit ARM: also using prebuilt packages from piwheels.org"
fi
echo "    Installing Python packages — can take 10-20 minutes on an Orange Pi One..."
"$VENV/bin/pip" install --upgrade pip --quiet ${PIP_EXTRA[@]+"${PIP_EXTRA[@]}"}
"$VENV/bin/pip" install -r "$SERVER_DIR/requirements.txt" --quiet ${PIP_EXTRA[@]+"${PIP_EXTRA[@]}"}

say 3/6 "Configuration (.env)"
if [ -f "$SERVER_DIR/.env" ]; then
    echo "Existing .env kept."
else
    cp "$SERVER_DIR/.env.example" "$SERVER_DIR/.env"
    chmod 600 "$SERVER_DIR/.env"
    echo "Created .env from .env.example — SECRET_KEY and LICENSE_HMAC_SECRET are"
    echo "generated automatically on first start."
fi

say 4/6 "Service ($UNIT_DST)"
# An older installer created a 'pisonet' unit with a broken path — retire it.
if [ -f /etc/systemd/system/pisonet.service ]; then
    systemctl disable --now pisonet 2>/dev/null || true
    rm -f /etc/systemd/system/pisonet.service
    echo "Removed the old 'pisonet' service."
fi
cp "$SERVER_DIR/deploy/pisonex.service" "$UNIT_DST"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"

say 5/6 "Network name (pisonex.local)"
# PC clients default to http://pisonex.local — make this board answer to it.
hostnamectl set-hostname pisonex
if grep -q '^127\.0\.1\.1' /etc/hosts; then
    sed -i 's/^127\.0\.1\.1.*/127.0.1.1\tpisonex/' /etc/hosts
else
    printf '127.0.1.1\tpisonex\n' >> /etc/hosts
fi
# Restart (not just enable): avahi reads the host name only at start-up, so an
# already-running daemon would keep announcing the old name (orangepione.local).
systemctl enable avahi-daemon >/dev/null 2>&1
systemctl restart avahi-daemon

say 6/6 "Checking the server"
for _ in $(seq 1 30); do
    if curl -fsS -m 2 http://127.0.0.1/health >/dev/null 2>&1; then break; fi
    sleep 2
done
if curl -fsS -m 2 http://127.0.0.1/health; then
    echo
else
    journalctl -u "$SERVICE" -n 40 --no-pager
    die "The server did not answer on port 80 — see the log above."
fi

IP="$(hostname -I | awk '{print $1}')"
cat <<EOF

============================================================
  Pisonex is running  (version $(cat "$INSTALL_ROOT/VERSION" 2>/dev/null || echo "?"))

  Dashboard:  http://$IP/dashboard
              http://pisonex.local/dashboard
  Login:      admin / admin123   -> change it under Account

  Logs:       journalctl -u $SERVICE -f
  Restart:    sudo systemctl restart $SERVICE
  Update:     cd $INSTALL_ROOT && sudo git pull && sudo bash $SERVER_DIR/deploy/install.sh
============================================================
EOF
