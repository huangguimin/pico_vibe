---
name: pico-vibe-status
description: Install, verify, or diagnose the pico_sdio RGB status-light and touch-approval integration for Cursor, Codex, or Claude Code.
---

# Pico Vibe Status

Use the packaged Pico Vibe application and its maintained installer rather than
recreating hook JSON or writing the RAM disk from several processes.

- Customer deployment: extract the release ZIP and run `INSTALL_PICO_VIBE.cmd`.
- Developer packaging: run `BUILD_PICO_VIBE_RELEASE.cmd`; the customer ZIP is
  self-contained and does not require Python or a .NET installation.
- Open the control panel from the Start menu, tray icon, or with
  `PicoVibeHook.exe control show`. Use it to select AI/system/music mode,
  brightness, effects, permission scope, and per-context touch mappings.
- Uninstall with `UNINSTALL_PICO_VIBE.cmd`. User presets under
  `%LOCALAPPDATA%\PicoVibe` are intentionally preserved.
- Diagnose the board by checking that the `PICO_RGB` volume contains
  `VIBE0.BIN` and `VIBE1.BIN`, both exactly 512 bytes. Logs are stored at
  `%LOCALAPPDATA%\PicoVibe\pico-vibe.log`.

For Codex and Claude Code, use their native `PermissionRequest` hooks. Do not simulate Enter/Esc or turn every `PreToolUse` event into an approval: that consumes touches during ordinary running work. A Claude `Notification` is not an approval boundary and must never arm touch approval. If no device is mounted or a touch times out, decline to decide so the AI can show its normal approval UI.

The desktop broker is the sole normal device writer. A running/tools event must
never arm the yellow permission state. Only a real `PermissionRequest` creates
a fresh approval window; an earlier touch cannot approve a later request.

After install or upgrade, remind the user to open `/hooks` once and trust the
new or changed user-level hook. Never bypass hook trust automatically.
