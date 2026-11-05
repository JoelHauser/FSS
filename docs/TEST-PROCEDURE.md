# Test procedure, round 1

Goal: find out **when** the stutter happens and what runs in that frame. About an hour, on the
machine you play on. Nothing here deletes or changes your mods.

## Setup, once

1. Unzip `FirstShotSmoothness-0.0.1.zip` over your SPT
   root, so you get `BepInEx\plugins\FirstShotSmoothness.dll`.
2. Start the game once, then quit. This creates
   `BepInEx\config\com.mybutthasarash.firstshotsmoothness.cfg`.
3. In that file set:
   - `Diagnostics Enabled = true`
   - `Probe Level = EventsOnly`
4. Start the game and check `BepInEx\LogOutput.log` for `Diagnostics on.` If you see lines with
   `MISSING` or `FAILED`, stop and send me the log: your build differs from the one I inspected.
5. Leave these alone for every run below: graphics settings, FPS cap, VSync, resolution, mods.
   Do not clear any shader or driver cache.

**The fixed test**

- Map: Factory, daytime, offline. **AI Amount: None** if your raid settings offer it. If they
  don't, say so in your notes.
- Loadout: one automatic rifle with no suppressor, one ammo type, and a pistol. Same every time.
- **Do not open your inventory during a raid**, and don't alt-tab. Opening the inventory changes
  the game's garbage collection for the rest of that raid, which would muddy the test.
- After spawning, stand still for 20 seconds, then look at open sky if you can, or the farthest
  surface you can see.

**What counts as a first shot:** the first round you fire in that launch or raid, after that
20-second wait. The report flags these automatically.

**Shot spacing:** single shots 3 seconds apart. This keeps windows separate, and stays clear of a
once-per-second game timer that could otherwise look like a first-shot effect.

## Run A: app launch vs raid (5 launches, `EventsOnly`)

Each launch counts as one independent sample, so more shots in one raid do not substitute for it.

For each launch, set `Run Label` to `A1`, `A2` ... `A5` before starting the game:

1. Start the game. Do not fire anything in the hideout.
2. **Raid 1:** the fixed test. Fire 10 single shots 3 s apart. Wait 5 s. Fire one 3-second
   full-auto burst. Wait 5 s.
3. Leave the raid (Esc, Leave) and return to the menu. Wait for `Report written` in the log.
4. **Raid 2, same launch:** exactly the same actions.
5. Quit the game fully.

Do **A1 right after a reboot** and note that, because the first read of game files from disk can
cost far more than later reads. A2 to A5 can follow one after another.

## Run B: stages and switching (3 launches, `Stages`)

Set `Probe Level = Stages`, labels `B1` to `B3`. One raid each, same start, then:

1. 5 single shots at the sky, 3 s apart.
2. 5 single shots at a nearby concrete wall, 3 s apart.
3. 5 single shots at a metal surface or the ground, 3 s apart.
4. Switch to the pistol, wait 3 s, 3 shots, 3 s apart.
5. Switch back to the rifle, wait 3 s, 3 shots.
6. Stand still for 2 minutes, then 3 shots.

## Run C: bots (2 launches, `Stages`)

Labels `C1`, `C2`. **AI Amount: Medium**, same map. One raid each: 10 single shots at the sky,
3 s apart, then kill one bot if you meet one. Just play naturally after that.

## Tell me how it feels

Please write a few lines, because this settles what we are actually chasing:

- Do you feel it on the first shot only, every first shot of a burst, or every shot?
- Is it every raid, or only the first raid after launching?
- Roughly how long does it feel: a blip, or a clear freeze?
- Which runs felt worst?

## What to send

Zip these and send them:

- The whole `FirstShotSmoothness-reports` folder in your SPT root.
- `BepInEx\LogOutput.log` from the last run.
- `%USERPROFILE%\AppData\LocalLow\Battlestate Games\EscapeFromTarkov\Player.log`, if it exists.
- Your notes, including anything that went wrong in a run.

Keep a failed run rather than deleting it: say what happened and it will be marked as excluded,
with the reason. A run is only excluded for a stated cause, such as alt-tabbing, opening the
inventory, or a counter in `session.txt` showing dropped data. Never because its numbers look odd.

**If a report is missing for a raid:** a raid with no shots writes nothing, on purpose. If the
game crashes, send the log and turn off `Runtime JIT And GC Events`, then retest.

**Optional but valuable:** PresentMon captures presented frames, which this mod cannot see.
Run `PresentMon --process_name EscapeFromTarkov.exe --output_file a1.csv --qpc_time` alongside A1
and B1. Its timestamps line up with the report's `qpc` columns.

## How results are judged

- Baseline: median frame time of up to 60 frames before the shot, stopping at the previous shot.
- Spike: slowest of the shot frame and the next 3, at least 3 ms and 1.5x over its baseline.
- First-shot claims use one number per launch, 5 from Run A. They are shown individually, with
  absolute times, not just as an average.
- Near-shot spikes are compared with the rate of slow frames away from shots.

These thresholds are starting definitions, set before seeing data. If they change, the report
will say so and show both.

## Removing it

Delete `BepInEx\plugins\FirstShotSmoothness.dll`, the `.cfg` above, and the
`FirstShotSmoothness-reports` folder. Nothing else was touched.
