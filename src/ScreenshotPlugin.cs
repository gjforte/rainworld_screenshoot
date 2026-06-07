using BepInEx;
using RWCustom;
using UnityEngine;

namespace Screenshoot
{
    [BepInPlugin(MOD_ID, MOD_NAME, MOD_VERSION)]
    public class ScreenshotPlugin : BaseUnityPlugin
    {
        public const string MOD_ID = "tr0z.screenshoot";
        public const string MOD_NAME = "Screenshoot";
        public const string MOD_VERSION = "0.1.0";

        public static ScreenshotPlugin Instance;
        public static ScreenshotOptions Options;
        public static BepInEx.Logging.ManualLogSource Log;

        // True while a capture run is in progress. Read by the GetCameraBestIndex
        // hook (to stop the camera auto-following the player) and by Update (to
        // ignore further hotkey presses until the run finishes).
        public static bool Capturing;

        private bool _initialized;

        public void OnEnable()
        {
            Instance = this;
            Options = new ScreenshotOptions();
            Log = Logger;

            On.RainWorld.OnModsInit += OnModsInit;
        }

        private void OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);

            if (_initialized) return;
            _initialized = true;

            MachineConnector.SetRegisteredOI(MOD_ID, Options);

            // The only thing that auto-moves the camera between positions to follow
            // the player. Suppressing it during a run lets us drive the camera through
            // every position ourselves without the game yanking it back.
            On.RoomCamera.GetCameraBestIndex += (orig, cam) =>
            {
                if (Capturing) return;
                orig(cam);
            };

            Logger.LogInfo("Screenshoot loaded!");
        }

        public void Update()
        {
            if (Capturing) return;

            bool clean = Input.GetKeyDown(Options.CleanModeKey.Value);
            bool live = Input.GetKeyDown(Options.LiveModeKey.Value);
            if (!clean && !live) return;

            var game = (Custom.rainWorld?.processManager?.currentMainLoop) as RainWorldGame;
            if (game == null || game.cameras == null || game.cameras.Length == 0)
            {
                Logger.LogInfo("Screenshoot: not in a game, ignoring hotkey.");
                return;
            }

            var cam = game.cameras[0];
            if (cam?.room == null || cam.room.cameraPositions == null || cam.room.cameraPositions.Length == 0)
            {
                Logger.LogInfo("Screenshoot: no room loaded, ignoring hotkey.");
                return;
            }

            // `clean` wins if both somehow fired on the same frame.
            StartCoroutine(ScreenshotCapture.Run(game, cleanMode: clean));
        }
    }
}
