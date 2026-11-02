#!/bin/bash
# ─────────────────────────────────────────────────────────────────────
#  shrink-image.sh — make a Pisonex Orange Pi SD image small and fast to flash
#
#  Win32 Disk Imager reads the whole card (e.g. 15.9 GB, mostly empty), and
#  Etcher then writes all of it. This shrinks the root filesystem to its data
#  plus a small margin (~2.4 GB) and re-enables Armbian's first-boot resize,
#  so the card still grows to its full size on the customer's first boot.
#  The .img.gz download hardly changes; flashing gets ~6x faster and the image
#  fits 4–8 GB cards.
#
#  Usage (Linux or WSL — needs e2fsprogs, fdisk/sfdisk, gzip):
#      tools/shrink-image.sh <card.img> <out.img.gz> [expected-version]
#  From Windows use:  python tools/release.py image <card.img>
#
#  The input image is never modified. Work files go next to the output.
# ─────────────────────────────────────────────────────────────────────
set -euo pipefail

IN="${1:-}"; OUT="${2:-}"; EXPECT="${3:-}"
[ -n "$IN" ] && [ -n "$OUT" ] || { sed -n '4,16p' "$0"; exit 2; }
[ -f "$IN" ] || { echo "input not found: $IN" >&2; exit 1; }
case "$OUT" in *.img.gz) ;; *) echo "output must end in .img.gz" >&2; exit 1 ;; esac

MARGIN_BYTES=$((128 * 1024 * 1024))   # free space left in the shrunk filesystem
ALIGN_BYTES=$((16 * 1024 * 1024))     # SD-card erase-block alignment
UNIT=/usr/lib/systemd/system/armbian-resize-filesystem.service
LINK=/etc/systemd/system/basic.target.wants/armbian-resize-filesystem.service

die()  { echo "[x] $*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }
for t in sfdisk e2fsck resize2fs debugfs dumpe2fs gzip cmp; do
    command -v "$t" >/dev/null || die "missing tool: $t (Debian: apt-get install e2fsprogs fdisk gzip)"
done

WORK="$(dirname "$OUT")/.shrink-$$"
mkdir -p "$WORK"
trap 'rm -rf "$WORK" "$OUT.part"' EXIT
PART="$WORK/rootfs.ext4"
SMALL="$WORK/small.img"
fsval() { dumpe2fs -h "$PART" 2>/dev/null | awk -F: -v k="$1" '$1==k {gsub(/ /,"",$2); print $2}'; }

step "1. partition table"
mapfile -t ROWS < <(sfdisk -d "$IN" | grep 'start=')
[ "${#ROWS[@]}" -eq 1 ] || die "expected exactly one partition, found ${#ROWS[@]}"
START=$(sed -E 's/.*start= *([0-9]+).*/\1/' <<<"${ROWS[0]}")
SIZE=$(sed -E 's/.*size= *([0-9]+).*/\1/' <<<"${ROWS[0]}")
TYPE=$(sed -E 's/.*type= *([0-9a-fA-F]+).*/\1/' <<<"${ROWS[0]}")
[ "$TYPE" = "83" ] || die "partition type $TYPE is not Linux (83)"
echo "root partition: start sector $START, $SIZE sectors"

step "2. copy the partition out"
dd if="$IN" of="$PART" bs=4M iflag=skip_bytes,count_bytes \
   skip=$((START * 512)) count=$((SIZE * 512)) status=none
e2fsck -fy "$PART" >/dev/null || [ $? -le 1 ] || die "filesystem check failed"
BS=$(fsval "Block size")
[ -n "$BS" ] || die "not an ext2/3/4 filesystem"
echo "ext4 '$(fsval "Filesystem volume name")', UUID $(fsval "Filesystem UUID"), block size $BS"

MARKER=$(debugfs -R "cat /opt/pisonex/RELEASE" "$PART" 2>/dev/null | head -1 || true)
echo "release marker: ${MARKER:-<none>}"
if [ -n "$EXPECT" ]; then
    case "$MARKER" in "v$EXPECT "*) ;; *) die "image is not v$EXPECT (marker: '${MARKER:-none}') — was make-release.sh run for this version?" ;; esac
fi
if debugfs -R "stat /opt/pisonex/server-orangepi/main.py" "$PART" 2>/dev/null | grep -q '^Inode:'; then
    die "server source (main.py) is still on the card — run make-release.sh --commit before imaging"
fi

step "3. grow-on-first-boot (Armbian resize service)"
debugfs -R "stat $UNIT" "$PART" 2>/dev/null | grep -q '^Inode:' \
    || die "$UNIT not in the image — the card would stay at the shrunk size"
if debugfs -R "stat $LINK" "$PART" 2>/dev/null | grep -q '^Inode:'; then
    echo "already enabled"
else
    debugfs -w -R "symlink $LINK $UNIT" "$PART" 2>/dev/null
    debugfs -R "stat $LINK" "$PART" 2>/dev/null | grep -q '^Inode:' || die "could not enable the resize service"
    echo "enabled"
fi
if debugfs -R "stat /root/.no_rootfs_resize" "$PART" 2>/dev/null | grep -q '^Inode:'; then
    debugfs -w -R "rm /root/.no_rootfs_resize" "$PART" 2>/dev/null
    echo "removed /root/.no_rootfs_resize"
fi
e2fsck -fy "$PART" >/dev/null || [ $? -le 1 ] || die "filesystem check failed after enabling resize"

step "4. shrink"
resize2fs -M "$PART" >/dev/null 2>&1
MIN=$(fsval "Block count")
MARGIN=$((MARGIN_BYTES / BS)); ALIGN=$((ALIGN_BYTES / BS))
TARGET=$(( ( (MIN + MARGIN + ALIGN - 1) / ALIGN ) * ALIGN ))
resize2fs "$PART" "$TARGET" >/dev/null 2>&1
e2fsck -fn "$PART" >/dev/null || die "filesystem check failed after shrinking"
truncate -s $((TARGET * BS)) "$PART"
echo "$(( MIN * BS / 1048576 )) MiB minimum -> $(( TARGET * BS / 1048576 )) MiB ($(( $(fsval "Free blocks") * BS / 1048576 )) MiB free)"

step "5. build the small image"
head -c $((START * 512)) "$IN" > "$SMALL"                    # MBR + U-Boot area
cat "$PART" >> "$SMALL"
echo "$START,$(( TARGET * BS / 512 ))" | sfdisk -q -N 1 --no-reread --no-tell-kernel "$SMALL"

step "6. verify"
WANT=$(( START * 512 + TARGET * BS ))
[ "$(stat -c %s "$SMALL")" -eq "$WANT" ] || die "image size mismatch"
OUTSIDE=$(cmp -l -n $((START * 512)) "$IN" "$SMALL" | awk '$1 < 447 || $1 > 510' | wc -l || true)
[ "$OUTSIDE" -eq 0 ] || die "$OUTSIDE bytes outside the partition table changed in the boot area"
cmp -s -n $((TARGET * BS)) -i $((START * 512)):0 "$SMALL" "$PART" || die "partition copy mismatch"
[ "$(sfdisk -d "$SMALL" | grep -c 'start=')" -eq 1 ] || die "partition table broken"
echo "boot area unchanged (only the partition entry), partition intact, size $(( WANT / 1048576 )) MiB"

step "7. compress"
gzip -c -6 "$SMALL" > "$OUT.part"
gzip -t "$OUT.part"
mv -f "$OUT.part" "$OUT"
echo "$(basename "$OUT"): $(( $(stat -c %s "$OUT") / 1048576 )) MiB download, $(( WANT / 1048576 )) MiB to flash"
echo "sha256 $(sha256sum "$OUT" | cut -d' ' -f1)"
echo "SHRINK OK"
