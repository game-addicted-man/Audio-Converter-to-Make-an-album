"""Regenerate the code-drawn app icon using only Python's standard library."""
from pathlib import Path
import math
import struct
import zlib


def png(size):
    pixels = bytearray()
    heights = [0.18, 0.40, 0.58, 0.32, 0.14]
    for y in range(size):
        pixels.append(0)
        for x in range(size):
            red = green = blue = alpha = 0
            for sy in range(4):
                for sx in range(4):
                    px = (x + (sx + 0.5) / 4) / size
                    py = (y + (sy + 0.5) / 4) / size
                    if math.hypot(px - 0.5, py - 0.5) <= 0.48:
                        color = (26, 32, 42)
                        for i, height in enumerate(heights):
                            cx = 0.23 + 0.135 * i
                            nearest_y = min(max(py, 0.5 - height / 2), 0.5 + height / 2)
                            if math.hypot(px - cx, py - nearest_y) < 0.029:
                                color = (108, 231, 204)
                        red += color[0]
                        green += color[1]
                        blue += color[2]
                        alpha += 255
            if alpha:
                covered = alpha / 255
                pixels.extend((round(red / covered), round(green / covered), round(blue / covered), round(alpha / 16)))
            else:
                pixels.extend((0, 0, 0, 0))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")


sizes = [16, 32, 48, 256]
images = [png(size) for size in sizes]
offset = 6 + 16 * len(images)
directory = bytearray(struct.pack("<HHH", 0, 1, len(images)))
for size, data in zip(sizes, images):
    directory.extend(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
Path(__file__).with_name("src").joinpath("app.ico").write_bytes(directory + b"".join(images))
