#!/usr/bin/env python3
"""Checks tools/analyze.py against a made-up report whose right answers are known.

    python tools/test_analyze.py <empty scratch folder>
"""
import contextlib
import csv
import io
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze  # noqa: E402

FREQ = 10_000_000
MARKS = ["LoopStart", "Initialization", "FixedUpdate", "Update", "RenderStart", "RenderEnd"]
failures = 0


def check(ok, what):
    global failures
    print(("pass  " if ok else "FAIL  ") + what)
    if not ok:
        failures += 1


def ticks(ms):
    return int(ms * FREQ / 1000)


def write(folder, name, header, data):
    with open(os.path.join(folder, name), "w", newline="", encoding="utf-8") as handle:
        out = csv.writer(handle)
        out.writerow(header)
        out.writerows(data)


def build(folder):
    """200 frames of 10 ms. Frame 100 is 40 ms, spent in physics. Frames 150-159 are not saved."""
    os.makedirs(folder, exist_ok=True)
    frames, starts = [], {}
    t = 1_000_000
    for f in range(1, 201):
        length = 40.0 if f == 100 else 10.0
        starts[f] = t
        if not 150 <= f <= 159:
            row = [f, t, t + ticks(0.5), t + ticks(1), t + ticks(length - 6), t + ticks(length - 5), t + ticks(length - 1), 0, 0, 1, -1, -1, 3 if f == 100 else 1]
            frames.append(row)
        t += ticks(length)
    write(folder, "frames.csv", ["frame"] + MARKS + ["gc_count", "heap_bytes", "gc_mode", "gpu_us", "cpu_us", "physics_steps"], frames)
    write(folder, "shots.csv", ["shot", "frame", "qpc", "shooter", "weapon_template", "weapon_instance", "ammo", "flags", "since_last_local_ticks", "alive_players"], [
        [1, 100, starts[100] + 5, "Local", 0, 1, 0, "None", 0, 3],
        [2, 50, starts[50], "Bot", 0, 1, 0, "None", 0, 3],
    ])
    probe = ["Probe", "HfxGore", 0, ticks(2), ticks(2), 0, 0]
    write(folder, "events.csv", ["qpc", "frame", "kind", "probe", "shot", "inclusive_ticks", "exclusive_ticks", "a", "b"], [
        [starts[100] + 6, 100] + probe,
        [starts[100] + 7, 100, "Kill", "", 0, 0, 0, 1, 12],
        [starts[100] + 8, 100, "ShotLink", "", 1, 0, 0, 77, 0],
        [starts[100] + 9, 100, "Impact", "", 0, 0, 0, 77, 2 | 5 << 8 | 1 << 16],
        [starts[60], 60, "Impact", "", 0, 0, 0, 90, 1 | 5 << 8],
        [starts[100] + ticks(500), 150, "FeltStutter", "", 0, 0, 0, 0, 0],
        [starts[30], 30, "FeltStutter", "", 0, 0, 0, 0, 0],
        [starts[100] + 10, 100, "PoolObjectCreated", "", 1, 0, 0, 0, 0],
    ])
    write(folder, "native.csv", ["qpc", "kind", "arg", "main_thread"], [
        # A compile inside the slow frame.
        [starts[100] + 100, "JitBegin", 111, 1],
        [starts[100] + 100 + ticks(4), "JitDone", 111, 1],
        # A compile inside an unsaved frame: it must not land on saved frame 149.
        [starts[155], "JitBegin", 222, 1],
        [starts[155] + ticks(4), "JitDone", 222, 1],
        # A compile on another thread blocks nothing here.
        [starts[120], "JitBegin", 333, 0],
        [starts[120] + ticks(4), "JitDone", 333, 0],
    ])
    write(folder, "jit_names.csv", ["method_ptr", "name"], [[111, "HollywoodFX.Gore.GoreController:Apply (x)"], [222, "EFT.Unsaved:Frame ()"], [333, "EFT.Other:Thread ()"]])
    write(folder, "histogram.csv", ["bucket_ms", "frames_away_from_shots", "frames_near_shots"], [["10.00", 190, 9], ["40.00", 0, 1]])
    write(folder, "names.csv", ["id", "name"], [[0, "tpl weapon"], [1, "inst"]])
    with open(os.path.join(folder, "session.txt"), "w", encoding="utf-8") as handle:
        handle.write(
            "[build]\nrun_label=t\n[clock]\nsync_stopwatch=1000000\nsync_raw_qpc=5000000\nraw_qpc_frequency=20000000\n"
            "[other_patches_on_firing_path]\nMuzzleManager.Shot <- HollywoodFX prefix X\n"
            "[settings_of_those_mods]\ncom.janky.hollywoodfx|Misc|Spent Shells Lifetime (seconds)=60\n"
            f"[counts]\nstopwatch_frequency={FREQ}\nframes_dropped=0\nevents_dropped=0\nshots_dropped=0\nnative_events_dropped=0\n")
    return starts


def main():
    folder = os.path.join(sys.argv[1], "made-up")
    starts = build(folder)
    raid = analyze.Raid(folder)

    check(raid.times[100] == 40.0, "frame time is the gap between loop starts")
    check(analyze.spikes(raid) == [100], "only the slow frame is a spike")
    check(raid.baselines[100] == 10.0, "the baseline is the frames before it")

    gaps = raid.gaps(100)
    check(abs(sum(gaps.values()) - 40.0) < 1e-6, f"a frame's phases add up to its whole time: {sum(gaps.values()):.3f}")
    check(gaps["physics and FixedUpdate"] > 30, "the slow phase is named: " + str({k: round(v, 1) for k, v in gaps.items()}))

    check(111 in [int(m) for _, m in raid.jit_by_frame[100]], "a compile lands on its own frame")
    check(raid.frame_at(starts[155]) is None and 149 not in raid.jit_by_frame, "a compile in an unsaved frame lands on no frame")
    check(not any(int(m) == 333 for items in raid.jit_by_frame.values() for _, m in items), "another thread's compile is not counted")

    table = analyze.context_table(raid)
    check(table["local shot"][0] == 1 and table["death"][0] == 1 and table["ammo pool object created"][0] == 1, "events near the spike are found")
    check(table["other bullet impact"][0] == 0 and table["other bullet impact"][2] > 0, "an event far from the spike only counts for other frames")
    check(table["several physics steps"][0] == 1, "several physics steps are seen")
    extra, worst = analyze.where_spike_time_went(raid)
    check(worst.most_common(1)[0][0] == "physics and FixedUpdate", "the extra time is put on the right work")
    check(analyze.probe_time_in_spikes(raid)["HfxGore"] == 2.0, "timed code in spike frames adds up")

    shots = analyze.shot_rows(raid)
    check(len(shots) == 1 and shots[0]["spike"] and shots[0]["worst_frame"] == 100, "the local shot is credited with the spike, and the bot's is not listed")

    late = analyze.Raid(folder)
    late.shots = [dict(late.shots[0], frame="95", qpc=str(starts[95]))]
    row = analyze.shot_rows(late)[0]
    check(row["worst_frame"] == 100, "a spike 5 frames into the firing cycle is still tied to the shot")

    felt = analyze.felt_rows(raid)
    check(felt[1]["spike"] and felt[1]["frame"] == 100, "a press 0.5 s after the spike finds it")
    check(not felt[0]["spike"], "a press with no spike before it says so")

    impacts = analyze.impact_rows(raid)
    check(len(impacts) == 1 and impacts[0]["kind"] == "body", "your impact is linked, another's is left out")
    deaths = analyze.death_rows(raid)
    check(deaths[0]["meters"] == 12 and deaths[0]["worst"] == 40.0, "a death reports what followed it")

    # PresentMon: 20 MHz raw counter against the report's 10 MHz clock, offset by the sync pair.
    raw = lambda sw: 5_000_000 + (sw - 1_000_000) * 2
    pm_path = os.path.join(sys.argv[1], "pm.csv")
    write(pm_path and sys.argv[1], "pm.csv", ["Application", "CPUStartQPC", "MsBetweenPresents"],
          # A present's interval ends at that present, so a slow frame's row comes at its end.
          [["EscapeFromTarkov.exe", raw(starts[f + 1]), 40.0 if f == 100 else 10.0] for f in range(1, 200)])
    captures = analyze.load_presentmon(pm_path)
    pm = analyze.presentmon_rows(raid, captures)
    check(pm["median"] == 10.0 and pm["over_double"] == 1, "PresentMon rows land inside the raid after clock conversion")
    check(pm["shot_worst"] == [40.0] and pm["felt"][1] == 40.0 and pm["felt"][0] == 10.0, "the slow present is matched to the shot and the press, not the other press")

    bad = os.path.join(sys.argv[1], "bad.csv")
    write(sys.argv[1], "bad.csv", ["Application", "Unknown"], [["x", 1]])
    try:
        analyze.load_presentmon(bad)
        check(False, "unknown PresentMon columns are refused")
    except analyze.PresentMonError as e:
        check("Unknown" in str(e), "unknown PresentMon columns are refused and named")
    seconds = os.path.join(sys.argv[1], "seconds.csv")
    write(sys.argv[1], "seconds.csv", ["TimeInSeconds", "MsBetweenPresents"], [[1.5, 10.0]])
    try:
        analyze.load_presentmon(seconds)
        check(False, "a capture without --qpc_time is refused")
    except analyze.PresentMonError as e:
        check("--qpc_time" in str(e), "a capture without --qpc_time is refused")

    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        analyze.report(folder, captures)
    text = out.getvalue()
    for needle in ("spike frames in what was kept: 1", "physics and FixedUpdate", "felt-a-stutter presses: 2, 1 with", "HollywoodFX (1)", "Spent Shells Lifetime", "PresentMon:", "deaths"):
        check(needle in text, "the printed report has: " + needle)

    print("ALL PASSED" if failures == 0 else f"{failures} FAILED")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
