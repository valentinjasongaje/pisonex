"""Wake-on-LAN: power on a café PC by sending the standard "magic packet".

The packet is 6 x 0xFF followed by the PC's MAC address repeated 16 times,
sent as a UDP broadcast. The PC's network card watches for it while the PC
is off (BIOS "Wake on LAN" on, cable plugged in, PC still on mains power).

It is sent to the limited broadcast (255.255.255.255) and, when the PC's
last IP is known, to that /24's broadcast address too: on a server with
several network cards (e.g. Windows with Wi-Fi + Ethernet) the limited
broadcast may leave through the wrong one. Ports 9 and 7 are the two that
network cards listen on.
"""
from __future__ import annotations

import ipaddress
import logging
import re
import socket

logger = logging.getLogger(__name__)

_PORTS = (9, 7)


def normalize_mac(mac: str | None) -> str | None:
    """'AA:BB:CC:DD:EE:FF' / 'AA-BB-…' / 'AABBCC…' → 'AABBCCDDEEFF', or None
    for anything that isn't a usable unicast MAC (e.g. the client's "UNKNOWN")."""
    hex_only = re.sub(r"[^0-9A-Fa-f]", "", mac or "")
    if len(hex_only) != 12 or hex_only.upper() in ("000000000000", "FFFFFFFFFFFF"):
        return None
    return hex_only.upper()


def magic_packet(mac_hex: str) -> bytes:
    return b"\xff" * 6 + bytes.fromhex(mac_hex) * 16


def _targets(ip: str | None) -> list[str]:
    targets = ["255.255.255.255"]
    if ip:
        try:
            net = ipaddress.ip_network(f"{ip}/24", strict=False)
            if net.is_private:
                targets.append(str(net.broadcast_address))
        except ValueError:
            pass
    return targets


def wake(mac: str | None, ip: str | None = None) -> bool:
    """Send the magic packet. True if at least one copy left this machine."""
    mac_hex = normalize_mac(mac)
    if not mac_hex:
        return False
    packet = magic_packet(mac_hex)
    sent = False
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        for target in _targets(ip):
            for port in _PORTS:
                try:
                    sock.sendto(packet, (target, port))
                    sent = True
                except OSError as exc:
                    logger.debug("WoL to %s:%s failed: %s", target, port, exc)
    return sent
