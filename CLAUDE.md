# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

**Screenshoot** — a Rain World BepInEx plugin (mod id `tr0z.screenshoot`, .NET Framework
4.7.2) that stitches every camera in the *current room* into a single full-room PNG.

The whole point is correct camera-overlap handling. Rain World rooms have multiple
overlapping camera positions. Naive screenshot mods paste each camera's full frame on
top of the next, so anything in an overlap gets drawn twice (ghosted/doubled). This mod
assigns **every output pixel to exactly one camera** — no duplication. The starting
partition is nearest-camera (Voronoi), but each overlap boundary is then rerouted along a
**minimum-error seam** so the cut runs through pixels where the two cameras agree (sky,
flat ground) and detours around foreground objects where parallax makes them disagree.
Still no blending: a pixel comes from one camera; only *where* we switch cameras moves.

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

Clean mode hides every currently-visible sprite in the camera's render layers except the
baked `levelGraphic`/`backgroundGraphic` (see `HideRoomObjects`) — walking the Futile layers
rather than `spriteLeasers`, because the player and some cosmetic sprites render on paths
that aren't standard leasers and would otherwise leak through. Both modes also hide loose UI
overlays attached directly to `Futile.stage` (outside the camera's sprite layers) — e.g. mod
co-op player tags like *The Orphans*' "Orphan1/2" labels — via `HideLooseStageOverlays`.

Hiding is **surgical and reversible**: we record exactly the nodes we hid (`_hiddenNodes`,
only ones that were visible) and `RestoreScene` re-shows only those. A blanket
"show-everything" restore is what previously blacked the screen (it un-hid a full-screen
effect overlay the game keeps hidden, painting over the world while the HUD stayed on top).

Configurable in the Remix options menu (keys, settle frames, output folder). Holding either
Ctrl while pressing a hotkey also dumps each individual camera frame next to the stitched
image (for diagnosing seams) — passed as `dumpFrames` into `Run`; there is no checkbox.
Output defaults to `Pictures\screenshots\` (resolved robustly across Windows/Linux; falls
back to `RainWorld_Data\screenshots` if no Pictures/home folder is findable). The absolute
path is logged.

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
`cam.pos` deltas** (exact in world units), then assign owners in two steps:
1. `BuildOwner` — nearest-centered covering frame per pixel (lowest index wins ties). This
   is the stable Voronoi base, and the guaranteed-safe fallback.
2. `RefineSeams` — for each pair of overlapping frames, reroute the shared boundary into a
   **minimum-error seam**: classify the pair as left/right (vertical seam) or top/bottom
   (horizontal seam) by overlap-rect aspect, then a DP (`ColumnSeamRect`/`RowSeamRect`)
   finds the least-disagreement path (`Cost` = squared RGB diff between the two cameras).
   `RefineVertical`/`RefineHorizontal` only ever swap a pixel *between that pair*, so a
   third camera at a 4-way corner — and any layout that doesn't fit the neighbor model —
   keeps its Voronoi label. Diagonal-only (corner) overlaps are skipped. No blending.

The seam logic was developed and validated offline against a 14-frame dump in
`.scratch/seamproto/` (gitignored): it reproduces the game's Voronoi output exactly, then
the min-error seam cut total seam discontinuity ~94% on ORO_FOREST_W. Re-run that proto to
re-validate any future stitch change.

### Why we don't read the baked texture directly

`RoomCamera.levelTexture` is palette-index + depth **encoded**, not final color — the real
pixels come from the `LevelColor` shader combined with the palette, lightmap, and ~a dozen
effect fields. So we let the GPU render and read the result back, for both modes.

### Key game internals (from decompiling PUBLIC-Assembly-CSharp.dll)

- `RWCustom.Custom.rainWorld.processManager.currentMainLoop as RainWorldGame` → the game.
- `game.cameras[0]` → the active `RoomCamera`; `cam.room.cameraPositions` (`Vector2[]`).
- Each camera's level texture maps 1:1 to world rect `[CamPos[i], CamPos[i] + 1400x800]`,
  with depth/parallax **baked into the pixels** per camera. Overlaps therefore can't align
  perfectly for deep objects. The min-error seam hides this by cutting where the cameras
  match instead of through objects; what it can't fix is a perfectly uniform region (e.g. a
  flat-lit wall), where a faint tonal hairline along the seam can remain. Inherent to the
  baked art, not a bug to chase.
- `cam.pos` is the world coord the screen bottom-left maps to (offset from `CamPos` by
  `hDisplace+8, 18`). We place frames by `pos`, not `CamPos`.

## First-run test / diagnostic plan

The game must be run manually (interactive). On the first run, capture diagnostics:

1. Load a story save, stand in a room with several cameras (e.g. an Outskirts room).
2. Press **Ctrl+F9** (holding Ctrl dumps the individual camera frames too).
3. Check `BepInEx/LogOutput.log` for the `Screenshoot:` lines:
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
