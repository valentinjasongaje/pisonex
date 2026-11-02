# Release & Imaging Guide

How to turn a working development Orange Pi into a customer-ready,
flashable SD-card image with the source code obscured and remote access
disabled.

> **Audience:** the maintainer of this repo (you).
> **Frequency:** every time you ship a new version to customers.
> **Time:** ~45 min hands-on per release, plus compute waits.

---

## Contents

1. [What we're producing](#what-were-producing)
2. [Security model & honest tradeoffs](#security-model--honest-tradeoffs)
3. [Pre-release checklist](#pre-release-checklist)
4. [The 4-phase release pipeline](#the-4-phase-release-pipeline)
   - Phase 1 — Snapshot the dev SD card
   - Phase 2 — Sanitize on the live Orange Pi
   - Phase 3 — Image the SD card on your laptop
   - Phase 4 — Restore dev environment
5. [Smoke testing the image](#smoke-testing-the-image)
6. [What the customer experiences](#what-the-customer-experiences)
7. [v1.1 and beyond — releasing updates](#v11-and-beyond--releasing-updates)
8. [Recovery procedures](#recovery-procedures)
9. [Decisions log / FAQ](#decisions-log--faq)

---

## What we're producing

A single file, `pisonex-opi-one-v<version>.img.gz` (~620 MB download). It is
**shrunk**: Etcher writes only ~2.4 GB instead of the whole card (≈6× faster to
flash, fits 4–8 GB cards), and the card grows to its full size on first boot.

The customer flow with that file:

1. Download the `.img.gz`
2. Open [balenaEtcher](https://etcher.balena.io/), pick the `.img.gz` and a blank
   SD card (8 GB or larger), click Flash
3. Pop the card into an Orange Pi, connect Ethernet, power on
4. Wait ~90 seconds (first-boot regenerates secrets, machine-id, etc.)
5. Browse to `http://pisonex.local/dashboard` (or `http://<dhcp-assigned-ip>/dashboard`)
6. Log in as `admin` / `admin123`, change the password (sidebar → **Account**),
   set the branch name, activate the license

That's it. No SSH, no terminal, no customer access to source.

---

## Security model & honest tradeoffs

The release image is designed to keep your source out of reach of a
**casual customer** — a café owner who might browse the SD card filesystem
or peek at logs. It is **not** hardened against a determined reverse-engineer
with physical access.

| Threat | Defence | Effective? |
|---|---|---|
| Customer SSH'ing in to read code | SSH service stopped + disabled + masked | ✅ stops 100% |
| Customer mounting SD card on Windows / Linux laptop | `.py` source deleted; only `.pyc` bytecode remains | ⚠️ raises bar significantly (Python 3.11+ decompilers are unreliable) but determined attacker can recover most logic |
| Customer reading commit history & dev notes | `.git`, `*.md`, `tests/`, `.env.example` removed | ✅ |
| Customer reading the client / Windows server source | `/opt/pisonex` is a full repo clone — everything except `server-orangepi/`, `venv/` and `VERSION` is deleted (`client/`, `server/`, `server-windows/`, `tools/`, `docs/`, `deploy/`) | ✅ |
| Customer extracting your secrets | `SECRET_KEY` / `LICENSE_HMAC_SECRET` blanked; first boot generates fresh ones per device (blank, placeholder or < 32 chars is always regenerated). Dev license, DB, backups, wallpapers, Wi-Fi passwords removed | ✅ each device has unique secrets |
| Customer cloning the SD to make pirate copies | Nothing prevents this | ❌ accepted risk |
| Customer reading `/etc/shadow` to extract root password hash | Strong 32-char random password, different per build | ⚠️ hash is recoverable but uncrackable in reasonable time |
| Reverse-engineer with Nuitka-style decompile expertise | None | ❌ out of scope for v1 |

**The choice we made** for v1: "fully sealed" — no remote updates, no
support tunnel, no in-the-field debugging. Every update means the
customer downloads a new image and re-flashes. This is the simplest
and safest model; we can revisit later if customer support pain grows.

---

## Pre-release checklist

Run through this before kicking off any release.

- [ ] All target code is committed and pushed to `origin/main`, and
      `python tools/smoke_test.py server-orangepi` passes on your PC
- [ ] You've tested the feature/fix manually on the dev Orange Pi
- [ ] The dev Orange Pi is on the right commit:
      `cd /opt/pisonex && git pull && git log -1 --oneline` matches what you
      want to ship, and `cat /opt/pisonex/VERSION` is the version you expect
- [ ] You re-ran the installer after the pull, so packages and the systemd unit
      are current: `sudo bash /opt/pisonex/server-orangepi/deploy/install.sh`
- [ ] `pip check` reports no broken dependencies
- [ ] `journalctl -u pisonex --since '1 day ago' | grep -iE 'error|traceback'`
      returns nothing
- [ ] You have a blank SD card for smoke testing (different physical card
      from the master)
- [ ] You have a password manager open and ready to capture the rotated
      root password (it's printed exactly once)
- [ ] You've allocated **~45 minutes** of uninterrupted time

---

## The 4-phase release pipeline

### Phase 1 — Snapshot the dev SD card

**Do this BEFORE running the sanitize script.** Once you sanitize, the
dev environment on that card is gone. The snapshot is your safety net
and your starting point for the *next* release.

1. Power off the dev Orange Pi: `ssh root@<dev-pi-ip> 'shutdown -h now'`
2. Wait for the LED to go dark, then physically eject the SD card
3. Plug the SD into your laptop with a USB SD card reader
4. **On Windows:** open [Win32 Disk Imager](https://sourceforge.net/projects/win32diskimager/)
   as Administrator, fill in:
   - **Image File:** `C:\Users\You\Desktop\dev-backup-YYYY-MM-DD.img`
     (click the folder icon, type the filename, click Open)
   - **Device:** the SD card's drive letter
   - **Read Only Allocated Partitions:** leave UNCHECKED
   - Click **Read**, wait ~15–25 minutes
5. **On Linux/macOS:** `sudo dd if=/dev/sdX of=~/dev-backup-YYYY-MM-DD.img bs=4M status=progress`
6. Put the SD back into the Pi and power it on (you'll need it for Phase 2)

> **Why this is non-negotiable:** Phase 2's sanitize script is one-way
> on this physical card. Without a Phase 1 snapshot you'd have to rebuild
> the dev environment from scratch (~20 min) every time you want to
> develop another feature.

### Phase 2 — Sanitize on the live Orange Pi

Run [`make-release.sh`](make-release.sh) in dry-run mode first, then for real.

> **Version:** bump it on the dev machine with `python tools/release.py bump <ver>`,
> commit + push, and `git pull` on the Pi. `make-release.sh` then defaults
> `RELEASE_VERSION` to `v$(cat /opt/pisonex/VERSION)`, so you can drop the
> `RELEASE_VERSION=…` prefix below (it still works as an override). The confirm
> prompt expects that same string.

The script must run from **outside** `/opt/pisonex` (step 6 deletes that
folder's `deploy/`; the script refuses to run from there). Copy it to `/root`:

```bash
ssh root@<dev-pi-ip>
cp /opt/pisonex/deploy/make-release.sh /root/ && chmod +x /root/make-release.sh
```

**Dry-run** (always do this first — it changes nothing):

```bash
/root/make-release.sh
```

The dry-run prints exactly what *would* happen, starting with the commit it
will ship (and a warning if `/opt/pisonex` has uncommitted changes). Read
every section. If anything looks wrong, fix it before committing.

**Commit (one-way!)** — in the same SSH session, so you can see and copy the
root password it prints:

```bash
/root/make-release.sh --commit
# Type the release version to confirm (expecting 'v1.0.7'): v1.0.7
```

The script will:

| # | Step | What it does |
|---|---|---|
| 1 | Stop pisonex service | `systemctl stop pisonex` |
| 2 | Compile .py → .pyc | `python -m compileall -b` (legacy layout, alongside source) |
| 3 | Patch systemd unit | `ExecStart=... main.py` → `... main.pyc` |
| 4 | **Verify service runs from bytecode** | Starts service, waits 5 s, aborts if it died |
| 5 | Delete .py source files | Every `.py` in `server-orangepi/` removed; only `.pyc` remains |
| 6 | Remove dev-only files | Every folder of the repo clone except `server-orangepi/`, `venv/`, `VERSION`; `.git`, `server-orangepi/deploy`, `data/` (dev license), `.env.example`, `*.md`, tests |
| 7 | Reset .env | `SECRET_KEY`/`LICENSE_HMAC_SECRET` → **blank** (regenerated per device on first boot); `ADMIN_PASSWORD` → `admin123`; `CLIENT_API_KEY` → empty; `BRANCH_NAME` → `My Internet Cafe` |
| 8 | Wipe runtime state | `pisonet.db` (+ WAL), `pisonet.log*`, `backups/`, uploaded wallpapers, journald |
| 9 | Disable SSH | `stop` + `disable` + `mask` (un-startable) |
| 10 | **Rotate root password** | 32-char random; printed ONCE to your terminal — capture it |
| 11 | Strip SSH identity files | host keys, authorized_keys, known_hosts |
| 12 | Clear history, machine-id, Wi-Fi; set hostname; re-enable resize | systemd regenerates machine-id on first boot; saved Wi-Fi networks removed; hostname → `pisonex` so clients find `http://pisonex.local`; Armbian's `armbian-resize-filesystem` re-enabled so a shrunk image grows to the card on first boot |
| 13 | Clean apt cache, tmp, pip caches | reduces image size |
| 14 | Drop release marker | `/opt/pisonex/RELEASE` records version + build timestamp |
| 15 | fstrim unused blocks | zeros out free space so the image compresses small |
| 16 | Self-destruct + shutdown | script removes itself, then `shutdown -h now` |

> **⚠️ Step 10 — save the root password immediately.** It's printed exactly
> once in the SSH output. Store it in your password manager labeled
> `Pisonex <version> root console password`. This is your **only** key
> back into a deployed device (via local HDMI + USB keyboard) since SSH
> is disabled.

The SSH connection drops at the end because `shutdown -h now` kills sshd —
that's expected.

### Phase 3 — Image the SD card on your laptop

Wait for the Orange Pi's LED to go fully dark, then eject the SD card and
plug it into your laptop.

#### 3a. Read the SD card into an .img file

**Windows (Win32 Disk Imager as Administrator):**

| Field | Value |
|---|---|
| Image File | e.g. `…\build\output\v<version>\card.img` (click 📁, name the file, Open; the folder must exist) |
| Device | the SD card's drive letter — the Linux card often shows as an "Unknown" volume; check it's the SD card's size, not a USB stick |
| Read Only Allocated Partitions | UNCHECKED |

Click **Read**. It produces a file the full size of the card (e.g. 15.9 GB for a
16 GB card). That's expected — the next step shrinks it.

#### 3b. Shrink + compress (always)

```bash
python tools/release.py image "..\build\output\v<version>\card.img"
```

(From the workspace folder: `.\release image <path>`.) This runs
[`tools/shrink-image.sh`](../tools/shrink-image.sh) in WSL and writes
`build/output/v<version>/pisonex-opi-one-v<version>.img.gz`, then adds it to
`SHA256SUMS.txt`. It:

1. refuses an image whose `/opt/pisonex/RELEASE` marker isn't `v<VERSION>`, or that
   still has server `.py` source (i.e. `make-release.sh --commit` wasn't run);
2. makes sure Armbian's first-boot resize is enabled (make-release.sh does it too);
3. shrinks the root filesystem to its data + 128 MiB (16 MiB aligned, ~2.4 GB);
4. rebuilds the image (first 4 MiB = MBR + U-Boot kept byte-for-byte, partition
   table updated) and verifies it before gzip.

The input `.img` is never modified. Takes ~2 minutes. Delete the big `.img` once
the shrunk image has been test-flashed (keep a `-full.img.gz` of it in `backup/`
if you want a fallback).

**Needs WSL** (one-time): `wsl --install -d Debian` in an admin PowerShell. On a PC
without virtualization use WSL 1: enable the "Windows Subsystem for Linux" feature
(`dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all`,
reboot), `wsl --set-default-version 1`, then `wsl --install -d Debian`. Debian
already includes e2fsprogs/fdisk. Different distro name: set `PISONEX_WSL_DISTRO`.

#### 3c. Verify

Flash the `.img.gz` to a **spare** card (it should show ~2.4 GB in Etcher), boot it,
and check **Overview → Server Health → Disk** shows about the card's full size
(reboot once if it still shows ~2.3 GB — Armbian sometimes finishes the resize on
the second boot). See [Smoke testing the image](#smoke-testing-the-image).

### Phase 4 — Restore dev environment

You currently have an empty dev SD card (it became the release card in
Phase 2). To resume development:

**With a Phase 1 snapshot:**

```bash
# Linux/macOS
sudo dd if=~/dev-backup-YYYY-MM-DD.img of=/dev/sdX bs=4M status=progress

# Windows — use Win32 Disk Imager's Write mode (NOT Read)
```

Pop the SD back into the Pi, power on, you're back to dev.

**Without a Phase 1 snapshot** (build dev from scratch — ~20 min):

1. Flash a fresh Armbian / Orange Pi OS (Debian Bookworm, minimal/server) image
   for the **Orange Pi One** to a blank SD with Etcher
2. Boot with Ethernet, find the IP, `ssh root@<ip>` (set the root password
   when asked)
3. `apt-get update && apt-get install -y git`
4. `git clone https://github.com/valentinjasongaje/pisonex.git /opt/pisonex`
5. `bash /opt/pisonex/server-orangepi/deploy/install.sh` — installs packages
   into `/opt/pisonex/venv`, creates `.env`, installs + starts the `pisonex`
   service, sets the hostname to `pisonex` and runs a health check
6. **Snapshot this immediately** as `dev-backup.img` so next time is a
   1-step Etcher flash, not a 20-min setup.

---

## Smoke testing the image

**Always test the image before shipping.** Catches "works on master but
breaks on fresh flash" surprises.

1. Take a **different** blank SD card (32 GB+) — never test on the master
   card, you'll lose it
2. Open Etcher → Flash from file → pick `pisonex-v1.X.img.gz` → pick the
   blank SD → Flash (takes ~10 min)
3. Pop the SD into any Orange Pi, connect Ethernet, power on
4. Wait **~90 seconds** for first-boot
5. Browse to `http://pisonex.local/dashboard`. If that name doesn't resolve,
   find the IP in your router's DHCP client list (the device shows up as
   `pisonex`) and use `http://<that-ip>/dashboard`

**Pass criteria:**

| Check | Expected |
|---|---|
| Dashboard loads within ~1 s | ✓ |
| Login `admin` / `admin123` succeeds | ✓ |
| Overview shows no PCs (none of your dev PCs or members came along) | ✓ |
| Overview → Server Health | live CPU/RAM/temp values |
| Settings → Branch card | `My Internet Cafe` |
| Settings → Security card | Client API Key disabled |
| License page | fresh trial / "not activated" — **not** your dev license |
| Backup page | empty list (no dev backups) |
| A Windows PC with `PisonexClientSetup-v<ver>.exe` installed | registers and shows on Overview without typing a server URL |
| Flash a **second** card from the same image, boot both | each gets its own `SECRET_KEY` (logging in on one does not log you in on the other) |

If anything fails, see [Recovery procedures](#recovery-procedures).

---

## What the customer experiences

After they download `pisonex-v1.X.img.gz` and follow your instructions:

1. **Flash with Etcher** — pick zip + SD card → Flash. ~10 min.
2. **First boot** — pop card into Orange Pi, connect Ethernet, power on.
   Wait 90 seconds. During this time the device:
   - Regenerates `/etc/machine-id` (systemd does this automatically)
   - Runs `_enforce_secure_defaults()` which sees the blank
     `SECRET_KEY` / `LICENSE_HMAC_SECRET`, generates per-device ones and
     writes them back to `.env`
   - `Base.metadata.create_all()` creates the SQLite schema
   - Seeds the `ServerConfig` singleton with defaults
3. **Dashboard** — browse to `http://pisonex.local/dashboard` (or the
   IP from their router's admin page; the device appears as `pisonex`)
4. **Login** — `admin` / `admin123`
5. **Configure** — sidebar → Account → change the admin password;
   Settings → Branch card → set their branch name; License → activate
6. **Install Windows clients** — run `PisonexClientSetup-v<ver>.exe` on each
   PC. Clients default to `http://pisonex.local`, so nothing to type unless
   their network blocks mDNS (then enter the server IP in the client's admin
   panel, Ctrl+Shift+F12)

The customer needs:
- One Orange Pi One with ≥32 GB SD card
- Ethernet to their LAN router
- Power supply
- (Per-PC) Windows clients with the `PisoNetClient` installer

**The customer never:**
- Sees a Linux terminal
- Sees source code
- Knows the root password
- Has SSH access

---

## v1.1 and beyond — releasing updates

This is the workflow for any future release. Same pipeline, every time.

```
laptop (source of truth)
   │ git push origin main
   ▼
dev Orange Pi (with dev SD card from your snapshot)
   │ git pull && systemctl restart pisonex && test changes
   ▼
Phase 1 — snapshot dev SD → dev-backup-YYYY-MM-DD.img
Phase 2 — make-release.sh --commit (RELEASE_VERSION=v1.1)
Phase 3 — read SD → release.py image → pisonex-opi-one-v<ver>.img.gz
Phase 4 — restore dev-backup → resume work
   │
   ▼
Distribute pisonex-opi-one-v<ver>.img.gz → customers back up, re-flash, restore
```

**Customer impact of each release:**

- **Before re-flashing:** Dashboard → Backup → Create backup now →
  **Download**, and keep the file on a laptop
- Customer downloads the new `.img.gz` and re-flashes their SD with Etcher
- After boot: log in with `admin` / `admin123` → Backup → **Restore from a
  file** → members, balances, sales, PCs, rates and settings come back
  (the admin password too). Then re-activate the license.
- Without that backup file everything is lost and they reconfigure from scratch
- Per-device time: ~15–20 min

**Most code changes don't require the Orange Pi at all.** Run the server
on your Windows laptop for everything except GPIO/hardware-specific
changes:

```bash
cd pisonex/server-orangepi    # or server/ for the non-OPi variant
python -m venv venv
source venv/Scripts/activate  # Windows Git Bash
pip install -r requirements.txt
python main.py
# browse http://localhost:8000/dashboard
```

Only move to the dev Orange Pi when testing the coin signal, relay, GPIO
pins, or anything `hardware/controller.py` related.

### Future: smoother updates

If/when manual re-flashing becomes painful (e.g. >10 deployed devices),
consider in this order:

1. **"Update available" banner** on the dashboard — a 30-min feature
   that hits `https://www.pisonex.com/api/latest-version` and shows
   customers when a new image exists. Still manual re-flash, but they
   actually know to do it.
2. **Self-updater with signed bytecode** — service pulls a signed
   `update.tar.gz` from your CDN nightly, verifies with a public key
   baked into the image, replaces `.pyc` files, restarts. ~1–2 days of
   work. Doesn't re-open SSH.
3. **Per-device support tunnel** — customer-toggled reverse SSH from
   their device to your support server. Only when really needed.

---

## Recovery procedures

### "I sanitized but forgot to snapshot the dev SD"

Build dev from scratch on a fresh card — see Phase 4 above. ~20 min.
Snapshot it immediately afterwards.

### "The smoke test failed — dashboard never came up"

You can't SSH (it's disabled in the image). Options:

1. **Plug a monitor + USB keyboard** into the Orange Pi's HDMI + USB
   ports → log in at the local console as `root` with the rotated
   password you saved in Phase 2 → run `journalctl -u pisonex -n 100`
   and `systemctl status pisonex` to diagnose.
2. **Pop the card into your laptop** and inspect the filesystem with a
   Linux box / WSL / a USB ext4 reader. Check
   `/opt/pisonex/server-orangepi/.env`, `/opt/pisonex/RELEASE`,
   `/var/log/journal/`. Look for obvious .env corruption or missing files.
3. **Rebuild the image** — restore dev from snapshot, fix whatever was
   broken on dev, rerun the pipeline.

The Phase 2 step 4 (verify service from bytecode) is the gate that
catches most issues *before* `.py` files are deleted, so if you get past
the dry-run cleanly, smoke-test failures should be rare.

### "A deployed customer device stopped working"

Per the "fully sealed" decision, your only options are:

1. **Customer mails the SD card back** to you → you fix it on dev, image
   it, mail back. Slow but works.
2. **You mail them a new flashed SD card** — they swap, then restore their
   latest downloaded backup (Backup → Restore from a file). Anything after
   that backup is lost.
3. **Talk them through HDMI-console recovery** — works in principle but
   you'd be asking a café owner to use Linux at a terminal. Not realistic.

If this happens more than once or twice, time to reconsider the "fully
sealed" choice — see "Future: smoother updates" above.

### "I lost the rotated root password"

You have a few avenues, in increasing order of pain:

1. Check your password manager — that's where it should be
2. Check your terminal history / scrollback from the day you ran `make-release.sh --commit`
3. **Reset via local console with Armbian recovery mode** — boot the
   Orange Pi with the SD card, hold down a key to enter U-Boot, boot
   with `init=/bin/bash`, mount the rootfs RW, `passwd root`. Documented
   in Armbian's docs.
4. **Re-image from a backup `.img`** — wipes whatever changes you made,
   restores known-password state

---

## Decisions log / FAQ

### Why .pyc instead of Nuitka native binary?

Nuitka would be stronger protection but adds ~30 minutes of build time
and produces a binary that's harder to debug if something goes wrong in
the field. For v1 with a small customer count, `.pyc` is enough — Python
3.11+ decompilers are unreliable, and we're defending against casual
customers, not security researchers. Revisit if the product matures and
the threat model changes.

### Why is SSH disabled instead of just locking root?

A locked root account on its own doesn't stop SSH from being a port the
customer (or anyone on their LAN) can probe. Disabling and **masking** SSH
guarantees the daemon can't be started by any normal means.

### Why default admin password `admin123` instead of auto-generated?

UX. If we auto-generated the admin password on first boot, the customer
would need to find it somewhere (a log file? a printed sticker?) before
they could log into the dashboard for the first time. `admin123` is
universally known to be "the default, change me immediately." The
dashboard could be enhanced later to force a password change on first
login.

### Why per-build random root password instead of fixed?

If a single customer ever extracts the root password from `/etc/shadow`
on their image and shares it, every device with that image is vulnerable.
A per-build random password contains the blast radius to a single release.

### Why no remote update mechanism?

Two reasons:
1. **Threat surface** — every remote channel (SSH, HTTP update server,
   reverse tunnel) is something a customer or attacker could probe.
   Fully sealed = nothing inbound to attack.
2. **Simplicity** — we don't have to maintain a signed-update
   infrastructure, a CDN, or a tunnel server.

The cost is manual re-flashing per release. Acceptable for small
customer counts; revisit at ~10 deployed devices.

### How big does a customer's SD card need to be?

**8 GB recommended, 4 GB works.** Phase 3b shrinks the image to its data
(~2.4 GB), and Armbian's resize service grows it to the whole card on first
boot. An image that was *not* shrunk is the full size of the card it was read
from, and Etcher refuses cards smaller than that — so always run
`release.py image`.

### Why is the compressed image so small (~1 GB)?

`fstrim` in step 15 of the sanitize script tells the filesystem to mark
all unused blocks as discarded, which translates to "write zeros to free
space." Zeros compress to nearly nothing. What's left is roughly
3 GB of actual data (Armbian + Python venv + your app) compressed at
about 3.5:1.

### Can I commit `make-release.sh` to source control?

Yes — it's in `deploy/make-release.sh` and tracked in git. The script
doesn't contain any secrets or proprietary algorithms; it's standard
Linux sanitization steps. Customers who somehow obtained the source could
read it but couldn't use it to "undo" a sanitized image — the .py files
are *gone*, not encrypted.

### Where do the per-device secrets actually come from?

`_enforce_secure_defaults()` in `main.py` runs on every server startup.
If `SECRET_KEY` or `LICENSE_HMAC_SECRET` is blank, a known placeholder
(`changeme`, the shipped default, …) or shorter than 32 characters, it
generates a fresh random 64-hex-char value and writes it back to `.env`;
a strong value is never touched. The sanitize script **blanks** both so the
first boot of every flashed card generates its own. `ADMIN_PASSWORD` is not
generated — it stays `admin123` until the customer changes it. We never
bake real secrets into the image.

> Older versions of the script wrote `changeme` and the server only replaced its
> exact built-in default, so an image made then would have shipped the same
> `SECRET_KEY` to every customer (anyone could forge a dashboard login).
> Never ship an image made with an older `make-release.sh` / `main.py`.
