using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Scripting;

namespace FirstShotSmoothness.Diagnostics
{
    /// <summary>
    /// Timestamps the phases of every frame from inside Unity's player loop. A release player has
    /// no profiler, but this still tells where a slow frame spent its time: script phases, the
    /// audio update, the render submit, or the wait at the top of the next frame that shows up
    /// when the GPU or the render thread is behind. These are main-thread wall-clock times: they
    /// do not measure presented frames and they do not isolate GPU time.
    /// </summary>
    internal static class FrameProbe
    {
        private struct Marker { }

        private static readonly FrameTiming[] Timing = new FrameTiming[1];

        private static Action _tick;
        private static bool _frameTiming;
        private static int _lastLoopFrame = -1;

        public static bool Installed { get; private set; }

        public static string LoopDescription { get; private set; } = "";

        public static void Install(Action tickOncePerFrame)
        {
            _tick = tickOncePerFrame;
            _frameTiming = FrameTimingManager.IsFeatureEnabled();
            Apply();
            Installed = true;
        }

        public static bool FrameTimingAvailable => _frameTiming;

        /// <summary>
        /// The game sets its own player loop more than once during startup and raid load, which
        /// can drop these entries. The plugin's Update calls this; if the loop stopped reaching
        /// us, the entries go back in.
        /// </summary>
        public static void Watchdog()
        {
            if (Installed && Time.frameCount - _lastLoopFrame > 2)
            {
                Apply();
            }
        }

        private static void Apply()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem[] phases = root.subSystemList;
            for (int i = 0; i < phases.Length; i++)
            {
                phases[i].subSystemList = Remove(phases[i].subSystemList);
                Type type = phases[i].type;
                if (type == typeof(TimeUpdate)) { Add(ref phases[i], 0, Entry(LoopStart)); }
                else if (type == typeof(Initialization)) { Add(ref phases[i], 0, Entry(() => Stamp(Mark.Initialization))); }
                else if (type == typeof(EarlyUpdate))
                {
                    Add(ref phases[i], 0, Entry(() => Stamp(Mark.EarlyUpdate)));
                    Around(ref phases[i], typeof(EarlyUpdate.UpdatePreloading), Mark.PreloadStart, Mark.PreloadEnd);
                }
                else if (type == typeof(FixedUpdate)) { Add(ref phases[i], 0, Entry(FixedStep)); }
                else if (type == typeof(PreUpdate)) { Add(ref phases[i], 0, Entry(() => Stamp(Mark.PreUpdate))); }
                else if (type == typeof(Update))
                {
                    Add(ref phases[i], 0, Entry(() => Stamp(Mark.Update)));
                    Around(ref phases[i], typeof(Update.ScriptRunBehaviourUpdate), Mark.ScriptUpdateStart, Mark.ScriptUpdateEnd);
                }
                else if (type == typeof(PreLateUpdate))
                {
                    Add(ref phases[i], 0, Entry(() => Stamp(Mark.PreLateUpdate)));
                    Before(ref phases[i], typeof(PreLateUpdate.DirectorUpdateAnimationBegin), Mark.AnimationStart);
                    After(ref phases[i], typeof(PreLateUpdate.DirectorUpdateAnimationEnd), Mark.AnimationEnd);
                    Around(ref phases[i], typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate), Mark.LateScriptStart, Mark.LateScriptEnd);
                    Around(ref phases[i], typeof(PreLateUpdate.ParticleSystemBeginUpdateAll), Mark.ParticlesBeginStart, Mark.ParticlesBeginEnd);
                }
                else if (type == typeof(PostLateUpdate))
                {
                    Add(ref phases[i], 0, Entry(() => Stamp(Mark.PostLateUpdate)));
                    Around(ref phases[i], typeof(PostLateUpdate.UpdateAudio), Mark.AudioStart, Mark.AudioEnd);
                    Around(ref phases[i], typeof(PostLateUpdate.FinishFrameRendering), Mark.RenderStart, Mark.RenderEnd);
                    Around(ref phases[i], typeof(PostLateUpdate.ParticleSystemEndUpdateAll), Mark.ParticlesEndStart, Mark.ParticlesEndEnd);
                    Around(ref phases[i], typeof(PostLateUpdate.UpdateAllSkinnedMeshes), Mark.SkinnedMeshesStart, Mark.SkinnedMeshesEnd);
                    Around(ref phases[i], typeof(PostLateUpdate.UpdateAllRenderers), Mark.RenderersStart, Mark.RenderersEnd);
                    Around(ref phases[i], typeof(PostLateUpdate.PresentAfterDraw), Mark.PresentStart, Mark.PresentEnd);
                    Add(ref phases[i], phases[i].subSystemList.Length, Entry(() => Stamp(Mark.LoopEnd)));
                }
            }
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
            LoopDescription = Describe(root);
            _lastLoopFrame = Time.frameCount;
        }

        private static PlayerLoopSystem Entry(PlayerLoopSystem.UpdateFunction update)
        {
            return new PlayerLoopSystem { type = typeof(Marker), updateDelegate = update };
        }

        private static PlayerLoopSystem[] Remove(PlayerLoopSystem[] systems)
        {
            var kept = new List<PlayerLoopSystem>(systems ?? Array.Empty<PlayerLoopSystem>());
            kept.RemoveAll(s => s.type == typeof(Marker));
            return kept.ToArray();
        }

        private static void Add(ref PlayerLoopSystem phase, int index, PlayerLoopSystem entry)
        {
            var list = new List<PlayerLoopSystem>(phase.subSystemList);
            list.Insert(index, entry);
            phase.subSystemList = list.ToArray();
        }

        private static void Before(ref PlayerLoopSystem phase, Type target, Mark mark)
        {
            int index = Array.FindIndex(phase.subSystemList, s => s.type == target);
            if (index >= 0)
            {
                Add(ref phase, index, Entry(() => Stamp(mark)));
            }
        }

        private static void After(ref PlayerLoopSystem phase, Type target, Mark mark)
        {
            int index = Array.FindIndex(phase.subSystemList, s => s.type == target);
            if (index >= 0)
            {
                Add(ref phase, index + 1, Entry(() => Stamp(mark)));
            }
        }

        private static void Around(ref PlayerLoopSystem phase, Type target, Mark before, Mark after)
        {
            int index = Array.FindIndex(phase.subSystemList, s => s.type == target);
            if (index >= 0)
            {
                Add(ref phase, index + 1, Entry(() => Stamp(after)));
                Add(ref phase, index, Entry(() => Stamp(before)));
            }
        }

        private static string Describe(PlayerLoopSystem root)
        {
            var text = new StringBuilder();
            foreach (PlayerLoopSystem phase in root.subSystemList)
            {
                text.Append(phase.type?.Name).Append(':');
                foreach (PlayerLoopSystem system in phase.subSystemList ?? Array.Empty<PlayerLoopSystem>())
                {
                    text.Append(' ').Append(system.type?.Name);
                }
                text.AppendLine();
            }
            return text.ToString();
        }

        private static void FixedStep()
        {
            Recorder.Current?.MarkFixedStep(Stopwatch.GetTimestamp());
        }

        private static void Stamp(Mark mark)
        {
            Recorder.Current?.SetMark(mark, Stopwatch.GetTimestamp());
        }

        private static void LoopStart()
        {
            long now = Stopwatch.GetTimestamp();
            _lastLoopFrame = Time.frameCount;
            _tick();

            Session session = Recorder.Current;
            if (session == null)
            {
                return;
            }

            Recorder.ResetDepth();
            long gpu = -1;
            long cpu = -1;
            if (_frameTiming)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, Timing) == 1)
                {
                    gpu = (long)(Timing[0].gpuFrameTime * 1000.0);
                    cpu = (long)(Timing[0].cpuFrameTime * 1000.0);
                }
            }
            session.BeginFrame(now, GC.CollectionCount(0), GC.GetTotalMemory(false), (long)GarbageCollector.GCMode, gpu, cpu);
        }
    }
}
