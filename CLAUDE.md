# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

**Screenshoot** — a Rain World BepInEx plugin (mod id `tr0z.screenshoot`, .NET Framework
4.7.2) that stitches every camera in the *current room* into a single full-room PNG.

The whole point is correct camera-overlap handling. Rain World rooms have multiple
overlapping camera positions. Naive screenshot mods paste each camera's full frame on
top of the next, so anything in an overlap gets drawn twice (ghosted/doubled). This mod
assigns **every output pixel to exactly one camera** — the nearest camera (by center)
among those that actually cover that pixel. That produces a hard seam down the middle of
each overlap, with no duplication.

## Build

```
dotnet build
```

The build copies `Screenshoot.dll` + `modinfo.json` into
`Rain World/RainWorld_Data/StreamingAssets/mods/screenshoot/`. Rain World install path is
assumed to be `C:\Program Files (x86)\Steam\steamapps\common\Rain World`.

## Usage

In-game, in a room:
- **F9** — clean shot (room geometry only; creatures, player and HUD hidden).
- **F10** — live shot (creatures, player, weather; only the HUD is hidden).

Configurable in the Remix options menu (keys, settle frames, output folder, debug dump).
Output defaults to `Pictures\Rain World Screenshots\`. The absolute path is logged.

## Architecture

- `src/ScreenshotPlugin.cs` — entry point. Reads the hotkeys in `Update()` and starts the
  capture coroutine. Hooks `On.RoomCamera.GetCameraBestIndex` to a no-op **while capturing**
  so the camera doesn't auto-follow the player as we drive it through positions.
- `src/ScreenshotCapture.cs` — the capture coroutine + stitcher (see below).
- `src/ScreenshotOptions.cs` — Remix `OptionInterface` (key binders, settle-frame slider,
  output-dir text box, debug-dump checkbox).

### How capture works (the important bit)

For each camera index `i` in `room.cameraPositions`:
1. `cam.MoveCamera(i)` — this loads the camera's level-texture bytes **synchronously**
   (`File.ReadAllBytes`), so we immediately call `cam.ApplyPositionChange()` to apply it
   deterministically rather than waiting for `RoomCamera.Update` to notice next frame.
2. Wait `SettleFrames` frames (reasserting the hidden HUD/sprites each frame) so
   `GrafUpdate` redraws the level/background/lightmap at the new position.
3. `yield return new WaitForEndOfFrame()` then `Texture2D.ReadPixels` the back buffer.
4. Record `cam.pos` (the world-space draw position) alongside the pixels.

Then stitch (`Stitch`): place each frame on a common pixel grid using **relative
`cam.pos` deltas** (exact in world units), and for each canvas pixel pick the
nearest-centered frame that covers it (lowest index wins ties → stable seam). No blending.

### Why we don't read the baked texture directly

`RoomCamera.levelTexture` is palette-index + depth **encoded**, not final color — the real
pixels come from the `LevelColor` shader combined with the palette, lightmap, and ~a dozen
effect fields. So we let the GPU render and read the result back, for both modes.

### Key game internals (from decompiling PUBLIC-Assembly-CSharp.dll)

- `RWCustom.Custom.rainWorld.processManager.currentMainLoop as RainWorldGame` → the game.
- `game.cameras[0]` → the active `RoomCamera`; `cam.room.cameraPositions` (`Vector2[]`).
- Each camera's level texture maps 1:1 to world rect `[CamPos[i], CamPos[i] + 1400x800]`,
  with depth/parallax **baked into the pixels** per camera. Overlaps therefore can't align
  perfectly for deep objects — the midline seam minimizes but can't remove this. Inherent
  to the art, not a bug to chase.
- `cam.pos` is the world coord the screen bottom-left maps to (offset from `CamPos` by
  `hDisplace+8, 18`). We place frames by `pos`, not `CamPos`.

## First-run test / diagnostic plan

The game must be run manually (interactive). On the first run, capture diagnostics:

1. Enable **Dump individual camera frames** in Remix options.
2. Load a story save, stand in a room with several cameras (e.g. an Outskirts room).
3. Press **F9**. Check `BepInEx/LogOutput.log` for the `Screenshoot:` lines:
   - `ScreenSize` vs `Screen=WxH` — **must match**; if not, the pixel↔world scale is off.
   - Per-camera `CamPos` and `pos`, and the final canvas size.
4. Open the output folder and inspect:
   - **The per-camera `*_camN.png` frames** — are they crisp/native, full room art, no HUD?
   - **The stitched PNG** — do seams line up? Any black gaps (missing coverage)? Any doubled
     elements (overlap bug not fixed)?

**Test at your highest resolution, in a normal-sized room (e.g. Outskirts) first** — both
minimize the two most likely failures below.

### Things most likely to need iteration (verify empirically, don't assume)

- **Transparent gaps (the #1 risk).** We capture the on-screen `ScreenSize.x` world units,
  but each camera's texture is 1400 wide. Adjacent cameras spaced `Δ` apart leave a gap iff
  `Δ > ScreenSize.x` (same for Y vs `ScreenSize.y`). The stitch log prints the uncovered
  pixel %; if it's non-trivial and you see transparent strips, this is it. Higher resolution
  shrinks/removes it. The real fix is to capture the full 1400×800 level texture per camera
  via an offscreen RenderTexture instead of the screen — that's gap-free up to `Δ ≤ 1400`.
- **All-black captures.** If `RWCustom.Custom.rainWorld.MainCamera.targetTexture != null`,
  the game renders to a RenderTexture, not the backbuffer. `CaptureScreen` already redirects
  `ReadPixels` to that RT when present; if shots are still black, log whether `targetTexture`
  was non-null and confirm the redirect fired. (Pure-black with a null target → likely
  `WaitForEndOfFrame` timing; raise `SettleFrames`.)
- **Large room → no PNG.** `SavePng` builds a `Texture2D(w,h)`; if either dimension exceeds
  ~16384 (GPU max) it throws (caught + logged, no file written). The stitch logs a warning
  when the canvas is over the limit. Don't read this as "mod broken" — test a smaller room.
- **Clean-mode hiding** — `SetSpritesVisible` hides all sprite leasers. Room-camera-level
  effects (rain overlay `fullScreenEffect`, darkness, snow) are *not* leasers and may still
  tint clean shots. Add handling if they show up.
- **Seam alignment** — if frames are offset from each other, re-check the `pos` mapping and
  the `ScreenSize/Screen` scale (they should be equal).
- **WaitForEndOfFrame timing** — if captures come out as the *previous* camera or
  half-loaded, raise `SettleFrames`.

## Notes / patterns

- Hooks registered in `OnModsInit` with an `_initialized` guard (matches the other tr0z mods).
- `MachineConnector.SetRegisteredOI(MOD_ID, Options)` links the Remix panel.
- `.scratch/` and `.scratch_custom/` (gitignored) hold decompiled game source used as
  reference — regenerate with `ilspycmd <dll> -t <Type> -o ./.scratch`.
```
