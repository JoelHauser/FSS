using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Settings;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace FirstShotSmoothness.Diagnostics
{
    /// <summary>Settings a session reads when it starts.</summary>
    internal sealed class DiagnosticsSettings
    {
        public Func<int> FramesBefore;
        public Func<int> FramesAfter;
        public Func<string> RunLabel;
        public ProbeLevel Level;
        public bool NativeEvents;
        public bool IsolateMuzzle;
        public bool IsolateAudio;
        public bool IsolateImpacts;
        public bool IsolateHearing;
        public bool IsolateMuzzleLight;
        public bool HideRounds;
    }

    /// <summary>
    /// Ties the pieces together: installs them once at startup, opens a session when a raid has
    /// a player in it, and hands the finished session to the report writer when the raid ends.
    /// </summary>
    internal static class DiagnosticsHost
    {
        private const double HitchFloorMs = 8;
        private const int KeptFrames = 1 << 17;
        private const int EventRows = 1 << 18;
        private const int ShotRows = 1 << 16;

        private static readonly string[] FiringTypes =
        {
            "FirearmController", "FireOperation", "Firearms", "FirearmsEffects", "MuzzleManager", "WeaponSoundPlayer",
            "SuperBetterAudioQueue", "BetterAudio", "BetterSource", "BallisticsCalculator", "Shot", "Effects",
            "EffectsCommutator", "ClientGameWorld", "GameWorld", "ObjectsFactory", "Pools", "AIData",
            "GlobalEventDispatcher", "BotHearingSensor", "ProceduralWeaponAnimation", "ShotEffector", "Player",
            "Muzzle", "Shell", "AmmoPool", "WeaponPrefab", "Decal", "Corpse", "Ragdoll", "BotsController", "BotOwner", "AITaskManager"
        };

        private static DiagnosticsSettings _settings;
        private static ManualLogSource _log;
        private static List<string> _patchLines;
        private static string _overheadLine = "";
        private static string _reportRoot;
        private static GameWorld _world;
        private static long _nativeFirst;
        private static long _tscStart;
        private static long _qpcStart;
        private static string _header;
        private static string _startedAt;
        private static string _location;
        private static int _raidNumber;

        public static void Start(DiagnosticsSettings settings, Harmony harmony, ManualLogSource log)
        {
            _settings = settings;
            _log = log;
            _reportRoot = Path.Combine(Path.GetDirectoryName(Application.dataPath), "FirstShotSmoothness-reports");
            Recorder.MainThread = Environment.CurrentManagedThreadId;

            // Runtime events go first, to see as much of the startup JIT as the ring will hold.
            if (settings.NativeEvents && !NativeEvents.Install())
            {
                log.LogWarning("Runtime JIT/GC events are off: " + NativeEvents.FailureReason);
            }

            _patchLines = Probes.Install(harmony, settings.Level);
            _patchLines.AddRange(Isolation.Install(harmony, settings.IsolateMuzzle, settings.IsolateAudio, settings.IsolateImpacts, settings.IsolateHearing, settings.IsolateMuzzleLight, settings.HideRounds));
            foreach (string line in _patchLines.Where(l => !l.StartsWith("ok ") && !l.StartsWith("warmed ") && !l.StartsWith(Probes.OptionalAbsent)))
            {
                log.LogWarning(line);
            }

            _patchLines.Add("warmed " + Probes.Warm() + " of this plugin's own methods");
            _overheadLine = MeasureOverhead();
            FrameProbe.Install(Tick);
            log.LogInfo("Diagnostics on. Reports go to " + _reportRoot);
        }

        /// <summary>Time for one probe's own work, measured on a throwaway session.</summary>
        private static string MeasureOverhead()
        {
            const int Rounds = 100000;
            var scratch = new Session(1, 1, HitchFloorMs, 16, 16, 16);
            Session previous = Recorder.Current;
            Recorder.Current = scratch;

            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Rounds; i++)
            {
                Stopwatch.GetTimestamp();
            }
            long clock = Stopwatch.GetTimestamp() - start;

            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Rounds; i++)
            {
                Recorder.Exit(0, Recorder.Enter());
            }
            long probe = Stopwatch.GetTimestamp() - start;

            Recorder.Current = previous;
            double scale = 1e9 / Stopwatch.Frequency / Rounds;
            return "clock_read_ns=" + (clock * scale).ToString("0.0") + " probe_body_ns=" + (probe * scale).ToString("0.0");
        }

        /// <summary>Once a frame, at the top of the loop.</summary>
        private static void Tick()
        {
            bool live = Singleton<GameWorld>.Instantiated;
            GameWorld world = live ? Singleton<GameWorld>.Instance : null;
            if (Recorder.Current == null)
            {
                if (world != null && world.MainPlayer != null)
                {
                    Open(world);
                }
            }
            else if (!ReferenceEquals(world, _world))
            {
                Close();
            }
        }

        public static void OnQuit()
        {
            if (Recorder.Current != null)
            {
                // The process is going away: there is no time for a background write to finish.
                Close(wait: true);
            }
        }

        private static void Open(GameWorld world)
        {
            _world = world;
            _raidNumber++;
            _location = world.LocationId ?? "unknown";
            _startedAt = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _header = Header(world);
            Probes.OnRaidStart(world.MainPlayer.ProfileId);
            if (NativeEvents.Installed)
            {
                _nativeFirst = NativeEvents.Count;
                NativeEvents.SampleClocks(out _tscStart, out _qpcStart);
            }
            Recorder.Current = new Session(_settings.FramesBefore(), _settings.FramesAfter(), HitchFloorMs, KeptFrames, EventRows, ShotRows);
            _log.LogInfo("Recording raid " + _raidNumber + " on " + _location);
        }

        private static void Close(bool wait = false)
        {
            Session session = Recorder.Current;
            Recorder.Current = null;
            _world = null;
            session.Finish();
            if (session.ShotCount == 0)
            {
                // The hideout and raids with no shots: nothing to report.
                return;
            }

            var range = new ReportWriter.NativeRange { First = _nativeFirst };
            if (NativeEvents.Installed)
            {
                range.Last = NativeEvents.Count;
                range.TscStart = _tscStart;
                range.QpcStart = _qpcStart;
                NativeEvents.SampleClocks(out range.TscEnd, out range.QpcEnd);
            }

            string label = Sanitize(_settings.RunLabel());
            string folder = Path.Combine(_reportRoot, _startedAt + "-raid" + _raidNumber + "-" + _location + (label.Length > 0 ? "-" + label : ""));
            string[] names = Probes.NameTable.ToArray();
            string header = _header;
            ManualLogSource log = _log;
            Action write = () =>
            {
                try
                {
                    ReportWriter.Write(folder, session, header, names, range);
                    log.LogInfo("Report written: " + folder);
                }
                catch (Exception e)
                {
                    log.LogError("Report failed: " + e);
                }
            };
            if (wait)
            {
                write();
            }
            else
            {
                Task.Run(write);
            }
        }

        private static string Sanitize(string label)
        {
            var text = new StringBuilder();
            foreach (char c in label ?? "")
            {
                text.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return text.ToString();
        }

        /// <summary>The run's fixed facts. Main thread, at raid start, off every measured path.</summary>
        private static string Header(GameWorld world)
        {
            var text = new StringBuilder();
            text.AppendLine("[build]");
            text.AppendLine("plugin_version=" + FirstShotSmoothnessPlugin.PluginVersion);
            text.AppendLine("run_label=" + _settings.RunLabel());
            text.AppendLine("started=" + _startedAt);
            text.AppendLine("raid_number_this_launch=" + _raidNumber);
            text.AppendLine("seconds_since_app_start=" + Time.realtimeSinceStartup.ToString("0.0"));
            text.AppendLine("location=" + _location);
            text.AppendLine("world_type=" + world.GetType().Name);
            text.AppendLine("player_type=" + world.MainPlayer.GetType().Name);
            text.AppendLine("game_version=" + Application.version);
            text.AppendLine("unity_version=" + Application.unityVersion);
            text.AppendLine("clr_version=" + Environment.Version);
            text.AppendLine("debug_build=" + UnityEngine.Debug.isDebugBuild);
            text.AppendLine("probe_level=" + _settings.Level);
            text.AppendLine();

            // Mono's Stopwatch need not count in raw QPC units, which is what PresentMon writes.
            text.AppendLine("[clock]");
            ClockSync.Append(text);
            text.AppendLine();

            text.AppendLine("[system]");
            text.AppendLine("cpu=" + SystemInfo.processorType);
            text.AppendLine("cpu_threads=" + SystemInfo.processorCount);
            text.AppendLine("ram_mb=" + SystemInfo.systemMemorySize);
            text.AppendLine("gpu=" + SystemInfo.graphicsDeviceName);
            text.AppendLine("gpu_driver=" + SystemInfo.graphicsDeviceVersion);
            text.AppendLine("graphics_api=" + SystemInfo.graphicsDeviceType);
            text.AppendLine("graphics_multithreaded=" + SystemInfo.graphicsMultiThreaded);
            text.AppendLine("rendering_threading_mode=" + SystemInfo.renderingThreadingMode);
            text.AppendLine("screen=" + Screen.width + "x" + Screen.height + " fullscreen=" + Screen.fullScreenMode);
            text.AppendLine("refresh_hz=" + Screen.currentResolution.refreshRateRatio.value.ToString("0.##"));
            text.AppendLine("vsync_count=" + QualitySettings.vSyncCount);
            text.AppendLine("target_frame_rate=" + Application.targetFrameRate);
            text.AppendLine("quality_level=" + QualitySettings.names[QualitySettings.GetQualityLevel()]);
            text.AppendLine();

            text.AppendLine("[capabilities]");
            text.AppendLine("stopwatch_high_resolution=" + Stopwatch.IsHighResolution);
            text.AppendLine("profiler_supported=" + Profiler.supported);
            text.AppendLine("profiler_enabled=" + Profiler.enabled);
            text.AppendLine("frame_timing_manager=" + FrameProbe.FrameTimingAvailable);
            text.AppendLine("runtime_events=" + NativeEvents.Installed + (NativeEvents.FailureReason != null ? " (" + NativeEvents.FailureReason + ")" : ""));
            text.AppendLine("gc_mode=" + GarbageCollector.GCMode);
            text.AppendLine("gc_incremental=" + GarbageCollector.isIncremental);
            text.AppendLine("gc_time_slice_ns=" + GarbageCollector.incrementalTimeSliceNanoseconds);
            text.AppendLine("overhead " + _overheadLine);
            text.AppendLine();

            AppendGameSettings(text);

            text.AppendLine("[plugins]");
            foreach (BepInEx.PluginInfo plugin in Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.GUID))
            {
                text.AppendLine(plugin.Metadata.GUID + " " + plugin.Metadata.Version);
            }
            bool fika = Chainloader.PluginInfos.Keys.Any(k => k.IndexOf("fika", StringComparison.OrdinalIgnoreCase) >= 0);
            text.AppendLine("fika_present=" + fika);
            text.AppendLine();

            text.AppendLine("[probe_targets]");
            _patchLines.ForEach(l => text.AppendLine(l));
            text.AppendLine();

            text.AppendLine("[other_patches_on_firing_path]");
            HashSet<string> owners = AppendForeignPatches(text);
            text.AppendLine();

            // These mods' settings decide what the shot does, so a run is only comparable with
            // the same values. Read once at raid start: a change made mid-raid is not recorded.
            text.AppendLine("[settings_of_those_mods]");
            foreach (BepInEx.PluginInfo plugin in Chainloader.PluginInfos.Values)
            {
                if (plugin.Instance != null && owners.Contains(plugin.Instance.GetType().Assembly.GetName().Name))
                {
                    foreach (KeyValuePair<BepInEx.Configuration.ConfigDefinition, BepInEx.Configuration.ConfigEntryBase> entry in plugin.Instance.Config)
                    {
                        text.AppendLine(plugin.Metadata.GUID + "|" + entry.Key.Section + "|" + entry.Key.Key + "=" + entry.Value.BoxedValue);
                    }
                }
            }
            text.AppendLine();

            text.AppendLine("[player_loop]");
            text.Append(FrameProbe.LoopDescription);
            return text.ToString();
        }

        /// <summary>The game's own settings, read by reflection so a renamed one is just absent.</summary>
        private static void AppendGameSettings(StringBuilder text)
        {
            text.AppendLine("[game_settings]");
            try
            {
                SettingsManager manager = Singleton<SettingsManager>.Instance;
                foreach (string group in new[] { nameof(SettingsManager.Graphics), nameof(SettingsManager.PostFx), nameof(SettingsManager.Game), nameof(SettingsManager.Sound) })
                {
                    object provider = typeof(SettingsManager).GetField(group).GetValue(manager);
                    object settings = provider.GetType().GetField("Settings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(provider)
                        ?? provider.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(provider);
                    if (settings == null)
                    {
                        continue;
                    }
                    foreach (FieldInfo field in settings.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        PropertyInfo value = field.FieldType.GetProperty("Value");
                        if (value != null)
                        {
                            text.AppendLine(group + "." + field.Name + "=" + value.GetValue(field.GetValue(settings)));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                text.AppendLine("unavailable=" + e.GetType().Name + ": " + e.Message);
            }
            text.AppendLine();
        }

        /// <summary>
        /// Every Harmony patch by anyone else on a type in the firing path, by the assembly that
        /// owns it: SPT's patch class ids do not say which mod a patch belongs to. A prefix can
        /// skip the original, so kinds matter. Returns the owning assemblies.
        /// </summary>
        internal static HashSet<string> AppendForeignPatches(StringBuilder text)
        {
            var owners = new HashSet<string>();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Type type = method.DeclaringType;
                if (type == null || !FiringTypes.Any(t => type.Name.IndexOf(t, StringComparison.Ordinal) >= 0))
                {
                    continue;
                }
                HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
                AppendPatches(text, owners, method, info.Prefixes, "prefix");
                AppendPatches(text, owners, method, info.Postfixes, "postfix");
                AppendPatches(text, owners, method, info.Transpilers, "transpiler");
                AppendPatches(text, owners, method, info.Finalizers, "finalizer");
            }
            return owners;
        }

        private static void AppendPatches(StringBuilder text, HashSet<string> owners, MethodBase method, IEnumerable<HarmonyLib.Patch> patches, string kind)
        {
            foreach (HarmonyLib.Patch patch in patches)
            {
                Type declaring = patch.PatchMethod.DeclaringType;
                if (declaring == null || (declaring.Namespace ?? "").StartsWith("FirstShotSmoothness", StringComparison.Ordinal))
                {
                    continue;
                }
                string owner = declaring.Assembly.GetName().Name;
                owners.Add(owner);
                text.AppendLine(method.DeclaringType.Name + "." + method.Name + " <- " + owner + " " + kind + " " + declaring.Name);
            }
        }
    }

    /// <summary>A matching pair of readings, to convert PresentMon's raw QPC to this report's clock.</summary>
    internal static class ClockSync
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long count);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long frequency);

        public static void Read(out long stopwatch, out long raw, out long rawFrequency)
        {
            QueryPerformanceFrequency(out rawFrequency);
            long before = Stopwatch.GetTimestamp();
            QueryPerformanceCounter(out raw);
            stopwatch = before + (Stopwatch.GetTimestamp() - before) / 2;
        }

        public static void Append(StringBuilder text)
        {
            Read(out long stopwatch, out long raw, out long rawFrequency);
            text.AppendLine("sync_stopwatch=" + stopwatch);
            text.AppendLine("sync_raw_qpc=" + raw);
            text.AppendLine("raw_qpc_frequency=" + rawFrequency);
        }
    }
}
