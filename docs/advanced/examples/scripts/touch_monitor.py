#!/usr/bin/env python3
"""Poll TOUCH.TXT and print new gestures when seq increases."""

from __future__ import annotations

import sys
import time
from pathlib import Path


def read_fields(path: Path) -> dict[str, str]:
    text = path.read_text(encoding="utf-8", errors="ignore")
    fields: dict[str, str] = {}
    for line in text.splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        fields[key.strip()] = value.strip()
    return fields


def main() -> None:
    path = Path(sys.argv[1] if len(sys.argv) > 1 else "/Volumes/PICO_RGB/TOUCH.TXT")
    last_seq = -1
    print(f"watching {path}  (Ctrl+C to stop)")
    while True:
        try:
            fields = read_fields(path)
            seq = int(fields.get("seq", "0"))
            if seq > last_seq:
                print(f"seq={seq} evt={fields.get('evt')} t_ms={fields.get('t_ms')}")
                last_seq = seq
        except FileNotFoundError:
            print("waiting for volume...")
        except Exception as exc:  # noqa: BLE001 - demo script
            print(f"read error: {exc}")
        time.sleep(0.25)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
