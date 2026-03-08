#!/bin/bash
# Daily SQLite backup — add to crontab:
#   0 3 * * * /home/pi/pisonet/deploy/backup.sh

BACKUP_DIR=/home/pi/pisonet/backups
DB_PATH=/home/pi/pisonet/server/pisonet.db
TIMESTAMP=$(date +%Y%m%d_%H%M%S)

mkdir -p "$BACKUP_DIR"

sqlite3 "$DB_PATH" ".backup '$BACKUP_DIR/pisonet_$TIMESTAMP.db'"

if [ $? -eq 0 ]; then
    echo "Backup successful: pisonet_$TIMESTAMP.db"
else
    echo "Backup FAILED" >&2
    exit 1
fi

# Keep only last 7 days of backups
find "$BACKUP_DIR" -name "pisonet_*.db" -mtime +7 -delete
