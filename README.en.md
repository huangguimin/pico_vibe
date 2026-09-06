# Pico Vibe

[中文](README.md)

Make status visible.

For **Apple Silicon (M-series) Macs** with a full-size SD card slot. Insert the module, open the app, and drive lights plus touch actions from your Mac.

<p align="center">
  <img src="docs/images/hero-in-use.jpg" alt="Pico Vibe lit on a laptop" width="720" />
</p>

<p align="center"><em>Plug in the module, launch the app — the side lights follow status and music.</em></p>

## Download

Get the **Apple Silicon** installer from Releases:

**[→ Open Releases](https://github.com/huangguimin/pico_vibe/releases/latest)**

Look for: `PicoVibe-*-osx-arm64.dmg` (macOS M-series only for now)

## Install

1. Open the DMG and drag `Pico Vibe.app` into Applications
2. Insert the module; when the app shows connected, you’re ready
3. For Cursor / Codex / Claude Code: install AI integration in the app; Codex users must also trust the hook once via `/hooks`
4. Music mode may ask for Screen & System Audio Recording once (loudness only — no recording, no screen capture)
5. Touch “Next track” may ask for Automation access to Music / Spotify the first time

## Features

### AI ambience

Follows Cursor / Codex / Claude Code — idle, thinking, running, waiting for approval, done, and error each have their own look. When approval is needed, the lights flash yellow; tap the card edge to allow or deny.

![AI ambience UI](docs/images/app-ai.png)

*AI ambience: colors, brightness, and effects per AI state.*

### System status

Map CPU, memory, battery, and similar metrics to the light strip so load is readable at a glance.

![System status UI](docs/images/app-system.png)

*System status: pick a metric, then tune colors and LED range.*

### Music vibe

Lights breathe and pulse with whatever is playing on the system. Sensitivity and vibe style are adjustable in the app.

![Music vibe UI](docs/images/app-music.png)

*Music vibe: live level meter and sensitivity, with a live preview of the strip.*

### Touch

The **TOUCH** area on the exposed card edge supports single tap, double tap, and long press. Actions are configurable per mode, for example:

- **While AI is waiting for approval**: single tap allow, double tap or long press deny (defaults)
- **In music mode**: map to next track (Apple Music / Spotify), change light style, or switch the main mode

Change mappings in the app’s touch settings and save.

<p align="center">
  <img src="docs/images/hardware-inserted.jpg" alt="Module in the SD slot with TOUCH and LEDs exposed" width="560" />
</p>

<p align="center"><em>Once inserted, the LED row and TOUCH zone stay reachable for quick taps.</em></p>

## Advanced mode (no app required)

You can drive the module without Pico Vibe by editing files on `/Volumes/PICO_RGB/`.

| File | Purpose |
|---|---|
| `RGB.INI` | One-line resident effects |
| `RGB.SEQ` | Multi-step sequences |
| `LED.BIN` | 36-byte host frames |
| `TOUCH.TXT` | Read-only touch events |

Docs, sample configs, and Python scripts:

**[→ Advanced mode](docs/advanced/README.en.md)**

```bash
cp docs/advanced/examples/rgb/13_fire.ini /Volumes/PICO_RGB/RGB.INI
python3 docs/advanced/examples/scripts/led_rainbow.py /Volumes/PICO_RGB/LED.BIN
```

Quit the app first when using advanced files so writes do not collide.

