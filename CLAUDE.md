# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

```
client/           ← VB.NET WinForms client for Windows PCs
deploy/           ← Raspberry Pi install script + systemd service
docs/             ← API and hardware wiring docs
server/           ← FastAPI server (runs on Raspberry Pi or Windows)
server-orangepi/  ← FastAPI server variant for Orange Pi (OPi.GPIO, SUNXI mode) ← PRODUCTION
server-windows/   ← Windows service variant
```

## Orange Pi server — production deployment

The Orange Pi variant in `server-orangepi/` is the live café server.
**All production SSH work targets this machine.**

### Key paths on the Orange Pi

| What | Path |
|---|---|
| Project root | `/opt/pisonex/` |
| Server code | `/opt/pisonex/server-orangepi/` |
| Python venv | `/opt/pisonex/venv/` (shared, NOT inside server-orangepi/) |
| `.env` config | `/opt/pisonex/server-orangepi/.env` |
| SQLite DB | `/opt/pisonex/server-orangepi/pisonet.db` |
| systemd service | `/etc/systemd/system/pisonex.service` |

### Common SSH commands

```bash
# Navigate to server code
cd /opt/pisonex/server-orangepi

# Activate the venv (always use this path — NOT venv/bin/activate inside server-orangepi)
source /opt/pisonex/venv/bin/activate

# Deactivate venv when done
deactivate

# Restart the service  (service name is pisonex, NOT pisonet)
sudo systemctl restart pisonex

# Check service status
sudo systemctl status pisonex

# Watch live logs
journalctl -u pisonex -f

# Stop / start
sudo systemctl stop pisonex
sudo systemctl start pisonex
```

### Full update workflow (after a git push from dev machine)

```bash
cd /opt/pisonex/server-orangepi
git pull
source /opt/pisonex/venv/bin/activate
pip install -r requirements.txt
deactivate
sudo systemctl restart pisonex
journalctl -u pisonex -f   # confirm clean startup
```

### Dashboard

`http://<orange-pi-ip>/dashboard` — default login `admin / admin123`
(check `/opt/pisonex/server-orangepi/.env` for `ADMIN_PASSWORD` if changed).

### Orange Pi GPIO notes

- Uses `OPi.GPIO` in SUNXI mode (Allwinner SoC port numbering)
- Default coin pin: PA12 = SUNXI 12 (physical pin 3)
- Default relay pin: PA11 = SUNXI 11 (physical pin 5)
- Run `gpio readall` on the board to list available pins
- Coin edge polarity: FALLING (optocoupler board) or RISING (direct signal)
- Hardware settings are configurable live via dashboard Settings page (no restart needed)

## Running the server (development — Windows)

```bash
cd server-orangepi        # or server/ for the RPi variant
python -m venv venv && source venv/bin/activate   # first time only
pip install -r requirements.txt                   # first time only
python main.py                                    # starts on :8000
```

Dashboard: `http://localhost:8000/dashboard`

## Building the VB.NET client

Open `client/PisoNetClient/PisoNetClient.vbproj` in Visual Studio 2022.
Build → Publish as single-file executable. Target framework: `.NET 8`.

**Debug builds:** startup registration is skipped automatically (`#If DEBUG`).
Running in VS will never add the app to Windows startup.

**Rebuild after code changes:**
```bash
cd client/PisoNetClient
dotnet build PisoNetClient.vbproj --configuration Debug
```
Always stop the running exe first — it holds a file lock on the output.

The watchdog (`client/PnxSystem/`) is a separate VB.NET project — install with `client/install-watchdog.bat`.

## Architecture: how the pieces fit together

### Server-side data flow

`main.py` wires everything at startup:
1. Runs `_enforce_secure_defaults()` — auto-generates `SECRET_KEY`, `ADMIN_PASSWORD`, `LICENSE_HMAC_SECRET` if still at insecure defaults (writes to `.env`)
2. Runs `_migrate_schema()` — safe idempotent SQLite column renames (v2 minutes → v3 seconds)
3. Creates tables (`Base.metadata.create_all`) and seeds defaults (including `ServerConfig` singleton)
4. Loads `CLIENT_API_KEY` from `ServerConfig` DB table into `settings` (not from `.env`)
5. Starts `HardwareController` (skipped on non-OPi/RPi)
6. Launches background asyncio tasks: session expiry (30 s), license verification (1 h), membership auto-expiry (30 s)

**Session lifecycle:** Coins inserted → `HardwareController._process_coin()` calls `SessionService.add_time_by_pesos()` → creates/extends a `Session` row and sets `pc.is_locked = False` → client's next heartbeat response includes `is_locked=False` and `remaining_seconds` → client unlocks.

**Heartbeat response** (`api/pc.py:heartbeat`) is the single sync point. It bundles: lock state, remaining seconds, time-added notification, pending command, per-PC message, shop-wide announcement, wallpaper url/hash, coin slot state, membership state, receiving-coins flag. All volatile state lives in `command_store.py` — an in-memory thread-safe module that resets on server restart.

### Hardware state machine (`hardware/controller.py`)

```
IDLE ──(client "Insert Coin")──▶ ACCEPTING   (relay powered, slot live)
ACCEPTING ──(coin pulses)──────▶ add time to that PC, stay ACCEPTING
ACCEPTING ──(idle timeout)─────▶ IDLE         (relay off, slot closed)
```

Coin processing runs in a daemon thread (`_process_coin`) so the GPIO ISR thread isn't blocked.

### Client architecture (VB.NET)

`Program.vb` wires all services on the STA thread, then enters `Application.Run()`.

Key services:
- `SessionManager` — 1 s local countdown (independent of network) + 1 s heartbeat. Local timer locks PC when it hits zero even if server unreachable. Server response always overwrites `_remainingSeconds` (server is source of truth on reconnect).
- `LockManager` — shows/hides `LockForm` as full-screen topmost window. Defers `UnlockPC()` while `_receivingCoins = True` so lock screen stays open until user finishes inserting coins.
- `TimerOverlay` — floating timer shown during active sessions. Contains "＋ Add Time" CTA that opens the coin slot and shows receiving-coins mini card (pulsing dot, live ₱X total, 30 s countdown bar, Done button).
- `ApiService` — all HTTP calls, attaches `X-API-Key` header if configured
- `WatchdogService` (`PnxSystem` project) — checks every 5 s whether `PisoNetClient.exe` is running; restarts it if gone

**Client configuration:**
- **Registry** (`HKLM\SOFTWARE\PisoNet\Client`): `ServerUrl`, `PCNumber`, `ApiKey`, UI settings. Managed by `AppConfig.vb`. Default `ServerUrl` is `http://192.168.1.21`.
- **DPAPI-encrypted file** (`%ProgramData%\PisoNet\license.dat`): license data + admin PIN hash. Managed by `LicenseStore.vb`.

### "Add Time" coin flow (client-side)

1. User clicks "＋ Add Time" in `TimerOverlay` (or "Insert Coin" on `LockForm`)
2. Client calls `POST /api/pc/{n}/request-coins` → server opens slot
3. Heartbeat returns `receiving_coins=True` → overlay shows receiving card + countdown bar
4. Each coin: `COIN_PULSE_TIMEOUT` (3 s) fires → coin credited → `time_added_seconds` in heartbeat → voice/toast deferred until slot closes
5. User clicks "Done inserting Coins" or countdown hits 0 → `POST /api/pc/{n}/done-inserting-coins`
6. Slot closes → `receiving_coins=False` → `LockManager` completes deferred unlock → voice fires

### Dashboard (`server-orangepi/dashboard/routes.py`)

Server-rendered with Jinja2 + HTMX — no JS framework. Authentication uses a JWT in a `pisonet_session` cookie (HS256).

Settings page cards:
- **Server Health** — OPi CPU/RAM/disk/temp/uptime via psutil (auto-refreshes 10 s)
- **Network** — DHCP ↔ static IP via nmcli or `/etc/network/interfaces`
- **Branch**, **General** (idle shutdown + coin slot idle timeout), **PC Management Presets**
- **Coin Slot Hardware** — GPIO pins, relay, edge, debounce, pulse timeout (live reload)
- **Security** — client API key
- **System Control** — Restart Service, Reboot OPi, Shutdown OPi

## Key configuration

`server-orangepi/.env` (copy from `.env.example`). On first startup, `_enforce_secure_defaults()` auto-generates `SECRET_KEY`, `ADMIN_PASSWORD`, and `LICENSE_HMAC_SECRET`.

`CLIENT_API_KEY` — managed via dashboard Settings → Security (stored in `server_config` DB table). Changes apply immediately without restart.

## Schema migrations

No Alembic. `_migrate_schema()` in `main.py` handles all renames/additions via raw SQLite `ALTER TABLE`. Each is guarded by a column-existence check so it's safe to run on every startup.
