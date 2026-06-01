# Pisonex Setup Guide — Orange Pi One

A complete walkthrough for setting up your Pisonex café management server
on an Orange Pi One. From parts in a box to a working dashboard in about
30 minutes.

> **Difficulty:** Beginner-friendly. Knowing which end of a screwdriver
> is which is enough.
> **Time:** ~30 minutes total (flash 10 min + boot 2 min + wiring 15 min).
> **What you'll have at the end:** A running Pisonex server you can
> log into from any browser on your local network, ready to accept
> coin-based timer sessions for your PCs.

---

## Table of contents

1. [What you'll need](#what-youll-need)
2. [Part 1 — Flash the SD card](#part-1--flash-the-sd-card)
3. [Part 2 — First boot](#part-2--first-boot)
4. [Part 3 — Wire the hardware](#part-3--wire-the-hardware)
5. [Part 4 — Configure the dashboard](#part-4--configure-the-dashboard)
6. [Part 5 — Install Windows clients](#part-5--install-windows-clients-on-each-pc)
7. [Testing the system](#testing-the-system)
8. [Troubleshooting](#troubleshooting)

---

## What you'll need

### Hardware

| Item | Notes | Approximate price (PHP) |
|---|---|---|
| Orange Pi One single-board computer | The brain of your café. H3 quad-core ARM. | ₱1,200 – 1,500 |
| MicroSD card, 32 GB Class 10 minimum | Avoid generic no-name brands — they fail under load. SanDisk Ultra or Samsung Evo recommended. | ₱350 – 600 |
| Power supply, 5V 2A, 4.0×1.7 mm barrel jack | Do **not** use a phone charger via the USB-OTG port — it'll undervolt and crash. | ₱200 – 400 |
| Ethernet cable, CAT5e or better | Length depends on your shop layout. | ₱100 – 300 |
| Coin acceptor, 12V multi-coin (e.g. CH-926) | The unit that physically accepts pesos. Comes with its own 12V power adapter. | ₱1,500 – 2,500 |
| 12V power adapter for the coin acceptor | Often included with the acceptor; if not, 12V 1A is enough. | ₱200 – 400 |
| Relay module, 5V single-channel | Lets the Pi power-cycle the coin acceptor (optional but recommended). | ₱60 – 150 |
| Jumper wires, female-to-female | At least 5; a 40-wire pack is cheap and useful. | ₱50 – 150 |
| Project enclosure / box (optional) | A small plastic project box keeps everything tidy and safe. | ₱150 – 500 |

**Optional add-ons** (covered briefly in [Part 3](#optional-keypad--lcd-standalone-unit)):

- 3×4 matrix keypad + 16×2 LCD with I2C backpack → for a "standalone"
  PC-selection unit so customers can pick which PC they're paying for.

### Software

- A computer (Windows, Mac, or Linux) with internet access
- A MicroSD card reader (USB SD reader or built-in laptop slot)
- [balenaEtcher](https://etcher.balena.io/) — free, ~150 MB download
- The Pisonex image file (`pisonex-v1.0.zip` or newer) — provided by us

---

## Part 1 — Flash the SD card

This step writes the Pisonex operating system + software onto your
MicroSD card. It takes ~10 minutes.

### 1.1 Install Etcher

Go to [etcher.balena.io](https://etcher.balena.io/) and download the
installer for your operating system. Run it. Click Next a few times.
That's it.

### 1.2 Insert the MicroSD card into your computer

Plug your USB card reader into a USB port, then insert the MicroSD card
into the reader. (If your laptop has a built-in SD slot you can use that
instead — most need an SD adapter, which is usually included with the
MicroSD card.)

If Windows asks **"You need to format the disk in drive X: before you
can use it"** — click **Cancel**. Do NOT format the card. (This message
appears because the card has a Linux filesystem on it, which Windows
can't read. That's normal.)

### 1.3 Flash the image

Open Etcher. You'll see three buttons:

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│   ┌──────────────┐  ┌──────────────┐  ┌──────────────┐   │
│   │ Flash from   │  │              │  │              │   │
│   │     file     │  │   Select     │  │    Flash!    │   │
│   │              │  │   target     │  │              │   │
│   └──────────────┘  └──────────────┘  └──────────────┘   │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

1. Click **Flash from file** → browse to `pisonex-v1.0.zip` (or whatever
   version we provided) → click Open. Etcher accepts the `.zip` directly;
   no need to unzip first.
2. Click **Select target** → pick your MicroSD card from the list. It
   usually shows up as "Generic SD Card 32 GB" or similar. **Double-check
   the size matches your card** — if you accidentally pick an internal
   drive, Etcher will refuse, but it's worth a glance.
3. Click **Flash!** — confirm any prompts, enter your computer password
   if asked.
4. Wait ~10 minutes. Etcher's progress bar shows flashing then a
   verification step. Both must complete.

When you see **"Flash Complete!"** with a green checkmark, you're done.
Eject the SD card safely (Windows: right-click the drive → Eject).
Remove it from your reader.

---

## Part 2 — First boot

### 2.1 Insert SD into the Orange Pi

Find the MicroSD slot on the underside of the Orange Pi One board (it's
spring-loaded — push in to insert, push again to eject). Slide the card
in until it clicks.

### 2.2 Connect Ethernet

Plug your Ethernet cable into the Orange Pi's RJ45 port (the silver
square one) and the other end into your home/shop router or a network
switch. **Wi-Fi is not configured** on the Pisonex image — Ethernet only.
This is intentional (more reliable, no SSID/password to remember).

### 2.3 Connect power

Plug the 5V 2A barrel-jack power supply into the round black socket on
the Orange Pi. The green power LED should light up immediately. The red
"activity" LED will blink as the system boots.

> ⚠️ **Do not use a micro-USB phone charger.** The Orange Pi One *has* a
> micro-USB port but it's for data only — it cannot supply enough current
> to run the board reliably. Always use the 5V 2A barrel-jack supply.

### 2.4 Wait 90 seconds

First boot does extra work — it generates unique security keys for your
device, creates the database, and starts the Pisonex server. The first
boot is slower than subsequent boots; later boots take ~30 seconds.

### 2.5 Find your device's IP address

Open a web browser on your computer (any device on the same network
works — laptop, phone, tablet) and log into your router's admin page.
The router admin page address is usually one of:

- `http://192.168.1.1`
- `http://192.168.0.1`
- `http://192.168.254.254`

(Check the sticker on the back of your router if none of these work.)

Look for a section called **DHCP Clients**, **Connected Devices**, or
**LAN Status**. You'll see a list of devices currently connected. The
Orange Pi shows up as:

- **Name:** `orangepione`
- **IP address:** something like `192.168.1.27` or `192.168.0.105`

**Write down this IP address** — you'll use it from now on.

### 2.6 Open the dashboard

In your browser, go to:

```
http://192.168.1.27/dashboard
```

…replacing `192.168.1.27` with your actual IP from step 2.5.

You should see a login screen with the Pisonex logo. If you do —
**congrats, the server is alive.** If not, jump to
[Troubleshooting](#troubleshooting-the-dashboard-doesnt-load).

### 2.7 First login

| | |
|---|---|
| Username | `admin` |
| Password | `admin123` |

After logging in you'll be on the empty dashboard ("No PCs registered").
Don't worry about that yet — we'll wire the hardware and register PCs
next.

> 🔒 **Change the password immediately.** Go to Settings → Security
> → Admin Password → set a strong password. Anyone on your network can
> reach the dashboard, so don't leave it at `admin123`.

---

## Part 3 — Wire the hardware

This connects the coin acceptor to the Orange Pi so the server knows
when a peso is inserted.

### 3.1 Power down the Pi first

**Before touching any wires, power off the Orange Pi.** Unplug the
barrel-jack adapter. Wait 5 seconds for the LEDs to go fully dark.
Connecting wires to a live board can damage the GPIO pins permanently.

### 3.2 Orange Pi One GPIO header reference

The Orange Pi One has a 40-pin header along the long edge of the board.
With the board oriented so the **USB ports face you and the Ethernet
port is on the right**, the header looks like this:

```
                            ┌────────────────────────┐
                       3.3V │  1   2 │ 5V
        ★ COIN SIGNAL  PA12 │  3   4 │ 5V
        ★ RELAY CTRL   PA11 │  5   6 │ GND ★ COIN GND
                        PA6 │  7   8 │ PG6
                        GND │  9  10 │ PG7
                        PA1 │ 11  12 │ PA7
                        PA0 │ 13  14 │ GND
                        PA3 │ 15  16 │ PA19
                       3.3V │ 17  18 │ PA18
                        PC0 │ 19  20 │ GND
                        PC1 │ 21  22 │ PA2
                        PC2 │ 23  24 │ PC3
                        GND │ 25  26 │ PA21
                        PA7 │ 27  28 │ PA18
                        PA8 │ 29  30 │ GND
                        PA9 │ 31  32 │ PG8
                       PA10 │ 33  34 │ GND
                        PA20│ 35  36 │ PG9
                        PA21│ 37  38 │ PA13
                        GND │ 39  40 │ PA14
                            └────────────────────────┘
```

The pins marked with ★ are the ones you'll connect to. **Only three
pins on the entire header matter for basic operation:**

| Pin # | Name | Purpose |
|---|---|---|
| **Pin 3** | PA12 (default `COIN_PIN=12`) | The coin signal — a pulse arrives here each time a coin is accepted |
| **Pin 5** | PA11 (default `RELAY_PIN=11`) | Controls the relay that powers the coin acceptor on/off |
| **Pin 6** | GND | Common ground between the Pi and the coin acceptor circuit |

### 3.3 Coin acceptor wiring (basic, no relay)

This is the simplest configuration: the coin acceptor is always powered
on, and the Pi just listens for coin pulses on Pin 3.

**Coin acceptor wires** (CH-926 colour code — yours may differ; check
your model's manual):

| Wire color | Function | Connect to |
|---|---|---|
| Red | +12V power | + terminal of 12V power supply |
| Black | Ground | – terminal of 12V power supply **AND** Orange Pi Pin 6 (GND) |
| White (or "COIN" / "PULSE") | Coin signal output | Orange Pi **Pin 3** |
| Other wires (Counter, etc.) | Not used | Leave disconnected |

```
   ┌────────────────────────┐
   │     COIN ACCEPTOR      │
   │  ┌──────────────────┐  │       Black ──┬─── – of 12V supply
   │  │                  │  │               │
   │  │   CH-926 etc.    │  │               └─── Orange Pi GND (Pin 6)
   │  └──────────────────┘  │
   │   Red    Black  White  │       White ────── Orange Pi Pin 3 (PA12)
   └────┼──────┼─────┼──────┘
        │      │     │              Red ──────── + of 12V supply
        │      │     │
        ▼      ▼     ▼
       +12V   GND  SIGNAL
```

**Critical**: the coin acceptor's black wire MUST also be connected to
the Orange Pi's GND. Without this common ground, the signal pulse won't
register reliably.

### 3.4 Coin acceptor wiring (with relay — recommended)

The relay lets the Pi cut power to the coin acceptor between sessions.
This stops a customer from inserting coins when the server doesn't
expect them (which would create "orphan" credits with no PC selected).

You'll need a 5V single-channel relay module (the cheap blue ones from
any electronics store work fine).

**Relay module wiring:**

| Relay pin | Connect to |
|---|---|
| VCC | Orange Pi 5V (Pin 2 or Pin 4) |
| GND | Orange Pi GND (Pin 6, 9, 14, 20, etc.) |
| IN | Orange Pi **Pin 5** (PA11) |
| COM | + of 12V power supply |
| NO ("Normally Open") | Coin acceptor's **red** wire (+12V) |
| NC ("Normally Closed") | Not used |

```
                        ┌─── VCC ─── Orange Pi 5V  (Pin 2)
   ┌─────────────────┐  │
   │                 │  ├─── GND ─── Orange Pi GND (Pin 6)
   │  RELAY MODULE   │  │
   │                 │  ├─── IN  ─── Orange Pi Pin 5 (PA11)
   │   ┌─── COM ─────┤
   │   ├─── NO  ─────┤   COM ──── + of 12V supply
   │   └─── NC       │   NO  ──── Coin acceptor RED wire
   └─────────────────┘

   Coin acceptor BLACK wire → – of 12V supply AND Orange Pi GND
   Coin acceptor WHITE wire → Orange Pi Pin 3 (PA12)
```

When the Pisonex server wants to accept coins, it sends a HIGH signal
to Pin 5, the relay closes the COM↔NO contacts, 12V flows to the coin
acceptor, and it powers up ready to receive coins. When the server
closes the session (customer presses Done), Pin 5 goes LOW, relay opens,
acceptor powers down.

### 3.5 Sanity check before powering on

Before plugging anything back in, run this mental checklist:

- [ ] Coin acceptor's **black** wire goes to **both** the 12V supply's
      negative terminal **and** the Orange Pi's GND
- [ ] Coin acceptor's **red** wire goes to the **NO** terminal of the
      relay (or directly to +12V if not using a relay)
- [ ] Coin acceptor's **white** (signal) wire goes to Orange Pi **Pin 3**
- [ ] Relay's VCC, GND, IN go to 5V, GND, and Pin 5 on the Orange Pi
- [ ] The 12V supply is plugged into mains BUT not yet switched on
- [ ] The Orange Pi's 5V supply is plugged in BUT not yet switched on
- [ ] No bare wires touching each other anywhere

### 3.6 Power back on

1. Plug in / switch on the **12V supply first** (for the coin acceptor)
2. Then plug in the **Orange Pi's 5V supply**
3. Wait ~30 seconds for the Pi to boot
4. Browse to the dashboard (`http://<your-pi-ip>/dashboard`) and log in

### Optional: keypad + LCD (standalone unit)

If you're building the optional "standalone keypad unit" — a small box
with a 3×4 keypad and a 16×2 LCD that customers walk up to and key in
which PC they're paying for — additional pins are used:

- **Keypad rows** (output → input scan): PA17, PA27, PA22, PA5
- **Keypad cols** (input with pull-up): PA9, PA11, PA10
- **LCD**: I²C bus (uses Pin 3 SDA and Pin 5 SCL — conflicts with the
  coin/relay pins above, so the standalone unit uses a different setup)

If you're going with the standalone unit, see **Settings → Coin Slot
Hardware** on the dashboard — every GPIO pin is configurable from there.
You can also run `gpio readall` on the device console to see your
board's exact pin mapping.

---

## Part 4 — Configure the dashboard

Back in the browser, logged into the dashboard.

### 4.1 Settings → Branch

Set the **Branch Name** — this is how your shop will be identified on
the customer portal at pisonex.com (e.g. "Main Branch", "Tomas Morato",
"Branch 2"). Just a label; pick whatever makes sense to you.

### 4.2 Settings → General

Two important values here:

- **Idle auto-shutdown (minutes)** — how long a PC sits idle on the lock
  screen before the server tells it to shut down. Default 5 min. Set to
  0 to disable.
- **Coin slot idle timeout (seconds)** — how long the coin slot stays
  open with no coins arriving before it auto-closes. Default 30 s.

### 4.3 Settings → Coin Slot Hardware

Defaults match the wiring in Part 3:

- **Coin pin:** 12
- **Relay pin:** 11
- **Coin edge:** FALLING (for optocoupler boards like CH-926). If your
  coin acceptor pulses HIGH instead, change to RISING.
- **Coin debounce (ms):** 30
- **Coin pulse timeout (s):** 3.0

If you're getting double-counts (one coin → two pulses registered),
raise the debounce to 50–80 ms. If you're missing coins, you might
have the wrong edge polarity — try toggling FALLING/RISING.

### 4.4 Settings → Security

- **Admin Password** — change from `admin123` to something strong
- **Client API Key** — optional. If you enable this, all client PCs
  must be configured with the same key. Recommended for security; you
  can leave disabled for initial testing.

### 4.5 PC Management → Rate Settings

Tell the server how to convert pesos to time:

- **Default Rate:** Pesos per second (e.g. 5 pesos = 1800 seconds = 30 min)

This is the default; you can override per-PC later if some machines are
"premium" and cost more.

---

## Part 5 — Install Windows clients on each PC

Each customer PC in your café needs the Pisonex Client installed. The
client is a separate Windows installer we provide alongside the server
image.

### 5.1 Install on each PC

1. Copy `PisoNetClient-Setup.exe` to the PC (USB drive, network share,
   whatever's easiest)
2. Right-click → **Run as administrator**
3. Click through the installer (Next, Next, Install)
4. When the **Setup Dialog** appears on first launch, enter:
   - **Server URL:** `http://<your-pi-ip>` (e.g. `http://192.168.1.27`)
   - **PC Number:** a unique number for this PC (1, 2, 3, …)
5. Click Save

The lock screen will appear immediately. The PC is now waiting for a
coin / admin time-grant before it'll unlock.

### 5.2 Verify the PC shows up on the dashboard

Back on the dashboard's main page, the new PC should appear within a
few seconds as a card showing "PC 01 — Locked".

Repeat for each PC in your café (PC 2, PC 3, etc.). Each one needs a
**unique PC number** — don't reuse.

---

## Testing the system

### Test 1 — Coin acceptance

1. On the dashboard, click PC 01 → click **"Unlock"**. The PC should
   unlock immediately, with whatever default time you set (e.g. 30 min).
2. From the PC client side, the lock screen disappears, the timer
   overlay appears in the corner counting down.
3. Click **+ Add Time** on the timer overlay. The coin slot opens
   (the relay clicks, the coin acceptor's LED comes on).
4. Drop a peso into the coin slot. You should see the "Receiving Coins"
   card update with **₱5 inserted** (or whatever your peso landed as).
5. Click **Done inserting Coins**. The timer overlay updates with
   the new total time.

If all four steps work, **your hardware setup is complete.**

### Test 2 — Lock screen returns when time runs out

1. On the dashboard, click PC 01 → **Set Time → 10 seconds**.
2. Watch the timer overlay count down to 00:00.
3. The lock screen should reappear immediately when time hits zero.

### Test 3 — Dashboard is reachable from your phone

Any device on the same Wi-Fi as the Orange Pi can reach the dashboard.
Open your phone's browser, go to `http://<your-pi-ip>/dashboard`, log
in. This lets you manage the café from anywhere in the shop.

---

## Troubleshooting

### Troubleshooting: the dashboard doesn't load

**Symptom:** Browser shows "Connection refused" / "Site can't be
reached" when you go to `http://<ip>/dashboard`.

**Steps:**

1. **Confirm the Orange Pi is on.** The green power LED should be solid
   on, the red activity LED should blink occasionally.
2. **Confirm the IP address.** Routers sometimes change DHCP assignments
   after a restart. Re-check your router's client list — the device
   `orangepione` might have a different IP now.
3. **Confirm Ethernet is connected.** Both ends of the cable should
   click in firmly. Check the link light on the Pi's Ethernet port —
   it should be lit (usually green or amber).
4. **Try waiting longer.** First boot takes up to 90 seconds. After a
   power cycle, wait 30 seconds before trying.
5. **Try a different browser.** Some browsers cache aggressively. Use
   Incognito / Private mode.
6. **Re-flash the SD card.** Rare, but a corrupted flash will hang the
   boot. Re-flash from `pisonex-v1.0.zip` and try again.

### Troubleshooting: coins aren't being detected

**Symptom:** You drop a peso, the coin acceptor's LED flashes (good —
it accepted the coin) but the dashboard doesn't update.

**Steps:**

1. **Check the signal wire.** It must go to Pin 3 (PA12), not any other
   pin. Pin 1 (3.3V) is right next to it — easy to misread.
2. **Check the common ground.** The coin acceptor's black wire MUST
   connect to BOTH the 12V supply's negative AND the Orange Pi's GND.
   Without the GND link to the Pi, the signal voltage has no reference.
3. **Try toggling the edge polarity.** Go to Settings → Coin Slot
   Hardware → Coin edge, change FALLING ↔ RISING, save. If your
   acceptor pulses the opposite way to what we configured by default,
   this will fix it.
4. **Check the relay (if used).** When the coin slot is open from the
   dashboard, the relay should audibly click and the acceptor's LED
   should be on. If not, the relay isn't getting a control signal —
   check the IN wire to Pin 5.

### Troubleshooting: I forgot the admin password

**Symptom:** You changed the admin password and forgot it.

**Solution:** Unfortunately, with v1.0 there's no self-service password
reset (and SSH is disabled for security). The recovery is to re-flash
the SD card from the original `pisonex-v1.0.zip`, which resets
everything to defaults. You'll lose configured PCs, branch name, etc.
and have to redo first-time setup. For this reason, **write down the
admin password somewhere safe** when you change it.

### Troubleshooting: a customer PC keeps disconnecting

**Symptom:** The PC card on the dashboard says "Offline" intermittently.

**Steps:**

1. **Check the Ethernet/Wi-Fi between the Pi and the PC.** If it's
   Wi-Fi, signal strength may be poor. Wired is more reliable.
2. **Check the client's heartbeat in the Windows event log** (Event
   Viewer → Applications → Pisonex). Heartbeat failures here mean the
   client can't reach the server.
3. **Ping test.** From the customer PC's command prompt:
   `ping 192.168.1.27` (replace with your Pi's IP). Should reply within
   a few milliseconds; if it times out, the network is the issue.

### Troubleshooting: I need to update to a newer Pisonex version

When we release a new version (`pisonex-v1.1.zip`, etc.), the update
process is:

1. Download the new `.zip`
2. Power off the Orange Pi
3. Eject the SD card
4. Re-flash with Etcher (same process as Part 1)
5. Reinsert and boot
6. Reconfigure from scratch — branch name, password, PCs, etc.

> **All your customer data will be wiped.** This includes registered
> PCs, member accounts, transaction history. Make sure you've exported
> anything important (e.g. download the daily earnings report from
> Dashboard → Reports → Export) before flashing.

---

## Quick reference card

Stick this on the wall next to your Orange Pi:

```
┌────────────────────────────────────────────────────┐
│  PISONEX QUICK REFERENCE                           │
│                                                    │
│  Server IP:        ________________ (from router)  │
│  Dashboard URL:    http://_____/dashboard          │
│  Admin password:   ________________ (CHANGE ME)    │
│                                                    │
│  Coin pin:         Orange Pi pin 3 (PA12)          │
│  Relay pin:        Orange Pi pin 5 (PA11)          │
│  Ground:           Orange Pi pin 6                 │
│                                                    │
│  Boot time:        ~90 sec (first time)            │
│                    ~30 sec (subsequent)            │
│                                                    │
│  If the dashboard won't load:                      │
│    1. Wait 90 seconds                              │
│    2. Re-check IP via router                       │
│    3. Re-flash the SD card                         │
└────────────────────────────────────────────────────┘
```

---

## Need help?

If you get stuck on any step:

- Re-read the [Troubleshooting](#troubleshooting) section
- Check [pisonex.com/support](https://pisonex.com/support) (if you have
  internet access from the Pi's network)
- Email us with: your device's IP, the dashboard's error message (if
  any), and a photo of your wiring. We'll get you running.

Good luck and welcome to Pisonex.
