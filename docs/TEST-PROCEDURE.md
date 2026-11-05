# Test procedure

Round 1 takes about 45 minutes and finds out **what the stutter is tied to**. Round 2 tests only
what Round 1 points to, so most of it will never be needed.

Nothing here deletes or changes your mods. Keep your normal mods and HollywoodFX settings for
Round 1, because that's the setup where you get the stutter.

## Setup, once

1. Unzip `FirstShotSmoothness-0.0.3.zip` over your SPT root, so you get
   `BepInEx\plugins\FirstShotSmoothness.dll`.
2. Start the game once, then quit. This creates
   `BepInEx\config\com.mybutthasarash.firstshotsmoothness.cfg`.
3. In that file set `Diagnostics Enabled = true`.
4. Set `Felt A Stutter Key` to something you can hit mid-fight. The default is F9, which I haven't
   checked against your keybinds. A spare mouse side button works too (`Mouse3` or `Mouse4`).
5. Get [PresentMon](https://github.com/GameTechDev/PresentMon), the console exe, from its releases
   page. It shows the frames your display actually got, which this mod can't see.
6. Copy this repo's `scripts` folder somewhere on that PC.

Leave these alone for every run: graphics settings, FPS cap, VSync, resolution, mods. Don't clear
any shader or driver cache.

## Smoke test, 5 minutes

This build has never run in the game, so check it first.

1. Start the game and open `BepInEx\LogOutput.log`. You should see `Diagnostics on.`
2. Do a short raid, fire a few shots, press your stutter key, and leave.
3. The log should say `Report written:`, with a new folder in `FirstShotSmoothness-reports`.

**Stop and send me the log if** you see `MISSING` or `FAILED` lines, no report appears, or the game
crashes. `MuzzleManager.Shot` and `Effects.Emit` matter most, because I couldn't test those two
here. If it crashes, set `Runtime JIT And GC Events = false` and try again.

Delete that test report before starting Round 1.

## Round 1

### Run S: firing at nothing (2 launches)

Set `Probe Level = EventsOnly`, because this patches the least and gives the cleanest view of
first-use work. Use labels `S1` and `S2` in `Run Label`.

- Map: Factory, offline, **AI Amount: None** if your raid settings offer it. Otherwise tell me.
- Loadout: an automatic rifle with no suppressor and an optic if you normally use one, plus a
  pistol. Same every time. Write down the ammo.
- Don't open your inventory or alt-tab.

For each launch:

1. Start the game. Don't fire in the hideout.
2. Raid 1: stand still for 20 s after spawning, then look at open sky or the farthest surface you
   can see.
3. Fire 10 single shots, 3 s apart.
4. Fire a 3-second burst. Wait 5 s.
5. Switch to the pistol, wait 3 s, and fire 3 shots, 3 s apart.
6. Switch back, wait 60 s, and fire 3 single shots, 3 s apart.
7. Leave the raid and wait for `Report written` in the log.
8. Raid 2, same launch: exactly the same steps.
9. Quit fully.

Do `S1` right after a reboot and tell me you did. Cold disk reads can cost far more than later ones.

### Run G: real gunfights (2 launches, the important one)

Set `Probe Level = Stages`, labels `G1` and `G2`.

For each launch, start the capture **before** the game, from an administrator PowerShell if
PresentMon needs it:

```
powershell -ExecutionPolicy Bypass -File scripts\capture.ps1 -SPTPath H:\SPT4.1.X -Label G1 -PresentMon C:\Tools\PresentMon.exe
```

Then play normally, on a map and with bots where you usually get the stutter. Aim for at least
10 minutes with several real fights. Same map both times if you can.

- **Press your stutter key every time you feel the stutter.** A late press is fine, since it looks
  back 1.5 seconds. Don't press it when you don't feel one.
- Opening your inventory is fine here. Play as you normally would.
- Leave the raid properly, or die, so the report gets written, then quit the game.

The capture stops by itself when the game closes.

### Send

```
powershell -ExecutionPolicy Bypass -File scripts\pack-results.ps1 -SPTPath H:\SPT4.1.X
```

That makes a results zip in your SPT folder, with no profile or save data. Please also tell me:

- Your mouse polling rate, and whether the game is on an SSD.
- Roughly how many times per fight you felt it, and what you were doing: firing, being shot at,
  killing something, or just nearby.
- Anything that went wrong in a run. Keep failed runs instead of deleting them. A run is only
  excluded for a stated cause, such as alt-tabbing or dropped data in `session.txt`, never because
  its numbers look odd.

## Round 2: only the parts Round 1 points to

Each comparison changes one thing. Use at least 3 launches per setup, in mirrored order such as
A, B, B, A, A, B, so warming caches don't favour one. Same map, bots and route each time.

| If Round 1 points to | Compare | How |
|---|---|---|
| Spent shells and ammo pool growth | Lifetime 60 s against 1 s | HollywoodFX `Spent Shells Lifetime (seconds)`. Fire 3 full magazines inside 60 s, wait 70 s, fire 1 more |
| Blood and gore | On against off | HollywoodFX `Enable Gore Effects` |
| Ragdoll physics after kills | On against off | HollywoodFX `Enable Ragdoll Effects (RESTART)` |
| Muzzle effects | On against off | HollywoodFX `Enable Muzzle Effects (RESTART)` |
| Muzzle light shadows | On against off | HollywoodFX `Enable Muzzle Light Shadow (RESTART)` |
| HollywoodFX as a whole | Installed against not | Move its files out of `BepInEx\plugins`, and back afterwards |
| The round inside the weapon | Drawn against hidden | This mod's `Hide Rounds Inside Weapon` |
| Bot hearing | On against off | This mod's `Suppress Bot Hearing` |
| Weapon audio | On against off | This mod's `Suppress Weapon Audio` |
| Your impact effects | On against off | This mod's `Suppress Impact Effects` |
| Scope or tracers | With against without | Loadout change only |
| The diagnostics themselves | Plugin removed against plugin on | PresentMon decides this one |

Back up `BepInEx\config\com.janky.hollywoodfx.cfg` before changing it. The Suppress and Hide
settings change the game, so turn them off for normal play.

## How results are judged

- Baseline: median frame time of up to 60 saved frames just before.
- Spike: a frame at least 3 ms and 1.5x over its baseline.
- "Near" an event: within 200 ms. This shows what was happening, not what caused it, so spikes
  are compared with other frames, and only a Round 2 comparison can show a cause.
- Claims about launches use one number per launch, shown individually with absolute times.
- A key press with no spike means that stutter wasn't a slow main-thread frame. PresentMon, recoil
  and audio come next.

These thresholds were set before seeing any data. If they change, the results will say so and
show both.

## Removing it

Delete `BepInEx\plugins\FirstShotSmoothness.dll`, its `.cfg`, the `FirstShotSmoothness-reports`
folder, and any results zip. Nothing else was touched.
