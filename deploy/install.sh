#!/bin/bash
# PisoNet Raspberry Pi installation script
# Run as: bash install.sh

set -e

echo "=== PisoNet Installer ==="

# 1. System deps
sudo apt update && sudo apt install -y python3-pip python3-venv i2c-tools git sqlite3

# 2. Enable I2C
sudo raspi-config nonint do_i2c 0
echo "I2C enabled"

# 3. Detect LCD I2C address
echo "Scanning I2C bus..."
i2cdetect -y 1

# 4. Create venv and install Python deps
cd /home/pi/pisonet/server
python3 -m venv venv
source venv/bin/activate
pip install --upgrade pip
pip install -r requirements.txt
echo "Python deps installed"

# 5. Initialize database
python3 -c "
from database import engine, Base
from models import *
Base.metadata.create_all(engine)
print('Database initialized')
"

# 6. Install and enable systemd service
sudo cp /home/pi/pisonet/deploy/pisonet.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable pisonet
sudo systemctl start pisonet
echo "Service started"

# 7. Add GPIO permissions for pi user
sudo adduser pi gpio
sudo adduser pi i2c

# 8. Set up daily backup cron
(crontab -l 2>/dev/null; echo "0 3 * * * /home/pi/pisonet/deploy/backup.sh") | crontab -

echo ""
echo "=== Installation complete ==="
echo "Admin dashboard: http://$(hostname -I | awk '{print $1}'):8000/dashboard"
echo "Default login: admin / admin123"
echo "IMPORTANT: Change the admin password in .env before going live!"
echo ""
echo "Service status:"
sudo systemctl status pisonet --no-pager
