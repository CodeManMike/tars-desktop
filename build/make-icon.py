"""Draws tars.ico (16/32/48/256 px): an orange hexagon with a block 'T' on black. Stdlib only."""
import math
import struct
import sys
import zlib

ORANGE = (0xFF, 0x88, 0x00, 255)
BRIGHT = (0xFF, 0xB3, 0x47, 255)
BLACK = (0, 0, 0, 255)


def hexagon(n):
    c = (n - 1) / 2
    r = n / 2 - 0.25
    pts = [(c + r * math.cos(math.radians(90 + 60 * i)), c + r * math.sin(math.radians(90 + 60 * i))) for i in range(6)]

    def inside(x, y, shrink):
        # point-in-convex-polygon against the hexagon scaled by `shrink`
        sp = [(c + (px - c) * shrink, c + (py - c) * shrink) for px, py in pts]
        sign = None
        for i in range(6):
            (x1, y1), (x2, y2) = sp[i], sp[(i + 1) % 6]
            cross = (x2 - x1) * (y - y1) - (y2 - y1) * (x - x1)
            if cross != 0:
                s = cross > 0
                if sign is None:
                    sign = s
                elif s != sign:
                    return False
        return True

    stroke = max(1.0, n / 14)
    inner = 1 - (stroke * 2.2) / n
    img = []
    for y in range(n):
        row = []
        for x in range(n):
            px, py = x + 0.5 - 0.5, y + 0.5 - 0.5
            if inside(px, py, 1.0) and not inside(px, py, inner):
                row.append(ORANGE)
            elif inside(px, py, 1.0):
                row.append(BLACK)
            else:
                row.append((0, 0, 0, 0))
        img.append(row)
    # block T
    bar_w = round(n * 0.46)
    bar_h = max(2, round(n * 0.12))
    stem_w = max(2, round(n * 0.14))
    bar_w += (n - bar_w) % 2
    stem_w += (n - stem_w) % 2
    top = round(n * 0.30)
    bottom = round(n * 0.72)
    x0 = (n - bar_w) // 2
    for y in range(top, top + bar_h):
        for x in range(x0, x0 + bar_w):
            img[y][x] = BRIGHT
    sx = (n - stem_w) // 2
    for y in range(top, bottom):
        for x in range(sx, sx + stem_w):
            img[y][x] = BRIGHT
    return img


def png(img):
    n = len(img)
    raw = b"".join(b"\x00" + b"".join(bytes(p) for p in row) for row in img)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def ico(sizes, path):
    images = [png(hexagon(s)) for s in sizes]
    out = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    for s, data in zip(sizes, images):
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(out + b"".join(images))


if __name__ == "__main__":
    ico([16, 32, 48, 256], sys.argv[1] if len(sys.argv) > 1 else "tars.ico")
