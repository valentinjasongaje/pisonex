#!/bin/bash
# ─────────────────────────────────────────────────────────────────────
#  make-release.sh
#
#  Sanitizes this Orange Pi into a "customer-ready" state before SD-card
#  imaging.  Produces an image-able card where:
#
#    • The Python source on disk is .pyc bytecode only (.py files removed)
#    • SSH is stopped + disabled + masked
#    • root has a fresh long random password (only known to whoever runs
#      this script — printed to stdout exactly once)
#    • .env secrets reset to defaults so first boot regenerates them
#    • DB, logs, bash history, machine-id, ssh host keys wiped
#    • .git, .env.example, dev-only files removed
#    • Free space TRIM'd so the image compresses small
#
#  Default mode is --dry-run (prints what would happen, changes nothing).
#  Pass --commit to actually do it.  Once committed the device will
#  power off; yank the SD card to image it.
#
#  Run as root:   sudo ./make-release.sh           # dry-run
#                 sudo ./make-release.sh --commit  # for real
# ─────────────────────────────────────────────────────────────────────

set -uo pipefail

# ── Config ───────────────────────────────────────────────────────────
PROJECT_DIR="/opt/pisonex/server-orangepi"
VENV_PY="/opt/pisonex/venv/bin/python"
SYSTEMD_UNIT="/etc/systemd/system/pisonex.service"
SERVICE_NAME="pisonex"
RELEASE_VERSION="${RELEASE_VERSION:-v1.0}"

DRY_RUN=true
if [ "${1:-}" = "--commit" ]; then
    DRY_RUN=false
fi

# ── Helpers ──────────────────────────────────────────────────────────
C_RED=$'\e[31m'; C_GRN=$'\e[32m'; C_YEL=$'\e[33m'; C_BLU=$'\e[34m'; C_BLD=$'\e[1m'; C_OFF=$'\e[0m'

say()   { printf '%s\n' "$*"; }
info()  { printf '%s[*]%s %s\n' "$C_BLU" "$C_OFF" "$*"; }
ok()    { printf '%s[+]%s %s\n' "$C_GRN" "$C_OFF" "$*"; }
warn()  { printf '%s[!]%s %s\n' "$C_YEL" "$C_OFF" "$*"; }
die()   { printf '%s[x]%s %s\n' "$C_RED" "$C_OFF" "$*" >&2; exit 1; }
hdr()   { printf '\n%s━━ %s ━━%s\n' "$C_BLD" "$*" "$C_OFF"; }
plan()  { printf '    %s» %s%s\n' "$C_YEL" "$*" "$C_OFF"; }
exec_or_plan() {
    if $DRY_RUN; then
        plan "$*"
    else
        eval "$@"
    fi
}

# ── Sanity checks ────────────────────────────────────────────────────
[ "$EUID" -eq 0 ] || die "Must run as root."
[ -d "$PROJECT_DIR" ] || die "Project dir $PROJECT_DIR not found — wrong machine?"
[ -x "$VENV_PY" ] || die "Venv python $VENV_PY not found."

hdr "make-release.sh — PisoNet image prep ($RELEASE_VERSION)"
if $DRY_RUN; then
    warn "DRY-RUN mode.  Nothing will be changed.  Re-run with --commit to actually do it."
else
    warn "${C_BLD}COMMIT MODE${C_OFF} — this WILL destroy data on this device."
    warn "After this script runs the device will power off."
    say
    read -r -p "Type the release version to confirm (expecting '$RELEASE_VERSION'): " confirm
    [ "$confirm" = "$RELEASE_VERSION" ] || die "Confirmation mismatch.  Aborting."
fi

# ── 1. Stop the service ──────────────────────────────────────────────
hdr "1. Stop pisonex service"
if systemctl is-active --quiet "$SERVICE_NAME"; then
    exec_or_plan "systemctl stop $SERVICE_NAME"
    ok "Service stopped"
else
    info "Service already stopped"
fi

# ── 2. Compile .py → .pyc (legacy layout, alongside source) ──────────
hdr "2. Compile Python source to bytecode"
PY_COUNT=$(find "$PROJECT_DIR" -name '*.py' -not -path '*/__pycache__/*' | wc -l)
info "Found $PY_COUNT .py files in $PROJECT_DIR"
if $DRY_RUN; then
    plan "$VENV_PY -m compileall -b -q $PROJECT_DIR  (would create ~$PY_COUNT .pyc files alongside .py)"
else
    info "Compiling..."
    "$VENV_PY" -m compileall -b -q "$PROJECT_DIR" >/dev/null
    PYC_COUNT=$(find "$PROJECT_DIR" -name '*.pyc' -not -path '*/__pycache__/*' | wc -l)
    ok "Generated $PYC_COUNT .pyc files"
    [ "$PYC_COUNT" -ge "$PY_COUNT" ] || die "Compilation incomplete ($PYC_COUNT pyc vs $PY_COUNT py).  Aborting."
fi

# ── 3. Patch systemd unit to run main.pyc ─────────────────────────────
hdr "3. Patch systemd unit to run main.pyc"
if [ -f "$SYSTEMD_UNIT" ]; then
    if grep -q 'main\.py$\|main\.py ' "$SYSTEMD_UNIT" 2>/dev/null; then
        info "Will rewrite 'main.py' → 'main.pyc' in $SYSTEMD_UNIT"
        exec_or_plan "sed -i.bak 's|main\\.py\\b|main.pyc|g' $SYSTEMD_UNIT && rm -f ${SYSTEMD_UNIT}.bak && systemctl daemon-reload"
        ok "Unit patched and reloaded"
    else
        info "Unit already references main.pyc (or non-standard); not touching"
    fi
else
    warn "Systemd unit $SYSTEMD_UNIT not found"
fi

# ── 4. Quick service-start verification (only in commit mode) ────────
hdr "4. Verify service runs from bytecode"
if $DRY_RUN; then
    plan "Start service for 5 s, confirm it stays active, then stop"
else
    info "Starting service..."
    systemctl start "$SERVICE_NAME"
    sleep 5
    if systemctl is-active --quiet "$SERVICE_NAME"; then
        ok "Service runs from .pyc bytecode"
    else
        warn "Service failed to start from .pyc — last 30 lines of journal:"
        journalctl -u "$SERVICE_NAME" -n 30 --no-pager
        die "Verification failed.  Source state preserved (still has .py files).  Investigate and rerun."
    fi
    systemctl stop "$SERVICE_NAME"
fi

# ── 5. Delete .py files and __pycache__ ──────────────────────────────
hdr "5. Strip .py source"
info "Will delete $PY_COUNT .py files + any __pycache__ subdirs"
exec_or_plan "find $PROJECT_DIR -name '*.py' -delete"
exec_or_plan "find $PROJECT_DIR -type d -name '__pycache__' -exec rm -rf {} + 2>/dev/null || true"
ok ".py source removed"

# ── 6. Remove dev-only files ─────────────────────────────────────────
hdr "6. Remove dev-only files"
# Collect into a single dedup'd list so dry-run prints each path once.
declare -A SEEN
add_target() {
    local p="$1"
    [ -e "$p" ] || return
    [ -n "${SEEN[$p]:-}" ] && return
    SEEN[$p]=1
    exec_or_plan "rm -rf $p"
}
for path in \
    "/opt/pisonex/.git" \
    "/opt/pisonex/.gitignore" \
    "/opt/pisonex/.gitattributes" \
    "$PROJECT_DIR/test_coin_signal.py" \
    "$PROJECT_DIR/test_keypad.py" \
    "$PROJECT_DIR/data" \
    "$PROJECT_DIR/__pycache__"
do
    add_target "$path"
done

# Also remove any leftover docs/readmes/build scripts at the project root
for pat in '*.md' '*.example' 'README*' 'requirements-dev.txt' 'pytest.ini' '.pytest_cache' 'docs' 'tests' 'test'; do
    while IFS= read -r f; do
        add_target "$f"
    done < <(find "$PROJECT_DIR" -maxdepth 2 -name "$pat" 2>/dev/null)
done

# ── 7. Reset .env to defaults so first-boot regenerates secrets ──────
hdr "7. Reset .env secrets to defaults"
ENV_FILE="$PROJECT_DIR/.env"
if [ -f "$ENV_FILE" ]; then
    info "Current non-secret settings will be preserved.  Resetting secrets:"
    for var in SECRET_KEY LICENSE_HMAC_SECRET; do
        plan "  $var → changeme  (auto-regen by _enforce_secure_defaults on first boot)"
    done
    plan "  ADMIN_PASSWORD → admin123  (kept at default — customer changes via dashboard)"
    plan "  CLIENT_API_KEY → (empty)   (customer enables/generates via dashboard Security card)"
    plan "  BRANCH_NAME    → My Internet Cafe   (customer renames via dashboard Branch card)"
    if ! $DRY_RUN; then
        sed -i -E 's|^SECRET_KEY=.*|SECRET_KEY=changeme|'             "$ENV_FILE"
        sed -i -E 's|^LICENSE_HMAC_SECRET=.*|LICENSE_HMAC_SECRET=changeme|' "$ENV_FILE"
        sed -i -E 's|^ADMIN_PASSWORD=.*|ADMIN_PASSWORD=admin123|'     "$ENV_FILE"
        sed -i -E 's|^CLIENT_API_KEY=.*|CLIENT_API_KEY=|'             "$ENV_FILE"
        sed -i -E 's|^BRANCH_NAME=.*|BRANCH_NAME=My Internet Cafe|'   "$ENV_FILE"
        ok ".env reset"
    fi
else
    warn ".env not found at $ENV_FILE"
fi

# ── 8. Wipe runtime state ────────────────────────────────────────────
hdr "8. Wipe runtime state"
for path in \
    "$PROJECT_DIR/pisonet.db" \
    "$PROJECT_DIR/pisonet.db-journal" \
    "$PROJECT_DIR/pisonet.db-wal" \
    "$PROJECT_DIR/pisonet.db-shm" \
    "$PROJECT_DIR/pisonet.log"
do
    [ -e "$path" ] && exec_or_plan "rm -f $path"
done
exec_or_plan "journalctl --rotate >/dev/null 2>&1 || true"
exec_or_plan "journalctl --vacuum-time=1s >/dev/null 2>&1 || true"
ok "Runtime state wiped"

# ── 9. Disable + mask SSH ────────────────────────────────────────────
hdr "9. Disable SSH"
exec_or_plan "systemctl stop ssh"
exec_or_plan "systemctl disable ssh 2>/dev/null || true"
exec_or_plan "systemctl mask ssh"
ok "SSH disabled and masked (un-startable even by 'systemctl start')"

# ── 10. Rotate root password to a long random string ─────────────────
hdr "10. Rotate root password"
if $DRY_RUN; then
    plan "Generate 32-char random password, set root's password, PRINT IT ONCE"
    plan "  (you save it to a private file on your laptop)"
else
    NEW_ROOT_PW=$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32)
    echo "root:$NEW_ROOT_PW" | chpasswd
    echo
    echo "┌──────────────────────────────────────────────────────────────────┐"
    echo "│  ROOT PASSWORD FOR THIS IMAGE — SAVE IT NOW, NEVER PRINTED AGAIN │"
    echo "│                                                                  │"
    echo "│    $NEW_ROOT_PW    │"
    echo "│                                                                  │"
    echo "│  Use case: local console recovery if a deployed device ever      │"
    echo "│  needs manual intervention (SSH is disabled, so console is the   │"
    echo "│  only way in).  Customers do not have this password.             │"
    echo "└──────────────────────────────────────────────────────────────────┘"
    echo
    ok "Root password rotated"
fi

# ── 11. Remove SSH host keys + authorized_keys ───────────────────────
hdr "11. Strip SSH identity files (host keys, authorized_keys, known_hosts)"
exec_or_plan "rm -f /etc/ssh/ssh_host_*"
exec_or_plan "rm -f /root/.ssh/authorized_keys"
exec_or_plan "rm -f /root/.ssh/known_hosts"
ok "SSH identity stripped"

# ── 12. Clear shell history + machine-id ─────────────────────────────
hdr "12. Clear shell history + machine-id (systemd regenerates on first boot)"
for f in /root/.bash_history /root/.zsh_history /home/*/.bash_history; do
    [ -e "$f" ] || continue
    if $DRY_RUN; then
        plan "shred-then-remove $f"
    else
        shred -u "$f" 2>/dev/null || rm -f "$f"
    fi
done
exec_or_plan "truncate -s 0 /etc/machine-id"
exec_or_plan "rm -f /var/lib/dbus/machine-id"
ok "Shell + identity cleared"

# ── 13. Clean apt cache + tmp dirs ───────────────────────────────────
hdr "13. Clean caches"
exec_or_plan "apt-get clean -y >/dev/null"
exec_or_plan "rm -rf /tmp/* /var/tmp/* 2>/dev/null || true"
exec_or_plan "rm -rf /var/cache/apt/archives/partial/* 2>/dev/null || true"
ok "Caches cleaned"

# ── 14. Drop a release marker (small, no secrets) ────────────────────
hdr "14. Drop release marker"
REL_FILE="/opt/pisonex/RELEASE"
plan "echo '$RELEASE_VERSION  built $(date -u +%Y-%m-%dT%H:%M:%SZ)' > $REL_FILE"
if ! $DRY_RUN; then
    echo "$RELEASE_VERSION  built $(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$REL_FILE"
fi

# ── 15. fstrim to free unused blocks (shrinks final image) ───────────
hdr "15. TRIM unused blocks"
plan "fstrim -v /  (releases unused SD blocks so dd image compresses small)"
exec_or_plan "fstrim -v / >/dev/null 2>&1 || true"
ok "TRIM done"

# ── 16. Remove this script itself + power off ────────────────────────
hdr "16. Self-destruct + power off"
plan "shred-then-remove $0"
plan "shutdown -h now"
if ! $DRY_RUN; then
    SCRIPT_PATH="$(readlink -f "$0")"
    # Sync filesystem before pulling the rug out
    sync
    echo
    ok "Sanitize complete.  Powering off in 5 seconds..."
    echo "Once the LED goes dark you may safely remove the SD card."
    sleep 5
    shred -u "$SCRIPT_PATH" 2>/dev/null || rm -f "$SCRIPT_PATH"
    /sbin/shutdown -h now
fi

echo
if $DRY_RUN; then
    hdr "DRY-RUN COMPLETE"
    say "Nothing was changed.  Review the plan above."
    say "When you're ready, re-run as:"
    say "    ${C_BLD}sudo $0 --commit${C_OFF}"
    say
    say "You will be asked to confirm with the release version string ('$RELEASE_VERSION')."
fi
