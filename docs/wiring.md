# PisoNet Hardware Wiring Guide

> **Platform:** Raspberry Pi (BCM GPIO numbering)
> All GPIO pin numbers in this document use **BCM numbering** unless stated otherwise.

---

## Table of Contents

- [Raspberry Pi GPIO Reference](#raspberry-pi-gpio-reference)
- [Keypad (4×3 Matrix)](#keypad-43-matrix)
- [Coin Slot (UCB Mini v4)](#coin-slot-ucb-mini-v4)
- [Relay Module (Coin Acceptor Power)](#relay-module-coin-acceptor-power)
- [LCD Display (I2C 20×4)](#lcd-display-i2c-204)
- [Full Pin Summary](#full-pin-summary)
- [Troubleshooting](#troubleshooting)

---

## Raspberry Pi GPIO Reference

```
                    ┌──────────────────────────────────┐
              3.3V ─┤  1 │  2 ├─ 5V
           GPIO  2 ─┤  3 │  4 ├─ 5V
           GPIO  3 ─┤  5 │  6 ├─ GND
           GPIO  4 ─┤  7 │  8 ├─ GPIO 14
               GND ─┤  9 │ 10 ├─ GPIO 15
  [KEYPAD R1] GPIO 17─┤ 11 │ 12 ├─ GPIO 18
  [KEYPAD R2] GPIO 27─┤ 13 │ 14 ├─ GND
  [KEYPAD R3] GPIO 22─┤ 15 │ 16 ├─ GPIO 23
              3.3V ─┤ 17 │ 18 ├─ GPIO 24
  [KEYPAD C1] GPIO  9─┤ 19 │ 20 ├─ GND
  [KEYPAD C3] GPIO 10─┤ 21 │ 22 ├─ GPIO 25
  [KEYPAD C2] GPIO 11─┤ 23 │ 24 ├─ GPIO  8
               GND ─┤ 25 │ 26 ├─ GPIO  7
           GPIO  0 ─┤ 27 │ 28 ├─ GPIO  1
  [KEYPAD R4] GPIO  5─┤ 29 │ 30 ├─ GND
           GPIO  6 ─┤ 31 │ 32 ├─ GPIO 12
           GPIO 13 ─┤ 33 │ 34 ├─ GND
           GPIO 19 ─┤ 35 │ 36 ├─ GPIO 16
           GPIO 26 ─┤ 37 │ 38 ├─ GPIO 20
               GND ─┤ 39 │ 40 ├─ GPIO 21
                    └──────────────────────────────────┘
```

> Pins marked with component labels are used by this project.

---

## Keypad (4×3 Matrix)

A standard **12-key membrane keypad** (4 rows × 3 columns).

### Key Layout

```
┌───┬───┬───┐
│ 1 │ 2 │ 3 │  ← Row 1
├───┼───┼───┤
│ 4 │ 5 │ 6 │  ← Row 2
├───┼───┼───┤
│ 7 │ 8 │ 9 │  ← Row 3
├───┼───┼───┤
│ * │ 0 │ # │  ← Row 4
└───┴───┴───┘
 C1   C2   C3
```

**Special Keys:**
- `*` — Clear / Cancel (return to idle)
- `#` — Confirm / Enter (confirm PC selection)

### GPIO Connections

| Keypad Wire | Role   | BCM GPIO | Physical Pin | Direction     | Pull       |
|-------------|--------|----------|--------------|---------------|------------|
| Row 1       | R1     | GPIO 17  | Pin 11       | OUTPUT        | —          |
| Row 2       | R2     | GPIO 27  | Pin 13       | OUTPUT        | —          |
| Row 3       | R3     | GPIO 22  | Pin 15       | OUTPUT        | —          |
| Row 4       | R4     | GPIO 5   | Pin 29       | OUTPUT        | —          |
| Col 1       | C1     | GPIO 9   | Pin 21       | INPUT         | PULL-DOWN  |
| Col 2       | C2     | GPIO 11  | Pin 23       | INPUT         | PULL-DOWN  |
| Col 3       | C3     | GPIO 10  | Pin 21*      | INPUT         | PULL-DOWN  |

> ⚠️ **Note:** GPIO 9 and GPIO 10 are both on the SPI bus. In this project, SPI is not used, so they are safe to use as GPIO.

### Physical Connector Pin Order

The keypad flat cable connector (pins numbered **1–7 left to right**):

```
Connector:  1       2       3       4       5       6       7
           C2      R1      C1      C3      R4      R3      R2
          GPIO11  GPIO17  GPIO9  GPIO10  GPIO5   GPIO22  GPIO27
```

### How the Scan Works

Rows are driven **HIGH one at a time**. Columns are read as inputs with internal pull-down resistors. When a key is pressed, it shorts its row wire to its column wire — the column reads HIGH while that row is active.

```
Example: User presses key "5" (Row 2, Col 2)

1. Drive GPIO 27 (R2) → HIGH
2. Read GPIO 9  (C1) → LOW  (no key)
3. Read GPIO 11 (C2) → HIGH ← key detected!
4. Drive GPIO 27 (R2) → LOW
   Return: KEY_MAP[1][1] = '5'
```

### Wiring Diagram

```
 Raspberry Pi                  4×3 Keypad
 ┌──────────┐                 ┌──────────┐
 │ GPIO 17  ├────────────────►│ Row 1    │
 │ GPIO 27  ├────────────────►│ Row 2    │
 │ GPIO 22  ├────────────────►│ Row 3    │
 │ GPIO  5  ├────────────────►│ Row 4    │
 │          │                 │          │
 │ GPIO  9  │◄────────────────┤ Col 1    │
 │ GPIO 11  │◄────────────────┤ Col 2    │
 │ GPIO 10  │◄────────────────┤ Col 3    │
 │ GND      ├──── (not used for keypad, cols use internal pull-down)
 └──────────┘                 └──────────┘
```

---

## Coin Slot (UCB Mini v4)

The **UCB Mini v4** is a multi-denomination coin acceptor that outputs **pulse signals** — one pulse per peso.

### Denomination → Pulse Count

| Coin  | Pulses |
|-------|--------|
| ₱1    | 1      |
| ₱5    | 5      |
| ₱10   | 10     |

### Signal Connection

The coin acceptor outputs a **5V signal** which must be stepped down to **3.3V** before connecting to the Raspberry Pi GPIO.

**Use a voltage divider (1kΩ + 2kΩ):**

```
UCB Mini v4 SIG ──┬── 1kΩ ──┬── GPIO 4 (BCM) ── Pin 7
                  │          │
                  │         2kΩ
                  │          │
                 GND        GND
```

| UCB Mini v4 Wire | Connects To                              |
|------------------|------------------------------------------|
| 12V (power)      | Relay NO contact (switched 12V from PSU) |
| GND              | External 12V PSU (−) **and** Pi GND      |
| SIG              | 1kΩ resistor → GPIO 4 (Pin 7)            |

> ⚠️ **NEVER connect the 5V SIG wire directly to GPIO.** The Raspberry Pi GPIO is **3.3V tolerant only**. Exceeding 3.3V will permanently damage the GPIO pin.

### GPIO Connection

| Signal         | BCM GPIO | Physical Pin | Direction | Notes                              |
|----------------|----------|--------------|-----------|------------------------------------|
| Coin SIG input | GPIO 4   | Pin 7        | INPUT     | Via voltage divider. No pull used. |

### Signal Polarity

The default signal polarity is **FALLING** (signal goes LOW on each pulse). This is typical when the board has an optocoupler that inverts the signal.

To detect which polarity your board uses, run:
```bash
sudo venv/bin/python test_coin_signal.py
```

To change polarity, set in `.env`:
```
COIN_EDGE=RISING   # if signal goes HIGH on pulse
COIN_EDGE=FALLING  # if signal goes LOW on pulse (default)
```

### Timing Parameters

| Parameter          | Default | Description                                          |
|--------------------|---------|------------------------------------------------------|
| `COIN_DEBOUNCE_MS` | `30`    | Ignore transitions faster than 30ms (noise filter)   |
| `COIN_PULSE_TIMEOUT` | `3.0s` | Finalize coin total after 3s of silence              |

### Wiring Diagram

```
 12V PSU (+) ──► Relay COM
                 Relay NO ──────────────────► UCB Mini v4 (12V)
 12V PSU (−) ──────────────────────────────► UCB Mini v4 (GND)
                                            │
 Pi GND ────────────────────────────────────┘ (common GND)

 UCB Mini v4 (SIG) ──── 1kΩ ──── GPIO 4 (Pin 7)
                                │
                               2kΩ
                                │
                               GND
```

---

## Relay Module (Coin Acceptor Power)

A relay module controlled by the Raspberry Pi is used to cut power to the coin acceptor when a PC is not selected. This prevents coins from being inserted when no session is pending.

### GPIO Connection

| Signal         | BCM GPIO | Physical Pin | Direction | Notes                       |
|----------------|----------|--------------|-----------|-----------------------------|
| Relay IN (CTL) | GPIO 6   | Pin 31       | OUTPUT    | HIGH = relay ON (coin powered) |

### Relay Module Connections

| Relay Pin | Connects To              |
|-----------|--------------------------|
| VCC       | Pi Pin 2 (5V)            |
| GND       | Pi Pin 14 (GND)          |
| IN        | GPIO 6 (Pin 31)          |
| COM       | 12V PSU (+)              |
| NO        | UCB Mini v4 (12V input)  |
| NC        | Not connected            |

### Logic

| GPIO 6 State | Relay State | Coin Acceptor |
|--------------|-------------|---------------|
| LOW (0)      | OFF (open)  | No power — coins rejected |
| HIGH (1)     | ON (closed) | Powered — coins accepted  |

The relay is set **HIGH** only when the hardware controller enters `AWAITING_COINS` state, and **LOW** on any other state transition.

### Wiring Diagram

```
 Raspberry Pi                Relay Module           UCB Mini v4
 ┌──────────┐               ┌──────────┐
 │ Pin 2    ├──── 5V ──────►│ VCC      │
 │ Pin 14   ├──── GND ─────►│ GND      │
 │ GPIO 6   ├──── CTL ─────►│ IN       │
 │ (Pin 31) │               │          │
 └──────────┘               │ COM ◄────┼──── 12V PSU (+)
                            │ NO  ─────┼────────────────► 12V
                            └──────────┘
```

---

## LCD Display (I2C 20×4)

A **20-column × 4-row** character LCD with an **I2C backpack** (PCF8574 I/O expander).

### I2C Connection

| LCD I2C Pin | Connects To          | Physical Pin |
|-------------|----------------------|--------------|
| VCC         | Pi 5V                | Pin 2 or 4   |
| GND         | Pi GND               | Pin 6, 9, 14, 20, 25, 30, 34, or 39 |
| SDA         | Pi SDA (GPIO 2)      | Pin 3        |
| SCL         | Pi SCL (GPIO 3)      | Pin 5        |

### I2C Configuration

| Setting           | Default | Description                             |
|-------------------|---------|-----------------------------------------|
| `LCD_I2C_ADDRESS` | `0x27`  | I2C address of the PCF8574 backpack     |
| `LCD_I2C_PORT`    | `1`     | I2C bus number (always `1` on RPi 2+)   |

> **Common addresses:** Most backpacks use `0x27`. Some use `0x3F`. If the display doesn't work, run `sudo i2cdetect -y 1` to find the actual address and update `.env`:
> ```
> LCD_I2C_ADDRESS=0x3F
> ```

### Enable I2C on Raspberry Pi

```bash
sudo raspi-config
# Interface Options → I2C → Enable
sudo reboot
```

Verify the display is detected:
```bash
sudo apt install -y i2c-tools
sudo i2cdetect -y 1
```
Expected output (address `0x27`):
```
     0  1  2  3  4  5  6  7  8  9  a  b  c  d  e  f
20: -- -- -- -- -- -- -- 27 -- -- -- -- -- -- -- --
```

### LCD Screen Reference

| Screen           | Line 1                  | Line 2              | Line 3              | Line 4     |
|------------------|-------------------------|---------------------|---------------------|------------|
| `idle()`         | `  PisoNet v1.0  `      | `Enter PC number`   | `then press #`      | *(blank)*  |
| `pc_entry(n)`    | `  PisoNet v1.0  `      | `PC Number: [n_]`   | `# confirm * cancel`| *(blank)*  |
| `pc_selected(n)` | `  PC XX selected`      | `Insert coins`      | `* to cancel`       | *(blank)*  |
| `inserting_coins`| `  PC XX selected`      | `Inserted: ₱N`      | `Time: N min`       | *(blank)*  |
| `coin_inserted`  | `  ₱N added!`           | `+N min`            | `Total: N min`      | *(blank)*  |
| `error(msg)`     | `     ERROR`            | `<message>`         | *(blank)*           | *(blank)*  |
| `offline(n)`     | `  PC XX`               | `  is offline`      | *(blank)*           | *(blank)*  |
| `timeout()`      | `  Timed out`           | `Returning...`      | *(blank)*           | *(blank)*  |

### Wiring Diagram

```
 Raspberry Pi                I2C LCD (PCF8574 backpack)
 ┌──────────┐               ┌──────────────┐
 │ Pin 2    ├──── 5V ──────►│ VCC          │
 │ Pin 6    ├──── GND ─────►│ GND          │
 │ Pin 3    ├──── SDA ─────►│ SDA          │  (GPIO 2)
 │ Pin 5    ├──── SCL ─────►│ SCL          │  (GPIO 3)
 └──────────┘               └──────────────┘
```

---

## Full Pin Summary

All GPIO pins used by this project at a glance:

| BCM GPIO | Physical Pin | Component       | Role             | Direction |
|----------|--------------|-----------------|------------------|-----------|
| GPIO 2   | Pin 3        | LCD I2C         | SDA (data)       | I2C       |
| GPIO 3   | Pin 5        | LCD I2C         | SCL (clock)      | I2C       |
| GPIO 4   | Pin 7        | Coin Slot SIG   | Pulse input      | INPUT     |
| GPIO 5   | Pin 29       | Keypad Row 4    | Drive row HIGH   | OUTPUT    |
| GPIO 6   | Pin 31       | Relay (coin pwr)| Relay control    | OUTPUT    |
| GPIO 9   | Pin 21       | Keypad Col 1    | Read column      | INPUT     |
| GPIO 10  | Pin 19       | Keypad Col 3    | Read column      | INPUT     |
| GPIO 11  | Pin 23       | Keypad Col 2    | Read column      | INPUT     |
| GPIO 17  | Pin 11       | Keypad Row 1    | Drive row HIGH   | OUTPUT    |
| GPIO 22  | Pin 15       | Keypad Row 3    | Drive row HIGH   | OUTPUT    |
| GPIO 27  | Pin 13       | Keypad Row 2    | Drive row HIGH   | OUTPUT    |

---

## Complete System Wiring Diagram

```
                     ┌─────────────────────────────────┐
                     │         Raspberry Pi             │
                     │                                  │
   12V PSU ─────────►│ 5V (Pin 2)  ──► Relay VCC       │
                     │ GND (Pin 6) ──► Relay GND        │
                     │ GPIO 6      ──► Relay IN         │
                     │                                  │
                     │ GPIO 4  ◄── 1kΩ ── Coin SIG      │
                     │                       │           │
                     │ GND ──────── 2kΩ ─────┘          │
                     │                                  │
                     │ GPIO 17 ──► Keypad Row 1         │
                     │ GPIO 27 ──► Keypad Row 2         │
                     │ GPIO 22 ──► Keypad Row 3         │
                     │ GPIO  5 ──► Keypad Row 4         │
                     │ GPIO  9 ◄── Keypad Col 1         │
                     │ GPIO 11 ◄── Keypad Col 2         │
                     │ GPIO 10 ◄── Keypad Col 3         │
                     │                                  │
                     │ Pin 3 (SDA) ──► LCD SDA          │
                     │ Pin 5 (SCL) ──► LCD SCL          │
                     │ 5V (Pin 4)  ──► LCD VCC          │
                     │ GND (Pin 6) ──► LCD GND          │
                     └─────────────────────────────────┘

   Relay Module:
   ┌──────────┐
   │ COM ◄────┼──── 12V PSU (+)
   │ NO  ─────┼────────────────► UCB Mini v4 (12V)
   └──────────┘

   UCB Mini v4:
   ┌──────────────┐
   │ 12V ◄────────┼──── Relay NO
   │ GND ◄────────┼──── 12V PSU (−) + Pi GND (common)
   │ SIG ─────────┼──── voltage divider ──► GPIO 4
   └──────────────┘
```

---

## Troubleshooting

### Keypad

| Symptom | Likely Cause | Fix |
|---------|--------------|-----|
| Entire row of keys dead (1,2,3 or 4,5,6 or 7,8,9 or *,0,#) | Row wire disconnected or wrong pin | Re-check the row GPIO connection for that row |
| Entire column dead (1,4,7,* or 2,5,8,0 or 3,6,9,#) | Col wire disconnected or wrong pin | Re-check the column GPIO connection |
| Keys register wrong characters | Row or column wires in wrong order | Run `test_keypad.py` and compare actual vs expected output |
| One key only works when another is held | Connector pins swapped (e.g. R4↔C3) | Swap wires or update `KEYPAD_ROWS`/`KEYPAD_COLS` in `.env` |
| No keys detected at all | GPIO not set up, RPi.GPIO not installed | Run `sudo pip install RPi.GPIO` and check `test_keypad.py` |

### Coin Slot

| Symptom | Likely Cause | Fix |
|---------|--------------|-----|
| No coin detected | Wrong edge polarity | Run `test_coin_signal.py`, update `COIN_EDGE` in `.env` |
| Coin detected but wrong amount | Debounce too aggressive | Lower `COIN_DEBOUNCE_MS` in `.env` |
| Multiple counts per coin | Debounce too loose | Raise `COIN_DEBOUNCE_MS` in `.env` |
| GPIO damage warning | 5V connected directly to GPIO | Add voltage divider — GPIO max is 3.3V |
| Coin accepted when no PC selected | Relay not cutting power | Check relay wiring and GPIO 6 signal |

### LCD

| Symptom | Likely Cause | Fix |
|---------|--------------|-----|
| Nothing displayed | Wrong I2C address | Run `sudo i2cdetect -y 1` and update `LCD_I2C_ADDRESS` in `.env` |
| I2C not found at all | I2C disabled or wiring issue | Run `sudo raspi-config` → Enable I2C, check SDA/SCL |
| Garbled characters | Backpack contrast too low | Adjust the potentiometer on the I2C backpack |
| ImportError on startup | RPLCD not installed | Run `sudo venv/bin/pip install RPLCD` |

### Diagnosing with Test Scripts

```bash
# Stop the server first
sudo systemctl stop pisonet

cd /home/shogunlee3214/Documents/pisonex/server

# Test keypad — press all 12 keys
sudo venv/bin/python test_keypad.py

# Test coin slot — insert coins, check pulses and polarity
sudo venv/bin/python test_coin_signal.py
```
