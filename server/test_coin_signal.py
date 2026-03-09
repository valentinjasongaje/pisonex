"""
Quick diagnostic — run this on the Pi to see what signal BCM 4 is getting.

    sudo systemctl stop pisonet
    cd /home/pi/pisonet/server
    sudo venv/bin/python test_coin_signal.py

Insert a coin while it is running.  It will print every edge it sees
(RISING and FALLING) with a timestamp, plus the resting level of the pin.
Press Ctrl+C to stop.
"""
import time
import sys

try:
    import RPi.GPIO as GPIO
except ImportError:
    sys.exit("RPi.GPIO not found — run this on the Raspberry Pi.")

COIN_PIN = 4
_events: list[tuple[float, str]] = []
_start = time.monotonic()


def _on_edge(channel):
    level = GPIO.input(channel)
    edge  = "RISING  ↑" if level else "FALLING ↓"
    ts    = time.monotonic() - _start
    _events.append((ts, edge))
    print(f"  [{ts:7.3f}s]  BCM {channel}  {edge}  (level={level})")


GPIO.setmode(GPIO.BCM)
GPIO.setup(COIN_PIN, GPIO.IN, pull_up_down=GPIO.PUD_OFF)

# Clear any stale event detection from a previous run before adding ours
try:
    GPIO.remove_event_detect(COIN_PIN)
except Exception:
    pass

# Use BOTH edges and no pull so we see the raw signal from the custom board.
# If you see no events at all → signal is not reaching the pin (wiring/level issue).
# If events fire on FALLING → custom board inverts (optocoupler); set COIN_EDGE=FALLING.
# If events fire on RISING  → signal is active-HIGH;              set COIN_EDGE=RISING.
GPIO.add_event_detect(COIN_PIN, GPIO.BOTH, callback=_on_edge, bouncetime=5)

print(f"Monitoring BCM {COIN_PIN} — insert coins now.  Ctrl+C to stop.\n")
print(f"  Resting level right now: {GPIO.input(COIN_PIN)}")
print(f"  (0 = LOW, 1 = HIGH)\n")

try:
    while True:
        time.sleep(0.1)
except KeyboardInterrupt:
    pass
finally:
    GPIO.cleanup()
    print(f"\n── Summary ─────────────────────────────")
    print(f"  Total edges detected: {len(_events)}")
    if _events:
        rising  = sum(1 for _, e in _events if "RISING"  in e)
        falling = sum(1 for _, e in _events if "FALLING" in e)
        print(f"  RISING  (↑) : {rising}")
        print(f"  FALLING (↓) : {falling}")
        if falling > rising:
            print("\n  → Edge to use: FALLING (custom board inverts signal)")
            print("     Set COIN_EDGE=FALLING in your .env")
        else:
            print("\n  → Edge to use: RISING")
            print("     Set COIN_EDGE=RISING in your .env  (already the default)")
    else:
        print("\n  !! No signal detected at all.")
        print("     Check: voltage divider wiring, common GND, custom board output level.")
