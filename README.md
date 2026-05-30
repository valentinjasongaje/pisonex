# PISONEX — Centralized Internet Café Management System

A Raspberry Pi / Orange Pi-based PisoNet system with a coin slot and VB.NET PC clients.

> **Note:** The keypad and LCD have been removed from the current hardware build —
> the system runs **coin-slot only**. Coin acceptance is triggered from each PC's
> **Insert Coin** button on the client lock screen. The `keypad.py` / `lcd.py`
> drivers are kept in the repo (unused) so the keypad/LCD build can be re-enabled
> later. The coin and relay GPIO pins are editable from **Settings → Coin Slot
> Hardware** in the dashboard (no file edits or restart needed).

## Project Structure

```
pisonex/
├── server/                  Raspberry Pi server (Python + FastAPI)
│   ├── main.py              App entry point
│   ├── config.py            Settings (.env)
│   ├── database.py          SQLAlchemy + SQLite setup
│   ├── models.py            DB models
│   ├── schemas.py           Pydantic schemas
│   ├── api/                 REST API endpoints
│   │   ├── auth.py          Admin JWT auth
│   │   ├── pc.py            PC registration & heartbeat
│   │   ├── sessions.py      Session management
│   │   └── admin.py         Admin endpoints
│   ├── hardware/            GPIO hardware drivers
│   │   ├── coin_slot.py     Coin pulse detection
│   │   ├── keypad.py        3x4 matrix keypad scanner (kept, unused)
│   │   ├── lcd.py           20x4 I2C LCD controller (kept, unused)
│   │   └── controller.py    Coin-slot controller
│   ├── services/
│   │   ├── session_service.py  Session business logic
│   │   └── rate_service.py     Coin-to-time conversion
│   └── dashboard/           Admin web UI (Jinja2 + HTMX)
├── client/                  VB.NET Windows client
│   └── PisoNetClient/
│       ├── Program.vb       Entry point
│       ├── Config/          Server URL, PC number
│       ├── Services/        API, session, lock manager
│       └── Forms/           Lock screen, timer overlay
└── deploy/                  Deployment helpers
    ├── install.sh           Raspberry Pi setup script
    ├── pisonet.service      systemd service
    └── backup.sh            SQLite daily backup
```

## Quick Start

### Raspberry Pi Server

```bash
# Clone repo
git clone <repo> /home/pi/pisonet
cd /home/pi/pisonet

# Run installer
bash deploy/install.sh

# View logs
journalctl -u pisonet -f
```

### Configure GPIO pins

The coin and relay pins can be set from the dashboard at **Settings → Coin Slot
Hardware** (applied immediately, no restart). They can also be set in `server/.env`
as defaults:

```env
COIN_PIN=4
RELAY_PIN=6
COIN_EDGE=FALLING
COIN_DEBOUNCE_MS=30
COIN_PULSE_TIMEOUT=3.0
```

### Windows PC Client

1. Open `client/PisoNetClient/PisoNetClient.vbproj` in Visual Studio 2022
2. Set PC number and server IP in `Config/AppConfig.vb` (or via registry)
3. Build → publish as single-file executable
4. Copy to each PC — it registers itself with the server on first run

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | /api/pc/register | PC registers on startup |
| POST | /api/pc/heartbeat/{n} | PC polls every 10s |
| GET  | /api/pc/status | All PC statuses |
| POST | /api/session/add-time | Add time (coins or admin) |
| GET  | /api/session/{n} | Get session for PC |
| POST | /api/auth/token | Admin login |
| GET  | /api/admin/earnings | Revenue report |
| GET  | /api/admin/transactions | Transaction log |
| POST | /api/admin/rates | Update coin rate |

## Admin Dashboard

`http://<raspberry-pi-ip>/dashboard`

Default credentials: `admin` / `admin123`
**Change the password in `.env` before going live.**

## Hardware Wiring

```
COIN SLOT SIG → GPIO 4   (via 1kΩ + 2kΩ voltage divider: 5V → 3.3V)
RELAY IN      → GPIO 6   (HIGH = coin acceptor powered)
RELAY VCC     → 5V  (Pin 2)
RELAY GND     → GND (Pin 14)
```

The relay switches 12V to the UCB Mini v4 coin acceptor so it is only powered
while a PC is accepting coins. See the in-dashboard **Hardware Wiring Guide**
(Docs → Wiring) for full diagrams. Default pins are editable in Settings.

## Client Behavior When Server Is Unreachable

The client does **not** lock when the server goes offline. Instead:
- The local countdown timer continues ticking every second
- An "Offline — timer running" status is shown in the timer overlay
- When the server comes back, remaining time is re-synced from the server
- The PC only locks when local time actually reaches zero
