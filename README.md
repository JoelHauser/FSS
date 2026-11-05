# First Shot Smoothness

An SPT client mod investigating the brief stutter that can come with firing a weapon.

**Current state: diagnostics only.** Version 0.0.1 measures what happens around each shot. It does
not change the game and contains no optimization, because no cause has been proven yet. With
diagnostics off, the default, it does nothing at all.

- Findings and hypotheses: [docs/INVESTIGATION.md](docs/INVESTIGATION.md)
- How to run the tests: [docs/TEST-PROCEDURE.md](docs/TEST-PROCEDURE.md)

## Install

Unzip `FirstShotSmoothness-<version>.zip` over the SPT root. SPT 4.1.x, EFT 0.16.9.

## Configure

`BepInEx\config\com.mybutthasarash.firstshotsmoothness.cfg`, or F12 in game.

| Setting | Default | Applies |
|---|---|---|
| Enabled | true | Restart |
| Diagnostics Enabled | false | Restart |
| Probe Level | Stages | Restart |
| Runtime JIT And GC Events | true | Restart |
| Frames Before Shot | 240 (30 to 1000) | Next raid |
| Frames After Shot | 300 (30 to 2000) | Next raid |
| Run Label | empty | Next raid |
| Felt A Stutter Key | F9 | Next raid |
| Suppress Muzzle Effects, Weapon Audio, Impact Effects, Bot Hearing, Muzzle Light | false | Restart |
| Hide Rounds Inside Weapon | false | Restart |

The Suppress and Hide settings are causal tests that change the game. They need diagnostics on and
are for controlled comparisons only.

Reports go to `FirstShotSmoothness-reports` in the SPT root after each raid. Read them with
`python tools/analyze.py <report folder> [--presentmon capture.csv]`.

`scripts/capture.ps1` records the PC's setup, GPU log and PresentMon alongside a run, and
`scripts/pack-results.ps1` zips the results. Neither is in the release zip.

## Remove

Delete `BepInEx\plugins\FirstShotSmoothness.dll`, its `.cfg`, and `FirstShotSmoothness-reports`.

## Compatibility

Not tested in game with anything yet, including Fika and HollywoodFX. Patches only observe unless
a Suppress setting is on. Reports record other mods' patches on the firing path, and HollywoodFX
gets extra timing when present. See docs/INVESTIGATION.md section 8.
