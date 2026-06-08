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
                // The hook hid the HUD container (isVisible=false) every frame and
                // nothing turns it back on, so explicitly restore every camera.
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

            long covered = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                for (int y = 0; y < f.h; y++)
                {
                    int canvasY = f.oy + y;
                    int srcRow = y * f.w;
                    int dstRow = canvasY * cw;
                    for (int x = 0; x < f.w; x++)
                    {
                        int canvasX = f.ox + x;
                        if (Owns(frames, i, canvasX, canvasY))
                        {
                            canvas[dstRow + canvasX] = f.px[srcRow + x];
                            covered++;
                        }
                    }
                }
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

        // True if frame `me` is the nearest-centered frame that covers (px,py).
        // Iterating in index order and using strict '<' means lower index wins ties.
        private static bool Owns(List<Frame> frames, int me, int px, int py)
        {
            Frame m = frames[me];
            float md = Sq(px + 0.5f - m.cx) + Sq(py + 0.5f - m.cy);
            for (int j = 0; j < frames.Count; j++)
            {
                if (j == me) continue;
                Frame o = frames[j];
                if (px < o.ox || px >= o.ox + o.w || py < o.oy || py >= o.oy + o.h) continue;
                float d = Sq(px + 0.5f - o.cx) + Sq(py + 0.5f - o.cy);
                if (d < md || (d == md && j < me)) return false;
            }
            return true;
        }

        private static float Sq(float v) => v * v;

        private static Vector2 Custom_ScreenSize()
        {
            try { return RWCustom.Custom.rainWorld.options.ScreenSize; }
            catch { return new Vector2(Screen.width, Screen.height); }
        }

        // ---- scene hiding -------------------------------------------------------

        // Called from the RoomCamera.DrawUpdate hook every frame during a capture,
        // after the game has drawn its sprites — so the hide sticks through render.
        internal static void ApplyHiding(RoomCamera cam, bool cleanMode)
        {
            SetHud(cam, false);
            if (cleanMode) SetRoomObjectsVisible(cam, false);
        }

        private static void RestoreScene(RoomCamera cam)
        {
            SetHud(cam, true);
            SetRoomObjectsVisible(cam, true);
        }

        private static void SetHud(RoomCamera cam, bool visible)
        {
            var hud = cam.ReturnFContainer("HUD");
            if (hud != null) hud.isVisible = visible;
            var hud2 = cam.ReturnFContainer("HUD2");
            if (hud2 != null) hud2.isVisible = visible;
        }

        // Hide (or restore) every sprite in the camera's render layers EXCEPT the baked
        // level and background art, leaving a geometry-only image.
        //
        // We walk the actual Futile layer children rather than cam.spriteLeasers because
        // the leaser list missed things — the player and some cosmetic/background sprites
        // are drawn on paths that didn't show up as standard leasers, so they survived
        // into 'clean' shots. Enumerating the layers themselves catches everything that
        // actually renders, so nothing leaks through.
        private static void SetRoomObjectsVisible(RoomCamera cam, bool visible)
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
                    if (node == null) continue;
                    // Always keep the room art visible; toggle everything else.
                    node.isVisible = (node == keepLevel || node == keepBg) ? true : visible;
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
