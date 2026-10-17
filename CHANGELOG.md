# Changelog

## 0.9.0 - first public release
- NVIDIA DLSS for Phoenix Point (Direct3D 11): DLAA at native resolution and DLSS upscaling (Ultra Quality, Quality, Balanced, Performance, Ultra Performance, custom scale), in the tactical view and on the geoscape.
- Motion vectors, jitter, exposure and texture sharpness bias set up for the game's renderer; the geoscape globe, clouds, the rotating core and the planet's highlights are handled.
- Ambient occlusion can run at output resolution.
- Contact shadows (optional, on by default): short screen-space shadows toward the sun, with strength, distance, thickness, bias, steps and screen reach settings.
- In-game settings window (F11) with explanations for each setting and reset buttons.
- Optional frame rate limit override (the game limits itself to 60 fps).
- The personnel screen's animated soldier uses the game's own motion vectors, so it is anti-aliased like the rest.
- Changing the quality mode quickly no longer leaves the game slow: the texture sharpness bias is applied once the value has stayed the same for 2 seconds.
- Loads through BepInEx 5; no game files are changed.
