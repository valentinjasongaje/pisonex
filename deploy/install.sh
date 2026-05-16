#!/bin/bash
# PisoNet Raspberry Pi installation script
# Run as: bash deploy/install.sh  (from anywhere inside the project)

set -e

echo "=== Pisonex Installer ==="

# ── Resolve project root automatically ───────────────────────────────────────
# Works no matter where the project was placed (~/Documents/pisonex, /home/pi/pisonet, etc.)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
SERVER_DIR="$PROJECT_ROOT/server"

echo "Project root : $PROJECT_ROOT"
echo "Server dir   : $SERVER_DIR"

# Verify server dir exists
if [ ! -f "$SERVER_DIR/main.py" ]; then
    echo ""
    echo "ERROR: Cannot find server/main.py in $SERVER_DIR"
    echo "Make sure you run this script from inside the project folder."
    echo "Example:  cd ~/Documents/pisonex && bash deploy/install.sh"
    exit 1
fi

# ── 1. System deps ────────────────────────────────────────────────────────────
echo ""
echo "[1/10] Installing system packages..."
sudo apt update && sudo apt install -y python3-pip python3-venv i2c-tools git sqlite3 avahi-daemon avahi-utils

# ── 2. Enable I2C ────────────────────────────────────────────────────────────
echo ""
echo "[2/10] Enabling I2C..."
sudo raspi-config nonint do_i2c 0
echo "I2C enabled"

# ── 3. Detect LCD I2C address ─────────────────────────────────────────────────
echo ""
echo "[3/10] Scanning I2C bus (look for 0x27 or 0x3F)..."
i2cdetect -y 1 || echo "No I2C devices found yet (hardware not connected — that's OK)"

# ── 4. Python venv + deps ────────────────────────────────────────────────────
echo ""
echo "[4/10] Setting up Python virtual environment..."
cd "$SERVER_DIR"
python3 -m venv venv
source venv/bin/activate
pip install --upgrade pip --quiet
pip install -r requirements.txt
echo "Python deps installed"

# ── 5. Initialize database ───────────────────────────────────────────────────
echo ""
echo "[5/10] Initializing database..."
python3 -c "
from database import engine, Base
from models import *
Base.metadata.create_all(engine)
print('Database initialized')
"

# ── 6. Install systemd service ────────────────────────────────────────────────
echo ""
echo "[6/10] Installing systemd service..."

# Patch the service file with the actual project path AND actual username
# (newer Raspberry Pi OS uses custom usernames, not always 'pi')
SERVICE_SRC="$PROJECT_ROOT/deploy/pisonet.service"
SERVICE_TMP="/tmp/pisonet.service"

CURRENT_USER="$USER"
echo "Running as user: $CURRENT_USER"

sed "s|/home/pi/pisonet|$PROJECT_ROOT|g; s|User=pi|User=$CURRENT_USER|g; s|Group=pi|Group=$CURRENT_USER|g" \
    "$SERVICE_SRC" > "$SERVICE_TMP"

sudo cp "$SERVICE_TMP" /etc/systemd/system/pisonet.service
sudo systemctl daemon-reload
sudo systemctl enable pisonet
sudo systemctl start pisonet
echo "Service installed and started"

# ── 7. GPIO + I2C permissions ────────────────────────────────────────────────
echo ""
echo "[7/10] Setting GPIO/I2C permissions..."
sudo adduser "$USER" gpio 2>/dev/null || true
sudo adduser "$USER" i2c  2>/dev/null || true

# ── 8. Set hostname to pisonex (enables pisonex.local on the LAN) ────────────
echo ""
echo "[8/10] Setting hostname to 'pisonex'..."
sudo hostnamectl set-hostname pisonex
# Update /etc/hosts so the Pi resolves its own name instantly
sudo sed -i "s/127\.0\.1\.1.*/127.0.1.1\tpisonex/" /etc/hosts
echo "Hostname set — this Pi will be reachable as pisonex.local"

# ── 9. Enable mDNS via avahi ─────────────────────────────────────────────────
echo ""
echo "[9/10] Enabling avahi-daemon (mDNS for pisonex.local)..."
sudo systemctl enable avahi-daemon
sudo systemctl start avahi-daemon
echo "avahi-daemon running — pisonex.local is now resolvable on the LAN"

# ── 10. Daily backup cron ────────────────────────────────────────────────────
echo ""
echo "[10/10] Setting up daily backup..."
BACKUP_SCRIPT="$PROJECT_ROOT/deploy/backup.sh"
chmod +x "$BACKUP_SCRIPT"
(crontab -l 2>/dev/null | grep -v "pisonet"; echo "0 3 * * * $BACKUP_SCRIPT") | crontab -
echo "Backup scheduled at 3:00 AM daily"

# ── Done ──────────────────────────────────────────────────────────────────────
echo ""
echo "============================================"
echo "  PisoNet installation complete!"
echo "============================================"
echo ""
echo "  Admin dashboard:"
echo "  http://pisonex.local/dashboard"
echo "  http://$(hostname -I | awk '{print $1}')/dashboard"
echo ""
echo "  Login: admin / admin123"
echo "  IMPORTANT: Change the password in $SERVER_DIR/.env"
echo ""
echo "  Useful commands:"
echo "  View logs  : journalctl -u pisonet -f"
echo "  Restart    : sudo systemctl restart pisonet"
echo "  Stop       : sudo systemctl stop pisonet"
echo ""
sudo systemctl status pisonet --no-pager
