using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FirstShotSmoothness.Diagnostics;
using HarmonyLib;

// Runs in the game's mono runtime. Exits non-zero if any check fails.
internal static class HarnessMain
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "pass  " : "FAIL  ") + what);
        if (!ok)
        {
            _failures++;
        }
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("runtime: " + Type.GetType("Mono.Runtime")?.GetMethod("GetDisplayName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, null));
        string outDir = args[0];
        Recorder.MainThread = Environment.CurrentManagedThreadId;

        NativeTest();
        WindowTest();
        ClockTest();
        NestingTest();
        OverflowTest();
        ReportTest(outDir);
        PatchTest();

        Console.WriteLine(_failures == 0 ? "ALL PASSED" : _failures + " FAILED");
        return _failures == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static int NeverCalledBefore(int x) { return x * 31 + 7; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int WarmA(int x) { return x * 3; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int WarmB(int x) { return x * 5; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Sleepy() { return Enumerable.Range(0, 50).Select(i => i * 2).Sum(); }

    private static void NativeTest()
    {
        bool installed = NativeEvents.Install();
        Check(installed, "runtime event callbacks install: " + NativeEvents.FailureReason);
        if (!installed)
        {
            return;
        }

        NativeEvents.SampleClocks(out long tsc0, out long qpc0);
        NativeEvents.SampleClocks(out tsc0, out qpc0);
        long first = NativeEvents.Count;
        NeverCalledBefore(3);
        long afterOne = NativeEvents.Count;
        Check(afterOne >= first + 2, "first call of a method raises JIT begin and done (" + (afterOne - first) + " events)");

        long second = NativeEvents.Count;
        NeverCalledBefore(4);
        Check(NativeEvents.Count == second, "second call of the method raises nothing");

        bool named = false;
        for (long i = first; i < afterOne; i++)
        {
            NativeEvents.Read(i, out _, out long kind, out long arg, out long thread);
            if (kind == NativeEvents.KindJitDone && NativeEvents.MethodName((IntPtr)arg).Contains("NeverCalledBefore"))
            {
                named = thread == NativeEvents.MainThreadId;
            }
        }
        Check(named, "event names the method and the calling thread");

        long beforeGc = NativeEvents.Count;
        int gcBefore = GC.CollectionCount(0);
        GC.Collect();
        long gcEvents = 0;
        var seen = new SortedSet<long>();
        for (long i = beforeGc; i < NativeEvents.Count; i++)
        {
            NativeEvents.Read(i, out _, out long kind, out long arg, out _);
            if (kind == NativeEvents.KindGc)
            {
                gcEvents++;
                seen.Add(arg & 0xFFFFFFFF);
            }
        }
        Check(gcEvents > 0, "GC.Collect raises GC events (" + gcEvents + ", phases " + string.Join(",", seen) + "), CollectionCount " + gcBefore + "->" + GC.CollectionCount(0));

        // Which call really compiles? This decides how any JIT warm-up has to work.
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        long c0 = NativeEvents.Count;
        RuntimeHelpers.PrepareMethod(typeof(HarnessMain).GetMethod(nameof(WarmA), flags).MethodHandle);
        long byPrepare = NativeEvents.Count - c0;
        c0 = NativeEvents.Count;
        typeof(HarnessMain).GetMethod(nameof(WarmB), flags).MethodHandle.GetFunctionPointer();
        long byPointer = NativeEvents.Count - c0;
        c0 = NativeEvents.Count;
        WarmB(1);
        long afterPointer = NativeEvents.Count - c0;
        Console.WriteLine("      native events: PrepareMethod " + byPrepare + ", GetFunctionPointer " + byPointer + ", first call after GetFunctionPointer " + afterPointer);
        Check(byPointer >= 2 && afterPointer == 0, "GetFunctionPointer compiles a method ahead of its first call");

        // A run of compiles, timed: does the time the ring reports match the wall clock?
        long ringStart = NativeEvents.Count;
        long wall = Stopwatch.GetTimestamp();
        Sleepy();
        wall = Stopwatch.GetTimestamp() - wall;
        long ringEnd = NativeEvents.Count;
        Spin(200);
        NativeEvents.SampleClocks(out long tsc1, out long qpc1);
        double ticksPerTsc = (double)(qpc1 - qpc0) / (tsc1 - tsc0);
        long jitTsc = 0;
        var open = new Stack<long>();
        int compiled = 0;
        for (long i = ringStart; i < ringEnd; i++)
        {
            NativeEvents.Read(i, out long tsc, out long kind, out _, out _);
            if (kind == NativeEvents.KindJitBegin)
            {
                open.Push(tsc);
            }
            else if (kind == NativeEvents.KindJitDone && open.Count > 0)
            {
                long started = open.Pop();
                if (open.Count == 0)
                {
                    jitTsc += tsc - started;
                }
                compiled++;
            }
        }
        double jitMs = jitTsc * ticksPerTsc * 1000.0 / Stopwatch.Frequency;
        double wallMs = wall * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine("      LINQ first use: " + compiled + " methods compiled, " + jitMs.ToString("0.00") + " ms JIT of " + wallMs.ToString("0.00") + " ms wall");
        Check(compiled > 3 && jitMs > 0 && jitMs <= wallMs * 1.01, "JIT time adds up inside the wall time, clocks calibrated");

        // Compiles from another thread must be attributed to it.
        long mark = NativeEvents.Count;
        var thread2 = new System.Threading.Thread(() => { Enumerable.Repeat(1.5, 3).Average(); });
        thread2.Start();
        thread2.Join();
        bool other = false;
        for (long i = mark; i < NativeEvents.Count; i++)
        {
            NativeEvents.Read(i, out _, out long kind, out _, out long thread);
            other |= kind == NativeEvents.KindJitDone && thread != NativeEvents.MainThreadId;
        }
        Check(other, "a compile on another thread is marked as not the main thread");
    }

    private static Session Fresh(int before, int after, int kept = 4096)
    {
        return new Session(before, after, 8, kept, 4096, 256);
    }

    private static long _time = 1000000;

    private static void Tick(Session s, double ms = 5)
    {
        _time += (long)(ms * Stopwatch.Frequency / 1000);
        s.BeginFrame(_time, 0, 0, 0, -1, -1);
    }

    private static long[] KeptIds(Session s)
    {
        var ids = new long[s.KeptCount];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = s.Kept[i * Session.FrameStride + Session.FrameId];
        }
        return ids;
    }

    private static void WindowTest()
    {
        Session s = Fresh(10, 20);
        for (int i = 0; i < 100; i++) { Tick(s); }
        Check(s.KeptCount == 0, "no shot, no hitch: nothing kept");
        s.AddShot(Shooter.Local, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        long shotFrame = s.Frame;
        for (int i = 0; i < 100; i++) { Tick(s); }
        long[] ids = KeptIds(s);
        Check(ids.Length == 31 && ids[0] == shotFrame - 10 && ids[ids.Length - 1] == shotFrame + 20, "one shot keeps exactly before+1+after frames: " + ids.Length + " from " + (ids.Length > 0 ? ids[0] - shotFrame : 0));

        s = Fresh(10, 20);
        for (int i = 0; i < 50; i++) { Tick(s); }
        long firstShot = s.Frame + 1;
        long lastShot = 0;
        for (int i = 0; i < 30; i++)
        {
            Tick(s);
            s.AddShot(Shooter.Local, 0, 0, 0, ShotFlags.None, 0, 0, _time);
            lastShot = s.Frame;
            Tick(s);
        }
        for (int i = 0; i < 60; i++) { Tick(s); }
        ids = KeptIds(s);
        bool unique = ids.Distinct().Count() == ids.Length;
        bool contiguous = ids.Zip(ids.Skip(1), (a, b) => b - a).All(d => d == 1);
        Check(unique && contiguous && ids[0] == firstShot - 10 && ids[ids.Length - 1] == lastShot + 20, "overlapping automatic-fire windows keep each frame once, no gaps: " + ids.Length);

        s = Fresh(10, 20);
        for (int i = 0; i < 100; i++) { Tick(s); }
        long hitch = s.Frame;
        Tick(s, 40);
        for (int i = 0; i < 60; i++) { Tick(s); }
        ids = KeptIds(s);
        Check(ids.Contains(hitch) && ids.Length > 0, "a hitch with no shot keeps a window: " + ids.Length);
        Check(s.Histogram.Skip(Session.HistogramBuckets).Sum() == 0 && s.Histogram.Take(Session.HistogramBuckets).Sum() > 0, "it counts as away from shots in the histogram");

        s = Fresh(10, 20);
        for (int i = 0; i < 40; i++) { Tick(s); }
        s.AddShot(Shooter.Bot, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        for (int i = 0; i < 60; i++) { Tick(s); }
        Check(s.KeptCount > 0 && s.KeptCount <= 10 + 1 + 20 && s.ShotCount == 1 && s.Histogram.Skip(Session.HistogramBuckets).Sum() == 0, "a bot shot keeps a window but does not count as near a local shot");

        s = new Session(10, 300, 8, 4096, 16, 16);
        for (int i = 0; i < 40; i++) { Tick(s); }
        s.AddShot(Shooter.Bot, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        for (int i = 0; i < 400; i++) { Tick(s); }
        Check(s.KeptCount == 10 + 1 + 60, "a bot shot's window is capped at 60 frames after it: " + s.KeptCount);

        s = Fresh(10, 20, 8192);
        for (int i = 0; i < 40; i++) { Tick(s); }
        long killFrame = s.Frame;
        s.RetainFrames(1500);
        for (int i = 0; i < 1600; i++) { Tick(s); }
        Check(s.KeptCount == 1500 + 11, "a death keeps its long window: " + s.KeptCount);

        s = Fresh(10, 20);
        for (int i = 0; i < 20; i++) { Tick(s); }
        s.AddShot(Shooter.Local, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        long stepped = s.Frame;
        s.MarkFixedStep(111);
        s.MarkFixedStep(222);
        s.MarkFixedStep(333);
        for (int i = 0; i < 40; i++) { Tick(s); }
        long Field(long frame, int field)
        {
            int row = Array.IndexOf(KeptIds(s), frame);
            return s.Kept[row * Session.FrameStride + field];
        }
        Check(Field(stepped, Session.FrameMarks + (int)Mark.FixedUpdate) == 111 && Field(stepped, Session.FramePhysicsSteps) == 3, "several physics steps keep the first start time and count them");
        Check(Field(stepped + 1, Session.FramePhysicsSteps) == 0, "a frame with no physics step counts none");

        s = Fresh(10, 20);
        for (int i = 0; i < 5; i++) { Tick(s); }
        s.AddShot(Shooter.Local, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        Tick(s);
        s.Finish();
        Check(s.KeptCount >= 4 && KeptIds(s).Distinct().Count() == s.KeptCount, "a raid ending inside a window flushes without duplicates: " + s.KeptCount);
    }

    private static void ClockTest()
    {
        ClockSync.Read(out long sw0, out long raw0, out long rawFrequency);
        Spin(200);
        ClockSync.Read(out long sw1, out long raw1, out _);
        double swMs = (sw1 - sw0) * 1000.0 / Stopwatch.Frequency;
        double rawMs = (raw1 - raw0) * 1000.0 / rawFrequency;
        Console.WriteLine("      Stopwatch frequency " + Stopwatch.Frequency + ", raw QPC frequency " + rawFrequency);
        Check(Math.Abs(swMs - rawMs) < 0.5, "the clock sync pair converts between clocks: " + swMs.ToString("0.00") + " vs " + rawMs.ToString("0.00") + " ms");
    }

    private static void NestingTest()
    {
        Session s = Fresh(10, 20);
        Recorder.Current = s;
        Tick(s);
        int outer = Recorder.Enter();
        Spin(0.5);
        int inner = Recorder.Enter();
        Spin(1.0);
        int innermost = Recorder.Enter();
        Spin(0.5);
        Recorder.Exit(2, innermost);
        Recorder.Exit(1, inner);
        Spin(0.5);
        Recorder.Exit(0, outer);

        double Ms(int row, int field) => s.Events[row * Session.EventStride + field] * 1000.0 / Stopwatch.Frequency;
        Check(s.EventCount == 3, "three nested probes, three rows");
        double innerExcl = Ms(1, Session.EventExclusive);
        double outerIncl = Ms(2, Session.EventInclusive);
        double outerExcl = Ms(2, Session.EventExclusive);
        Check(innerExcl >= 0.9 && innerExcl < 3, "exclusive leaves out the child: " + innerExcl.ToString("0.00") + " ms, want 1.0");
        Check(outerIncl >= 2.4 && outerIncl < 5 && outerExcl >= 0.9 && outerExcl < 3, "outer inclusive " + outerIncl.ToString("0.00") + " ms, want 2.5; exclusive " + outerExcl.ToString("0.00") + ", want 1.0");
        double sumExcl = Ms(0, Session.EventExclusive) + Ms(1, Session.EventExclusive) + Ms(2, Session.EventExclusive);
        Check(Math.Abs(sumExcl - outerIncl) < 0.01, "exclusive times sum to the root's inclusive, nothing double counted");

        // A probed method that throws leaves the stack deep until the frame resets it.
        Recorder.Enter();
        Recorder.ResetDepth();
        int fresh = Recorder.Enter();
        Check(fresh == 0, "a frame start repairs depth after an exception");
        Recorder.Exit(0, fresh);

        int offThread = 0;
        var t = new System.Threading.Thread(() => offThread = Recorder.Enter());
        t.Start();
        t.Join();
        Check(offThread == -1, "a probe off the main thread records nothing");
        Recorder.Current = null;
    }

    private static void Spin(double ms)
    {
        long end = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < end) { }
    }

    private static void OverflowTest()
    {
        Session s = new Session(10, 20, 8, 8, 2, 2);
        for (int i = 0; i < 30; i++) { Tick(s); }
        s.AddShot(Shooter.Local, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        s.AddShot(Shooter.Local, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        long dropped = s.AddShot(Shooter.Local, 0, 0, 0, ShotFlags.None, 0, 0, _time);
        for (int i = 0; i < 3; i++) { s.AddEvent(EventKind.Probe, 0, 0, 1, 1, 0, 0, _time); }
        for (int i = 0; i < 60; i++) { Tick(s); }
        Check(s.KeptCount == 8 && s.FramesDropped == 23, "kept buffer full: frames dropped and counted (" + s.FramesDropped + ")");
        Check(s.EventCount == 2 && s.EventsDropped == 1 && s.ShotCount == 2 && s.ShotsDropped == 1 && dropped == -1, "event and shot overflow counted");
    }

    private static void ReportTest(string outDir)
    {
        Session s = Fresh(10, 20);
        for (int i = 0; i < 30; i++) { Tick(s, 7); }
        long id = s.AddShot(Shooter.Local, 0, 1, 0, ShotFlags.FirstInApp | ShotFlags.FirstInRaid, 0, 12, _time);
        s.AddEvent(EventKind.Probe, (int)ProbeId.Shot, id, 120000, 60000, 0, 0, _time);
        s.AddEvent(EventKind.ShotLink, 0, id, 0, 0, 77, 0, _time);
        Tick(s, 25);
        s.AddEvent(EventKind.Impact, 0, 0, 0, 0, 77, 1 | 3 << 8 | 1 << 16, _time);
        for (int i = 0; i < 40; i++) { Tick(s, 7); }
        s.Finish();

        NativeEvents.SampleClocks(out long tsc0, out long qpc0);
        long first = NativeEvents.Count;
        Sleepy2();
        NativeEvents.SampleClocks(out long tsc1, out long qpc1);
        var range = new ReportWriter.NativeRange { First = first, Last = NativeEvents.Count, TscStart = tsc0, QpcStart = qpc0, TscEnd = tsc1, QpcEnd = qpc1 };
        string folder = Path.Combine(outDir, "synthetic");
        ReportWriter.Write(folder, s, "[build]\nrun_label=synthetic\n", new[] { "tpl weapon", "instance", "tpl ammo" }, range);
        foreach (string file in new[] { "session.txt", "frames.csv", "events.csv", "shots.csv", "histogram.csv", "names.csv", "native.csv", "jit_names.csv" })
        {
            Check(File.Exists(Path.Combine(folder, file)) && new FileInfo(Path.Combine(folder, file)).Length > 0, "report has " + file);
        }
        string[] jit = File.ReadAllLines(Path.Combine(folder, "jit_names.csv"));
        Check(jit.Any(l => l.Contains("Sleepy2")), "jit_names.csv resolves real method names");
        Check(File.ReadAllLines(Path.Combine(folder, "frames.csv")).Length == s.KeptCount + 1, "frames.csv has one row per kept frame");
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static int Sleepy2() { return new List<int> { 1, 2 }.Count; }

    private static void PatchTest()
    {
        var harmony = new Harmony("harness");
        List<string> lines0 = null;
        foreach (ProbeLevel level in new[] { ProbeLevel.Stages })
        {
            List<string> lines = Probes.Install(harmony, level);
            lines0 = lines;
            lines.AddRange(Isolation.Install(harmony, true, true, true, true, true, true));
            foreach (string line in lines) { Console.WriteLine("      " + line); }
            // These two classes' static initializers call Unity, which is not loaded here, so they can only
            // be patched in the game. HollywoodFX patches both of these exact methods in the game.
            string[] needUnity = { "MuzzleManager.Shot", "Effects.Emit" };
            foreach (string line in lines.Where(l => l.StartsWith("FAILED "))) { Console.WriteLine("      reason: " + line); }
            bool Expected(string l) => l.StartsWith("ok ") || l.StartsWith("ISOLATION ON ") || needUnity.Any(n => l.StartsWith("FAILED " + n + ":"));
            Check(lines.Count >= 30 && lines.All(Expected), "every target patches here except the 2 that need Unity: " + lines.Count(l => l.StartsWith("ok ") || l.StartsWith("ISOLATION ON ")) + " of " + lines.Count);
        }

        Check(lines0.Contains("ok HollywoodFX.Patches.ShotDelegateWrapperPatch.OnShot") && lines0.Contains("ok HollywoodFX.ImpactController.Emit"), "HollywoodFX's own methods are found by name and timed when present");
        ForeignModTest();

        // The real generic probe wrapper, run for real, then the warm-up that keeps it out of the JIT log.
        int prepared = Probes.Warm();
        Check(prepared > 40, "warm-up prepares the plugin's own methods: " + prepared);
        var timed = new Harmony("harness-timed");
        var tag = typeof(Probes).GetNestedType("T16", System.Reflection.BindingFlags.NonPublic);
        typeof(Probes).GetMethod("Time", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).MakeGenericMethod(tag)
            .Invoke(null, new object[] { timed, typeof(HarnessMain).GetMethod(nameof(Probed), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static), ProbeId.ApplyShot });
        Session live = new Session(10, 20, 16, 64, 16, 16);
        Recorder.Current = live;
        long beforeProbe = NativeEvents.Count;
        Probed(1);
        long jitted = NativeEvents.Count - beforeProbe;
        Recorder.Current = null;
        Check(live.EventCount == 1 && live.Events[Session.EventProbe] == (int)ProbeId.ApplyShot, "a generic probe wrapper runs and records its id");
        for (long i = beforeProbe; i < NativeEvents.Count; i++) { NativeEvents.Read(i, out _, out long k, out long a2, out _); if (k == NativeEvents.KindJitDone) { Console.WriteLine("      compiled on first call: " + NativeEvents.MethodName((IntPtr)a2)); } }
        Check(jitted == 0, "a warmed probe compiles nothing on its first real call: " + jitted + " native events");

        // What a real Harmony-wrapped call costs here, against the same method bare.
        const int Rounds = 2000000;
        var probe = new Harmony("harness-cost");
        probe.Patch(typeof(HarnessMain).GetMethod(nameof(Patched), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
            new HarmonyMethod(typeof(HarnessMain).GetMethod(nameof(CostPrefix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)),
            new HarmonyMethod(typeof(HarnessMain).GetMethod(nameof(CostPostfix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
        Session s = new Session(10, 20, 16, 1 << 8, 16, 16);
        Recorder.Current = s;
        long a = Stopwatch.GetTimestamp();
        for (int i = 0; i < Rounds; i++) { Bare(i); }
        long bare = Stopwatch.GetTimestamp() - a;
        a = Stopwatch.GetTimestamp();
        for (int i = 0; i < Rounds; i++) { Patched(i); }
        long patched = Stopwatch.GetTimestamp() - a;
        Recorder.Current = null;
        double ns = (patched - bare) * 1e9 / Stopwatch.Frequency / Rounds;
        Console.WriteLine("      one probe adds " + ns.ToString("0") + " ns per call (Harmony wrapper, two clock reads, stack)");
        Check(ns < 2000, "probe overhead is well under a microsecond or two");
    }

    // A stand-in for a mod like HollywoodFX: a prefix that does work and skips the original.
    private static void ForeignModTest()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var time = typeof(Probes).GetMethod("Time", flags);
        var foreign = new HarmonyMethod(typeof(FakeMod).GetMethod(nameof(FakeMod.SlowSkippingPrefix)));

        // Whichever patched first, the probe has to wrap the other mod's prefix.
        var before = new Harmony("fake-mod-before");
        before.Patch(typeof(FakeMuzzleManager).GetMethod(nameof(FakeMuzzleManager.PatchedFirstByMod)), foreign);
        time.MakeGenericMethod(typeof(TagA)).Invoke(null, new object[] { new Harmony("timed-a"), typeof(FakeMuzzleManager).GetMethod(nameof(FakeMuzzleManager.PatchedFirstByMod)), ProbeId.MuzzleManagerShot });
        time.MakeGenericMethod(typeof(TagB)).Invoke(null, new object[] { new Harmony("timed-b"), typeof(FakeMuzzleManager).GetMethod(nameof(FakeMuzzleManager.PatchedFirstByProbe)), ProbeId.EffectsEmit });
        before.Patch(typeof(FakeMuzzleManager).GetMethod(nameof(FakeMuzzleManager.PatchedFirstByProbe)), foreign);

        Session s = Fresh(10, 20);
        Recorder.Current = s;
        Recorder.ResetDepth();
        Tick(s);
        FakeMuzzleManager.OriginalRan = 0;
        FakeMuzzleManager.PatchedFirstByMod();
        FakeMuzzleManager.PatchedFirstByProbe();
        Recorder.Current = null;
        double Row(int i) => s.Events[i * Session.EventStride + Session.EventInclusive] * 1000.0 / Stopwatch.Frequency;
        Check(FakeMuzzleManager.OriginalRan == 0, "the other mod's prefix skipped the original, as HollywoodFX's does");
        Check(s.EventCount == 2 && Row(0) >= 0.9 && Row(1) >= 0.9, "the probe times the other mod's prefix in either patch order: " + (s.EventCount == 2 ? Row(0).ToString("0.00") + " and " + Row(1).ToString("0.00") + " ms" : s.EventCount + " rows"));

        var text = new System.Text.StringBuilder();
        HashSet<string> owners = DiagnosticsHost.AppendForeignPatches(text);
        string listed = text.ToString();
        Check(owners.Contains("Harness") && listed.Contains("FakeMuzzleManager.PatchedFirstByMod <- Harness prefix FakeMod") && !listed.Contains("Timed"), "the report names other mods' patches by assembly and kind, and not its own");
    }

    private struct TagA { }
    private struct TagB { }

    [MethodImpl(MethodImplOptions.NoInlining)] private static int Probed(int x) { return x + 2; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Bare(int x) { return x + 1; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Patched(int x) { return x + 1; }
    private static void CostPrefix(out int __state) { __state = Recorder.Enter(); }
    private static void CostPostfix(int __state) { Recorder.Exit(0, __state); }
}

internal static class FakeMod
{
    public static bool SlowSkippingPrefix()
    {
        long end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < end) { }
        return false;
    }
}

internal static class FakeMuzzleManager
{
    public static int OriginalRan;

    [MethodImpl(MethodImplOptions.NoInlining)] public static void PatchedFirstByMod() { OriginalRan++; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void PatchedFirstByProbe() { OriginalRan++; }
}

// Stand-ins for HollywoodFX's real type and method names, as of its 4.1 source.
namespace HollywoodFX.Patches
{
    internal class ShotDelegateWrapperPatch
    {
        [MethodImpl(MethodImplOptions.NoInlining)] private static void OnShot(EFT.Ballistics.Shot shotResult) { }
    }
}

namespace HollywoodFX
{
    internal class ImpactController
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void Emit(object kinetics) { }
    }

    internal class ImpactEffects
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void Emit(object kinetics) { }
    }

    internal class BattleAmbience
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void Emit(object kinetics, float baseSizeScale) { }
    }
}

namespace HollywoodFX.Gore
{
    internal class GoreController
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void Apply(object kinetics) { }
    }
}
