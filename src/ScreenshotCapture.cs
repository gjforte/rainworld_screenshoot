using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Screenshoot
{
    /// <summary>
    /// Drives the active RoomCamera through every camera position in the current
    /// room, grabs one screen frame per position, and stitches them into a single
    /// full-room PNG.
    ///
    /// The anti-redundancy trick: every output pixel is sourced from exactly ONE
    /// camera — the nearest camera (by center) among those that actually cover the
    /// pixel. That's a hard seam along the perpendicular bisector between adjacent
    /// camera centers, i.e. the middle of each overlap. No alpha blending, so the
    /// doubled/ghosted elements you get from pasting whole frames on top of each
    /// other never appear.
    ///
    /// Parallax note: Rain World bakes depth displacement per camera, so a deep
    /// object genuinely sits at slightly different spots in two overlapping
    /// cameras. The midline seam minimizes that discontinuity but cannot remove it
    /// — that's inherent to the art, not a bug.
    /// </summary>
    internal static class ScreenshotCapture
    {
        private struct Frame
        {
            public Color32[] px;     // bottom-up, length w*h
            public int w, h;
            public int ox, oy;       // pixel offset of bottom-left within the canvas
            public float cx, cy;     // center, in canvas pixel coords
            public int index;        // camera position index (for tie-breaks)
            public Vector2 worldPos; // recorded cam.pos at capture time (diagnostics)
        }

        public static IEnumerator Run(RainWorldGame game, bool cleanMode)
        {
            ScreenshotPlugin.CleanMode = cleanMode;
            ScreenshotPlugin.Capturing = true;

            RoomCamera cam = game.cameras[0];
            Room room = cam.room;
            string roomName = room.abstractRoom?.name ?? "room";
            int nCams = room.cameraPositions.Length;
            int originalPos = cam.currentCameraPosition;
            int settle = Mathf.Max(1, ScreenshotPlugin.Options.SettleFrames.Value);

            var log = ScreenshotPlugin.Log;
            log.LogInfo($"Screenshoot: capturing '{roomName}' ({nCams} cameras), " +
                        $"mode={(cleanMode ? "clean" : "live")}, " +
                        $"ScreenSize={game.rainWorld.options.ScreenSize}, " +
                        $"Screen={Screen.width}x{Screen.height}, settle={settle}");

            var frames = new List<Frame>(nCams);
            Exception failure = null;

            try
            {
                for (int i = 0; i < nCams; i++)
                {
                    cam.MoveCamera(i);
                    // MoveCamera loads the texture bytes synchronously (File.ReadAllBytes)
                    // and flags it for applying, so we apply the position change immediately
                    // and deterministically instead of waiting for RoomCamera.Update next frame.
                    ApplyIfReady(cam);

                    // Let GrafUpdate redraw the level/background/lightmap at the new
                    // position for a few frames so the screen actually shows it. The
                    // DrawUpdate hook hides HUD/sprites each of these frames.
                    for (int s = 0; s < settle; s++)
                        yield return null;

                    // Read the back buffer only after the frame has finished rendering.
                    yield return new WaitForEndOfFrame();

                    Frame f;
                    try
                    {
                        f = CaptureScreen(i, cam.pos);
                    }
                    catch (Exception e)
                    {
                        failure = e;
                        break;
                    }
                    frames.Add(f);
                    log.LogInfo($"Screenshoot:   cam {i}: CamPos={cam.CamPos(i)} pos={cam.pos} ({f.w}x{f.h})");
                }
            }
            finally
            {
                // Stop the DrawUpdate hook from hiding anything further, then restore.
                ScreenshotPlugin.Capturing = false;
                // The hook hid the HUD container (isVisible=false) every frame and nothing
                // turns it back on, so explicitly restore. RestoreScene now re-shows only the
                // nodes we actually hid (see _hiddenNodes), not a blanket show-everything.
                for (int c = 0; c < game.cameras.Length; c++)
                    if (game.cameras[c] != null) RestoreScene(game.cameras[c]);
                cam.MoveCamera(originalPos);
                ApplyIfReady(cam);
            }

            if (failure != null)
            {
                log.LogError($"Screenshoot: capture failed: {failure}");
                yield break;
            }
            if (frames.Count == 0)
            {
                log.LogError("Screenshoot: no frames captured.");
                yield break;
            }

            // Stitching + PNG encoding is pure CPU; doing it after restore keeps the
            // visible glitch (hidden HUD/creatures) as short as possible.
            try
            {
                Stitch(frames, out Color32[] canvas, out int cw, out int ch);
                string dir = OutputDir();
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string tag = cleanMode ? "clean" : "live";
                string path = Path.Combine(dir, $"{roomName}_{tag}_{stamp}.png");
                SavePng(canvas, cw, ch, path);
                log.LogInfo($"Screenshoot: saved {cw}x{ch} -> {path}");

                if (ScreenshotPlugin.Options.DebugDumpFrames.Value)
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var f = frames[i];
                        string fp = Path.Combine(dir, $"{roomName}_{tag}_{stamp}_cam{f.index}.png");
                        SavePng(f.px, f.w, f.h, fp);
                    }
                    log.LogInfo($"Screenshoot: dumped {frames.Count} raw camera frames.");
                }
            }
            catch (Exception e)
            {
                log.LogError($"Screenshoot: stitch/save failed: {e}");
            }
        }

        // Apply a queued camera-position change now, but only when the game has
        // actually flagged one and the texture bytes are present (avoids decoding
        // an empty buffer if the file was missing, and avoids a redundant re-apply).
        private static void ApplyIfReady(RoomCamera cam)
        {
            if (cam.applyPosChangeWhenTextureIsLoaded
                && cam.preLoadedTexture != null && cam.preLoadedTexture.Length > 0)
                cam.ApplyPositionChange();
        }

        private static Frame CaptureScreen(int index, Vector2 worldPos)
        {
            // The game usually renders to the backbuffer (active == null), but in some
            // modes it renders to a RenderTexture (isRT / mainCamera.targetTexture).
            // ReadPixels reads whatever is the active target, so point it at the RT when
            // there is one — otherwise we'd read an all-black backbuffer.
            RenderTexture rt = null;
            try { rt = RWCustom.Custom.rainWorld?.MainCamera?.targetTexture; } catch { }

            int w = rt != null ? rt.width : Screen.width;
            int h = rt != null ? rt.height : Screen.height;

            RenderTexture prevActive = RenderTexture.active;
            if (rt != null) RenderTexture.active = rt;
            try
            {
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tex.Apply();
                Color32[] px = tex.GetPixels32(); // row 0 = bottom
                UnityEngine.Object.Destroy(tex);
                return new Frame { px = px, w = w, h = h, index = index, worldPos = worldPos };
            }
            finally
            {
                if (rt != null) RenderTexture.active = prevActive;
            }
        }

        /// <summary>
        /// Place every frame on a common pixel grid (using relative cam.pos deltas,
        /// which are exact in world units), then for each canvas pixel pick the
        /// nearest covering camera. Lowest index wins ties, so the seam is stable.
        /// </summary>
        private static void Stitch(List<Frame> frames, out Color32[] canvas, out int cw, out int ch)
        {
            // World units per captured pixel. Equal across frames (same resolution),
            // so the exact value only sets canvas scale, never alignment.
            Vector2 screenSize = ScreenshotPlugin.Instance != null
                ? Custom_ScreenSize()
                : new Vector2(frames[0].w, frames[0].h);
            float scaleX = screenSize.x / frames[0].w;
            float scaleY = screenSize.y / frames[0].h;
            if (scaleX <= 0f || float.IsNaN(scaleX)) scaleX = 1f;
            if (scaleY <= 0f || float.IsNaN(scaleY)) scaleY = 1f;

            float minX = float.MaxValue, minY = float.MaxValue;
            for (int i = 0; i < frames.Count; i++)
            {
                minX = Mathf.Min(minX, frames[i].worldPos.x);
                minY = Mathf.Min(minY, frames[i].worldPos.y);
            }

            int maxRight = 0, maxTop = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                f.ox = Mathf.RoundToInt((f.worldPos.x - minX) / scaleX);
                f.oy = Mathf.RoundToInt((f.worldPos.y - minY) / scaleY);
                f.cx = f.ox + f.w * 0.5f;
                f.cy = f.oy + f.h * 0.5f;
                frames[i] = f;
                maxRight = Mathf.Max(maxRight, f.ox + f.w);
                maxTop = Mathf.Max(maxTop, f.oy + f.h);
            }

            cw = maxRight;
            ch = maxTop;
            canvas = new Color32[cw * ch]; // default (0,0,0,0) = transparent for uncovered gaps

            var log = ScreenshotPlugin.Log;
            if (cw > 16384 || ch > 16384)
                log.LogWarning($"Screenshoot: canvas {cw}x{ch} exceeds the 16384 GPU texture limit; " +
                               "PNG encoding will likely fail. Try a smaller room.");

            // Assign every covered pixel to exactly one camera: start from the nearest
            // covering camera (Voronoi), then reroute each overlap boundary into a
            // minimum-error seam so the cut runs through pixels where the two cameras
            // agree (sky, flat ground) instead of slicing across foreground objects.
            // Still no blending — each output pixel comes from a single camera; only
            // *where* we switch cameras moves.
            int[] owner = BuildOwner(frames, cw, ch);
            RefineSeams(frames, owner, cw, ch);

            long covered = 0;
            for (int k = 0; k < owner.Length; k++)
            {
                int oi = owner[k];
                if (oi < 0) continue;
                Frame f = frames[oi];
                int x = k % cw, y = k / cw;
                canvas[k] = f.px[(y - f.oy) * f.w + (x - f.ox)];
                covered++;
            }

            long total = (long)cw * ch;
            long gaps = total - covered;

            // The cameras don't tile into a perfect rectangle (their CamPos differ by a
            // few px between rows/columns), so the bounding-box canvas has thin see-through
            // slivers around the edges — which render as white in image viewers. Fill them
            // from the nearest covered pixel so the output is a clean, fully-opaque image.
            // (Large interior gaps from wide camera spacing would also be filled here, but
            // crudely; that case still wants the offscreen-RT full-texture capture.)
            int filled = FillUncovered(canvas, cw, ch);

            log.LogInfo($"Screenshoot: canvas {cw}x{ch}, uncovered (transparent) pixels: " +
                        $"{gaps} ({100f * gaps / total:0.00}%) — filled {filled} edge pixels.");
        }

        // Dilate opaque pixels inward to cover transparent ones; force the rest opaque.
        private static int FillUncovered(Color32[] canvas, int cw, int ch)
        {
            var opaque = new bool[canvas.Length];
            int remaining = 0;
            for (int i = 0; i < canvas.Length; i++)
            {
                opaque[i] = canvas[i].a > 0;
                if (!opaque[i]) remaining++;
            }
            int total = remaining;

            // One pixel of growth per pass; slivers are only a few px, so this converges fast.
            for (int pass = 0; pass < 16 && remaining > 0; pass++)
            {
                bool filledAny = false;
                for (int y = 0; y < ch; y++)
                {
                    int row = y * cw;
                    for (int x = 0; x < cw; x++)
                    {
                        int idx = row + x;
                        if (opaque[idx]) continue;
                        // Read neighbours from the pre-pass snapshot so fills don't smear sideways.
                        int src = -1;
                        if (x > 0 && opaque[idx - 1]) src = idx - 1;
                        else if (x < cw - 1 && opaque[idx + 1]) src = idx + 1;
                        else if (y > 0 && opaque[idx - cw]) src = idx - cw;
                        else if (y < ch - 1 && opaque[idx + cw]) src = idx + cw;
                        if (src >= 0)
                        {
                            Color32 c = canvas[src];
                            c.a = 255;
                            canvas[idx] = c;
                            filledAny = true;
                            remaining--;
                        }
                    }
                }
                if (!filledAny) break;
                for (int i = 0; i < canvas.Length; i++) opaque[i] = canvas[i].a > 0;
            }

            // Safety: anything still uncovered (e.g. a fully-empty row) becomes opaque black,
            // never transparent — so it can't show up white in a viewer.
            for (int i = 0; i < canvas.Length; i++)
                if (canvas[i].a == 0) canvas[i] = new Color32(0, 0, 0, 255);

            return total;
        }

        // Nearest-covering-camera label per canvas pixel (-1 = uncovered). Iterating in
        // index order with strict '<' keeps the lowest index on ties, so the base
        // partition is the same stable Voronoi the mod always used. The seam refinement
        // below only moves boundaries between genuine neighbors away from this base.
        private static int[] BuildOwner(List<Frame> frames, int cw, int ch)
        {
            int[] owner = new int[cw * ch];
            int n = frames.Count;
            for (int y = 0; y < ch; y++)
            {
                int row = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    int best = -1; float bestD = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        Frame f = frames[i];
                        if (x < f.ox || x >= f.ox + f.w || y < f.oy || y >= f.oy + f.h) continue;
                        float dx = x + 0.5f - f.cx, dy = y + 0.5f - f.cy;
                        float d = dx * dx + dy * dy;
                        if (best < 0 || d < bestD) { bestD = d; best = i; }
                    }
                    owner[row + x] = best;
                }
            }
            return owner;
        }

        // For every pair of overlapping frames, reroute their shared boundary into a
        // minimum-error seam. A pair is a left/right neighbor (vertical seam) when their
        // overlap is taller than wide, or a top/bottom neighbor (horizontal seam) when
        // wider than tall; corner-only (diagonal) overlaps are left to the Voronoi base.
        // Refinement only ever swaps a pixel between the two cameras of the pair, so a
        // third camera at a 4-way corner — and any layout that doesn't fit — keeps its
        // safe Voronoi label.
        private static void RefineSeams(List<Frame> frames, int[] owner, int cw, int ch)
        {
            int n = frames.Count;
            var vert = new List<int[]>(); // a = left  (smaller cx)
            var horz = new List<int[]>(); // a = upper (smaller cy)
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Frame fi = frames[i], fj = frames[j];
                    int xa = Math.Max(fi.ox, fj.ox), xb = Math.Min(fi.ox + fi.w, fj.ox + fj.w);
                    int ya = Math.Max(fi.oy, fj.oy), yb = Math.Min(fi.oy + fi.h, fj.oy + fj.h);
                    int ow = xb - xa, oh = yb - ya;
                    if (ow <= 0 || oh <= 0) continue;
                    int hMin = Math.Min(fi.h, fj.h), wMin = Math.Min(fi.w, fj.w);
                    if (oh >= ow && oh >= hMin / 2)
                        vert.Add(fi.cx <= fj.cx ? new[] { i, j } : new[] { j, i });
                    else if (ow > oh && ow >= wMin / 2)
                        horz.Add(fi.cy <= fj.cy ? new[] { i, j } : new[] { j, i });
                }

            // Columns first, then rows — matches the validated ordering.
            foreach (int[] p in vert) RefineVertical(frames, owner, cw, p[0], p[1]);
            foreach (int[] p in horz) RefineHorizontal(frames, owner, cw, p[0], p[1]);
        }

        // Left camera a, right camera b: a min-error vertical path (one x per row)
        // through the overlap; pixels left of it go to a, the rest to b.
        private static void RefineVertical(List<Frame> frames, int[] owner, int cw, int a, int b)
        {
            Frame fa = frames[a], fb = frames[b];
            int xa = Math.Max(fa.ox, fb.ox), xb = Math.Min(fa.ox + fa.w, fb.ox + fb.w);
            int ya = Math.Max(fa.oy, fb.oy), yb = Math.Min(fa.oy + fa.h, fb.oy + fb.h);
            int[] seam = ColumnSeamRect(frames, a, b, xa, xb, ya, yb);
            for (int y = ya; y < yb; y++)
            {
                int sx = seam[y - ya], row = y * cw;
                for (int x = xa; x < xb; x++)
                {
                    int k = row + x;
                    if (owner[k] == a || owner[k] == b) owner[k] = (x < sx) ? a : b;
                }
            }
        }

        // Upper camera a, lower camera b: a min-error horizontal path (one y per column);
        // pixels on the smaller-y side go to a, the rest to b.
        private static void RefineHorizontal(List<Frame> frames, int[] owner, int cw, int a, int b)
        {
            Frame fa = frames[a], fb = frames[b];
            int xa = Math.Max(fa.ox, fb.ox), xb = Math.Min(fa.ox + fa.w, fb.ox + fb.w);
            int ya = Math.Max(fa.oy, fb.oy), yb = Math.Min(fa.oy + fa.h, fb.oy + fb.h);
            int[] seam = RowSeamRect(frames, a, b, xa, xb, ya, yb);
            for (int x = xa; x < xb; x++)
            {
                int sy = seam[x - xa];
                for (int y = ya; y < yb; y++)
                {
                    int k = y * cw + x;
                    if (owner[k] == a || owner[k] == b) owner[k] = (y < sy) ? a : b;
                }
            }
        }

        // Minimum-error vertical seam over canvas rect [xa,xb) x [ya,yb): dynamic program
        // top-to-bottom, each row's cut at most one pixel from the row above. Returns the
        // crossover x for each row. Cost is how much the two cameras disagree there.
        private static int[] ColumnSeamRect(List<Frame> frames, int a, int b, int xa, int xb, int ya, int yb)
        {
            int W = xb - xa, H = yb - ya;
            double[,] M = new double[H, W];
            int[,] back = new int[H, W];
            for (int xi = 0; xi < W; xi++) M[0, xi] = Cost(frames, a, b, xa + xi, ya);
            for (int yi = 1; yi < H; yi++)
                for (int xi = 0; xi < W; xi++)
                {
                    double best = M[yi - 1, xi]; int bk = xi;
                    if (xi > 0 && M[yi - 1, xi - 1] < best) { best = M[yi - 1, xi - 1]; bk = xi - 1; }
                    if (xi < W - 1 && M[yi - 1, xi + 1] < best) { best = M[yi - 1, xi + 1]; bk = xi + 1; }
                    M[yi, xi] = Cost(frames, a, b, xa + xi, ya + yi) + best;
                    back[yi, xi] = bk;
                }
            int cur = 0; double bv = double.MaxValue;
            for (int xi = 0; xi < W; xi++) if (M[H - 1, xi] < bv) { bv = M[H - 1, xi]; cur = xi; }
            int[] seam = new int[H];
            for (int yi = H - 1; yi >= 0; yi--) { seam[yi] = xa + cur; cur = back[yi, cur]; }
            return seam;
        }

        // Minimum-error horizontal seam over canvas rect: DP left-to-right, returns the
        // crossover y for each column.
        private static int[] RowSeamRect(List<Frame> frames, int a, int b, int xa, int xb, int ya, int yb)
        {
            int W = xb - xa, H = yb - ya;
            double[,] M = new double[W, H];
            int[,] back = new int[W, H];
            for (int yi = 0; yi < H; yi++) M[0, yi] = Cost(frames, a, b, xa, ya + yi);
            for (int xi = 1; xi < W; xi++)
                for (int yi = 0; yi < H; yi++)
                {
                    double best = M[xi - 1, yi]; int bk = yi;
                    if (yi > 0 && M[xi - 1, yi - 1] < best) { best = M[xi - 1, yi - 1]; bk = yi - 1; }
                    if (yi < H - 1 && M[xi - 1, yi + 1] < best) { best = M[xi - 1, yi + 1]; bk = yi + 1; }
                    M[xi, yi] = Cost(frames, a, b, xa + xi, ya + yi) + best;
                    back[xi, yi] = bk;
                }
            int cur = 0; double bv = double.MaxValue;
            for (int yi = 0; yi < H; yi++) if (M[W - 1, yi] < bv) { bv = M[W - 1, yi]; cur = yi; }
            int[] seam = new int[W];
            for (int xi = W - 1; xi >= 0; xi--) { seam[xi] = ya + cur; cur = back[xi, cur]; }
            return seam;
        }

        // Squared RGB disagreement between frames a and b at canvas pixel (x,y); both
        // must cover it (callers only ask within the overlap rect).
        private static long Cost(List<Frame> frames, int a, int b, int x, int y)
        {
            Frame fa = frames[a], fb = frames[b];
            Color32 ca = fa.px[(y - fa.oy) * fa.w + (x - fa.ox)];
            Color32 cb = fb.px[(y - fb.oy) * fb.w + (x - fb.ox)];
            int dr = ca.r - cb.r, dg = ca.g - cb.g, db = ca.b - cb.b;
            return (long)dr * dr + dg * dg + db * db;
        }

        private static Vector2 Custom_ScreenSize()
        {
            try { return RWCustom.Custom.rainWorld.options.ScreenSize; }
            catch { return new Vector2(Screen.width, Screen.height); }
        }

        // ---- scene hiding -------------------------------------------------------

        // Exactly the Futile nodes we hid during the current capture (clean mode). We
        // restore THESE and only these afterward — never a blanket "show everything",
        // which would also reveal sprites the game deliberately keeps hidden (e.g. a
        // full-screen black effect overlay) and black out the world.
        private static readonly HashSet<FNode> _hiddenNodes = new HashSet<FNode>();

        // Reused each frame: the set of camera sprite-layer containers to never hide.
        private static readonly HashSet<FNode> _keepLayers = new HashSet<FNode>();

        // Called from the RoomCamera.DrawUpdate hook every frame during a capture,
        // after the game has drawn its sprites — so the hide sticks through render.
        internal static void ApplyHiding(RoomCamera cam, bool cleanMode)
        {
            SetHud(cam, false);
            if (cleanMode) HideRoomObjects(cam);
            // Always (both modes): hide loose UI overlays attached directly to the stage.
            HideLooseStageOverlays(cam);
        }

        // Some mods (e.g. "The Orphans" co-op player tags) attach labels/sprites directly to
        // the Futile stage, OUTSIDE any camera's sprite layers — so the per-layer hiding above
        // never sees them and they leak into shots. Hide every currently-visible loose stage
        // child that isn't one of the cameras' own sprite-layer containers (which hold the room
        // itself), tracking them so RestoreScene puts back exactly that set. These are UI, like
        // the HUD, so we suppress them in both clean and live modes.
        private static void HideLooseStageOverlays(RoomCamera cam)
        {
            FContainer stage = Futile.stage;
            if (stage == null) return;

            // Never hide the room: collect every camera's sprite-layer containers.
            _keepLayers.Clear();
            RoomCamera[] cams = cam.game != null ? cam.game.cameras : null;
            if (cams != null)
                foreach (RoomCamera rc in cams)
                {
                    if (rc == null || rc.SpriteLayers == null) continue;
                    foreach (FContainer ly in rc.SpriteLayers)
                        if (ly != null) _keepLayers.Add(ly);
                }

            int n = stage.GetChildCount();
            for (int i = 0; i < n; i++)
            {
                FNode node = stage.GetChildAt(i);
                if (node == null || _keepLayers.Contains(node)) continue;
                if (node.isVisible)
                {
                    node.isVisible = false;
                    _hiddenNodes.Add(node);
                }
            }
        }

        private static void RestoreScene(RoomCamera cam)
        {
            SetHud(cam, true);
            // Re-show only what we actually hid. Setting it true is safe even if a node was
            // since removed from its container (isVisible is just a flag); the game re-asserts
            // per-frame culling on the next DrawUpdate anyway.
            foreach (FNode node in _hiddenNodes)
                if (node != null) node.isVisible = true;
            _hiddenNodes.Clear();
        }

        private static void SetHud(RoomCamera cam, bool visible)
        {
            var hud = cam.ReturnFContainer("HUD");
            if (hud != null) hud.isVisible = visible;
            var hud2 = cam.ReturnFContainer("HUD2");
            if (hud2 != null) hud2.isVisible = visible;
        }

        // Hide every CURRENTLY-VISIBLE sprite in the camera's render layers except the baked
        // level and background art, leaving a geometry-only image — and remember each one so
        // RestoreScene can put back exactly that set.
        //
        // We walk the actual Futile layer children rather than cam.spriteLeasers because the
        // leaser list missed things — the player and some cosmetic/background sprites are drawn
        // on paths that didn't show up as standard leasers, so they survived into 'clean' shots.
        // Enumerating the layers themselves catches everything that actually renders.
        //
        // Hiding only the already-visible nodes (and tracking them) is what makes the restore
        // exact: nodes the game kept invisible are never touched, so we can't accidentally
        // un-hide an effect overlay and black out the world.
        private static void HideRoomObjects(RoomCamera cam)
        {
            FContainer[] layers = cam.SpriteLayers;
            if (layers == null) return;

            FNode keepLevel = cam.levelGraphic;
            FNode keepBg = cam.backgroundGraphic;

            for (int l = 0; l < layers.Length; l++)
            {
                FContainer layer = layers[l];
                if (layer == null) continue;
                int n = layer.GetChildCount();
                for (int i = 0; i < n; i++)
                {
                    FNode node = layer.GetChildAt(i);
                    if (node == null || node == keepLevel || node == keepBg) continue;
                    if (node.isVisible)
                    {
                        node.isVisible = false;
                        _hiddenNodes.Add(node);
                    }
                }
            }
        }

        // ---- output -------------------------------------------------------------

        private static string OutputDir()
        {
            string custom = ScreenshotPlugin.Options.OutputDir.Value;
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(PicturesDir(), "screenshots");
        }

        // Resolve a stable, absolute Pictures folder across Windows and Linux.
        //
        // On Linux under Mono, GetFolderPath(MyPictures) is unreliable: when XDG user
        // dirs aren't configured it returns "" (or sometimes $HOME). An empty/relative
        // result would make the save path relative to the game's working directory,
        // which differs depending on how the game was launched — the inconsistency we
        // saw on Linux. So we only trust MyPictures when it's a non-empty absolute path,
        // and otherwise fall back through $HOME/Pictures, $HOME, then MyDocuments.
        private static string PicturesDir()
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrEmpty(pics) && Path.IsPathRooted(pics))
                return pics;

            string home = Environment.GetEnvironmentVariable("HOME")            // Linux/macOS
                          ?? Environment.GetEnvironmentVariable("USERPROFILE");  // Windows fallback
            if (!string.IsNullOrEmpty(home) && Path.IsPathRooted(home))
            {
                string homePics = Path.Combine(home, "Pictures");
                return Directory.Exists(homePics) ? homePics : home;
            }

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (!string.IsNullOrEmpty(docs) && Path.IsPathRooted(docs))
                return docs;

            // Last resort before cwd: drop alongside the game install. Application.dataPath
            // points at the RainWorld_Data folder on a standalone build, which is always a
            // findable, writable absolute path.
            string data = Application.dataPath;
            if (!string.IsNullOrEmpty(data) && Directory.Exists(data))
                return data;

            return Directory.GetCurrentDirectory();
        }

        private static void SavePng(Color32[] px, int w, int h, string path)
        {
            // RGB24 (no alpha) — our images are fully opaque, and dropping alpha means no
            // viewer can ever render a stray transparent pixel as white.
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.SetPixels32(px);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            File.WriteAllBytes(path, png);
        }
    }
}
