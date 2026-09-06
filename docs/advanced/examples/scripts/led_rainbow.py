#!/usr/bin/env python3
"""Scroll a dim rainbow across LED.BIN (12×RGB = 36 bytes)."""

from __future__ import annotations

import math
import os
import sys
import time
from pathlib import Path


def hsv(h: float) -> tuple[int, int, int]:
    h = h % 1.0
    i = int(h * 6)
    f = h * 6 - i
    q = int(255 * (1 - f))
    t = int(255 * f)
    return [
        (255, t, 0),
        (q, 255, 0),
        (0, 255, t),
        (0, q, 255),
        (t, 0, 255),
        (255, 0, q),
    ][i % 6]


def main() -> None:
    path = Path(sys.argv[1] if len(sys.argv) > 1 else "/Volumes/PICO_RGB/LED.BIN")
    n = 12
    t = 0.0
    print(f"writing {path}  (Ctrl+C to stop)")
    while True:
        buf = bytearray()
        for i in range(n):
            r, g, b = hsv((t + i) / n)
            buf += bytes((r // 4, g // 4, b // 4))
        with open(path, "wb", buffering=0) as f:
            f.write(buf)
            f.flush()
            os.fsync(f.fileno())
        time.sleep(0.05)
        t = (t + 0.05) % n


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
