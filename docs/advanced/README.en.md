# Advanced mode (no Pico Vibe app required)

After you insert the module, the computer mounts a small volume named **`PICO_RGB`** (older firmware may use `PICO_RAM`).  
You can control lights, read touch events, and install OTA updates by copying files — without installing Pico Vibe.

| File | Direction | Purpose |
|---|---|---|
| `RGB.INI` | host → device | One-line resident effect |
| `RGB.SEQ` | host → device | Multi-step autonomous sequence |
| `LED.BIN` | host → device | 36-byte frames (12×RGB) |
| `TOUCH.TXT` | device → host | Touch gestures (read-only) |
| Official OTA package | host → device | Copy to root to upgrade |

**Priority (high → low):** `LED.BIN` → `RGB.SEQ` → `RGB.INI` → default rainbow  

Tip: quit Pico Vibe (or stop its light writes) while using advanced files so they do not fight over the volume.

Typical macOS path: `/Volumes/PICO_RGB/`

---

## `RGB.INI` in three steps

1. Insert the module and open `/Volumes/PICO_RGB/`
2. Copy an example to the root as **`RGB.INI`** (exact name)
3. Save and wait ~0.1–0.5 s for the effect to apply

```bash
cp docs/advanced/examples/rgb/13_fire.ini /Volumes/PICO_RGB/RGB.INI
```

Or create `RGB.INI` with one line:

```text
MODE=fire,R=255,G=160,B=40,BR=100,SPD=100
```

Comments start with `#` or `;`. Only the first line is parsed.

### Modes

`solid`, `off`, `blink`, `rainbow`, `rainbow_cycle`, `chase`, `comet`, `theater`, `wave`, `scan`, `sparkle`, `twinkle`, `breathe`, `gradient`, `fire`, `palette`

### Common parameters

| Key | Meaning |
|---|---|
| `BR` | Brightness 0–255 |
| `SPD` | Speed 1–255 |
| `DIR` | Direction (`0` forward, non-zero reverse) |
| `R G B` / `R2 G2 B2` | Primary / secondary color |
| `N` / `START` | LED count / start index |
| `ON` `OFF` `PULSES` `GAP` `FADE` | `blink` timing |
| `PAL` | Palette 0–3 |
| `GAMMA` / `MAXMA` | Gamma / current limit (mA) |

Ready-made samples: [`examples/rgb/`](examples/rgb/).

---

## Sequences (`RGB.SEQ`)

```bash
cp docs/advanced/examples/rgb_seq/01_heartbeat.seq /Volumes/PICO_RGB/RGB.SEQ
```

| Key | Meaning |
|---|---|
| `LOOP` | Full-sequence repeats; `0` = forever |
| `MS` | Step duration (default 1000) |
| `REPEAT` | Repeat current step |
| `TRANS=fade` / `XFADE=` | Cross-fade |

Up to ~16 steps. Deleting `RGB.SEQ` restores the last valid `RGB.INI`.

---

## Frames (`LED.BIN`)

Exactly **36 bytes**: 12 LEDs × `R,G,B`. While present, it overrides `RGB.SEQ` / `RGB.INI`.

```bash
python3 -c 'open("/Volumes/PICO_RGB/LED.BIN","wb").write(bytes([64,0,0]*6+[0,64,0]*6))'
python3 docs/advanced/examples/scripts/led_rainbow.py /Volumes/PICO_RGB/LED.BIN
```

Always flush/`fsync` after writing.

---

## Touch (`TOUCH.TXT`, read-only)

```text
seq=3
evt=click
t_ms=12540
```

`evt`: `none` / `click` / `dblclick` / `long`  

Handle events only when `seq` increases. Poll every 200–500 ms. Do not delete or write this file.

```bash
python3 docs/advanced/examples/scripts/touch_monitor.py /Volumes/PICO_RGB/TOUCH.TXT
```

---

## OTA

Copy an official upgrade package to the volume root and wait. Do not unplug power during upgrade. Use official packages only.

---

## Notes

- This is a tiny config volume, **not a storage card**.
- Full AI / system / music modes still work best with the [Pico Vibe app](../../README.en.md).

[中文](README.md)
