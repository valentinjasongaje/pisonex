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

A single file, e.g. `pisonex-v1.0.zip` (~855 MB compressed, ~31 GB raw).

The customer flow with that file:

1. Download the `.zip`
2. Open [balenaEtcher](https://etcher.balena.io/), pick the zip and a blank
   SD card (32 GB minimum), click Flash
3. Pop the card into an Orange Pi, connect Ethernet, power on
4. Wait ~90 seconds (first-boot regenerates secrets, machine-id, etc.)
5. Browse to `http://<dhcp-assigned-ip>/dashboard`
6. Log in as `admin` / `admin123`, change the password, set the branch name

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
| Customer mounting SD card on Windows / Linux laptop | `.py` source deleted; only `.pyc` bytecode remains | ⚠️ raises bar significantly (Python 3.13 decompilers are unreliable) but determined attacker can recover most logic |
| Customer reading commit history & dev notes | `.git`, `*.md`, `tests/`, `.env.example` removed | ✅ |
| Customer extracting your secrets | All `.env` secrets reset to defaults; first boot auto-generates fresh ones per device | ✅ each device has unique secrets |
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

- [ ] All target code is committed and pushed to `origin/main`
- [ ] You've tested the feature/fix manually on the dev Orange Pi
- [ ] The dev Orange Pi at `/opt/pisonex/server-orangepi` is on the right
      commit (`git rev-parse --short HEAD` matches what you want to ship)
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

**Dry-run** (always do this first — it changes nothing):

```bash
scp deploy/make-release.sh root@<dev-pi-ip>:/root/make-release.sh
ssh root@<dev-pi-ip> 'chmod +x /root/make-release.sh && RELEASE_VERSION=v1.1 /root/make-release.sh'
```

The dry-run prints exactly what *would* happen. Read every section. If
anything looks wrong, fix it before committing.

**Commit (one-way!):**

```bash
echo "v1.1" | ssh root@<dev-pi-ip> 'RELEASE_VERSION=v1.1 /root/make-release.sh --commit'
```

The script will:

| # | Step | What it does |
|---|---|---|
| 1 | Stop pisonex service | `systemctl stop pisonex` |
| 2 | Compile .py → .pyc | `python -m compileall -b` (legacy layout, alongside source) |
| 3 | Patch systemd unit | `ExecStart=... main.py` → `... main.pyc` |
| 4 | **Verify service runs from bytecode** | Starts service, waits 5 s, aborts if it died |
| 5 | Delete .py source files | All 30 .py files removed; only .pyc remains |
| 6 | Remove dev-only files | `.git`, `.env.example`, `tests/`, `docs/`, `*.md`, etc. |
| 7 | Reset .env secrets | `SECRET_KEY`/`LICENSE_HMAC_SECRET` → `changeme` (auto-regen on boot); `ADMIN_PASSWORD` → `admin123`; `BRANCH_NAME` → empty |
| 8 | Wipe runtime state | `pisonet.db`, `pisonet.log`, journald |
| 9 | Disable SSH | `stop` + `disable` + `mask` (un-startable) |
| 10 | **Rotate root password** | 32-char random; printed ONCE to your terminal — capture it |
| 11 | Strip SSH identity files | host keys, authorized_keys, known_hosts |
| 12 | Clear shell history & machine-id | systemd regenerates machine-id on first boot |
| 13 | Clean apt cache + tmp | reduces image size |
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
| Image File | `C:\path\to\pisonex-v1.1.img` (click 📁, name the file, Open) |
| Device | SD card drive letter (e.g. `[F:\]`) |
| Read Only Allocated Partitions | UNCHECKED |

Click **Read**. Takes ~15–25 minutes; produces a ~31 GB file.

**Linux/macOS:**

```bash
sudo dd if=/dev/sdX of=pisonex-v1.1.img bs=4M status=progress
```

The image will be the *full size of the SD card* (e.g. 31 GB for a 32 GB
card), even though most of it is zeros. That's normal — we compress next.

#### 3b. Compress

The raw image is ~31 GB but ~95 % zeros (because of step 15's fstrim).
Compression collapses it to ~800 MB – 1.5 GB.

**Best option — gzip (Git Bash, Linux, macOS):**

```bash
gzip -v pisonex-v1.1.img         # produces .img.gz, removes original
gzip -kv pisonex-v1.1.img        # keeps original (need 32+ GB free)
```

Etcher accepts `.gz` directly.

**Windows alternative — WinRAR:**

1. Right-click `pisonex-v1.1.img` → WinRAR → **Add to archive…**
2. Set **Archive format: ZIP** (NOT RAR — Etcher doesn't accept RAR)
3. Change the archive name extension to `.zip`
4. Compression: Normal (Best only saves ~3% extra for double the time)
5. Click OK; wait ~15 minutes

**Windows alternative — 7-Zip:**

1. Right-click → 7-Zip → Add to archive
2. Archive format: **gzip**
3. Click OK

#### 3c. Verify

The output file should be 800 MB – 1.5 GB. If it's much larger, something
went wrong (likely fstrim was skipped). If much smaller, also suspicious —
sanity-check by opening the archive and confirming the inner file is the
full ~31 GB.

This `.zip` (or `.gz`) is your shipping artifact.

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

1. Flash a fresh Armbian image to a blank SD with Etcher
2. Boot, find the IP, `ssh root@<ip>` (default Armbian password)
3. `apt update && apt install -y python3.13 python3.13-venv git sqlite3`
4. `git clone https://github.com/valentinjasongaje/pisonex.git /opt/pisonex`
5. `python3.13 -m venv /opt/pisonex/venv`
6. `/opt/pisonex/venv/bin/pip install -r /opt/pisonex/server-orangepi/requirements.txt`
7. `cp /opt/pisonex/pisonex/deploy/pisonet.service /etc/systemd/system/`
   *(adjust paths inside the unit file if your install layout differs from
   `/opt/pisonex/server-orangepi/`)*
8. `systemctl daemon-reload && systemctl enable pisonex && systemctl start pisonex`
9. **Snapshot this immediately** as `dev-backup.img` so next time is a
   1-step Etcher flash, not a 20-min setup.

---

## Smoke testing the image

**Always test the image before shipping.** Catches "works on master but
breaks on fresh flash" surprises.

1. Take a **different** blank SD card (32 GB+) — never test on the master
   card, you'll lose it
2. Open Etcher → Flash from file → pick `pisonex-v1.X.zip` → pick the
   blank SD → Flash (takes ~10 min)
3. Pop the SD into any Orange Pi, connect Ethernet, power on
4. Wait **~90 seconds** for first-boot
5. Find the IP via your router's DHCP client list (the device shows up as
   `orangepione`)
6. Browse to `http://<that-ip>/dashboard`

**Pass criteria:**

| Check | Expected |
|---|---|
| Dashboard loads within ~1 s | ✓ |
| Login `admin` / `admin123` succeeds | ✓ |
| Dashboard home shows "No PCs registered" empty state | ✓ |
| Settings → Branch card | field empty |
| Settings → Security card | "Client API Key: disabled" |
| Settings → Server Health | live CPU/RAM/temp values |
| The systemd service is running and stable | ✓ (no restart loops) |

If anything fails, see [Recovery procedures](#recovery-procedures).

---

## What the customer experiences

After they download `pisonex-v1.X.zip` and follow your instructions:

1. **Flash with Etcher** — pick zip + SD card → Flash. ~10 min.
2. **First boot** — pop card into Orange Pi, connect Ethernet, power on.
   Wait 90 seconds. During this time the device:
   - Regenerates `/etc/machine-id` (systemd does this automatically)
   - Runs `_enforce_secure_defaults()` which generates per-device
     `SECRET_KEY` and `LICENSE_HMAC_SECRET` and writes them back to `.env`
   - `Base.metadata.create_all()` creates the SQLite schema
   - Seeds the `ServerConfig` singleton with defaults
3. **Find IP** — they check their router's admin page; the device appears
   as `orangepione` with a DHCP-assigned 192.168.x.x address
4. **Dashboard** — browse to `http://<that-ip>/dashboard`
5. **Login** — `admin` / `admin123`
6. **Configure** — Settings → Branch card → set their branch name;
   Settings → Security → change admin password
7. **Install Windows clients** — flash USB sticks with the
   `PisoNetClient.exe` they received separately, point each PC client at
   the server IP

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
Phase 3 — read SD + compress → pisonex-v1.1.zip
Phase 4 — restore dev-backup → resume work
   │
   ▼
Distribute pisonex-v1.1.zip → customers re-flash → manual reconfigure
```

**Customer impact of each release:**

- Customer downloads new `.zip` (855 MB-ish each)
- Customer re-flashes their SD with Etcher
- **All customer config is lost** — branch name, admin password, registered
  PCs, members, transaction history. They reconfigure from scratch.
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

Only move to the dev Orange Pi when testing coin signal, LCD, GPIO pins,
or anything `hardware/controller.py` related.

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
2. **You mail them a new flashed SD card** — they swap, they reconfigure.
   They lose any local data (sessions in flight, transaction history not
   yet synced to pisonex.com).
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
3.13 decompilers are unreliable, and we're defending against casual
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

**32 GB minimum.** The raw image is the full size of the SD card it was
made from (31 GB for a 32 GB card). Etcher refuses to flash an image
larger than the target card, so a customer with a 16 GB card cannot use
this image as-is. If you need to support smaller cards, look at
[PiShrink](https://github.com/Drewsif/PiShrink) — it shrinks the
partition to actual data (~4 GB), and the resulting image will flash to
any 8 GB+ card.

### Why is the compressed image so small (~855 MB)?

`fstrim` in step 15 of the sanitize script tells the filesystem to mark
all unused blocks as discarded, which translates to "write zeros to free
space." Zeros compress to nearly nothing. The 855 MB you see is roughly
3 GB of actual data (Armbian + Python venv + your app) compressed at
3.5:1 ratio.

### Can I commit `make-release.sh` to source control?

Yes — it's in `deploy/make-release.sh` and tracked in git. The script
doesn't contain any secrets or proprietary algorithms; it's standard
Linux sanitization steps. Customers who somehow obtained the source could
read it but couldn't use it to "undo" a sanitized image — the .py files
are *gone*, not encrypted.

### Where do the per-device secrets actually come from?

`_enforce_secure_defaults()` in `main.py` runs on every server startup.
It checks if `SECRET_KEY`, `LICENSE_HMAC_SECRET`, or `ADMIN_PASSWORD` are
still at insecure default values; if so, it generates fresh random ones
and writes them back to `.env`. The sanitize script's job is just to
**reset those values to the defaults** so the next boot triggers
regeneration. We never bake real secrets into the image.
