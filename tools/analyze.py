#!/usr/bin/env python3
"""Reads First Shot Smoothness report folders and says what happened around each hitch and shot.

    python tools/analyze.py <report folder> [more folders...] [--presentmon capture.csv]

A folder is one raid. A PresentMon capture applies to the folders given with it. Standard library
only. tools/test_analyze.py checks this file against a report with known answers.

Definitions, so the numbers mean one thing:
  frame time   LoopStart of the next frame minus LoopStart of this one: main-thread wall clock.
               Not a presented frame, and not GPU time.
  baseline     median frame time of up to BASELINE_FRAMES saved frames just before this one, in
               an unbroken run. A median, so a few slow frames do not shift it.
  spike        a frame over its baseline by at least SPIKE_MS and by SPIKE_RATIO.
  shot cycle   the frames starting within CYCLE_MS of a shot. Work for a shot lands later than its
               frame: on an automatic the next round is chambered at 75% of the firing cycle.
  near         an event within CONTEXT_MS of the frame.

"near" shows what happened around a spike. It does not show that it caused the spike. The
comparison with non-spike frames is there to catch events that are simply always happening.
"""
import bisect
import csv
import os
import statistics
import sys
from collections import Counter, defaultdict, deque

BASELINE_FRAMES = 60
MIN_BASELINE = 10
CYCLE_MS = 150.0
MAX_CYCLE_FRAMES = 40
CONTEXT_MS = 200.0
FELT_WINDOW_MS = 1500.0
SPIKE_MS = 3.0
SPIKE_RATIO = 1.5

FRAME_FACTS = {"frame", "gc_count", "heap_bytes", "gc_mode", "gpu_us", "cpu_us", "physics_steps"}

# Which work the time after each mark is, up to the next mark in that frame.
GAP_NAMES = {
    "LoopStart": "wait for last frame/present",
    "Initialization": "init",
    "EarlyUpdate": "early update",
    "PreloadStart": "asset preload integration",
    "PreloadEnd": "early update",
    "FixedUpdate": "physics and FixedUpdate",
    "PreUpdate": "pre-update",
    "Update": "update",
    "ScriptUpdateStart": "scripts Update",
    "ScriptUpdateEnd": "update",
    "PreLateUpdate": "pre-late update",
    "AnimationStart": "animation",
    "AnimationEnd": "pre-late update",
    "ParticlesBeginStart": "particles begin",
    "ParticlesBeginEnd": "pre-late update",
    "LateScriptStart": "scripts LateUpdate",
    "LateScriptEnd": "pre-late update",
    "PostLateUpdate": "post-late update",
    "ParticlesEndStart": "particles end",
    "ParticlesEndEnd": "post-late update",
    "SkinnedMeshesStart": "skinned meshes",
    "SkinnedMeshesEnd": "post-late update",
    "RenderersStart": "renderers",
    "RenderersEnd": "post-late update",
    "AudioStart": "audio",
    "AudioEnd": "post-late update",
    "RenderStart": "render submit",
    "RenderEnd": "post-late update",
    "PresentStart": "present",
    "PresentEnd": "post-late update",
    "LoopEnd": "end of loop",
}

GC_PAUSE_BEGIN = 6  # PRE_STOP_WORLD
GC_PAUSE_END = 9    # POST_START_WORLD

CONTEXTS = ["local shot", "bot/remote shot", "your bullet impact", "other bullet impact", "death",
            "hit on you", "ammo pool object created"]


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


def section(folder, name):
    lines, inside = [], False
    with open(os.path.join(folder, "session.txt"), encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if line.startswith("["):
                inside = line == "[" + name + "]"
            elif inside and line:
                lines.append(line)
    return lines


def jit_owner(name):
    """The first namespace segment, so a mod's first-use code is told apart from the game's."""
    head = name.split(":")[0].split(".")[0].split("/")[0]
    return head if head in ("HollywoodFX", "FirstShotSmoothness", "SPT", "EFT", "System", "UnityEngine") else "game or other"


def fmt(value, spec=".1f"):
    return "-" if value is None else format(value, spec)


class Raid:
    def __init__(self, folder):
        self.folder = folder
        self.facts = session(folder)
        self.freq = float(self.facts["stopwatch_frequency"])
        self.frames = {}
        loaded = rows(folder, "frames.csv")
        self.marks = [k for k in (loaded[0] if loaded else {}) if k not in FRAME_FACTS]
        for r in loaded:
            parsed = {k: int(v) for k, v in r.items()}
            self.frames[parsed["frame"]] = parsed
        self.events = rows(folder, "events.csv")
        self.shots = rows(folder, "shots.csv")
        self.native = rows(folder, "native.csv")
        self.jit_names = {r["method_ptr"]: r["name"] for r in rows(folder, "jit_names.csv")}
        self.names = {r["id"]: r["name"] for r in rows(folder, "names.csv")}
        self.starts = sorted((r["LoopStart"], f) for f, r in self.frames.items() if r["LoopStart"])
        self.jit_by_frame, self.gc_by_frame = self._native_by_frame()
        self.probes_by_frame = defaultdict(list)
        for e in self.events:
            if e["kind"] == "Probe":
                self.probes_by_frame[int(e["frame"])].append(e)
        self.times = {f: self.frame_ms(f) for f in self.frames}
        self.baselines = self._baselines()
        self.context = self._context_times()

    def ms(self, ticks):
        return ticks * 1000.0 / self.freq

    def ticks(self, ms):
        return ms * self.freq / 1000.0

    def frame_ms(self, frame):
        a, b = self.frames.get(frame), self.frames.get(frame + 1)
        if not a or not b or not a["LoopStart"] or not b["LoopStart"]:
            return None
        return self.ms(b["LoopStart"] - a["LoopStart"])

    def frame_at(self, qpc):
        """The saved frame this time falls in, or None if it fell in a frame that was not saved."""
        keys = self.starts
        if not keys or qpc < keys[0][0]:
            return None
        start, frame = keys[bisect.bisect_right(keys, (qpc, float("inf"))) - 1]
        following = self.frames.get(frame + 1)
        if following is None or not following["LoopStart"] or qpc >= following["LoopStart"]:
            return None
        return frame

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

    def _baselines(self):
        result = {}
        recent = deque(maxlen=BASELINE_FRAMES)
        previous = None
        for frame in sorted(self.frames):
            if previous is not None and frame != previous + 1:
                recent.clear()
            previous = frame
            if len(recent) >= MIN_BASELINE:
                result[frame] = statistics.median(recent)
            t = self.times.get(frame)
            if t is not None:
                recent.append(t)
        return result

    def is_spike(self, frame):
        t, base = self.times.get(frame), self.baselines.get(frame)
        return t is not None and base is not None and t - base >= SPIKE_MS and t >= base * SPIKE_RATIO

    def excess(self, frame):
        t, base = self.times.get(frame), self.baselines.get(frame)
        return None if t is None or base is None else t - base

    def _context_times(self):
        times = {name: [] for name in CONTEXTS}
        for s in self.shots:
            times["local shot" if s["shooter"] == "Local" else "bot/remote shot"].append(int(s["qpc"]))
        for e in self.events:
            kind, qpc = e["kind"], int(e["qpc"])
            if kind == "Impact":
                times["your bullet impact" if int(e["b"]) >> 16 else "other bullet impact"].append(qpc)
            elif kind == "Kill":
                times["death"].append(qpc)
            elif kind == "HitOnLocal":
                times["hit on you"].append(qpc)
            elif kind == "PoolObjectCreated":
                times["ammo pool object created"].append(qpc)
        for values in times.values():
            values.sort()
        self.felt = sorted(int(e["qpc"]) for e in self.events if e["kind"] == "FeltStutter")
        return times

    def near(self, name, frame):
        """Whether this kind of event fell within CONTEXT_MS of the frame."""
        r, end = self.frames.get(frame), self.frames.get(frame + 1)
        if not r or not end:
            return False
        lo, hi = r["LoopStart"] - self.ticks(CONTEXT_MS), end["LoopStart"] + self.ticks(CONTEXT_MS)
        values = self.context[name]
        i = bisect.bisect_left(values, lo)
        return i < len(values) and values[i] <= hi

    def gaps(self, frame):
        """All of the frame's time, by the work between consecutive marks. They sum to frame time."""
        r, end = self.frames.get(frame), self.frames.get(frame + 1)
        if not r or not end or not r["LoopStart"] or not end["LoopStart"]:
            return {}
        stamps = sorted((r[m], m) for m in self.marks if r[m] and r["LoopStart"] <= r[m] < end["LoopStart"])
        stamps.append((end["LoopStart"], None))
        result = defaultdict(float)
        for (t0, mark), (t1, _) in zip(stamps, stamps[1:]):
            result[GAP_NAMES.get(mark, mark)] += self.ms(t1 - t0)
        return dict(result)

    def describe(self, frame):
        parts = []
        gaps = self.gaps(frame)
        if gaps:
            top = sorted(gaps.items(), key=lambda kv: -kv[1])[:3]
            parts.append(", ".join(f"{k} {v:.1f}" for k, v in top))
        steps = self.frames.get(frame, {}).get("physics_steps", 0)
        if steps > 1:
            parts.append(f"{steps} physics steps")
        jit = self.jit_by_frame.get(frame)
        if jit:
            parts.append(f"JIT {len(jit)} methods {sum(t for t, _ in jit):.1f} ms")
        if frame in self.gc_by_frame:
            parts.append(f"GC pause {self.gc_by_frame[frame]:.1f} ms")
        probes = sorted(self.probes_by_frame.get(frame, []), key=lambda e: -int(e["exclusive_ticks"]))[:3]
        if probes:
            parts.append(", ".join(f"{e['probe']} {self.ms(int(e['exclusive_ticks'])):.2f}" for e in probes))
        return " | ".join(parts)


def spikes(raid):
    return sorted(f for f in raid.frames if raid.is_spike(f))


def context_table(raid):
    """For each kind of event: how often it is near a spike frame, and near any other frame."""
    spike_set = set(spikes(raid))
    others = [f for f in raid.frames if f in raid.baselines and raid.times.get(f) is not None and f not in spike_set]
    table = {}
    for name in CONTEXTS:
        table[name] = (sum(raid.near(name, f) for f in spike_set), len(spike_set),
                       sum(raid.near(name, f) for f in others), len(others))
    table["JIT in the same frame"] = (sum(f in raid.jit_by_frame for f in spike_set), len(spike_set),
                                      sum(f in raid.jit_by_frame for f in others), len(others))
    table["GC pause in the same frame"] = (sum(f in raid.gc_by_frame for f in spike_set), len(spike_set),
                                           sum(f in raid.gc_by_frame for f in others), len(others))

    def several(f):
        return raid.frames[f].get("physics_steps", 0) > 1

    table["several physics steps"] = (sum(several(f) for f in spike_set), len(spike_set),
                                      sum(several(f) for f in others), len(others))
    return table


def where_spike_time_went(raid):
    """Which work was most above its usual time in spike frames, summed over them."""
    usual = defaultdict(list)
    for f in raid.frames:
        if f in raid.baselines and not raid.is_spike(f):
            for name, value in raid.gaps(f).items():
                usual[name].append(value)
    medians = {k: statistics.median(v) for k, v in usual.items()}
    extra = Counter()
    worst = Counter()
    for f in spikes(raid):
        gaps = raid.gaps(f)
        if gaps:
            over = {k: v - medians.get(k, 0.0) for k, v in gaps.items()}
            for k, v in over.items():
                extra[k] += max(v, 0.0)
            worst[max(over, key=over.get)] += 1
    return extra, worst


def probe_time_in_spikes(raid):
    total = Counter()
    for f in spikes(raid):
        for e in raid.probes_by_frame.get(f, []):
            total[e["probe"]] += raid.ms(int(e["exclusive_ticks"]))
    return total


def felt_rows(raid):
    """For each key press: the worst frame in the moments before it, allowing for reaction time."""
    result = []
    for mark in raid.felt:
        lo = mark - raid.ticks(FELT_WINDOW_MS)
        first = bisect.bisect_left(raid.starts, (lo, -1))
        last = bisect.bisect_right(raid.starts, (mark, float("inf")))
        candidates = [f for _, f in raid.starts[first:last] if raid.excess(f) is not None]
        if not candidates:
            result.append(dict(qpc=mark, frame=None, excess=None, spike=False))
            continue
        worst = max(candidates, key=raid.excess)
        result.append(dict(qpc=mark, frame=worst, excess=raid.excess(worst), spike=raid.is_spike(worst),
                           seconds_before=raid.ms(mark - raid.frames[worst]["LoopStart"]) / 1000.0))
    return result


def shot_rows(raid):
    result = []
    for s in raid.shots:
        if s["shooter"] != "Local":
            continue
        frame, qpc = int(s["frame"]), int(s["qpc"])
        limit = qpc + raid.ticks(CYCLE_MS)
        cycle = []
        for f in range(frame, frame + MAX_CYCLE_FRAMES):
            r = raid.frames.get(f)
            if r is None or r["LoopStart"] > limit:
                break
            cycle.append(f)
        measured = [f for f in cycle if raid.excess(f) is not None]
        worst = max(measured, key=raid.excess) if measured else None
        result.append(dict(shot=s["shot"], frame=frame, base=raid.baselines.get(frame),
                           worst=raid.times.get(worst) if worst else None, worst_frame=worst,
                           excess=raid.excess(worst) if worst else None, spike=bool(worst and raid.is_spike(worst)),
                           flags=s["flags"], weapon=raid.names.get(s["weapon_template"], "?"), ammo=raid.names.get(s["ammo"], "?"),
                           since=raid.ms(int(s["since_last_local_ticks"])) / 1000.0, alive=s["alive_players"]))
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
        measured = [f for f in range(frame, frame + 4) if raid.excess(f) is not None]
        worst = max((raid.excess(f) for f in measured), default=None)
        result.append(dict(shot=shot_of[e["a"]], frame=frame, kind={0: "none", 1: "world", 2: "body"}.get(b & 255, "?"),
                           material=(b >> 8) & 255, excess=worst))
    return result


def death_rows(raid):
    """Frame times after each death, to see whether corpses cost anything while they settle."""
    result = []
    for e in raid.events:
        if e["kind"] != "Kill":
            continue
        frame, qpc = int(e["frame"]), int(e["qpc"])
        before = [raid.times[f] for f in range(frame - 120, frame) if raid.times.get(f) is not None]
        after, physics = [], []
        for f in range(frame, frame + 1500):
            r = raid.frames.get(f)
            if r is None or r["LoopStart"] > qpc + raid.ticks(15000.0):
                break
            t = raid.times.get(f)
            if t is not None:
                after.append(t)
                physics.append(raid.gaps(f).get("physics and FixedUpdate", 0.0))
        result.append(dict(frame=frame, victim=e["a"], meters=int(e["b"]),
                           before=statistics.median(before) if len(before) >= 10 else None,
                           after=statistics.median(after) if len(after) >= 10 else None,
                           worst=max(after) if after else None,
                           physics=statistics.median(physics) if len(physics) >= 10 else None,
                           covered_s=sum(after) / 1000.0))
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


class PresentMonError(Exception):
    pass


def load_presentmon(path):
    """(raw QPC, milliseconds between presents). Refuses to guess at columns it does not recognize."""
    with open(path, newline="", encoding="utf-8", errors="replace") as handle:
        reader = csv.DictReader(handle)
        header = reader.fieldnames or []
        time_col = next((c for c in ("CPUStartQPC", "CPUStartTime", "TimeInSeconds") if c in header), None)
        ms_col = next((c for c in ("MsBetweenPresents", "FrameTime", "msBetweenPresents") if c in header), None)
        if time_col is None or ms_col is None:
            raise PresentMonError("unrecognized columns: " + ", ".join(header))
        result = []
        for row in reader:
            try:
                qpc, ms = float(row[time_col]), float(row[ms_col])
            except (TypeError, ValueError):
                continue
            if qpc != int(qpc):
                raise PresentMonError(f"column {time_col} is not a raw counter: capture with --qpc_time")
            result.append((int(qpc), ms))
    return sorted(result)


def presentmon_rows(raid, captures):
    """Puts the capture on the report's clock and compares presents around felt stutters and shots."""
    f = raid.facts
    if not all(k in f for k in ("sync_stopwatch", "sync_raw_qpc", "raw_qpc_frequency")):
        raise PresentMonError("this report has no clock sync")
    sw, raw, raw_freq = int(f["sync_stopwatch"]), int(f["sync_raw_qpc"]), int(f["raw_qpc_frequency"])
    converted = [(sw + (q - raw) * raid.freq / raw_freq, ms) for q, ms in captures]
    start, end = (raid.starts[0][0], raid.starts[-1][0]) if raid.starts else (0, 0)
    inside = [x for x in converted if start <= x[0] <= end]
    if not inside:
        return None
    times = [x[0] for x in inside]
    median = statistics.median(x[1] for x in inside)

    def worst(lo, hi):
        i, j = bisect.bisect_left(times, lo), bisect.bisect_right(times, hi)
        return max((inside[k][1] for k in range(i, j)), default=None)

    felt = [worst(q - raid.ticks(FELT_WINDOW_MS), q) for q in raid.felt]
    shots = [worst(q, q + raid.ticks(CYCLE_MS)) for q in raid.context["local shot"]]
    return dict(presents=len(inside), median=median, over_double=sum(x[1] > median * 2 for x in inside),
                felt=felt, shot_worst=[s for s in shots if s is not None])


def report(folder, presentmon=None):
    raid = Raid(folder)
    f = raid.facts
    print("\n=== " + os.path.basename(os.path.normpath(folder)))
    print(f"label={f.get('run_label', '')} location={f.get('location')} raid_this_launch={f.get('raid_number_this_launch')} "
          f"api={f.get('graphics_api')} vsync={f.get('vsync_count')} cap={f.get('target_frame_rate')} probes={f.get('probe_level')} "
          f"gc_mode_at_start={f.get('gc_mode')} runtime_events={f.get('runtime_events')}")
    for key in ("frames_dropped", "events_dropped", "shots_dropped", "native_events_dropped"):
        if f.get(key, "0") != "0":
            print(f"WARNING {key}={f[key]}: results are incomplete")

    for line in section(folder, "game_settings"):
        if any(k in line for k in ("DLSS", "FSR", "Reflex", "VSync", "AntiAliasing", "Shadows", "SSR=")):
            print("  " + line)
    patches = section(folder, "other_patches_on_firing_path")
    owners = Counter(p.split(" <- ")[1].split(" ")[0] for p in patches if " <- " in p)
    if owners:
        print("other mods patching the firing path: " + ", ".join(f"{k} ({v})" for k, v in owners.most_common()))
    for line in section(folder, "settings_of_those_mods"):
        if any(k in line for k in ("Spent Shells Lifetime", "Enable Muzzle Effects", "Enable Muzzle Light Shadow", "Enable Gore Effects", "Enable Ragdoll Effects")):
            print("  " + line.split("|", 1)[1])

    shots = shot_rows(raid)
    print(f"local shots {len(shots)}, bot/remote shots {len(raid.shots) - len(shots)}, frames kept {len(raid.frames)}")

    # ---- starting from the hitch
    found = spikes(raid)
    print(f"\nspike frames in what was kept: {len(found)}")
    if found:
        print("what was near them, against other kept frames (kept frames are not a random sample):")
        for name, (a, total_a, b, total_b) in context_table(raid).items():
            share_a = a / total_a if total_a else 0.0
            share_b = b / total_b if total_b else 0.0
            print(f"  {name:28} {a:5} of {total_a:<5} {share_a:6.0%}   others {share_b:6.0%}")
        extra, worst_part = where_spike_time_went(raid)
        print("work most above its usual time, frames where it was the largest:")
        for name, count in worst_part.most_common(8):
            print(f"  {name:28} {count:5} frames, {extra[name]:8.1f} ms extra in total")
        by_probe = probe_time_in_spikes(raid)
        if by_probe:
            print("timed code inside spike frames, own time in total:")
            for name, ms in by_probe.most_common(12):
                print(f"  {name:28} {ms:8.2f} ms")
        print("worst 12 frames:")
        for frame in sorted(found, key=raid.excess, reverse=True)[:12]:
            nearby = [n for n in CONTEXTS if raid.near(n, frame)]
            print(f"  frame {frame} +{raid.excess(frame):.1f} ms: {raid.describe(frame)}")
            print(f"      near: {', '.join(nearby) if nearby else 'nothing recorded'}")

    # ---- what the player felt
    felt = felt_rows(raid)
    if felt:
        matched = sum(r["spike"] for r in felt)
        print(f"\nfelt-a-stutter presses: {len(felt)}, {matched} with a spike frame in the {FELT_WINDOW_MS / 1000:.1f} s before")
        for r in felt:
            if r["frame"] is None:
                print("  press with no saved frames before it")
            else:
                print(f"  worst frame {r['seconds_before']:.2f} s before the press, +{r['excess']:.1f} ms"
                      f"{'' if r['spike'] else ' (not a spike)'}: {raid.describe(r['frame'])}")
        if matched < len(felt):
            print("  a press with no spike means the feeling did not come from a slow main-thread frame. Check PresentMon, recoil and audio.")

    # ---- starting from the shot
    print("\nfirst-use and spike shots (ms over baseline, slowest frame in the firing cycle)")
    for s in shots:
        if s["spike"] or s["flags"] != "None":
            print(f" shot {s['shot']:>5} base {fmt(s['base'])} worst {fmt(s['worst'])} excess {fmt(s['excess'])} "
                  f"{'SPIKE' if s['spike'] else ''} since {s['since']:.1f}s [{s['flags']}] {s['weapon'].split(' ')[0][-6:]}")
            if s["spike"]:
                print(f"       frame {s['worst_frame']} (+{s['worst_frame'] - s['frame']}): {raid.describe(s['worst_frame'])}")

    later = [s for s in shots if s["flags"] == "None" and s["excess"] is not None]
    if later:
        excess = [s["excess"] for s in later]
        print(f"\nrepeat shots: n={len(later)} median excess {statistics.median(excess):.2f} ms, "
              f"worst {max(excess):.2f} ms, spikes {sum(s['spike'] for s in later)}")

    based = [s for s in shots if s["base"] is not None]
    if len(based) >= 20:
        q = len(based) // 4
        print(f"baseline frame time: first {q} shots {statistics.median(s['base'] for s in based[:q]):.2f} ms, "
              f"last {q} shots {statistics.median(s['base'] for s in based[-q:]):.2f} ms")

    created = [e for e in raid.events if e["kind"] == "PoolObjectCreated"]
    if shots:
        first_shot = shots[0]["frame"]
        mid = [e for e in created if int(e["frame"]) >= first_shot]
        by_frame = Counter(int(e["frame"]) for e in mid)
        print(f"ammo pool objects created: {len(created) - len(mid)} before the first shot, {len(mid)} after it")
        spike_set = set(found)
        for frame, count in by_frame.most_common(10):
            print(f"   frame {frame}: {count} created, frame time {fmt(raid.times.get(frame))} ms{' SPIKE FRAME' if frame in spike_set else ''}")

    impacts = impact_rows(raid)
    if not impacts and any(e["kind"] == "ShotLink" for e in raid.events):
        print("WARNING no impacts linked to shots. Fired into nothing, or a mod replaced ClientGameWorld.ShotDelegate (HollywoodFX notes that Fika does).")
    by_kind = defaultdict(list)
    for i in impacts:
        if i["excess"] is not None:
            by_kind[i["kind"]].append(i["excess"])
    for kind, values in sorted(by_kind.items()):
        print(f"your impacts on {kind}: n={len(values)} median excess {statistics.median(values):.2f} ms, worst {max(values):.2f} ms")

    deaths = death_rows(raid)
    if deaths:
        print("\ndeaths (median frame ms before, median after, worst after, median physics ms, seconds of frames saved after):")
        for d in deaths:
            print(f"  frame {d['frame']} at {d['meters']} m: {fmt(d['before'])} -> {fmt(d['after'])}, worst {fmt(d['worst'])}, "
                  f"physics {fmt(d['physics'], '.2f')}, {d['covered_s']:.1f} s")

    # ---- runtime events
    jit_frames = [fr for fr, items in raid.jit_by_frame.items() if items]
    if jit_frames:
        total = sum(t for items in raid.jit_by_frame.values() for t, _ in items)
        print(f"\nmain-thread JIT in saved frames: {sum(len(raid.jit_by_frame[f]) for f in jit_frames)} methods, {total:.1f} ms, in {len(jit_frames)} frames")
        names = Counter()
        for fr in found:
            for t, method in raid.jit_by_frame.get(fr, []):
                names[raid.jit_names.get(method, method)] += t
        by_owner = Counter()
        for name, t in names.items():
            by_owner[jit_owner(name)] += t
        if by_owner:
            print("   JIT in spike frames by owner: " + ", ".join(f"{k} {v:.2f} ms" for k, v in by_owner.most_common()))
        if by_owner.get("FirstShotSmoothness"):
            print("   this plugin's own code should be 0: a measurement artifact")
        for name, t in names.most_common(20):
            print(f"   {t:7.2f} ms  {name[:110]}")

    if raid.gc_by_frame:
        print(f"GC pauses in saved frames: {len(raid.gc_by_frame)}, worst {max(raid.gc_by_frame.values()):.1f} ms")
    modes = Counter(r["gc_mode"] for r in raid.frames.values())
    print(f"gc_mode by kept frame (0 disabled, 1 enabled): {dict(modes)}")

    away, near, away_spikes, near_spikes = spikes_away_from_shots(folder)
    if away or near:
        print(f"all frames over 33 ms: {away_spikes} of {away} away from your shots, {near_spikes} of {near} near your shots")

    if presentmon:
        try:
            pm = presentmon_rows(raid, presentmon)
        except PresentMonError as e:
            print(f"PresentMon not compared: {e}")
            return
        if pm is None:
            print("PresentMon: the capture does not overlap this raid")
            return
        print(f"\nPresentMon: {pm['presents']} presents, median {pm['median']:.2f} ms, {pm['over_double']} over double the median")
        if pm["shot_worst"]:
            print(f"   worst present in each firing cycle: median {statistics.median(pm['shot_worst']):.2f} ms, worst {max(pm['shot_worst']):.2f} ms")
        for value in pm["felt"]:
            print(f"   felt press: worst present in the {FELT_WINDOW_MS / 1000:.1f} s before: {fmt(value, '.2f')} ms")


def main():
    args = sys.argv[1:]
    presentmon = None
    if "--presentmon" in args:
        at = args.index("--presentmon")
        try:
            presentmon = load_presentmon(args[at + 1])
        except PresentMonError as e:
            print(f"PresentMon capture not used: {e}")
        args = args[:at] + args[at + 2:]
    if not args:
        print(__doc__)
        return 2
    for folder in args:
        report(folder, presentmon)
    return 0


if __name__ == "__main__":
    sys.exit(main())
