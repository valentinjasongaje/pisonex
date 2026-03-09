"""
Quick diagnostic — run this on the Pi to see what signal BCM 4 is getting.

    sudo systemctl stop pisonet
    cd /home/shogunlee3214/Documents/pisonex/server
    sudo venv/bin/python test_coin_signal.py

Insert a coin while it is running.  It will print every transition it sees
(RISING and FALLING) with a timestamp, plus the resting level of the pin.
Press Ctrl+C to stop.

Uses 1 ms polling — no sysfs interrupt registration, so no
"Failed to add edge detection" errors.
"""
import time
import sys

try:
    import RPi.GPIO as GPIO
except ImportError:
    sys.exit("RPi.GPIO not found — run this on the Raspberry Pi.")

COIN_PIN  = 4
RELAY_PIN = 6
_events: list[tuple[float, str]] = []
_start = time.monotonic()

GPIO.setmode(GPIO.BCM)
GPIO.setup(RELAY_PIN, GPIO.OUT, initial=GPIO.LOW)
GPIO.setup(COIN_PIN,  GPIO.IN,  pull_up_down=GPIO.PUD_OFF)

# Power the coin acceptor via relay
GPIO.output(RELAY_PIN, GPIO.HIGH)
print(f"Relay BCM {RELAY_PIN} → HIGH (coin acceptor powered)\n")

time.sleep(0.1)   # let acceptor stabilise before sampling resting level
last_level = GPIO.input(COIN_PIN)
_resting_level = last_level

print(f"Monitoring BCM {COIN_PIN} — insert coins now.  Ctrl+C to stop.\n")
print(f"  Resting level right now: {last_level}")
print(f"  (0 = LOW, 1 = HIGH)\n")

try:
    while True:
        level = GPIO.input(COIN_PIN)

        if level != last_level:
            ts   = time.monotonic() - _start
            edge = "RISING  ↑" if level else "FALLING ↓"
            _events.append((ts, edge))
            print(f"  [{ts:7.3f}s]  BCM {COIN_PIN}  {edge}  (level={level})")
            last_level = level

        time.sleep(0.001)

except KeyboardInterrupt:
    pass
finally:
    GPIO.cleanup()
    print(f"\n── Summary ─────────────────────────────")
    print(f"  Total transitions detected: {len(_events)}")
    if _events:
        rising  = sum(1 for _, e in _events if "RISING"  in e)
        falling = sum(1 for _, e in _events if "FALLING" in e)
        print(f"  RISING  (↑) : {rising}")
        print(f"  FALLING (↓) : {falling}")
        # Correct recommendation: resting HIGH → pulses are FALLING edges
        #                         resting LOW  → pulses are RISING  edges
        if _resting_level == 1:
            print("\n  → Edge to use: FALLING  (pin rests HIGH, coin pulse goes LOW)")
            print("     COIN_EDGE=FALLING  ← already the default, no change needed")
        else:
            print("\n  → Edge to use: RISING  (pin rests LOW, coin pulse goes HIGH)")
            print("     Set COIN_EDGE=RISING in your .env")
    else:
        print("\n  !! No signal detected at all.")
        print("     Check: voltage divider wiring, common GND, custom board output level.")
