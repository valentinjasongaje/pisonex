"""Wake-on-LAN: sends a UDP broadcast magic packet to power on a PC.

This is a purely server-to-network operation — a powered-off PC has no client
running to poll `command_store`, so waking it can't reuse the normal
push_command/heartbeat delivery path used for shutdown/restart/lock.
"""

import logging
import socket

from sqlalchemy.orm import Session as DBSession

from models import PC

logger = logging.getLogger(__name__)

_WOL_PORT = 9


def _mac_to_bytes(mac_address: str) -> bytes:
    """Parse a MAC address string (with or without ':'/'-' separators) into 6 raw bytes."""
    cleaned = mac_address.strip().translate({ord(c): None for c in ":-. "})
    if len(cleaned) != 12:
        raise ValueError(f"Invalid MAC address: {mac_address!r}")
    return bytes.fromhex(cleaned)


def send_magic_packet(mac_address: str) -> bool:
    """Broadcast a Wake-on-LAN magic packet for the given MAC address."""
    try:
        mac_bytes = _mac_to_bytes(mac_address)
    except ValueError:
        logger.warning("Cannot send WoL packet, invalid MAC address: %r", mac_address)
        return False

    packet = b"\xff" * 6 + mac_bytes * 16

    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.sendto(packet, ("255.255.255.255", _WOL_PORT))

    logger.info("Sent WoL magic packet to %s", mac_address)
    return True


def wake_pc(pc: PC) -> bool:
    """Send a magic packet to wake the given PC. Returns False if it has no MAC on file."""
    if not pc.mac_address:
        return False
    return send_magic_packet(pc.mac_address)


def wake_all(db: DBSession) -> tuple[int, int]:
    """Send a magic packet to every registered PC with a MAC address on file.

    Returns (woken_count, skipped_no_mac_count).
    """
    pcs = db.query(PC).order_by(PC.pc_number).all()
    woken = 0
    skipped = 0
    for pc in pcs:
        if wake_pc(pc):
            woken += 1
        else:
            skipped += 1
    return woken, skipped
