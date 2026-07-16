# Changelog

## 0.1.34

- Move debug output (annotated images) from the app's own install directory to
  `/share/sourdough_monitor/debug` (requires the add-on's new `map: - share:rw`), so it's
  reachable via Home Assistant's Samba/File Editor add-ons and survives container rebuilds
  instead of living inside the container's ephemeral filesystem.
- Add a daily-rotated `diagnostics-YYYYMMDD.jsonl` sidecar log next to the debug images:
  one line per sample with the detection method, band contrast, and raw pixel positions, so
  an exported debug folder carries the numbers behind each image without needing MQTT debug
  mode captured separately.
- Automatically prune debug images and diagnostics log files older than
  `VisionOptions.DebugRetentionHours` (default 48h), so the export folder stays a bounded
  rolling window instead of accumulating one file per sample forever.

## 0.1.33

- Stop auto-resetting the session on a single collapse-looking reading. A jar reappearing
  after a detection gap (moved out of frame, occlusion, glare while the vision pipeline
  reacquires the surface) would often produce one or two off readings before settling back
  onto the true level, and those alone could trip the collapse-reset threshold and wipe an
  in-progress session. `AnalysisOptions.CollapseConfirmSamples` (default 3) now requires the
  apparent collapse to persist across that many consecutive samples - which a real punch-down
  or deflating starter does, but a transient misdetection doesn't - before the session is
  actually reset.

## 0.1.32

- Add a "session start" reference line and label to the debug image: drawn at the dough
  height recorded when the current session began, re-derived each frame from that frame's
  detected jar bottom (which doesn't move between frames) so it stays correctly placed as
  the dough rises.
- Publish the session start time as `session_start` on the MQTT state topic and as a new
  "Session Started" HA sensor (`device_class: timestamp`), alongside the existing rise/rate/
  peak-ETA sensors.

## 0.1.31

- Fix the new plausibility gate (0.1.30) locking out real dough handling for a long time:
  feeding the starter, punching down before shaping, or a fold that briefly puffs the dough
  up before it settles into a bigger container all move the surface faster than the gate's
  organic-fermentation rate budget, and unlike a misdetected frame, the new height doesn't
  revert on the next sample. Previously the gate would keep rejecting every frame until
  enough elapsed real time inflated the budget past the jump - tens of minutes for a large
  drop. `AnalysisOptions.MaxImplausibleJumpRejects` (default 2) now caps consecutive
  rejections, so a real handling event is unavailable for at most a couple of cycles before
  it's accepted and handed to the existing collapse-reset logic.

## 0.1.30

- Port `RiseAnalyzer`'s missing physical-plausibility gate from the unused `GrowthTracker`
  prototype: a raw reading implying more than `MaxRisePxPerMinute` (default 4px/min, plus a
  `JitterTolerancePx` allowance) of movement since the last accepted sample is now rejected
  outright instead of being smoothed in, so a single misdetected frame (glare, jar-base
  lock-on) can no longer drag the reported rise. `RiseAnalyzer.Analyze` returns `null` for a
  rejected reading, which `Worker` now treats the same as an unavailable measurement.
- Delete `GrowthTracker` and its exclusive supporting types (`GrowthOptions`,
  `GrowthAnalysis`, `GrowthPhase`, `GrowthSample`): a parallel analyzer implementation that
  was never wired into the app (`RiseAnalyzer` is the one actually running). Its useful idea
  (the plausibility gate above) has been folded into `RiseAnalyzer`; the rest duplicated
  functionality `RiseAnalyzer` already has (rolling-median smoothing, sigmoid-based ETA) with
  a cruder fit method.

## 0.1.29

- Lower the default `frigate_sample_interval_minutes` further, from 5 to 1.

## 0.1.28

- Increase data density: lower the default `frigate_sample_interval_minutes` from 10 to 5,
  and widen the allowed range to `float(0.25,60)` so sub-minute polling (down to 15s) is
  possible for anyone who wants denser readings. `Worker` now retries a failed snapshot
  fetch or detection up to `FrigateOptions.SnapshotRetryCount` (default 2) times, 5s apart,
  within the same cycle instead of silently dropping that interval's data point on a single
  flaky camera/network hiccup.

## 0.1.27

- Raise the band-detection acceptance threshold (`MinStepContrast` 15 -> 55): without
  backlighting, the strongest bright/dark step in the jar column is often the jar's own base
  (glass foot, table-contact shadow) rather than the dough surface, and it can still clear a
  low threshold — observed on a real ambient-lit jar reporting the dough top 94% of the way
  down the jar (essentially the jar's base) with a contrast of 50. Frames like that now fall
  back to the edge-energy method instead of confidently reporting the jar's base as the dough
  surface.

## 0.1.26

- Round `band_contrast` to a whole number before publishing; it's a diagnostic viewed at a
  glance in HA and never needed sub-pixel-intensity precision.

## 0.1.25

- Fix the persisted rolling-slope window silently zeroing out on every restart:
  `RiseAnalyzer.SaveState()`/`RestoreState()` stored it as `List<(DateTimeOffset, double)>`,
  but `System.Text.Json` only serializes public properties, not the fields a `ValueTuple`
  exposes, so every entry round-tripped as `{}` and came back as `Time = default, Slope = 0`.
  The peak-detection check reads a flat/falling slope window as "practically peaked", so a
  restart could make the "Starter Peaked" sensor fire early even mid-rise. Replaced the tuple
  with a proper `SlopeSample` record.

## 0.1.24

- Fix dough-surface detection locking onto IR glare/condensation hot spots instead of the
  real dough surface: use per-row median intensity (robust to a narrow bright/dark outlier
  patch) instead of a row mean when building the band-detection intensity profile

## 0.1.23

- Stabilize rise/rate/peak predictions: smooth raw height readings before baseline and fit
  calculations, warm-start the sigmoid fit from the previous cycle's solution, derive the
  practical peak from the fitted plateau instead of a fixed threshold, and replace scattered
  magic numbers with named, documented AnalysisOptions

## 0.1.22

- Refactor SigmoidFitter, update configuration options, and clean up code structure

## 0.1.21

- Refactor growth analysis and remove console rendering

## 0.1.20

- Add debug mode support and related MQTT publishing features

## 0.1.19

- Update run.sh to execute SourdoughMonitor directly

## 0.1.18

- Update Dockerfile and run.sh for improved script execution

## 0.1.17

- Refactor Dockerfile for improved path handling and comments

## 0.1.16

- Refactor Dockerfile for improved build and runtime stages

## 0.1.15

- Fix comment formatting in JarLevelDetector

## 0.1.14

- Add native library copy command to Dockerfile

## 0.1.13

- Add OpenCvSharp native libraries path to run script

## 0.1.12

- Update logging configuration to use console and set level to Information

## 0.1.11

- Add AsciiConsoleRenderer, FrigateSnapshotClient, JarLevelDetector, and Worker classes

## 0.1.10

- Refactor Dockerfile and .dockerignore for improved file handling

## 0.1.9

- Refactor Dockerfile and run.sh for improved build process; add .dockerignore

## 0.1.8

- Refactor MQTT and Frigate options for improved configuration handling

## 0.1.7

- Update version to 0.1.6 and add changelog entry

## 0.1.6

- Add pre-commit hook for automatic version bumping and changelog update

## 0.1.5

-

## 0.1.4

## 0.1.3

## 0.1.2

- Removed `init: true` from addon config to fix `s6-envdir` runtime error

## 0.1.1

- Initial HA add-on release
- Monitors sourdough rise from Frigate camera snapshots
- Publishes growth readings via MQTT with Home Assistant MQTT discovery
- Renders annotated debug images when configured
