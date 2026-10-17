# PP Enhancer - NVIDIA DLSS for Phoenix Point

Graphics enhancements for Phoenix Point on NVIDIA RTX GPUs: DLSS upscaling and DLAA (DLSS as anti-aliasing), plus optional contact shadows.
Works in the tactical view and on the geoscape. Unofficial fan mod.

## Requirements
- An NVIDIA RTX GPU with a recent driver, Windows, the Steam version of Phoenix Point (Direct3D 11, the default).
- **BepInEx 5, 64-bit** (tested with 5.4.23.5): https://github.com/BepInEx/BepInEx/releases . Extract it into the game folder (next to `PhoenixPointWin64.exe`) and run the game once.
- NVIDIA's DLSS runtime (`nvngx_dlss.dll`) is included in the download.

## Install
1. Install BepInEx 5 as above.
2. Extract the download into the game folder, so that you get `BepInEx\plugins\PPEnhancer\` with the mod's files in it.
3. Start the game and press **F11** (in a mission or on the geoscape). The window says DLSS is running once it is.

If `nvngx_dlss.dll` is missing (an antivirus may remove it) the window says so and the game keeps its normal renderer.

Do not run it together with Modnix-only setups that replace BepInEx; use BepInEx 5. It does not edit the game's files, and it is meant to work with
Terror from the Void and Steam Workshop mods (they change gameplay and data, not the renderer).

## Using it
Open the settings window with **F11**. Every setting has a tooltip and a reset button; hover for help.
- **Quality**: Native (DLAA) renders at full resolution and uses DLSS only as anti-aliasing (best image, highest cost). Ultra Quality, Quality, Balanced and Performance render at
  a smaller internal resolution and upscale it (faster).
- **Texture mip map bias**: below native, samples textures with a negative mip map bias so they stay as sharp as at native. It costs a little GPU time.
- **Contact shadows**: short shadows where objects meet the ground and each other. Strength, Distance, Thickness, Bias, Steps and Screen reach tune them.
  You can bind a key to switch them on and off in the config file (`ToggleKey` in `[ContactShadows]`), handy for comparing.
- **Frame rate limit**: the game caps itself at 60 fps. `FrameRateLimit` in the config can leave that (0), remove the limit (-1) or set a number.
- Settings are saved in `BepInEx\config\mallahowl.ppenhancer.cfg`. Delete that file to start over.

## Known limits
- The home screen camera (`HomeScreen_Camera`) is not processed by default; add it to `Cameras` in the config to try it.
- Only Direct3D 11 is supported. Only the main tactical camera and the geoscape camera are processed.
- With the game's own anti-aliasing option, the mod turns the game's AA off while DLSS runs (setting `DisableGameAA`).
- The mod has been tested on one machine; please report problems with your GPU, driver and what you were doing.

## Troubleshooting
The log is `BepInEx\plugins\PPEnhancer\PPUpscaler.log` (and BepInEx's own `BepInEx\LogOutput.log`). Include them in a bug report.

## Building from source
- Core: `dotnet build -c Release` in `managed`; loader: the same in `loader`. They reference the game's `PhoenixPointWin64_Data\Managed` and BepInEx. Point them at the game with
  the `PP_DIR` environment variable or a git-ignored `local.props` (`<PPDir>`).
- Native plugin: `native\build.bat` with the Visual Studio C++ build tools and NVIDIA's DLSS SDK (https://github.com/NVIDIA/DLSS) in `..\ThirdParty\DLSS`, or set `DLSS_SDK`.
- `package.ps1` builds everything and assembles `dist\PPEnhancer-<version>.zip`. It stops if a build fails, if the native plugin is stale, if NVIDIA's license text is missing,
  or if the output contains a forbidden string.

## Credits and licenses
- PP Enhancer is released under the [MIT License](LICENSE). That covers the code in this repository only.
- **DLSS is a technology of NVIDIA Corporation.** This mod is unofficial and not sponsored or endorsed by NVIDIA. NVIDIA, DLSS, RTX and GeForce RTX are trademarks of NVIDIA Corporation.
- The release download includes two NVIDIA-licensed files, `nvngx_dlss.dll` and `PPUpscalerNative.dll` (which contains statically linked NVIDIA NGX code), under the NVIDIA RTX SDKs License.
  The license text and a notice come with the download (`licenses` and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). The source repository contains no NVIDIA files.
- BepInEx and Harmony do the loading and patching; neither is included here.
- Phoenix Point is a game by Snapshot Games. This is an unofficial fan modification and no game files are included.
