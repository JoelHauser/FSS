# First Shot Smoothness: working notes for Claude

Evidence-driven investigation of a stutter on firing, per Joel's brief of 2026-11-05: establish
the cause, apply the smallest justified fix, prove it reduces frame-time spikes without changing
gameplay. **Do not add an optimization without a report that proves its cause.**
docs/INVESTIGATION.md holds the findings, hypotheses and what each result leads to.

## State

0.0.3 is diagnostics only (HollywoodFX: INVESTIGATION.md section 8; hitch-centred tools: section 9), built against a mirror, **never run in game**. Waiting on the reports
from docs/TEST-PROCEDURE.md. Repo https://github.com/JoelHauser/FSS.git (public). The zip is
not attached to any release yet; `dist/` is ignored, so rebuild it with scripts\pack.ps1.

## Building on the work machine

`C:\HUH` is unpatched, and must stay untouched. Build a mirror in a scratch folder:

1. Copy `C:\HUH\BepInEx\core` and `C:\HUH\EscapeFromTarkov_Data\Managed` into
   `<mirror>\BepInEx\core` and `<mirror>\EscapeFromTarkov_Data\Managed`.
2. Keep `Assembly-CSharp.dll` as `.spt-bak` and apply
   `C:\HUH\SPT_Runtime\SPT_Data\Launcher\Patches\SPT-core\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll.delta`
   with SharpHDiffPatch.Core: `new HDiffPatch(); p.Initialize(delta); p.Patch(input, output, false, default, false, false);`
3. `scripts\pack.ps1 -SPTPath <mirror>` from PowerShell.

Unlike PlateDurabilityUI, this source names SPT-remapped members (`_player`, `FireOperationBase`),
so it only compiles against a patched assembly.

## Testing without the game

`tools/Harness` shares the plugin's source and runs in the game's real runtime, hosted by
`tools/MonoHost`:

```
dotnet build tools\Harness\Harness.csproj -c Release -p:SPTPath=<mirror> -o <hbin>
dotnet build tools\MonoHost\MonoHost.csproj -c Release -o <mhost>
# <asm> = all of Managed + BepInEx\core + Harness.exe in one folder
<mhost>\MonoHost.exe C:\HUH\MonoBleedingEdge\EmbedRuntime C:\HUH\MonoBleedingEdge\etc <asm> <asm>\Harness.exe <outdir>
```

It defines stand-ins with HollywoodFX's type names. "cant resolve internal call" lines are expected: UnityPlayer isn't loaded. It exits non-zero on a
failed check. It writes `<outdir>\synthetic`, which `tools/analyze.py` can read.

`python tools/test_analyze.py <empty folder>` checks the analyzer against a report with known answers.

## Things learned the hard way

- A diagnostic looking for first-shot JIT must not cause it: `Probes.Warm` compiles the plugin's
  own code. `method.MethodHandle.GetFunctionPointer()` compiles anything, including closed
  generics; the `PrepareMethod(handle, instantiation)` overload did **not** compile a closed
  generic here.
- Native callbacks must stay machine code. Managed code in a JIT or GC callback can re-enter the
  JIT or take a lock the runtime holds.
- Don't hook empty virtuals like `RegisterShot`: a `base.` call can be inlined past the patch.
- "IL Compile Error" patching `Effects` or `MuzzleManager` methods in the harness is a harness
  limit: their static initializers call Unity, which isn't loaded. The harness allows exactly
  `MuzzleManager.Shot` and `Effects.Emit`. Those two are untested until the game runs.
- Probes must wrap other mods' patches: `Priority.First` prefix, `Priority.Last` postfix. HollywoodFX
  has prefixes on the firing path that skip the original.
- SPT `ModulePatch` Harmony ids are patch class names, so name a patch's owner by its assembly.
- When reading native events in a test, freeze the end index first: the test's own code JITs too.
- First-use cost varies hugely with disk state: the same 24 LINQ methods took 80 ms cold and about
  2.5 ms warm in the harness, so a cold run is not comparable to a warm one.

## Rules

- Keep measured paths free of allocation, formatting, LINQ, reflection and file I/O.
- Report writer: no Unity API, it runs on a background thread.
- Release zips hold only `BepInEx/plugins/FirstShotSmoothness.dll`. Version in the csproj and the plugin.
- No hidden live rounds, forced GC, thread priority changes or broad preloading, ever. See the
  invariants in docs/INVESTIGATION.md section 7.
