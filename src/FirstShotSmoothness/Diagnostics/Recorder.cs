using System;
using System.Diagnostics;

namespace FirstShotSmoothness.Diagnostics
{
    /// <summary>The points in one frame where the player loop records the time.</summary>
    internal enum Mark
    {
        LoopStart,
        Initialization,
        EarlyUpdate,
        FixedUpdate,
        PreUpdate,
        Update,
        PreLateUpdate,
        PostLateUpdate,
        AudioStart,
        AudioEnd,
        RenderStart,
        RenderEnd,
        LoopEnd,
        Count
    }

    /// <summary>What an event row is.</summary>
    internal enum EventKind
    {
        Probe,
        TriggerPress,
        TriggerRelease,
        DryFire,
        ShotLink,
        Impact,
        ClipState
    }

    internal enum Shooter
    {
        Local,
        Bot,
        Remote
    }

    [Flags]
    internal enum ShotFlags
    {
        None = 0,
        FirstInApp = 1 << 0,
        FirstInRaid = 1 << 1,
        WeaponTemplateFirstInApp = 1 << 2,
        WeaponTemplateFirstInRaid = 1 << 3,
        WeaponInstanceFirstInRaid = 1 << 4,
        AmmoFirstInApp = 1 << 5,
        AmmoFirstInRaid = 1 << 6,
        FirstSinceDraw = 1 << 7,
        Silenced = 1 << 8
    }

    /// <summary>
    /// One raid's measurements, in buffers allocated once when the raid starts. Nothing on the
    /// measured path allocates, formats, reflects or touches a file.
    ///
    /// Frames pass through a small ring. A frame is copied to the kept buffer only if it falls
    /// inside the window around a local shot or a hitch, and it is decided once, when it is old
    /// enough for every shot that could claim it to have happened. So overlapping windows during
    /// automatic fire keep each frame exactly once; shots and frames join on frame id afterwards.
    /// Every frame, kept or not, also lands in a frame-time histogram, split by whether it was
    /// near a local shot, which gives the rate of hitches when nobody is firing.
    /// </summary>
    internal sealed class Session
    {
        public const int FrameId = 0;
        public const int FrameMarks = 1;
        public const int FrameGcCount = FrameMarks + (int)Mark.Count;
        public const int FrameHeapBytes = FrameGcCount + 1;
        public const int FrameGcMode = FrameHeapBytes + 1;
        public const int FrameGpuMicros = FrameGcMode + 1;
        public const int FrameCpuMicros = FrameGpuMicros + 1;
        public const int FrameStride = FrameCpuMicros + 1;

        public const int EventTime = 0;
        public const int EventFrame = 1;
        public const int EventKindField = 2;
        public const int EventProbe = 3;
        public const int EventShot = 4;
        public const int EventInclusive = 5;
        public const int EventExclusive = 6;
        public const int EventA = 7;
        public const int EventB = 8;
        public const int EventStride = 9;

        public const int ShotId = 0;
        public const int ShotFrame = 1;
        public const int ShotTime = 2;
        public const int ShotShooter = 3;
        public const int ShotWeaponTemplate = 4;
        public const int ShotWeaponInstance = 5;
        public const int ShotAmmo = 6;
        public const int ShotFlagsField = 7;
        public const int ShotSinceLastLocal = 8;
        public const int ShotAlive = 9;
        public const int ShotStride = 10;

        public const int HistogramBuckets = 400;
        public const double HistogramBucketMs = 0.25;

        private const int RingFrames = 2048;
        private const int RingMask = RingFrames - 1;

        public readonly int KeptCapacity;
        public readonly int EventCapacity;
        public readonly int ShotCapacity;
        public readonly long[] Kept;
        public readonly long[] Events;
        public readonly long[] Shots;
        public readonly long[] Histogram = new long[2 * HistogramBuckets];

        public readonly int FramesBefore;
        public readonly int FramesAfter;
        public readonly long StartTime;

        public int KeptCount;
        public int EventCount;
        public int ShotCount;
        public long FramesDropped;
        public long EventsDropped;
        public long ShotsDropped;
        public long FramesSeen;

        private readonly long[] _ring = new long[RingFrames * FrameStride];
        private readonly long _hitchFloorTicks;
        private readonly long _bucketTicks;

        private long _frame;
        private long _nextToDecide = 1;
        private long _lastLocalShot = long.MinValue / 2;
        private long _retainUntil = long.MinValue / 2;
        private double _typicalTicks;

        public Session(int framesBefore, int framesAfter, double hitchFloorMs, int keptFrames, int events, int shots)
        {
            if (framesBefore + 2 >= RingFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(framesBefore));
            }
            FramesBefore = framesBefore;
            FramesAfter = framesAfter;
            KeptCapacity = keptFrames;
            EventCapacity = events;
            ShotCapacity = shots;
            Kept = new long[keptFrames * FrameStride];
            Events = new long[events * EventStride];
            Shots = new long[shots * ShotStride];
            _hitchFloorTicks = (long)(hitchFloorMs * Stopwatch.Frequency / 1000.0);
            _bucketTicks = (long)(HistogramBucketMs * Stopwatch.Frequency / 1000.0);
            StartTime = Stopwatch.GetTimestamp();
        }

        public long Frame => _frame;

        /// <summary>Opens a frame and settles the one before it. Called at the top of the player loop.</summary>
        public void BeginFrame(long now, long gcCount, long heapBytes, long gcMode, long gpuMicros, long cpuMicros)
        {
            long previous = _frame;
            long frame = ++_frame;
            int slot = (int)(frame & RingMask) * FrameStride;
            Array.Clear(_ring, slot, FrameStride);
            _ring[slot + FrameId] = frame;
            _ring[slot + FrameMarks] = now;
            _ring[slot + FrameGcCount] = gcCount;
            _ring[slot + FrameHeapBytes] = heapBytes;
            _ring[slot + FrameGcMode] = gcMode;
            _ring[slot + FrameGpuMicros] = gpuMicros;
            _ring[slot + FrameCpuMicros] = cpuMicros;
            FramesSeen++;

            if (previous > 0)
            {
                long total = now - _ring[(int)(previous & RingMask) * FrameStride + FrameMarks];
                WatchForHitch(previous, total);
                Decide(previous - FramesBefore);
            }
        }

        public void SetMark(Mark mark, long now)
        {
            _ring[(int)(_frame & RingMask) * FrameStride + FrameMarks + (int)mark] = now;
        }

        private void WatchForHitch(long frame, long total)
        {
            // Hitches keep a window of their own, so a stall with no shot near it is on record.
            // This only chooses what to keep; the analysis decides what counts as a spike.
            if (_typicalTicks <= 0)
            {
                _typicalTicks = total;
            }
            if (total > _hitchFloorTicks && total > _typicalTicks * 2)
            {
                RetainThrough(frame + FramesAfter);
            }
            else
            {
                _typicalTicks += (total - _typicalTicks) * 0.05;
            }
        }

        private void RetainThrough(long frame)
        {
            if (frame > _retainUntil)
            {
                _retainUntil = frame;
            }
        }

        /// <summary>
        /// Settles every frame up to <paramref name="upTo"/>. It runs FramesBefore behind the
        /// live frame, so any trigger at or after a frame still claims it. A trigger T claims
        /// frames T-before..T+after, so only the most recent one needs comparing: by the time a
        /// frame is settled, T-before cannot be past it.
        /// </summary>
        private void Decide(long upTo)
        {
            for (; _nextToDecide <= upTo; _nextToDecide++)
            {
                long frame = _nextToDecide;
                int slot = (int)(frame & RingMask) * FrameStride;
                long start = _ring[slot + FrameMarks];
                long next = _ring[(int)((frame + 1) & RingMask) * FrameStride + FrameMarks];
                if (next > start)
                {
                    int bucket = (int)Math.Min((next - start) / _bucketTicks, HistogramBuckets - 1);
                    bool nearShot = frame <= _lastLocalShot + FramesAfter;
                    Histogram[(nearShot ? HistogramBuckets : 0) + bucket]++;
                }
                if (frame <= _retainUntil)
                {
                    if (KeptCount < KeptCapacity)
                    {
                        Array.Copy(_ring, slot, Kept, KeptCount * FrameStride, FrameStride);
                        KeptCount++;
                    }
                    else
                    {
                        FramesDropped++;
                    }
                }
            }
        }

        /// <summary>Settles what is left at the end of the raid.</summary>
        public void Finish()
        {
            Decide(_frame - 1);
        }

        public long AddShot(Shooter shooter, long weaponTemplate, long weaponInstance, long ammo, ShotFlags flags, long sinceLastLocal, long alive, long now)
        {
            if (shooter == Shooter.Local)
            {
                _lastLocalShot = _frame;
                RetainThrough(_frame + FramesAfter);
            }
            if (ShotCount >= ShotCapacity)
            {
                ShotsDropped++;
                return -1;
            }
            int at = ShotCount * ShotStride;
            long id = ShotCount + 1;
            Shots[at + ShotId] = id;
            Shots[at + ShotFrame] = _frame;
            Shots[at + ShotTime] = now;
            Shots[at + ShotShooter] = (long)shooter;
            Shots[at + ShotWeaponTemplate] = weaponTemplate;
            Shots[at + ShotWeaponInstance] = weaponInstance;
            Shots[at + ShotAmmo] = ammo;
            Shots[at + ShotFlagsField] = (long)flags;
            Shots[at + ShotSinceLastLocal] = sinceLastLocal;
            Shots[at + ShotAlive] = alive;
            ShotCount++;
            return id;
        }

        public void AddEvent(EventKind kind, int probe, long shot, long inclusive, long exclusive, long a, long b, long now)
        {
            if (EventCount >= EventCapacity)
            {
                EventsDropped++;
                return;
            }
            int at = EventCount * EventStride;
            Events[at + EventTime] = now;
            Events[at + EventFrame] = _frame;
            Events[at + EventKindField] = (long)kind;
            Events[at + EventProbe] = probe;
            Events[at + EventShot] = shot;
            Events[at + EventInclusive] = inclusive;
            Events[at + EventExclusive] = exclusive;
            Events[at + EventA] = a;
            Events[at + EventB] = b;
            EventCount++;
        }
    }

    /// <summary>
    /// The live session and the timing stack for nested probes. Exclusive time is a probe's own
    /// time less the probes called inside it, so a parent never counts a child twice.
    /// Main thread only: a probe on any other thread records nothing.
    /// </summary>
    internal static class Recorder
    {
        private const int MaxDepth = 32;

        public static volatile Session Current;

        /// <summary>The newest local shot, for events that belong to it.</summary>
        public static long CurrentShot;

        public static int MainThread;

        /// <summary>Per-frame probes only leave a row at or above this, to keep idle frames out.</summary>
        public static long QuietThresholdTicks = Stopwatch.Frequency / 10000;

        public static bool[] Quiet = new bool[64];

        private static readonly long[] Start = new long[MaxDepth];
        private static readonly long[] Children = new long[MaxDepth];
        private static int _depth;

        /// <summary>Drops any depth left by a probed method that threw. Called once a frame.</summary>
        public static void ResetDepth()
        {
            _depth = 0;
        }

        public static int Enter()
        {
            if (Current == null || Environment.CurrentManagedThreadId != MainThread)
            {
                return -1;
            }
            int depth = _depth;
            if (depth >= MaxDepth)
            {
                return -1;
            }
            _depth = depth + 1;
            Children[depth] = 0;
            Start[depth] = Stopwatch.GetTimestamp();
            return depth;
        }

        public static void Exit(int probe, int depth)
        {
            if (depth < 0)
            {
                return;
            }
            long now = Stopwatch.GetTimestamp();
            long inclusive = now - Start[depth];
            long exclusive = inclusive - Children[depth];
            _depth = depth;
            if (depth > 0)
            {
                Children[depth - 1] += inclusive;
            }
            if (Quiet[probe] && inclusive < QuietThresholdTicks)
            {
                return;
            }
            Current?.AddEvent(EventKind.Probe, probe, CurrentShot, inclusive, exclusive, depth, 0, Start[depth]);
        }
    }
}
