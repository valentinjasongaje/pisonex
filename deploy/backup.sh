#!/bin/bash
# Daily SQLite backup
# Automatically added to crontab by install.sh — runs at 3:00 AM

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
DB_PATH="$PROJECT_ROOT/server/pisonet.db"
BACKUP_DIR="$PROJECT_ROOT/backups"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)

mkdir -p "$BACKUP_DIR"

sqlite3 "$DB_PATH" ".backup '$BACKUP_DIR/pisonet_$TIMESTAMP.db'"

if [ $? -eq 0 ]; then
    echo "Backup OK: pisonet_$TIMESTAMP.db"
else
    echo "Backup FAILED" >&2
    exit 1
fi

# Keep only the last 7 days of backups
find "$BACKUP_DIR" -name "pisonet_*.db" -mtime +7 -delete
