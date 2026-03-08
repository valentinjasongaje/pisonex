# PisoNet — Centralized Internet Café Management System

A Raspberry Pi-based PisoNet system with coin slot, keypad, LCD, and VB.NET PC clients.

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
│   │   ├── keypad.py        3x4 matrix keypad scanner
│   │   ├── lcd.py           20x4 I2C LCD controller
│   │   └── controller.py    Main state machine
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

### Configure GPIO pins in server/.env

```env
COIN_PIN=4
KEYPAD_ROWS=[17,27,22,10]
KEYPAD_COLS=[9,11,5]
LCD_I2C_ADDRESS=0x27
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

`http://<raspberry-pi-ip>:8000/dashboard`

Default credentials: `admin` / `admin123`
**Change the password in `.env` before going live.**

## Hardware Wiring

```
COIN SLOT   → GPIO 4   (with 10kΩ pull-up to 3.3V)
KEYPAD ROW0 → GPIO 17
KEYPAD ROW1 → GPIO 27
KEYPAD ROW2 → GPIO 22
KEYPAD ROW3 → GPIO 10
KEYPAD COL0 → GPIO 9
KEYPAD COL1 → GPIO 11
KEYPAD COL2 → GPIO 5
LCD SDA     → GPIO 2  (I2C)
LCD SCL     → GPIO 3  (I2C)
```

## Client Behavior When Server Is Unreachable

The client does **not** lock when the server goes offline. Instead:
- The local countdown timer continues ticking every second
- An "Offline — timer running" status is shown in the timer overlay
- When the server comes back, remaining time is re-synced from the server
- The PC only locks when local time actually reaches zero
