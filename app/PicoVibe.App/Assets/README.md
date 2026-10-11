# Pico Vibe application icon

`PicoVibe.png` is the 1024×1024 RGBA master artwork. `PicoVibe.ico` is the
Windows resource generated from that master and contains 16, 20, 24, 32, 40,
48, 64, 128, and 256 pixel frames.

The mark follows the application palette: an indigo rounded tile with mint,
amber, and coral status nodes connected as a shallow rhythmic V. It must remain
legible at 16 pixels and must not gain text or detail outside the tile.

The master was generated with OpenAI's built-in image generation tool on
2026-08-22 from this production prompt:

> Create a polished Windows application icon for Pico Vibe, an RGB status-light
> and touch companion for developers. Center one deep-indigo rounded-square
> tile containing three bold luminous circular status nodes arranged as a
> compact rhythmic shallow V: mint on the left, amber in the center, coral on
> the right, joined by a simple white-violet waveform. Use a crisp geometric
> style with restrained premium depth, strong 16-pixel legibility, transparent
> space outside the tile, and no text, letters, device drawings, hands,
> mascots, watermark, mockup background, or external shadow.

Post-processing removes only disconnected alpha artifacts outside the main
tile, centers the artwork, and resamples it into the listed ICO sizes. Validate
all frames on both light and dark backgrounds before replacing the checked-in
files.
