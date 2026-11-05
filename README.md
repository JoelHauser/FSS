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
| Frames Before Shot | 120 (30 to 1000) | Next raid |
| Frames After Shot | 300 (30 to 2000) | Next raid |
| Run Label | empty | Next raid |
| Suppress Muzzle Effects, Weapon Audio, Impact Effects, Bot Hearing | false | Restart |

The four Suppress settings are causal tests that change the game. They need diagnostics on and
are for controlled comparisons only.

Reports go to `FirstShotSmoothness-reports` in the SPT root after each raid. Read them with
`python tools/analyze.py <report folder>`.

## Remove

Delete `BepInEx\plugins\FirstShotSmoothness.dll`, its `.cfg`, and `FirstShotSmoothness-reports`.

## Compatibility

Not tested in game with anything yet, including Fika. Patches only observe unless a Suppress
setting is on.
