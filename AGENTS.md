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
- Vision detection logic lives in [Vision/JarLevelDetector.cs](Vision/JarLevelDetector.cs).
- Growth analysis lives in [Analysis](Analysis).
- Tests live in [SourdoughMonitor.Tests](SourdoughMonitor.Tests).

## Notes for detector fixes
- The jar bottom should be inferred from the visible lower edge of the jar region when possible, not from the raw wall bounds alone.
- When debugging detection issues, inspect both the live preview and any saved debug images in the debug output folder.
