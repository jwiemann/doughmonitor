# AGENTS.md

## Project overview

- This repository contains a .NET 8 console app for monitoring sourdough rise from camera snapshots.
- The core flow is:
  1. fetch a snapshot,
  2. detect the jar and dough surface,
  3. track growth over time,
  4. publish readings.

## Working conventions

- Prefer small, targeted changes over broad rewrites.
- Keep detector logic and rendering logic separate.
- Preserve existing behavior unless the task explicitly calls for a change.
- Avoid adding many tests for every small change; add only the minimum regression coverage needed.
- Do not run the full test suite unless a relevant file changed or the user explicitly asks for it.
- When changing detection behavior, add or update tests in the test project only when warranted.

## Build and test

- Build: `dotnet build`
- Test: `dotnet test`
- Focused detector tests: `dotnet test SourdoughMonitor.Tests/SourdoughMonitor.Tests.csproj --filter JarLevelDetectorTests`

## Important areas

- Detection: the vision-LLM detector [ha-addon/Vision/LlmJarDetector.cs](ha-addon/Vision/LlmJarDetector.cs) (prompt, parsing and validation in `LlmAnnotation.cs`, OpenRouter client in `ha-addon/Services`) or the classic `JarLevelDetector`, chosen at runtime by the HA switch "LLM Detection" (`SwitchableDetector`, both implement `IJarDetector`). Jens tunes the classic one while the LLM one is the fallback for critical moments; the notes below describe the classic one.
- Dough domain (Jens, 2026-10-16): daily refeed replaces the dough with 30 g spelt wholemeal flour + 30 ml warm water + 3 g previous sourdough; a bake build is 80 g water + 80 g flour + 8 g sourdough (same 1:10:10 ratio, 2.7x the start volume: baseline height about 103-115 px vs about 270 px). He judges the dough ready when it has doubled (+100 %). Measured: rise is almost linear at 17-24 %/h and stops abruptly; peak rise varied (+125 % after 10 h on 10-14, about +75 % on 10-13, +60 % after 3.4 h on the 10-15 night), so only the doubling time can be predicted hours ahead (`TargetCrossing`), the peak itself 40-70 min ahead.
- Growth analysis lives in [Analysis](Analysis).
- Tests live in [SourdoughMonitor.Tests](SourdoughMonitor.Tests).

## Notes for detector fixes

- The jar bottom should be inferred from the visible lower edge of the jar region when possible, not from the raw wall bounds alone.
- Dough height is measured from the dough floor (the warm body's lower end, measured per frame; the scene's median glass-base offset stands in for faint frames), not from the glass bottom; `LevelMeasurement.DoughFloorPx` is null until a scene's offset is known, and `RiseAnalyzer` keeps one height basis per session.
- When debugging detection issues, inspect both the live preview and any saved debug images in the debug output folder.
