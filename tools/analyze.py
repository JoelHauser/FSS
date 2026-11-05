#!/usr/bin/env python3
"""Reads First Shot Smoothness report folders and says what happened around each local shot.

    python tools/analyze.py <report folder> [more folders...]

A folder is one raid. Standard library only.

Definitions, so the numbers mean one thing:
  frame time   LoopStart of the next frame minus LoopStart of this one: main-thread wall clock.
               Not a presented frame, and not GPU time.
  baseline     median frame time of the BASELINE_FRAMES frames before the shot, stopping at the
               previous local shot so a burst's frames do not become their own baseline.
  shot frames  the shot's frame and the SHOT_SPAN frames after it, which covers work the shot
               queues for later: rendering its effects, starting its audio.
  spike        the slowest shot frame, when it is over baseline by at least SPIKE_MS and by
               SPIKE_RATIO. Excess is its time minus the baseline.
"""
import csv
import os
import statistics
import sys
from collections import Counter, defaultdict

BASELINE_FRAMES = 60
SHOT_SPAN = 3
IMPACT_SPAN = 2
SPIKE_MS = 3.0
SPIKE_RATIO = 1.5

PHASES = [
    ("wait/present", "LoopStart", "Initialization"),
    ("init", "Initialization", "EarlyUpdate"),
    ("early", "EarlyUpdate", "FixedUpdate"),
    ("physics", "FixedUpdate", "PreUpdate"),
    ("preupdate", "PreUpdate", "Update"),
    ("update", "Update", "PreLateUpdate"),
    ("prelate", "PreLateUpdate", "PostLateUpdate"),
    ("audio", "AudioStart", "AudioEnd"),
    ("render", "RenderStart", "RenderEnd"),
]

GC_PAUSE_BEGIN = 6  # PRE_STOP_WORLD
GC_PAUSE_END = 9    # POST_START_WORLD


def rows(folder, name):
    path = os.path.join(folder, name)
    if not os.path.exists(path):
        return []
    with open(path, newline="", encoding="utf-8", errors="replace") as handle:
        return list(csv.DictReader(handle))


def session(folder):
    facts = {}
    with open(os.path.join(folder, "session.txt"), encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if "=" in line:
                key, value = line.strip().split("=", 1)
                facts[key] = value
    return facts


class Raid:
    def __init__(self, folder):
        self.folder = folder
        self.facts = session(folder)
        self.freq = float(self.facts["stopwatch_frequency"])
        self.frames = {int(r["frame"]): r for r in rows(folder, "frames.csv")}
        for r in self.frames.values():
            for key in r:
                r[key] = int(r[key])
        self.events = rows(folder, "events.csv")
        self.shots = rows(folder, "shots.csv")
        self.native = rows(folder, "native.csv")
        self.jit_names = {r["method_ptr"]: r["name"] for r in rows(folder, "jit_names.csv")}
        self.names = {r["id"]: r["name"] for r in rows(folder, "names.csv")}
        self.starts = sorted((r["LoopStart"], f) for f, r in self.frames.items())
        self.jit_by_frame, self.gc_by_frame = self._native_by_frame()
        self.probes_by_frame = defaultdict(list)
        for e in self.events:
            if e["kind"] == "Probe":
                self.probes_by_frame[int(e["frame"])].append(e)

    def ms(self, ticks):
        return ticks * 1000.0 / self.freq

    def frame_ms(self, frame):
        a, b = self.frames.get(frame), self.frames.get(frame + 1)
        if not a or not b or not a["LoopStart"] or not b["LoopStart"]:
            return None
        return self.ms(b["LoopStart"] - a["LoopStart"])

    def frame_at(self, qpc):
        lo, hi = 0, len(self.starts) - 1
        if hi < 0 or qpc < self.starts[0][0]:
            return None
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if self.starts[mid][0] <= qpc:
                lo = mid
            else:
                hi = mid - 1
        return self.starts[lo][1]

    def _native_by_frame(self):
        jit = defaultdict(list)
        gc = defaultdict(float)
        open_jit = []
        gc_start = None
        for e in self.native:
            qpc = int(e["qpc"])
            if e["kind"] == "Gc":
                phase = int(e["arg"])
                if phase == GC_PAUSE_BEGIN:
                    gc_start = qpc
                elif phase == GC_PAUSE_END and gc_start is not None:
                    frame = self.frame_at(gc_start)
                    if frame is not None:
                        gc[frame] += self.ms(qpc - gc_start)
                    gc_start = None
            elif e["main_thread"] == "1":
                if e["kind"] == "JitBegin":
                    open_jit.append((qpc, e["arg"]))
                elif open_jit:
                    started, method = open_jit.pop()
                    frame = self.frame_at(started)
                    if frame is not None:
                        # Only the outermost compile counts as time, or nesting would double it.
                        jit[frame].append((self.ms(qpc - started) if not open_jit else 0.0, method))
        return jit, gc

    def baseline(self, frame, previous_shot):
        times = []
        f = frame - 1
        while f > previous_shot + SHOT_SPAN and len(times) < BASELINE_FRAMES:
            t = self.frame_ms(f)
            if t is None:
                break
            times.append(t)
            f -= 1
        return (statistics.median(times), len(times)) if len(times) >= 10 else (None, len(times))

    def phases(self, frame):
        r = self.frames.get(frame)
        if not r:
            return {}
        return {name: self.ms(r[b] - r[a]) for name, a, b in PHASES if r.get(a) and r.get(b)}

    def describe(self, frame):
        parts = []
        phases = self.phases(frame)
        if phases:
            top = sorted(phases.items(), key=lambda kv: -kv[1])[:3]
            parts.append(" ".join(f"{k}={v:.1f}" for k, v in top))
        jit = self.jit_by_frame.get(frame)
        if jit:
            parts.append(f"JIT {len(jit)} methods {sum(t for t, _ in jit):.1f}ms")
        if frame in self.gc_by_frame:
            parts.append(f"GC pause {self.gc_by_frame[frame]:.1f}ms")
        probes = sorted(self.probes_by_frame.get(frame, []), key=lambda e: -int(e["exclusive_ticks"]))[:3]
        if probes:
            parts.append(" ".join(f"{e['probe']}={self.ms(int(e['exclusive_ticks'])):.2f}" for e in probes))
        return " | ".join(parts)


def shot_rows(raid):
    result = []
    previous = -10**9
    for s in raid.shots:
        if s["shooter"] != "Local":
            continue
        frame = int(s["frame"])
        base, samples = raid.baseline(frame, previous)
        span = [(raid.frame_ms(f), f) for f in range(frame, frame + SHOT_SPAN + 1)]
        span = [x for x in span if x[0] is not None]
        worst, worst_frame = max(span) if span else (None, None)
        spike = (base is not None and worst is not None and worst - base >= SPIKE_MS and worst >= base * SPIKE_RATIO)
        result.append(dict(shot=s["shot"], frame=frame, base=base, samples=samples, worst=worst, worst_frame=worst_frame,
                           spike=spike, flags=s["flags"], weapon=raid.names.get(s["weapon_template"], "?"),
                           ammo=raid.names.get(s["ammo"], "?"),
                           since=raid.ms(int(s["since_last_local_ticks"])) / 1000.0, alive=s["alive_players"]))
        previous = frame
    return result


def impact_rows(raid):
    shot_of = {e["a"]: e["shot"] for e in raid.events if e["kind"] == "ShotLink"}
    result = []
    for e in raid.events:
        if e["kind"] != "Impact":
            continue
        b = int(e["b"])
        if not b >> 16 or e["a"] not in shot_of:
            continue
        frame = int(e["frame"])
        base, _ = raid.baseline(frame, frame - BASELINE_FRAMES - SHOT_SPAN - 1)
        span = [t for t in (raid.frame_ms(f) for f in range(frame, frame + IMPACT_SPAN + 1)) if t is not None]
        result.append(dict(shot=shot_of[e["a"]], frame=frame, kind={0: "none", 1: "world", 2: "body"}.get(b & 255, "?"),
                           material=(b >> 8) & 255, base=base, worst=max(span) if span else None))
    return result


def spikes_away_from_shots(folder):
    away = near = away_spikes = near_spikes = 0
    for r in rows(folder, "histogram.csv"):
        a, n, bucket = int(r["frames_away_from_shots"]), int(r["frames_near_shots"]), float(r["bucket_ms"])
        away += a
        near += n
        if bucket >= 33.0:
            away_spikes += a
            near_spikes += n
    return away, near, away_spikes, near_spikes


def fmt(value, spec=".1f"):
    return "-" if value is None else format(value, spec)


def report(folder):
    raid = Raid(folder)
    f = raid.facts
    print("\n=== " + os.path.basename(os.path.normpath(folder)))
    print(f"label={f.get('run_label', '')} location={f.get('location')} raid_this_launch={f.get('raid_number_this_launch')} "
          f"api={f.get('graphics_api')} vsync={f.get('vsync_count')} cap={f.get('target_frame_rate')} probes={f.get('probe_level')} "
          f"gc_mode_at_start={f.get('gc_mode')} runtime_events={f.get('runtime_events')}")
    for key in ("frames_dropped", "events_dropped", "shots_dropped", "native_events_dropped"):
        if f.get(key, "0") != "0":
            print(f"WARNING {key}={f[key]}: results are incomplete")

    shots = shot_rows(raid)
    print(f"local shots {len(shots)}, bot/remote shots {len(raid.shots) - len(shots)}, frames kept {len(raid.frames)}")

    # Every first-use class gets its own line, and every shot of it its own numbers.
    print("\nfirst-use and spike shots (ms; baseline median of frames before the shot)")
    for s in shots:
        if s["spike"] or s["flags"] != "None":
            print(f" shot {s['shot']:>5} base {fmt(s['base'])} worst {fmt(s['worst'])} "
                  f"excess {fmt(None if s['base'] is None or s['worst'] is None else s['worst'] - s['base'])} "
                  f"{'SPIKE' if s['spike'] else ''} since {s['since']:.1f}s [{s['flags']}] {s['weapon'].split(' ')[0][-6:]}")
            if s["spike"]:
                print(f"       frame {s['worst_frame']} (+{s['worst_frame'] - s['frame']}): {raid.describe(s['worst_frame'])}")

    later = [s for s in shots if s["flags"] == "None" and s["base"] is not None and s["worst"] is not None]
    if later:
        excess = [s["worst"] - s["base"] for s in later]
        print(f"\nrepeat shots: n={len(later)} median excess {statistics.median(excess):.2f} ms, "
              f"worst {max(excess):.2f} ms, spikes {sum(s['spike'] for s in later)}")

    impacts = impact_rows(raid)
    if impacts:
        by_kind = defaultdict(list)
        for i in impacts:
            if i["base"] is not None and i["worst"] is not None:
                by_kind[i["kind"]].append(i["worst"] - i["base"])
        for kind, values in sorted(by_kind.items()):
            print(f"impacts on {kind}: n={len(values)} median excess {statistics.median(values):.2f} ms, worst {max(values):.2f} ms")

    jit_frames = [fr for fr, items in raid.jit_by_frame.items() if items]
    if jit_frames:
        total = sum(t for items in raid.jit_by_frame.values() for t, _ in items)
        print(f"\nmain-thread JIT while recording: {sum(len(raid.jit_by_frame[f]) for f in jit_frames)} methods, {total:.1f} ms, in {len(jit_frames)} frames")
        spike_frames = {s["worst_frame"] for s in shots if s["spike"]}
        names = Counter()
        for fr in spike_frames:
            for t, method in raid.jit_by_frame.get(fr, []):
                names[raid.jit_names.get(method, method)] += t
        own = sum(t for n, t in names.items() if "FirstShotSmoothness" in n)
        if own:
            print(f"   of which this plugin's own code: {own:.2f} ms (a measurement artifact, should be 0)")
        for name, t in names.most_common(25):
            print(f"   {t:7.2f} ms  {name[:110]}")

    if raid.gc_by_frame:
        print(f"GC pauses while recording: {len(raid.gc_by_frame)} frames, worst {max(raid.gc_by_frame.values()):.1f} ms")
    modes = Counter(r["gc_mode"] for r in raid.frames.values())
    print(f"gc_mode by kept frame (0 disabled, 1 enabled): {dict(modes)}")

    away, near, away_spikes, near_spikes = spikes_away_from_shots(folder)
    if away or near:
        print(f"frames over 33 ms: {away_spikes} of {away} away from shots, {near_spikes} of {near} near shots")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    for folder in sys.argv[1:]:
        report(folder)
    return 0


if __name__ == "__main__":
    sys.exit(main())
