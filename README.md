# pta-multichart

An **A Dance of Fire and Ice (ADOFAI)** UnityModManager mod that brings **multi-track charting** to the level editor while compiling down to a **vanilla-playable single-track `.adofai` chart** — no custom `LevelEventType`, no `requiredMods`, and playable even without the mod installed.

[中文文档](README_zh-CN.md)

---

## Core Idea

ADOFAI only evaluates hits along a single main track. `pta-multichart` separates **editing representation (Logical Chart)** from **saved/played representation (Playable Chart)**:

1. **Logical Chart (in-editor & persisted metadata)**
   - Track `0` is the main track; tracks `1..N` are sub-tracks anchored to a main-track floor (`seqID`), inheriting exit angle, rotation direction (`Twirl`), and effective BPM (`SetSpeed`) at creation time.
   - All tracks render simultaneously in the editor (`TrackOverlay` draws non-active tracks as clickable ghost tiles). Clicking any tile on any track switches the editor cleanly to that track while keeping the global level `settings` intact.
   - The full logical chart JSON is stored inside a locked `EditorComment` on floor `0` prefixed with `[[PTA-MC:v1]]\n`.

2. **Playable Chart (compiled on `SaveLevel` / `Play`)**
   - **Sub-track clicks** are embedded into the main track at their exact song timestamps (`SubTrackOrigin`: `seq 1` aligns with the anchor floor and is not duplicated; `seq 0` is a ghost entry point that consumes 0 beats and does not render).
   - **Mid-arc clicks** split the host main-track arc and insert a **transparent midspin (`999`)** wrapped by `PositionTrack(opacity = 0, justThisTile = true)` plus a `Floor` decoration (`pta-mc-mask`) preserving the original main-track shape.
   - **Simultaneous / near-simultaneous hits** (`|Δt| < MinClickGapSeconds`, default `0.05s`) compile as **pseudo-doubles** (`1°` sweep + `999` midspin, deducting `1°` from the host tile so subsequent main-track timestamps stay unchanged).
   - **Sub-track visuals** compile into native `AddObject` (`Floor` / `Planet`) decorations with floor icons (`Swirl`, `Rabbit`, `Snail`, `Hold`, `Checkpoint`, etc.) and `MoveDecorations` planet orbits.
   - **Main-track events & repeats**: `floor` and `TileRelativeTo` (`ThisTile` / `Start` / `End`) are remapped via `FloorMap`; `RepeatEvents(Floor)` either adjusts `gapLength` (constant stride) or unrolls copies tagged `pta-mc-unrolled` (yielding to author `SetConditionalEvents` bucket tags on collision).
   - **Beat-grid note**: Under ADOFAI's tile model (`entry[i] = exit[i-1] + π`), each inserted click tile on the main track inherently adds `1` beat (`π`) to downstream main-track timestamps; `PreflightBuilder` reports a 6-item preflight summary in the editor panel (`F8` to toggle).

---

## Repository Structure

```text
pta-multichart/
├── pta-multichart.slnx
├── src/
│   ├── Core/                         # Pure C# (no Unity dependency; shared by mod & net9 tests)
│   │   ├── Model/                    # ChartModel (LogicalChart/Track/ChartEvent), MiniJson
│   │   ├── Timing/                   # TimelineBuilder (r150 beat/time/position math), ApplyPositionTracks
│   │   └── Compile/                  # ChartCompiler, EventMapper, RepeatEventProcessor,
│   │                                 # DecorationBuilder, PreflightBuilder, PlayableChart
│   └── Mod/                          # UMM Mod (net481, references ADOFAI/Unity DLLs)
│       ├── Main.cs                   # UMM entry (PtaMultichart.Main.Load)
│       ├── Info.json                 # UMM manifest
│       ├── Api.cs                    # Static reflection endpoints for automated E2E testing
│       ├── Game/                     # LevelAdapter, EventFactory
│       ├── Session/                  # MultitrackSession (track switching, begin/end playable)
│       └── Editor/                   # Harmony Patches, MultitrackPanel (IMGUI), TrackOverlay, PatchLoadGuard
├── mods/
│   └── pta-bridge/                   # Optional in-process HTTP test bridge mod (127.0.0.1:59731)
├── tests/
│   ├── TimingTests/                  # Headless net9 tests (47 checks)
│   ├── CompileTests/                 # Headless net9 tests (192 checks)
│   ├── LevelGen/                     # Test fixture generator for testlevels/
│   └── shots/                        # Editor verification screenshots
├── testlevels/                       # Regression fixtures (t0..t6, probe_b, template)
├── tools/                            # PowerShell bridge client & E2E verification script (test_all.ps1)
├── scripts/                          # Repo hygiene scripts (check-file-size.mjs)
└── docs/archive/                     # Historical design specs, test reports, and decision logs
```

---

## Requirements

- **ADOFAI**: r150 / 3.4.0 (Unity `6000.3.25f1`) + **UnityModManager** (`0.27.0+`)
- **.NET SDK**: .NET 9.0 SDK+ (builds both `net9.0` headless tests and `net481` mod targets; on Windows with VS/TargetingPack or `Microsoft.NETFramework.ReferenceAssemblies`)
- **Node.js** *(optional)*: for running `scripts/check-file-size.mjs`

---

## Build, Test & Deploy

### 1. Run Headless Unit Tests (No game installation required)

`src/Core` is pure C# with zero Unity dependencies, so contributors can iterate on timing and compiler logic completely offline:

```powershell
dotnet run --project tests/TimingTests/TimingTests.csproj -c Release
dotnet run --project tests/CompileTests/CompileTests.csproj -c Release
```

### 2. Build the Mod

Pass `-p:GameDir="..."` if your Steam installation is not at the default path (`I:\...` or `C:\...`):

```powershell
# Build & auto-deploy to <GameDir>\Mods\pta-multichart\
dotnet build src/Mod/pta-multichart.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice"

# Build only (outputs to out\pta-multichart\ without copying to GameDir)
dotnet build src/Mod/pta-multichart.csproj -c Release -p:Deploy=false
```

### 3. In-Game Usage

1. Open a level in ADOFAI's **Level Editor**.
2. Press **`F8`** (visible by default) to toggle the **PTA Multichart** panel.
3. Select a main-track tile and click **`Create sub track @ selected tile`**.
4. Click any track in the panel list—or click directly on ghost tiles in the viewport—to switch active tracks and edit normally with ADOFAI's native tools.
5. Click **`Recompile preview (log)`** to inspect the 6-item preflight report, or press **Play / Save** as usual.

### 4. Automated E2E Regression (Optional, via `pta-bridge`)

```powershell
# Build & deploy the HTTP test bridge mod (listens on 127.0.0.1:59731)
dotnet build mods/pta-bridge/pta-bridge.csproj -c Release

# With the game running and inside the editor:
pwsh -NoProfile -File tools/test_all.ps1
```

See [mods/pta-bridge/README.md](mods/pta-bridge/README.md) for the full HTTP command protocol.

---

## Contributing

1. **Keep `src/Core/` Unity-free**: Never add `UnityEngine` or `Assembly-CSharp` references into `src/Core/` so `TimingTests` and `CompileTests` remain fast and headless.
2. **Hard Compatibility Constraints**:
   - Do **not** register custom `LevelEventType` values.
   - Do **not** populate `requiredMods` in generated `.adofai` charts or `Requirements` in `Info.json`.
   - Keep generated tags under the `pta-mc*` namespace and preserve author `SetConditionalEvents` tags on collision.
3. **File Size Hygiene**: Check single-file line counts before opening a PR (warn > 400 lines, fail > 800 lines):
   ```bash
   node scripts/check-file-size.mjs --fail
   ```
4. **Before Submitting**: Ensure `TimingTests` (47/47), `CompileTests` (192/192), and `pta-multichart.csproj` build with 0 errors and 0 warnings.
