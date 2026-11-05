using BepInEx;
using BepInEx.Configuration;
using FirstShotSmoothness.Diagnostics;
using HarmonyLib;

namespace FirstShotSmoothness
{
    /// <summary>
    /// Finds out why firing a weapon can hitch, and later, once the cause is proven, removes it.
    /// This build measures only: with diagnostics off, the default, it patches nothing and adds
    /// no per-frame work. See docs/INVESTIGATION.md.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class FirstShotSmoothnessPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.firstshotsmoothness";
        public const string PluginName = "First Shot Smoothness";
        public const string PluginVersion = "0.0.1";

        private bool _diagnosticsRunning;

        private void Awake()
        {
            const string Restart = " Needs a game restart.";
            const string NextRaid = " Applies from the next raid.";

            ConfigEntry<bool> enabled = Config.Bind("1. General", "Enabled", true, "Master switch." + Restart);
            ConfigEntry<bool> diagnostics = Config.Bind("2. Diagnostics", "Diagnostics Enabled", false,
                "Records frame times around shots and writes reports to FirstShotSmoothness-reports in the game folder." + Restart);
            ConfigEntry<ProbeLevel> level = Config.Bind("2. Diagnostics", "Probe Level", ProbeLevel.Stages,
                "Stages times each step of the firing path. EventsOnly patches the least, for the cleanest view of first-use JIT." + Restart);
            ConfigEntry<bool> native = Config.Bind("2. Diagnostics", "Runtime JIT And GC Events", true,
                "Records every JIT compile and garbage collection from inside the Mono runtime." + Restart);
            ConfigEntry<int> before = Config.Bind("2. Diagnostics", "Frames Before Shot", 120,
                new ConfigDescription("Frames kept before each shot, used as the baseline." + NextRaid, new AcceptableValueRange<int>(30, 1000)));
            ConfigEntry<int> after = Config.Bind("2. Diagnostics", "Frames After Shot", 300,
                new ConfigDescription("Frames kept after each shot, long enough to cover the impact." + NextRaid, new AcceptableValueRange<int>(30, 2000)));
            ConfigEntry<string> label = Config.Bind("2. Diagnostics", "Run Label", "",
                "Added to the report folder's name, for example T1-run3." + NextRaid);

            const string Isolate = "CAUSAL TEST ONLY, changes the game: ";
            ConfigEntry<bool> muzzle = Config.Bind("3. Isolation Tests", "Suppress Muzzle Effects", false, Isolate + "no muzzle flash or smoke on your shots." + Restart);
            ConfigEntry<bool> audio = Config.Bind("3. Isolation Tests", "Suppress Weapon Audio", false, Isolate + "your gun makes no sound." + Restart);
            ConfigEntry<bool> impacts = Config.Bind("3. Isolation Tests", "Suppress Impact Effects", false, Isolate + "your bullets leave no impact effects." + Restart);
            ConfigEntry<bool> hearing = Config.Bind("3. Isolation Tests", "Suppress Bot Hearing", false, Isolate + "bots do not hear your shots." + Restart);

            if (!enabled.Value || !diagnostics.Value)
            {
                return;
            }

            var settings = new DiagnosticsSettings
            {
                FramesBefore = () => before.Value,
                FramesAfter = () => after.Value,
                RunLabel = () => label.Value,
                Level = level.Value,
                NativeEvents = native.Value,
                IsolateMuzzle = muzzle.Value,
                IsolateAudio = audio.Value,
                IsolateImpacts = impacts.Value,
                IsolateHearing = hearing.Value
            };
            DiagnosticsHost.Start(settings, new Harmony(PluginGuid), Logger);
            _diagnosticsRunning = true;
        }

        private void Update()
        {
            if (_diagnosticsRunning)
            {
                FrameProbe.Watchdog();
            }
        }

        private void OnApplicationQuit()
        {
            if (_diagnosticsRunning)
            {
                DiagnosticsHost.OnQuit();
            }
        }
    }
}
