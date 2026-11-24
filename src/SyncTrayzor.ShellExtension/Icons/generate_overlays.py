"""Generates the Explorer overlay icons (overlay_*.ico) used by the shell extension.

Overlay icons are drawn on top of the whole file icon, so each one is a transparent
square with a small badge in the bottom left corner. Only the standard library is
needed: python generate_overlays.py
"""

import math
import os
import struct
import zlib

SIZES = [16, 24, 32, 48, 256]
SUPERSAMPLING = 4

WHITE = (255, 255, 255)
BADGES = {
    "uptodate": (46, 125, 50),  # green
    "syncing": (21, 101, 192),  # blue
    "ignored": (117, 117, 117),  # grey
    "error": (198, 40, 40),  # red
}


def segment_distance(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def polyline(points, width):
    def inside(u, v):
        return any(
            segment_distance(u, v, *points[i], *points[i + 1]) <= width / 2
            for i in range(len(points) - 1)
        )

    return inside


def sync_arrow(u, v):
    # An open ring with an arrow head, like a "refresh" symbol
    r = math.hypot(u - 0.5, v - 0.5)
    angle = math.degrees(math.atan2(v - 0.5, u - 0.5)) % 360
    if abs(r - 0.24) <= 0.065 and not (300 <= angle <= 360):
        return True
    # Arrow head at the end of the ring (angle 300 degrees)
    tip = (0.5 + 0.24 * math.cos(math.radians(300)), 0.5 + 0.24 * math.sin(math.radians(300)))
    return point_in_triangle(u, v, (tip[0] - 0.15, tip[1] - 0.02), (tip[0] + 0.13, tip[1] - 0.06), (tip[0] + 0.02, tip[1] + 0.16))


def point_in_triangle(px, py, a, b, c):
    def sign(p1, p2, p3):
        return (p1[0] - p3[0]) * (p2[1] - p3[1]) - (p2[0] - p3[0]) * (p1[1] - p3[1])

    d1, d2, d3 = sign((px, py), a, b), sign((px, py), b, c), sign((px, py), c, a)
    has_neg = d1 < 0 or d2 < 0 or d3 < 0
    has_pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (has_neg and has_pos)


GLYPHS = {
    "uptodate": polyline([(0.27, 0.52), (0.43, 0.68), (0.74, 0.35)], 0.13),
    "syncing": sync_arrow,
    "ignored": polyline([(0.27, 0.5), (0.73, 0.5)], 0.15),
    "error": lambda u, v: polyline([(0.32, 0.32), (0.68, 0.68)], 0.13)(u, v)
    or polyline([(0.68, 0.32), (0.32, 0.68)], 0.13)(u, v),
}


def render(name, size):
    """Returns rows of (r, g, b, a) pixels, top row first"""
    diameter = max(9, round(size * 0.55))
    ring = max(1.0, diameter * 0.09) / diameter
    left, top = 0, size - diameter
    color, glyph = BADGES[name], GLYPHS[name]

    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            samples = []
            for sy in range(SUPERSAMPLING):
                for sx in range(SUPERSAMPLING):
                    u = (x - left + (sx + 0.5) / SUPERSAMPLING) / diameter
                    v = (y - top + (sy + 0.5) / SUPERSAMPLING) / diameter
                    r = math.hypot(u - 0.5, v - 0.5)
                    if r > 0.5:
                        samples.append(None)
                    elif r > 0.5 - ring or glyph(u, v):
                        samples.append(WHITE)
                    else:
                        samples.append(color)
            covered = [s for s in samples if s is not None]
            if not covered:
                row.append((0, 0, 0, 0))
                continue
            alpha = round(255 * len(covered) / len(samples))
            rgb = tuple(round(sum(c[i] for c in covered) / len(covered)) for i in range(3))
            row.append(rgb + (alpha,))
        rows.append(row)
    return rows


def to_dib(rows):
    size = len(rows)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = b"".join(bytes((b, g, r, a)) for row in reversed(rows) for (r, g, b, a) in row)
    mask_row = ((size + 31) // 32) * 4
    return header + pixels + bytes(mask_row * size)


def to_png(rows):
    size = len(rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    raw = b"".join(b"\x00" + bytes(c for px in row for c in px) for row in rows)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def write_ico(path, images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for size, blob in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset + len(data))
        data += blob
    with open(path, "wb") as f:
        f.write(header + entries + data)


def main():
    out_dir = os.path.dirname(os.path.abspath(__file__))
    for name in BADGES:
        images = []
        for size in SIZES:
            rows = render(name, size)
            images.append((size, to_png(rows) if size >= 256 else to_dib(rows)))
        write_ico(os.path.join(out_dir, f"overlay_{name}.ico"), images)
        print(f"overlay_{name}.ico")


if __name__ == "__main__":
    main()
