using Menu;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace Screenshoot
{
    public class ScreenshotOptions : OptionInterface
    {
        public readonly Configurable<KeyCode> CleanModeKey;
        public readonly Configurable<KeyCode> LiveModeKey;
        public readonly Configurable<int> SettleFrames;
        public readonly Configurable<string> OutputDir;
        public readonly Configurable<bool> DebugDumpFrames;

        public ScreenshotOptions()
        {
            // Clean mode (geometry-only) is still WIP and intentionally not surfaced in
            // the UI or description. The binding stays so F9 keeps working for testing.
            CleanModeKey = config.Bind("CleanModeKey", KeyCode.F9,
                new ConfigurableInfo("(experimental) geometry-only capture."));

            LiveModeKey = config.Bind("LiveModeKey", KeyCode.F10,
                new ConfigurableInfo("Hotkey: full-room screenshot of the current room (HUD hidden)."));

            SettleFrames = config.Bind("SettleFrames", 3,
                new ConfigurableInfo("Frames to wait after switching to each camera before grabbing it. " +
                                     "Raise if cameras come out half-loaded.",
                    new ConfigAcceptableRange<int>(1, 20)));

            OutputDir = config.Bind("OutputDir", "",
                new ConfigurableInfo("Folder to save PNGs in. Leave blank for Pictures\\Rain World Screenshots."));

            DebugDumpFrames = config.Bind("DebugDumpFrames", false,
                new ConfigurableInfo("Also save each individual camera frame next to the stitched image (for diagnosing seams)."));
        }

        public override void Initialize()
        {
            base.Initialize();

            var tab = new OpTab(this, "Options");
            Tabs = new[] { tab };

            float y = 560f;
            const float x = 20f;

            tab.AddItems(new OpLabel(x, y, "Screenshoot", bigText: true));
            y -= 34f;
            tab.AddItems(new OpLabel(x, y, "Stitch every camera in the current room into one full-room PNG, no overlap doubling."));
            y -= 44f;

            tab.AddItems(
                new OpKeyBinder(LiveModeKey, new Vector2(x, y), new Vector2(120f, 30f)),
                new OpLabel(x + 140f, y + 6f, "Full-room screenshot"));
            y -= 50f;

            tab.AddItems(
                new OpSlider(SettleFrames, new Vector2(x, y), 150),
                new OpLabel(x + 180f, y + 4f, "Settle frames per camera"));
            y -= 44f;

            tab.AddItems(
                new OpCheckBox(DebugDumpFrames, x, y),
                new OpLabel(x + 30f, y + 2f, "Dump individual camera frames (debug)"));
            y -= 44f;

            tab.AddItems(new OpLabel(x, y, "Output folder (blank = Pictures\\Rain World Screenshots):"));
            y -= 30f;
            tab.AddItems(new OpTextBox(OutputDir, new Vector2(x, y), 520f));
        }
    }
}
