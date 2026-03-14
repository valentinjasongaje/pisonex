"""
Keypad diagnostic — run this on the Pi to verify all 12 keys are working.

    sudo systemctl stop pisonet
    cd /home/shogunlee3214/Documents/pisonex/server
    sudo venv/bin/python test_keypad.py

Press every key on the keypad one by one. Each press is printed with a
timestamp. A live checklist shows which keys have been detected and which
are still missing. Press Ctrl+C to stop and see the final summary.

Wiring used (BCM):
    Rows (OUTPUT) : 17, 27, 22, 10  → Row 1–4
    Cols (INPUT)  :  9, 11,  5      → Col 1–3
"""
import time
import sys

try:
    import RPi.GPIO as GPIO
except ImportError:
    sys.exit("RPi.GPIO not found — run this on the Raspberry Pi.")

# ── Pin config ────────────────────────────────────────────────────────────────
ROWS = [17, 27, 22, 10]   # R1 → R4  (driven HIGH one at a time)
COLS = [ 9, 11,  5]       # C1 → C3  (read with pull-down)

KEY_MAP = [
    ['1', '2', '3'],
    ['4', '5', '6'],
    ['7', '8', '9'],
    ['*', '0', '#'],
]

ALL_KEYS   = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '*', '0', '#']
SCAN_DELAY = 0.05   # seconds between scans

# ── GPIO setup ────────────────────────────────────────────────────────────────
GPIO.setmode(GPIO.BCM)
for pin in ROWS:
    GPIO.setup(pin, GPIO.OUT, initial=GPIO.LOW)
for pin in COLS:
    GPIO.setup(pin, GPIO.IN, pull_up_down=GPIO.PUD_DOWN)

print("=" * 52)
print("  Keypad Test — press every key, Ctrl+C to stop")
print("=" * 52)
print(f"  Rows (OUT) : {ROWS}")
print(f"  Cols (IN)  : {COLS}")
print()
print("  Layout:")
print("  ┌───┬───┬───┐")
print("  │ 1 │ 2 │ 3 │")
print("  ├───┼───┼───┤")
print("  │ 4 │ 5 │ 6 │")
print("  ├───┼───┼───┤")
print("  │ 7 │ 8 │ 9 │")
print("  ├───┼───┼───┤")
print("  │ * │ 0 │ # │")
print("  └───┴───┴───┘")
print()

# ── State ─────────────────────────────────────────────────────────────────────
detected   = set()
events     = []          # list of (timestamp, key)
start_time = time.monotonic()
last_key   = None


def checklist() -> str:
    """Return a one-line checklist of all 12 keys."""
    parts = []
    for k in ALL_KEYS:
        parts.append(f"[✓{k}]" if k in detected else f"[ {k}]")
    return "  " + " ".join(parts)


def scan_once() -> str | None:
    """Drive each row HIGH and read columns. Returns key string or None."""
    for r_idx, row_pin in enumerate(ROWS):
        GPIO.output(row_pin, GPIO.HIGH)
        for c_idx, col_pin in enumerate(COLS):
            if GPIO.input(col_pin) == GPIO.HIGH:
                GPIO.output(row_pin, GPIO.LOW)
                return KEY_MAP[r_idx][c_idx]
        GPIO.output(row_pin, GPIO.LOW)
    return None


# ── Main loop ─────────────────────────────────────────────────────────────────
print("  Waiting for key presses...\n")
print(checklist())
print()

try:
    while True:
        key = scan_once()

        if key and key != last_key:
            ts = time.monotonic() - start_time
            events.append((ts, key))
            detected.add(key)

            row = next(r for r, row in enumerate(KEY_MAP) if key in row)
            col = next(c for c, k in enumerate(KEY_MAP[row]) if k == key)
            row_pin = ROWS[row]
            col_pin = COLS[col]

            print(f"  [{ts:7.3f}s]  Key '{key}'  "
                  f"(Row{row+1}=BCM{row_pin}, Col{col+1}=BCM{col_pin})")
            print(checklist())
            print()

            if detected == set(ALL_KEYS):
                print("  ✓ All 12 keys detected!")
                break

        last_key = key
        time.sleep(SCAN_DELAY)

except KeyboardInterrupt:
    pass

finally:
    GPIO.cleanup()

    print()
    print("── Summary " + "─" * 42)
    print(f"  Keys detected  : {len(detected)} / 12")

    if detected:
        print(f"  Working keys   : {' '.join(sorted(detected, key=lambda k: ALL_KEYS.index(k)))}")

    missing = [k for k in ALL_KEYS if k not in detected]
    if missing:
        print(f"  Missing keys   : {' '.join(missing)}")
        print()

        # Group missing keys by column to help diagnose wiring issues
        col_issues = {}
        for k in missing:
            row = next(r for r, row in enumerate(KEY_MAP) if k in row)
            col = next(c for c, key in enumerate(KEY_MAP[row]) if key == k)
            col_pin = COLS[col]
            col_issues.setdefault(col_pin, []).append(k)

        row_issues = {}
        for k in missing:
            row = next(r for r, row in enumerate(KEY_MAP) if k in row)
            row_pin = ROWS[row]
            row_issues.setdefault(row_pin, []).append(k)

        print("  Possible wiring faults:")
        for pin, keys in col_issues.items():
            if len(keys) >= 2:
                print(f"    → BCM{pin} (Col) — affects keys: {' '.join(keys)}")
        for pin, keys in row_issues.items():
            if len(keys) >= 3:
                print(f"    → BCM{pin} (Row) — affects keys: {' '.join(keys)}")
    else:
        print()
        print("  All keys are working correctly.")

    print("─" * 52)
