# First Shot Smoothness: investigation record

Status 2026-11-05: Stage A (inspection) and Stage B (diagnostics) done, build 0.0.5, four real raids
reported (sections 10 to 13). **No cause is established and no optimization exists.**

**What the user reports (the symptom, not yet measured):** the stutter happens occasionally when
firing at nothing and consistently in gunfights. They run HollywoodFX, and not ReShade or
RagdollKinetics. With a freecam mod they saw a round drawn inside the weapon when firing.
"Consistent in fights, occasional otherwise" suggests something that scales with combat more than
a once-per-launch first use, but that is an inference. Case 8 in the brief, a stutter coinciding
with firing but coming from elsewhere, is now a leading possibility. Everything below about the game's runtime behaviour is a hypothesis
until a report from a real raid says otherwise.

Labels used throughout:

- **Observed:** shown directly by the code, the data files, or a test that ran.
- **Inferred:** consistent with the evidence, not isolated.
- **Untested:** plausible, no runtime evidence yet.

## 1. Environment

Inspected on the work laptop's install, `C:\HUH`. It has never been started through the Launcher
(no `Assembly-CSharp.dll.spt-bak`, no Player.log, no BepInEx log). **The machine you play on,
`H:\SPT4.1.X`, was not reachable**, so its SPT version, mods and hardware are unknown until a
report's `session.txt` records them.

| Item | Value | Source |
|---|---|---|
| SPT | 4.1.3, release `SPT-4.1.3-40743-ddce41c` | spt-installer.log |
| Game build | 0.16.9.5.40743 | EscapeFromTarkov.exe version |
| Unity | 2022.3.43f1 | UnityPlayer.dll version |
| Scripting runtime | Mono 6.13.0, Boehm GC (`mono-2.0-bdwgc.dll`), JIT | ran it |
| BepInEx | 5.4.23.5, doorstop 4.5.0 | BepInEx.dll, .doorstop_version |
| Harmony | HarmonyX 2.9.0.0 | 0Harmony.dll |
| Target framework | net472 | matches the other SPT mods here |
| Boot flags | gfx jobs on, native gfx jobs on, `gc-max-time-slice=3` | boot.config |
| Fika | Not installed on `C:\HUH` (7Bpencil's `*.Fika.dll` are compatibility shims) | plugins folder |

Assembly note: `C:\HUH`'s Assembly-CSharp has obfuscated names. The readable one was rebuilt in a
scratch mirror by applying SPT's delta. The install itself was not changed.

Mods on `C:\HUH` touching the firing path (decompiled and checked):

| Mod | Patch | Effect |
|---|---|---|
| 7Bpencil MaterialEditor and WeaponCamoAndStickers | Postfix on `ObjectsFactory.CreateItemAsync` | On the chambered-round path. Wraps the task only if the item has custom materials or decals. |
| SPT `MemoryCollectionPatch` | Postfix on `MenuTaskBar.OnScreenChanged` | Changes GC behaviour mid-raid, see H6. |
| SPT `TinnitusFixPatch` | `BetterAudio.StartTinnitusEffect` | Not on the firing path. |

The report lists every other patch on firing types for the installed mod set, by owner.

## 2. What can actually be measured here

| Capability | Available? | Evidence |
|---|---|---|
| Monotonic high-resolution clock | Yes, Stopwatch (QPC) | |
| Unity Profiler, Recorder, most ProfilerRecorder counters | No, release player | `GC Allocated In Frame` string absent from UnityPlayer.dll |
| Shader compile logging | **No.** `logWhenShaderIsCompiled` exists, but no compile message string exists in the player | Searched UnityPlayer.dll |
| Per-phase frame timing | **Yes**, markers in the PlayerLoop | Built |
| GPU time | Unknown. `FrameTimingManager` is present but needs a build flag; the report records `IsFeatureEnabled()` | Runtime only |
| JIT: which method, when, how long | **Yes**, Mono profiler callbacks | Tested on the game's own runtime |
| GC pauses | **Yes** for full collections. Incremental slices may raise no events | Full collection tested; slices untested |
| Allocation per frame | Coarse only: Boehm heap size delta, meaningful while GC is off | |
| Presented frame time | Only externally, with PresentMon. The report's `qpc` columns are Stopwatch ticks; `session.txt` holds a sync pair, and the analyzer converts raw QPC with it | Frequencies matched here, not assumed |

CPU timings cannot show a GPU stall directly. A stall appears as main-thread time in the render
or wait phases, which proves where the main thread waited, not why.

## 3. Execution path (observed)

```
Animation event -> FirearmController.*FireOperation.OnFireEvent
  FireOperationBase.ProcessInventoryOperationsForSingleshotWithMalf   inventory, malfunction roll
  FirearmController.Shot
    Weapon.OnShot                               durability, overheat
    InitiateShot
      Player.OnMakingShot
        Skills.RecoilAction.Complete
        AIData.TryPlayShootSound                RATE LIMITED: once per second per shooter
          GlobalEventDispatcher.PlaySound       every bot's BotHearingSensor, synchronously
      BallisticsCalculator.CreateShot / Shoot
      OnStatisticsShot
      Firearms.PlayShotEffects -> MuzzleManager.Shot    sparks, fumes, smoke, light, heat
    WeaponSoundPlayer.FireBullet                picks clips, queues them; playback starts later,
                                                in SuperBetterAudioQueue.Update
    Firearms.MoveAmmoFromChamberToShellPort -> SetPatronInShellPort -> SetUsed(true)
Later frames (automatic: at these points of the firing cycle, in ProcessEvents):
  25%: InternalOnShellEjectEvent -> SpawnShell, after the end of the frame
  75%: Firearms.SetRoundIntoWeapon -> CreateItem -> Pools.PopOrCreate    next round, every shot
  ManualLateUpdate -> ProceduralWeaponAnimation.Shoot               recoil
  OnShellEjectEvent -> SpawnShell coroutine, after end of frame
  BallisticsCalculator.ManualUpdate -> ClientGameWorld.ShotDelegate -> PlayHitEffect
     -> Effects.AddEffectEmit, drained in Effects.Update -> Effect.Emit (decal, particles, sound)
```

Consequences for measuring: audio, recoil, shell, effects and impact all land in **later** frames.
A hitch one or two frames after the shot can still belong to it, and impacts are separate events
tied back by fire index.

**The game already warms a lot (observed).** This is why a generic warm-up mod could do nothing:

| Existing warm-up | Runs | Covers |
|---|---|---|
| `ShaderVariantCollection.WarmUp` on `Combined.generated` | App start | Bundled variants. Not mod bundles or variants missing from the list |
| `JitWarmUpper.Run` | App start | 14 types' own methods. **Not nested types**, so no `FireOperation` family, and not `Firearms`, `MuzzleManager`, `FirearmsEffects`, `SoundBank`, `SuperBetterAudioQueue`, `ProceduralWeaponAnimation`, `ObjectsFactory`, `AIData`, `BotHearingSensor` |
| `BallisticCalculatorPrewarmer` | Each raid load | Simulated shot through the calculator and `ShotDelegate` |
| `EffectsPrewarm` | Unknown | Emits all particle systems and materials. **No code references it**, so if it runs it is on a prefab. Runtime check needed |
| `AudioSourcePrewarmer` | Periodic | Player speech and step sources, not gunshot sources |

Round cost checked (observed): on-shot statistics are a counter plus a name check, so dropped as a
suspect. Ruled out by data (observed): the `CreateVisualBulletInstance` per-shot spawn only applies to
`ShowBullet` ammo, which is 2 of 210 ammo templates (airsoft, 20x1mm).

## 4. Hypotheses, in test order

| # | Hypothesis | Supporting code evidence | Predicted runtime signature | Test that could disprove it | Limitations |
|---|---|---|---|---|---|
| H1 | **First-use JIT** of firing code | Observed: Mono JIT; `JitWarmUpper` omits most of the path. Harness: about 0.12 ms per method warm, on this laptop; far more when files are cold | Main-thread JIT rows in the spike frame. Once per **app launch**, gone in raid 2. Per weapon type only if code paths differ | No JIT in spike frames, or the spike returns unchanged in raid 2 | `Stages` precompiles what it patches, so use `EventsOnly` for this. Mod JIT counts too |
| H2 | **First-use GPU or shader work** for muzzle, smoke, or impact. Stronger with an effects mod, see section 8 | Inferred: variants may be outside `Combined.generated`; `MuzzleManager` emits shared particle systems; `EffectsPrewarm`'s use unknown | Spike in `render` or `wait/present`, no JIT, 0 to 2 frames after the shot. Gone with Suppress Muzzle Effects | Same spike with muzzle effects suppressed | No shader log exists, so this stays Inferred without the isolation test. Driver cache makes run 1 differ from later runs |
| H3 | **Audio first play** | Observed: playback starts in queue update; weapon path never calls `LoadAudioData`. Clip varies by distance and indoors/outdoors | `ClipState` not Loaded, or `AudioQueueUpdate` or `audio` phase outlier at S to S+1. Recurs per clip variant | All clips Loaded, audio phases flat, no change with Suppress Weapon Audio | FMOD work may sit on its own thread, invisible here |
| H4 | **Bot hearing broadcast** imitating "first shot" | Observed: at most once per second, synchronous to all bots. A burst's first shot pays; the rest do not | `BotHearingBroadcast` outlier, scales with alive bots, absent with none. Recurs after any 1 s pause, every raid | Microseconds with bots present, or a spike with zero bots | Delayed tasks run later, outside this probe |
| H5 | **Ammo pool running out**, Instantiate during firing. Unlikely in the vanilla game, plausible with HollywoodFX, see section 8 | Observed: pop from an empty pool instantiates in place and adds `Shell`; the game tops the pool up to 3 spare. Vanilla: pool 60, shells return in about 1 s | `PoolObjectCreated` rows after the first shot, with `ChamberNextRound`/`PoolPop` outliers | None created during sustained fire | `PoolPop` is `Stages` only; `PoolObjectCreated` is in both levels |
| H6 | **GC** | Observed: `BaseLocalGame` disables GC for the raid only with at least **25 GB** RAM (SPT setting; client default 12). SPT's `MemoryCollectionPatch` **re-enables it on first inventory open mid-raid, permanently** | GC rows in the spike frame; `gc_mode` flips after inventory. Same rate near and away from shots if coincidental | No GC rows near spikes | A GC near a hitch does not mean firing caused it. Incremental slices may be silent |
| H7 | **Impact**, not firing | Observed: separate frames, deferred effects | Spike at the `Impact` frame, not at the shot. Absent firing at the sky | Same spike with no hit | |
| H8 | **Recurring per-shot cost** | Observed: per-shot async `CreateItemAsync`, closures in `OnMakingShot`, effects | Steady excess on repeat shots | Repeat-shot excess within noise | Heap delta is coarse |
| H9 | **Mod interaction** | Observed: 7Bpencil on chamber path; home mods unknown | Spike tied to a patched stage, gone in the minimal set | No change in the minimal set | Shared targets do not prove a conflict |
| H11 | **Spent case model first drawn on the first shot.** From the user's freecam sighting | Observed: pooled rounds start with the "shell" children off, and `SetUsed(true)` turns them on when firing. A freecam shows it exists, not what it costs | Render or wait spike with no JIT, once per ammo type, with HollywoodFX on or off. Gone with Hide Rounds Inside Weapon | No change with the round hidden | May share a shader and cost nothing. Later shots should be cheap either way |
| H12 | **Combat load**, not the shot: other shooters, deaths, bots | Observed: HollywoodFX runs muzzle effects for every visible shooter and impact effects immediately; corpses simulate for 15 s; bots hear and react | Spikes near bot shots, deaths or hits on you far more than near your shots alone. Physics or bot phases above usual | Spikes as common with no bots, or no link to those events | A link is not a cause. Needs Round 2 |
| H13 | **Not a slow frame at all**: pacing, recoil camera, audio | Observed: recoil moves the camera every shot. The rest untested | Key presses with no spike frame. PresentMon uneven where the mod is smooth | Presses reliably match spike frames | Depends on how quickly the key gets pressed |
| H14 | **Muzzle light or its shadow** | Observed: the light toggles per shot; HollywoodFX has a muzzle light shadow setting | Render spikes with shots, gone with that setting off | No change with it off | HollywoodFX mostly skips the game's own light |
| H10 | **Unrelated background work** | Untested | Spikes near shots no more frequent than away from them | `histogram.csv` rates differ clearly | Needs enough frames |

H1 leads because it has the most direct code evidence and costs one test. H4 and H6 matter
because they can **look** like first-shot bugs while being something else.

## 5. Diagnostic build

`FirstShotSmoothness.dll` 0.0.1 measures only. With diagnostics off, the default, it patches
nothing and does no per-frame work.

**Events.** Input: `FirearmController.SetTriggerPressed`. Accepted shot:
`FirearmController.Shot`. Dry fire: `DryShot`. Impact: `ClientGameWorld.ShotDelegate`, linked by
`Shot.FireIndex`. Firing work: probes in section 3. Frame interval: `LoopStart` to the next
`LoopStart`. Input receipt is not treated as a shot, and an impact is not assumed to share a frame.

**Files** in `FirstShotSmoothness-reports\<time>-raidN-<map>-<label>\`:

| File | Contents |
|---|---|
| `session.txt` | Versions, hardware, graphics API, VSync, cap, plugins, probe target results, other mods' firing patches, player loop order, overhead, drop counters |
| `frames.csv` | Kept frames: raw QPC at 13 loop marks, GC count, heap, GC mode, GPU/CPU time if available |
| `shots.csv` | Each shot: id, frame, shooter (Local/Bot/Remote), weapon, ammo, first-use flags, gap, alive players |
| `events.csv` | Probe timings (inclusive, exclusive), trigger, dry fire, impacts, clip state |
| `native.csv`, `jit_names.csv` | Every JIT and GC event with time and thread; method names |
| `histogram.csv` | Frame times of **all** frames, near vs away from local shots |
| `names.csv` | Id to weapon, ammo |

**How the requirements are met**

- Buffers are allocated once per raid (about 60 MB, plus 4 MB for the process). No file writes,
  formatting, LINQ or reflection in measured paths, apart from name-table entries at first sight
  of a weapon or ammo type.
- Overlapping windows: each frame is settled once, so automatic fire keeps a frame once. Tested.
- Overflow is counted and reported, never silent. Tested.
- Writing happens after the raid on a background thread with no Unity calls.
- The plugin compiles its own code at startup, so it does not appear as first-shot JIT. Tested.
- Patch targets resolve by exact signature at startup. Failures log one line and skip that probe.

**Tested in the game's own `mono-2.0-bdwgc.dll`** (`tools/Harness`, no Unity present): JIT and GC
capture, method names, thread attribution, window logic, nesting, overflow, report files, patch
targets resolving and patching (all but the 2 that need Unity, section 8), clock sync, and probe
cost of about 55 ns per call on this laptop. `tools/test_analyze.py` checks the analyzer against a
made-up report with known answers, including PresentMon alignment.

**Not tested: anything inside the actual game.** That includes PlayerLoop markers, loading in
BepInEx, the `Open`/`Close` raid lifecycle, and the callbacks under Unity's GC settings. The first
run's job is to prove those.

**Known measurement limits**

- `Stages` compiles probed methods at startup, hiding their first-use JIT. `EventsOnly` still
  precompiles `Shot`, `ShotDelegate`, `SetTriggerPressed`, `DryShot` and `CreateShot`.
- Probes add about 55 ns each to timed parents.
- `jit_names.csv` follows raw pointers; a dynamic method freed during the raid could crash at
  write time. All other files are written first.
- The session opens when a player exists, during load, so early frames can be loading hitches.
  Analysis only looks around shots.

## 6. What each result leads to

| Result | Next step |
|---|---|
| H1 confirmed | Compile the methods named in `jit_names.csv` during raid load, with `GetFunctionPointer`. Then verify they leave the first-shot frame and how much load time they add |
| H2 | Identify the effect by isolation, then find what prepares it. A generic warm-up call is not assumed to cover the real draw |
| H3 | Find which clips and how they load, then load those specific clips earlier |
| H4 | Not first-use. Profile `OnSoundPlayedInner`; only safe caching is allowed, hearing must not change |
| H6 | Mostly a settings or SPT-patch finding. Report it, do not force collections |
| Nothing above reaches the noise floor | Report inconclusive, ship no fix |

## 7. Invariants any fix must preserve

Damage and penetration, trajectories and collision, fire rate and input response, recoil and
animation, ammo use, weapon condition and malfunctions, all sounds and visuals (flash, smoke,
shells, impacts), AI hearing and reactions, quest, skill and stat updates, and network behaviour.

Never: hidden live rounds, forced GC, preloading everything, global thread priorities, driver
changes, or Unity work on background threads. A fix must not just move the hitch to weapon
switching or raid entry without that tradeoff being measured. Any fix keeps a master switch, so
baseline and optimized runs stay separately selectable.

## 8. Graphics and effects mods: HollywoodFX

An effects mod replaces part of what this investigation measures, so results from one mod set do
not carry over to another. The report now records, for any mod: its patches on firing types by
owning assembly and kind, and those mods' settings at raid start.

HollywoodFX was read from source, `SleepingPills/HollywoodFX` at 1bbdd02 ("Update to 4.1",
2026-09-17). **It is not on `C:\HUH`, was never run here, and your installed version may differ.**
Its bundle is not in that repository, so its shaders were not inspected.

| What it does (observed in source) | Why it matters here |
|---|---|
| Prefix on `MuzzleManager.Shot`, usually skipping the original | The game's muzzle emit often does not run, so a warm-up aimed at it would miss. Effects are chosen by weapon class and silenced state |
| Loads muzzle, impact and decal effects from its own bundle; removes the game's default particles in `Effects.Awake`. No warm-up code found | The game's warm-ups cannot cover these. Strengthens H2, and predicts first use per weapon class and per surface material, not once per launch |
| Prefix on `Effects.Emit` runs impact effects **immediately** | Vanilla defers to `Effects.Update`; here the cost lands in the hit frame |
| Replaces the ballistics callback with its own `OnShot`, which then calls the original | Runs outside `ClientGameWorld.ShotDelegate`, and the game's raid-load prewarm calls the delegate directly, skipping it |
| **Spent shell lifetime 60 s by default; the game uses 1 s** | Each round's pooled object stays out 60 times longer. New for H5: firing more than about 60 rounds of one ammo type inside a minute can force new pool objects mid-fight. Pools do not shrink during a raid, so it should fade. Also a possible per-frame cost from many live shells |
| Comment: Fika overrides `ShotDelegate` without calling base | My impact hook would see nothing under Fika. The analyzer warns if no impacts link to shots |

These are predictions from source. **None of it is measured.** HollywoodFX is a candidate, not a
culprit, and the leading hypothesis stays open.

**Changes to the diagnostics for this**

- Probes now wrap other mods' patches regardless of load order (prefix first, postfix last).
  Previously that depended on which plugin loaded first. Tested both orders with a stand-in mod
  whose prefix skips the original.
- New stages: `MuzzleManagerShot`, `EffectsEmit`, and, when found by name, `HfxOnShot` and
  `HfxImpactEmit`.
- New event: `PoolObjectCreated`, from `Shell.Awake`, at both probe levels.
- The analyzer groups JIT in spike frames by owner, reports pool growth after the first shot, and
  compares baseline frame time early and late in a raid.
- The existing Suppress switches still work, because they act above HollywoodFX's hooks:
  Muzzle at `Firearms.PlayShotEffects`, Impact at `PlayHitEffect`. Suppress Impact does not stop
  its `OnShot`, which also registers damage for gore.

**Limits**

- `MuzzleManager.Shot` and `Effects.Emit` could not be patched in the harness, because their
  classes' static initializers need Unity. They are untested until the game runs. If they fail
  there, the outer stages `MuzzleEffects` and `PlayHitEffect` still cover the same work.
- HollywoodFX's probes were tested against stand-ins with its type names, not the real DLL. If a
  version renames them, the report shows `MISSING` and the rest still works.
- HollywoodFX's per-frame rendering and post-processing change the baseline, so frame times are
  not comparable between runs with it on and off, only their spikes against their own baseline.

**Constraints on any later fix.** A warm-up must target whichever effects actually draw, with the
mod on and with it off. It must not depend on HollywoodFX's internals without a fallback. Lowering
its shell lifetime changes how the mod looks, so that is your choice as a setting, not something
this mod should do silently.

## 9. What 0.0.3 added, after the user's answers

- **Starts from the hitch as well as the shot.** Every spike in saved frames is listed with what
  happened within 200 ms, compared against other frames, plus which work ran above its usual time.
- **Felt A Stutter key.** Compares each press with the worst frame in the 1.5 s before it, and with
  PresentMon. This is the direct test of H13.
- **Bot shots keep a short window and deaths a long one**, 1500 frames, for comparison and corpse
  physics. Deaths and hits on you are events.
- **All of a frame is accounted for.** Time is split between consecutive marks, so phases sum to the
  frame. New marks: asset preload, script Update and LateUpdate, animation, particles, skinned
  meshes, renderers, present.
- **Fixes:** a JIT or GC in an unsaved frame was credited to the last saved frame. The physics mark
  kept the last step's start, under-reporting physics in slow frames; it now keeps the first and
  counts steps. Shots look across the firing cycle, 150 ms, not 3 frames. Burst shots now have a
  baseline.
- **New probes:** the round's move to the shell port and `SetUsed`, muzzle light, Reflex flash,
  `OnDead`, bot update, bot hearing, AI tasks, and HollywoodFX's gore, impact and ambience code.
- **New test switches:** Hide Rounds Inside Weapon, Suppress Muzzle Light.
- **Recorded:** game graphics settings, HollywoodFX's settings, and with `scripts/capture.ps1` the
  PC's setup, overlays, GPU load and temperature, and PresentMon.

**Still blind to:** worker-thread and GPU time, except as main-thread waiting; Mono type and
assembly loading apart from compiles; delayed bot reactions beyond the bot-update probes; and
anything outside the process except what the capture script reads.

## 10. First real raid (0.0.3, Reserve, 2026-11-05)

One raid on `H:\SPT4.1.X`, 65 local shots, 15,497 frames kept, nothing dropped. One raid is not proof:
everything here is **Observed** for this run and **Inferred** as a pattern.

Problems with the run:

- All 10 felt-stutter presses also toggled Camera Mode, which shares F9, so they are 5 on/off pairs,
  not felt stutters. Disregard them. No local shot fell inside a Camera Mode window, so the shot data
  stands. Camera Mode's Freeze was also used during the raid. 0.0.4 moves the key to Pause.
- `jit_names.csv` stopped at 9,736 of 24,083 methods: one name was not valid UTF-8 and
  `Marshal.PtrToStringAnsi` threw. Fixed in 0.0.4. The largest JIT item, 678 ms, is an unnamed pointer.
  Pointers change per launch, so this report cannot name it.

Results:

| Measure | Result |
|---|---|
| Repeat shots | n=64, median 1.29 ms over baseline, 7 spikes |
| Spike frames near a local shot | 5%, the same as other kept frames (kept frames are not a random sample) |
| First shot | +104.8 ms: 90.3 ms waiting on last frame/present, JIT only 12.5 ms |
| Your impacts on a body | n=13, median +10.89 ms, worst +100.75 ms |
| Your impacts on the world | n=36, median +0.87 ms, worst +24.74 ms |
| Kill at shot 32 | +100.7 ms: JIT 514 methods 68.1 ms, `PlayerOnDead` 40.93 ms inside `ApplyShot` 42.71 ms |
| Shots 34, 36, 57, 58 | +11 to +23 ms in render submit, with little or no JIT or timed script work |
| Baseline frame time | 13.76 ms over the first 16 shots, 18.63 ms over the last 16 |

Reading (**Inferred**): firing itself is usually cheap here. Fights may stutter because hits and kills
cost more than shots, which fits the symptom, and a separate render-side cost appears on some shots.
The first shot looks like a main-thread wait rather than JIT; a wait does not show its cause.

Next: a raid on 0.0.4 with Camera Mode unused, to name the JIT items and get real felt-stutter marks,
with PresentMon for the first-shot wait.

## 11. Second raid (0.0.4, Factory, 2026-11-05) and the crash

0.0.4 crashed the game at raid end, in the menu: access violation in `mono_method_full_name`, called
by the report writer (**Observed**: Player.log stack, Windows event 1000). 0.0.3's exception had been
stopping the writer before it reached a stale pointer, so 0.0.4's name fix exposed the crash. 0.0.5
records Mono's `method_free` event and never resolves a freed pointer. The harness saw 2,931 of 3,000
dynamic methods raise it, on the game's runtime. **Untested in game.** A method freed in the moment
between the check and the read is still a possible crash, as is a wrapper whose data went with a freed
method without an event of its own. Names are flushed line by line, so a crash keeps what was read.

The report has everything but the last of `jit_names.csv` (about 9,990 of 27,237 names). Probe Level was
EventsOnly, so per-stage timing is missing. No felt-stutter presses.

| Measure | Reserve | Factory |
|---|---|---|
| Repeat shots, median over baseline | +1.29 ms (n=64) | +1.10 ms (n=68) |
| Repeat shots that spiked | 7 | 8 |
| Impacts on a body, median | +10.89 ms (n=13) | +4.54 ms (n=12) |
| Impacts on the world, median | +0.87 ms (n=36) | +0.68 ms (n=92) |
| Spike frames near a local shot vs other kept frames | 5% / 5% | 4% / 4% |
| One huge mid-raid hitch, no shot near it | frame 7024: +9.6 s, 9,449 methods, 3.5 s JIT | frame 7542: +8.8 s, 9,408 methods, 3.3 s JIT |
| Largest single JIT item, unnamed | 678 ms | 797 ms |

Repeated across both raids (**Observed**): repeat shots stay cheap, body hits cost more than world hits,
and one mid-raid frame compiles about 9,400 methods. The first shot did not repeat: +105 ms on Reserve,
+15 ms on Factory, so the Reserve wait was not a fixed first-shot cost. Factory's larger shot spikes were
JIT inside `ShotDelegate` (shot 51, +81 ms, 59.7 ms of JIT) and GC pauses of 25-31 ms alongside a wait
for last frame/present (shots 43, 53).

Next: a raid on 0.0.5 at Probe Level Stages, to name the 9,400-method hitch and the largest JIT item.

## 12. Third raid (0.0.5, Factory, 2026-11-05)

0.0.5 wrote a full report with no crash: 28,751 names, 243 freed and skipped, 0 unreadable. Probe Level
was still EventsOnly. 101 local shots, 154 bot shots. One felt-stutter press, with no spike before it.

**The ~9 s mid-raid freeze is ModProfiler (Observed in this raid).** Frame 4015's first compiles are
`WebPanelController:TogglePanel` then `Instrumenter:InstrumentAll`, and its config says the first
activation instruments all mod code and can freeze the game. It is F11, and it is not a firing or
game cost. The first two raids show the same 9,400-method frame, but their names were lost, so
ModProfiler is **Inferred** there. Median frame time around that frame did not rise on either Factory
run (15.9 to 15.9, 15.7 to 15.6 ms); Reserve rose 14.6 to 16.6 ms.

**The first body hit of a launch compiles about 510 methods (Observed, 3 of 3 raids).**

| Raid | Frame of first body impact | Frame time | Compiles |
|---|---|---|---|
| Reserve | 10086 | 117.8 ms | 514 |
| Factory, 0.0.4 | 8778 | 96.5 ms | 527 |
| Factory, 0.0.5 | 5586 | 104.9 ms | 507 |

The compiles are the hit path: `BodyPartCollider`, armor, `ApplyShot`, `ProceedLocalAbsorbedDamage`,
HollywoodFX gore, `BotMemory`, statistics, quest checkers, ORBIT's death diagnostics. Each raid was the
first of its launch, so a later raid should not pay this; that is **Untested**.

Caveat: ModProfiler was activated before the first body hit in all three raids, so these counts and
the repeat costs below include whatever it adds. A raid without F11 is needed.

Later body hits still cost more (median +7.80 ms against +1.15 ms for world, n=78 and 95), and shots 36
and 73 reached +58 and +62 ms, with a 34.6 ms GC pause on shot 36. Without Stages timing, the
15 ms `scripts LateUpdate` frames on shots 22, 54 and 68 can't be attributed.

Next: a raid at Probe Level Stages without ever pressing F11, then a second raid in the same launch to
test whether the first-body-hit cost is once per launch.

## 13. Fourth raid (0.0.5, Factory, Stages, no ModProfiler, 2026-11-05)

The first clean run: Probe Level Stages, `Instrumenter` never compiled. 94 local shots, 39 bot shots,
no felt-stutter presses. The ~9 s frame is gone, which confirms section 12's ModProfiler reading.

**First-use JIT without ModProfiler (Observed).** First body hit: frame 3474, 53.6 ms, 236 compiles, and
it was also the first shot of the launch. First kill: frame 3684, 52.8 ms, 339 compiles. Together
that is about the 510 compiles of the earlier raids, where hit and kill fell in one frame (**Inferred**).

**Periodic GC is the largest repeating cost found so far (H6).** In both Factory raids measured for it:

| | Stages raid | EventsOnly raid |
|---|---|---|
| GC pauses in the play window | 83 in 332 s | 80 in 321 s |
| Within 3 s after a local shot | 14.2 per minute | 12.5 per minute |
| Otherwise | 15.1 per minute | 15.7 per minute |
| Pauses over 20 ms | 40 | 39 |

Pauses come in pairs, about 10 ms then about 30 ms, 0.1 to 0.2 s apart, with a median 8.26 s between
the large ones (Stages raid). A frame containing a large one has a median of 62.8 ms against a baseline
near 19 ms. The rate does not change in fights, so **fights do not cause it**. The worst repeat shots
(71, 72, 100: about +50 ms) are this GC landing near a shot: 44.71 ms in `CreateItem` on 71 and 72 is a
32.9 ms GC pause inside it, not slow item creation. Whether the period comes from a timer or a steady
allocation rate is **Untested**.

Why GC runs at all (**Observed** in code path and JIT order): this PC has 31.8 GB, so `BaseLocalGame`
would disable GC in raid (25 GB rule). Skills Extended's `AutomaticCollection:BeforeMode` intercepts that
(`Automatic garbage collection during raids = true`, default on). SPT's `MemoryCollectionPatch` also
called `CollectIncremental` at the inventory open at t=131 s. The managed heap went from 1.87 to 2.30 GB.

Whether this is what the user feels is **Inferred**: a 60+ ms frame every 8 s is more often noticed in a
fight, which would fit "occasional when firing at nothing, consistent in gunfights", but no felt press
has confirmed it.

Next: causal test. Same raid with that Skills Extended setting off and the inventory left closed, with
`Run Label` set. Memory will grow during it, and that tradeoff must be measured before any fix.
