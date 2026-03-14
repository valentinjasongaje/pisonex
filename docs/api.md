# PisoNet API Documentation

> **Base URL:** `http://<raspberry-pi-ip>:8000`
> **Version:** 1.0.0

---

## Table of Contents

- [General](#general)
- [Authentication](#authentication)
- [PC Endpoints](#pc-endpoints)
- [Session Endpoints](#session-endpoints)
- [Admin Endpoints](#admin-endpoints)
- [Dashboard Endpoints](#dashboard-endpoints)

---

## General

### Health Check

```
GET /health
```

Returns server status. No authentication required.

**Response `200 OK`**
```json
{
  "status": "ok",
  "version": "1.0.0"
}
```

---

## Authentication

All admin API endpoints require a **Bearer token** obtained from this endpoint.
Dashboard pages use a **session cookie** (`pisonet_session`) instead.

---

### POST `/api/auth/token`

Authenticate as admin and receive a JWT access token.

**Content-Type:** `application/x-www-form-urlencoded`

**Request Body (form fields)**

| Field      | Type   | Required | Description        |
|------------|--------|----------|--------------------|
| `username` | string | ✅       | Admin username     |
| `password` | string | ✅       | Admin password     |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/auth/token \
  -d "username=admin&password=admin123"
```

**Response `200 OK`**
```json
{
  "access_token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "token_type": "bearer"
}
```

**Response `401 Unauthorized`**
```json
{
  "detail": "Incorrect username or password"
}
```

> **Note:** Token expires in **8 hours**. Store it and include it as `Authorization: Bearer <token>` on all admin API requests.

---

## PC Endpoints

These endpoints are called by the **VB.NET client** running on each PC. No authentication required.

---

### POST `/api/pc/register`

Registers a PC with the server on first launch, or updates its MAC and IP on reconnect.

**Query Parameters**

| Param         | Type   | Required | Description                        |
|---------------|--------|----------|------------------------------------|
| `pc_number`   | int    | ✅       | Unique PC number (e.g. `1`, `2`)   |
| `mac_address` | string | ✅       | MAC address of the client machine  |

**Example Request**
```bash
curl -X POST "http://192.168.1.100:8000/api/pc/register?pc_number=1&mac_address=AA:BB:CC:DD:EE:FF"
```

**Response `200 OK`**
```json
{
  "pc_id": 1,
  "pc_number": 1,
  "name": "PC-01",
  "registered": true
}
```

**Response `200 OK` (already registered — updated IP/MAC)**
```json
{
  "pc_id": 1,
  "pc_number": 1,
  "name": "PC-01",
  "registered": false
}
```

---

### POST `/api/pc/heartbeat/{pc_number}`

Sent by the client every **10 seconds**. Marks the PC as online and returns the current session state. If remaining time changed server-side (e.g. admin added minutes), the client re-syncs its local countdown here.

**Path Parameters**

| Param       | Type | Description         |
|-------------|------|---------------------|
| `pc_number` | int  | The PC's number     |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/pc/heartbeat/1
```

**Response `200 OK` — Active session, PC unlocked**
```json
{
  "is_locked": false,
  "remaining_minutes": 28,
  "remaining_seconds": 45,
  "session_token": "tok_abc123",
  "time_added_minutes": 0
}
```

**Response `200 OK` — No session, PC locked**
```json
{
  "is_locked": true,
  "remaining_minutes": 0,
  "remaining_seconds": 0,
  "session_token": null,
  "time_added_minutes": 0
}
```

**Response `200 OK` — Admin just added time (client shows toast)**
```json
{
  "is_locked": false,
  "remaining_minutes": 60,
  "remaining_seconds": 0,
  "session_token": "tok_abc123",
  "time_added_minutes": 30
}
```

**Response `404 Not Found`**
```json
{
  "detail": "PC not registered"
}
```

> **Client behavior:** If `is_locked: true` and local timer hits zero → lock screen. If `time_added_minutes > 0` → show notification toast.

---

### GET `/api/pc/status`

Returns a list of all registered PCs with their current online/lock status. PCs that missed a heartbeat for more than `PC_HEARTBEAT_TIMEOUT` (30s) are automatically marked offline.

**Example Request**
```bash
curl http://192.168.1.100:8000/api/pc/status
```

**Response `200 OK`**
```json
[
  {
    "pc_number": 1,
    "name": "PC-01",
    "is_online": true,
    "is_locked": false,
    "ip_address": "192.168.1.101",
    "last_seen": "2026-03-14T10:30:00",
    "remaining_minutes": 28
  },
  {
    "pc_number": 2,
    "name": "PC-02",
    "is_online": false,
    "is_locked": true,
    "ip_address": "192.168.1.102",
    "last_seen": "2026-03-14T09:15:00",
    "remaining_minutes": 0
  }
]
```

---

### POST `/api/pc/{pc_number}/screenshot`

Client uploads a JPEG screenshot every 5 seconds for live monitoring in the dashboard. Stored in memory only (not persisted to disk).

**Path Parameters**

| Param       | Type | Description     |
|-------------|------|-----------------|
| `pc_number` | int  | The PC's number |

**Headers**

| Header         | Value              |
|----------------|--------------------|
| `Content-Type` | `image/jpeg`       |

**Body:** Raw JPEG binary (max **2 MB**)

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/pc/1/screenshot \
  -H "Content-Type: image/jpeg" \
  --data-binary @screenshot.jpg
```

**Response `200 OK`**
```json
{
  "status": "ok"
}
```

**Response `413`**
```json
{
  "detail": "Screenshot too large (max 2 MB)"
}
```

---

### POST `/api/pc/{pc_number}/lock`

Ends the active session and immediately locks the PC. Can be called by admin or dashboard.

**Path Parameters**

| Param       | Type | Description     |
|-------------|------|-----------------|
| `pc_number` | int  | The PC's number |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/pc/1/lock
```

**Response `200 OK`**
```json
{
  "status": "locked",
  "pc_number": 1
}
```

---

## Session Endpoints

---

### POST `/api/session/add-time`

Adds time to a PC by converting pesos to minutes using the configured coin rates. Called by the **hardware controller** when coins are inserted, or by admin.

**Content-Type:** `application/json`

**Request Body**

| Field       | Type | Required | Description                         |
|-------------|------|----------|-------------------------------------|
| `pc_number` | int  | ✅       | Target PC number                    |
| `pesos`     | int  | ✅       | Amount inserted (e.g. `5`, `10`)    |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/session/add-time \
  -H "Content-Type: application/json" \
  -d '{"pc_number": 1, "pesos": 5}'
```

**Response `200 OK`**
```json
{
  "pc_number": 1,
  "pesos_added": 5,
  "minutes_added": 30,
  "total_minutes": 60,
  "session_token": "tok_abc123"
}
```

**Response `404 Not Found`**
```json
{
  "detail": "PC 1 not found"
}
```

**Response `400 Bad Request`** (no matching coin rate)
```json
{
  "detail": "No rate configured for ₱5"
}
```

---

### GET `/api/session/{pc_number}`

Returns the current session status for a PC.

**Path Parameters**

| Param       | Type | Description     |
|-------------|------|-----------------|
| `pc_number` | int  | The PC's number |

**Example Request**
```bash
curl http://192.168.1.100:8000/api/session/1
```

**Response `200 OK` — Active session**
```json
{
  "has_session": true,
  "remaining_minutes": 28,
  "remaining_seconds": 45,
  "minutes_granted": 60,
  "started_at": "2026-03-14T10:00:00",
  "session_token": "tok_abc123"
}
```

**Response `200 OK` — No active session**
```json
{
  "has_session": false,
  "remaining_minutes": 0,
  "remaining_seconds": 0,
  "minutes_granted": 0,
  "started_at": null,
  "session_token": null
}
```

---

### POST `/api/session/{pc_number}/end`

Ends the active session and locks the PC.

**Path Parameters**

| Param       | Type | Description     |
|-------------|------|-----------------|
| `pc_number` | int  | The PC's number |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/session/1/end
```

**Response `200 OK`**
```json
{
  "status": "session ended",
  "pc_number": 1
}
```

---

## Admin Endpoints

> **Authentication required.** Include `Authorization: Bearer <token>` on every request.

---

### GET `/api/admin/earnings`

Returns total revenue for the last N days.

**Query Parameters**

| Param  | Type | Default | Description              |
|--------|------|---------|--------------------------|
| `days` | int  | `30`    | Look-back period in days |

**Example Request**
```bash
curl http://192.168.1.100:8000/api/admin/earnings?days=7 \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
{
  "total_pesos": 1250,
  "total_transactions": 87,
  "period_days": 7
}
```

---

### GET `/api/admin/earnings/daily`

Returns per-day earnings breakdown for charting.

**Query Parameters**

| Param  | Type | Default | Description              |
|--------|------|---------|--------------------------|
| `days` | int  | `7`     | Number of days to return |

**Example Request**
```bash
curl http://192.168.1.100:8000/api/admin/earnings/daily?days=7 \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
[
  { "date": "2026-03-08", "total_pesos": 150, "transactions": 10 },
  { "date": "2026-03-09", "total_pesos": 200, "transactions": 14 },
  { "date": "2026-03-10", "total_pesos": 175, "transactions": 12 },
  { "date": "2026-03-11", "total_pesos": 225, "transactions": 16 },
  { "date": "2026-03-12", "total_pesos": 190, "transactions": 13 },
  { "date": "2026-03-13", "total_pesos": 160, "transactions": 11 },
  { "date": "2026-03-14", "total_pesos": 150, "transactions": 11 }
]
```

---

### GET `/api/admin/transactions`

Returns paginated list of coin transactions, newest first.

**Query Parameters**

| Param    | Type | Default | Description               |
|----------|------|---------|---------------------------|
| `limit`  | int  | `100`   | Max records to return     |
| `offset` | int  | `0`     | Skip first N records      |

**Example Request**
```bash
curl "http://192.168.1.100:8000/api/admin/transactions?limit=10&offset=0" \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
[
  {
    "id": 42,
    "pc_id": 1,
    "amount_pesos": 10,
    "minutes_added": 60,
    "created_at": "2026-03-14T10:05:00"
  },
  {
    "id": 41,
    "pc_id": 3,
    "amount_pesos": 5,
    "minutes_added": 30,
    "created_at": "2026-03-14T09:45:00"
  }
]
```

---

### GET `/api/admin/rates`

Returns all active coin rates ordered by peso amount.

**Example Request**
```bash
curl http://192.168.1.100:8000/api/admin/rates \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
[
  { "id": 1, "pesos": 5,  "minutes": 30, "label": "₱5 = 30 minutes",  "is_active": true },
  { "id": 2, "pesos": 10, "minutes": 60, "label": "₱10 = 60 minutes", "is_active": true }
]
```

---

### POST `/api/admin/rates`

Creates a new coin rate. If a rate for the same peso amount already exists, the old one is deactivated.

**Content-Type:** `application/json`

**Request Body**

| Field     | Type   | Required | Description                        |
|-----------|--------|----------|------------------------------------|
| `pesos`   | int    | ✅       | Coin denomination (e.g. `5`)       |
| `minutes` | int    | ✅       | Minutes granted (e.g. `30`)        |
| `label`   | string | ❌       | Display label; auto-generated if omitted |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/admin/rates \
  -H "Authorization: Bearer eyJhbGci..." \
  -H "Content-Type: application/json" \
  -d '{"pesos": 5, "minutes": 30}'
```

**Response `200 OK`**
```json
{
  "id": 3,
  "pesos": 5,
  "minutes": 30,
  "label": "₱5 = 30 minutes",
  "is_active": true
}
```

---

### DELETE `/api/admin/rates/{rate_id}`

Soft-deletes a coin rate (marks `is_active = false`).

**Path Parameters**

| Param     | Type | Description              |
|-----------|------|--------------------------|
| `rate_id` | int  | ID of the rate to delete |

**Example Request**
```bash
curl -X DELETE http://192.168.1.100:8000/api/admin/rates/1 \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
{
  "status": "deleted"
}
```

---

### POST `/api/admin/pc/add-time`

Admin manually adds minutes to a PC without going through coin conversion.

**Content-Type:** `application/json`

**Request Body**

| Field       | Type | Required | Description                     |
|-------------|------|----------|---------------------------------|
| `pc_number` | int  | ✅       | Target PC number                |
| `minutes`   | int  | ✅       | Minutes to add (e.g. `30`)      |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/admin/pc/add-time \
  -H "Authorization: Bearer eyJhbGci..." \
  -H "Content-Type: application/json" \
  -d '{"pc_number": 2, "minutes": 30}'
```

**Response `200 OK`**
```json
{
  "pc_number": 2,
  "minutes_added": 30,
  "total_minutes": 90
}
```

---

### POST `/api/admin/pc/{pc_number}/lock`

End session and lock a PC immediately.

**Path Parameters**

| Param       | Type | Description     |
|-------------|------|-----------------|
| `pc_number` | int  | The PC's number |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/api/admin/pc/2/lock \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
{
  "status": "locked"
}
```

---

### GET `/api/admin/logs`

Returns system logs with optional filtering.

**Query Parameters**

| Param    | Type   | Default | Description                                       |
|----------|--------|---------|---------------------------------------------------|
| `limit`  | int    | `200`   | Max log entries to return                         |
| `level`  | string | `null`  | Filter by level: `INFO`, `WARNING`, `ERROR`       |
| `source` | string | `null`  | Filter by source: `hardware`, `api`, `session`    |

**Example Request**
```bash
curl "http://192.168.1.100:8000/api/admin/logs?level=ERROR&limit=50" \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
[
  {
    "id": 199,
    "level": "ERROR",
    "source": "hardware",
    "message": "Error processing ₱5 for PC 02: connection timeout",
    "created_at": "2026-03-14T10:22:00"
  }
]
```

---

### DELETE `/api/admin/logs`

Deletes system logs older than N days.

**Query Parameters**

| Param  | Type | Default | Description                       |
|--------|------|---------|-----------------------------------|
| `days` | int  | `30`    | Delete logs older than this many days |

**Example Request**
```bash
curl -X DELETE "http://192.168.1.100:8000/api/admin/logs?days=7" \
  -H "Authorization: Bearer eyJhbGci..."
```

**Response `200 OK`**
```json
{
  "deleted": 142
}
```

---

## Dashboard Endpoints

The dashboard is a **server-rendered** web UI (Jinja2 + HTMX) accessible via browser. Authentication uses a session cookie (`pisonet_session`) set on login.

---

### POST `/dashboard/login`

**Content-Type:** `application/x-www-form-urlencoded`

| Field      | Type   | Description      |
|------------|--------|------------------|
| `username` | string | Admin username   |
| `password` | string | Admin password   |

On success: redirects to `/dashboard`, sets `pisonet_session` cookie (HTTPOnly, SameSite=lax, 8h expiry).
On failure: re-renders login page with error message.

---

### GET `/dashboard/monitor/status`

Used by the monitor page (HTMX polling) to refresh PC live status.

**Response `200 OK`**
```json
[
  {
    "pc_number": 1,
    "name": "PC-01",
    "is_online": true,
    "is_locked": false,
    "remaining_minutes": 28,
    "remaining_seconds": 45,
    "remaining_total_sec": 1725,
    "has_screenshot": true
  }
]
```

---

### GET `/dashboard/api/pc/{pc_number}/screenshot`

Returns the latest in-memory JPEG screenshot for a PC.

**Response:** Binary JPEG (`image/jpeg`), `Cache-Control: no-store`

---

### POST `/dashboard/api/pc/add-time`

Same as `POST /api/admin/pc/add-time` but authenticated via session cookie (for dashboard UI use).

---

### POST `/dashboard/api/pc/{pc_number}/lock`

Same as `POST /api/admin/pc/{pc_number}/lock` but authenticated via session cookie.

---

### POST `/dashboard/api/pc/{pc_number}/rename`

Rename a PC from the dashboard.

**Content-Type:** `application/json`

**Request Body**

| Field  | Type   | Required | Description      |
|--------|--------|----------|------------------|
| `name` | string | ✅       | New display name |

**Example Request**
```bash
curl -X POST http://192.168.1.100:8000/dashboard/api/pc/1/rename \
  -H "Content-Type: application/json" \
  -d '{"name": "Gaming PC 1"}'
```

**Response `200 OK`**
```json
{
  "pc_number": 1,
  "name": "Gaming PC 1"
}
```

---

## Error Responses

All endpoints follow standard HTTP status codes:

| Status | Meaning                                  |
|--------|------------------------------------------|
| `200`  | Success                                  |
| `400`  | Bad request (invalid params or logic)    |
| `401`  | Unauthorized (missing or invalid token)  |
| `404`  | Resource not found                       |
| `413`  | Payload too large (screenshot upload)    |
| `422`  | Validation error (wrong field types)     |
| `500`  | Internal server error                    |

All error bodies follow this structure:
```json
{
  "detail": "Human-readable error message"
}
```

---

## Interactive Docs

FastAPI auto-generates interactive API docs. Open in browser while server is running:

- **Swagger UI:** `http://<pi-ip>:8000/docs`
- **ReDoc:** `http://<pi-ip>:8000/redoc`
