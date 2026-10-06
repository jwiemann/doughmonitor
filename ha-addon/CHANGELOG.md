# Changelog

## 0.1.49

- Measure the rise from the dough floor instead of the outer glass bottom. The dough body
  ends above the thick glass base (17 px of a 121 px fresh dough depth on the Weck 742 jar),
  so rises read about 14 % low. The floor is the lower end of the warm body in the jar's
  central strip, measured on every frame; frames with too faint a body use the scene's
  median glass-base offset (persisted with the geometry). `dough_height_px` now counts from
  the floor, and `dough_floor_px` is published with the diagnostics.
- A running session converts onto the floor basis when the floor is first measured (stored
  rises and rates are rescaled, the session is kept). Until a scene has a floor, heights
  run from the glass bottom as before, and a floor-based session rejects glass-bottom heights.
- The replay CSV gains a `dough_floor_px` column.

## 0.1.48

- Publish the starter rise rate only once it is statistically determined: the slope of the
  shortest recent span whose standard error is at most 2 %/h (new options
  `RateNoiseFloorPx`, `MaxRateStdErrPercentPerHour`; `SlopeWindowMinutes` is now the
  maximum look-back, default 120). Jitter after a reset or a step no longer yields
  values such as -100 %/h, and the trend is fitted on the unclamped rise.
- Confirm feeding drops on raw heights, so the pre-reset transition no longer feeds
  stale rises or steep rates into the trend; the new session starts at the confirmed level.
- A persistent rise after a gap (overnight growth first seen at dawn) stays in the session.

## 0.1.47

- Preserve feeding history through darkness, dawn reacquisition and snapshot resizing;
  persist the measurement coordinate basis and require verified geometry before reset.
- Reject the empty wooden stand instead of reporting it as dough.
- Clear a stale peaked flag after confirmed renewed growth, retain the session, and
  require a new observed plateau before declaring another peak.

## 0.1.46

- Detect the backlit jar/base and actual dough front instead of LED fades or wall
  reflections; keep supported geometry across lighting changes and resizes.
- Stabilize pale-dough detection and smaller-jar search windows; skip ambiguous glare.
- Predict the practical maximum only after observed slowdown, without inventing a
  future peak after the plateau.
- Add the local calibration/review loop, UTC and clock-based replay, and regressions.
- After updating, press **Reset** once to establish the new measurement baseline.

## 0.1.45

- Warm threshold calibrated (step 9): the surface line lands on the user-confirmed dough
  boundary instead of ~50 px inside the dough. Dead warm-run code removed.

## 0.1.44

- Surface = the warm (color) boundary: the pale smear band above the dough is static
  residue on the glass; the dark boundary tracks it and froze the level while the dough
  rose past it. Dark band stays as the fallback for pale feeds; warm-only frames are
  dropped (no consistency anchor available).

## 0.1.43

- Surface line sits at the saturated dough boundary (60% of the warm-coverage plateau)
  instead of the darker wet/shadow layer above it — the dark layer is not dough-colored.

## 0.1.42

- Surface line no longer jumps to the condensation line above the dough: the edge
  fallback must sit on actual dough (dark-coverage floor), and the warm method is only
  trusted when its top is not deeper than the dark band top (dark-but-not-warm dough
  layers made it read mid-dough).
- Camera load reduced: retries only for failed fetches (default 1), detection misses are
  re-sampled next cycle instead of re-fetching.

## 0.1.41

- Dough-highlight overlay off by default, new `debug_highlight_dough` add-on option.
- Jar column warm-first: the color signal defines the jar interior; the dark signal only
  fills in while the dough is too pale for the color filter.
- Front-edge refinement via the dark coverage for both methods.

## 0.1.40

- The dough-highlight overlay in the debug image is now OFF by default and controlled by
  the new add-on option `debug_highlight_dough` (default off): the translucent orange
  dough mask made it hard to tell shadows from actual dough in the raw scene.
- Jar column warm-first: the color signal defines the jar interior whenever a warm run
  wide enough for the jar exists — the column's right edge no longer bridges onto the
  shadowed wall/rack (observed drifting to the door frame at 1203 while the dough ends at
  ~800). The dark signal only fills in while the dough is too pale for the color filter
  (fresh feed).

## 0.1.40

- Debug image now shows the dough filters: orange overlay = where the color filter (warm
  tone) sees dough, light blue = where only the darkness filter does. The debug output was
  a bare grayscale frame before, making every color-based decision invisible.
- The warm method must now prove a warm BODY (mean warm coverage below the band top ≥
  `FrontEdgeCoverageFraction`): a thin warm glare line over a pale slurry no longer wins
  over the stable band method — this was flipping the surface by 30-90 px on the real
  post-feeding frames.
- Shared top-quarter neutral-reference helper for the column extent and the debug masks.

## 0.1.39

- Front-edge surface rule: through the cylindrical glass the dough's BACK edge appears
  higher than the front — the row-median crossed there and put the surface line on the
  jar's back side (observed after a re-feed: red line at 445 vs. true front level ~575).
  The surface is now the first row where ≥ `FrontEdgeCoverageFraction` (0.85) of the
  jar-interior width is dough (warm or dark), sustained across the dough body; the jar
  interior bounds come from the dough mask's own widest row so wall/shadow columns don't
  cap the fraction.
- Jar column works for a pale fresh-fed slurry too: the extent now uses warm OR dark
  dough pixels over the dough's own row band (adaptive, instead of a fixed frame-height
  zone that dilutes the fraction when the dough sits low), and frame-border columns are
  hard-excluded from the extent.
- Analyzer: a re-feed that drops the dough level far below the session baseline now resets
  the session (the absolute height comparison catches what the 0%-clamped rise percent
  hid); the "Peaked" flag requires the minimum observed rise on both the fitted-plateau
  and the flat path (a degenerate fit on flat lag-phase data declared "peaked" at 6%);
  degenerate fits (k < 0.05/h) are rejected and peak ETA hours are bounded, fixing an
  ETA-timestamp overflow on flat series.

## 0.1.38

- Auto-reset the rise session when the camera moves: the detector now persists the
  established jar column (`jar_geometry.json`), seeds it on restart, and raises
  `SceneChanged` when the column provably moved (3 consecutive off-aggregate extents, or a
  resolution change). `Worker` resets the analyzer session on it — the rise baseline is a
  pixel height in the old geometry and is meaningless after a camera move, so no manual
  reset button press is needed anymore.
- Fix a crash when the camera resolution changes while extents from the old size are
  still in the stabilization window: bounds are now clamped into the current frame and a
  resolution change clears the window immediately.
- Replay mirrors the same auto-reset and reports scene changes as warnings.

## 0.1.37

- Fix the jar column falling back to full-frame bounds (drawn at the door-frame edges on
  the real camera): the column is now derived from the dough's warm horizontal extent —
  the longest contiguous warm-column run, separated from warm-looking background structures
  (a cream-colored rack beside the jar) by the neutral wall gap
- Stabilize the drawn column lines: the per-frame extent flickers with lighting (dough
  edges wash out against the wall), so the bounds are the densest-cluster mode over the
  last ~1 h of extents; a camera/scene change (both edges off the aggregate for 3
  consecutive frames, or a failed measurement) resets the window immediately instead of
  blending two scenes for an hour
- Replay-verified against the live archive: per-scene line spread collapses to 0 px, the
  surface stays correct across a real camera bump, and the analyzer's plausibility gate
  blocks the scene-jump readings from the rise series as designed

## 0.1.36

- Add warm-tone surface detection as the primary method: the dough is tan (large red/blue
  channel spread) while glass, wall and background are neutral (sat 2-5 vs dough 25-32,
  measured on real frames). Stable under ambient light even for a fresh-fed light dough
  that shows almost no brightness step at all. Backlit nights keep the dark-band method,
  diffusely lit boxes the edge-energy fallback.
- Fix wall detection latching onto frame-border artifacts (door frames at x≈64/1280 and
  x≈1216/1280 on the real camera): vertical lines within 6% of the frame border are now
  rejected, letting the full-frame fallback column take over.
- Rework the dark-band surface rule after measuring the real morning scene: the dough
  fades only ~16 gray levels below the wall over tens of rows — no global above/below
  step and no Otsu split isolates it. The band is now found as a persistent drop below
  (bright reference − MinAmbientBandContrast), requiring genuine darkness
  (DarkBandMaxIntensity) and length (MinDarkBandFraction), or the massive backlit step.
  This also keeps rejecting the jar-base shadow (observed ~150 gray, brief) that
  motivated the 0.1.27 threshold raise.
- Replay-verified on the first real collection hour: 186/186 frames detected via the
  warm method, surface σ = 3 px, zero fake rise during the lag phase (previous state:
  4/145 detected with a 55% phantom rise).

## 0.1.35

- Add dark-frame gate: frames with no sufficiently bright region (P90 below
  `Vision:MinFrameIntensity`, default 25) or no intensity spread (P90−P10 below
  `Vision:MinFrameContrast`, default 30) are rejected as `dark_frame` instead of feeding
  night noise into the series; a backlit jar in a dark room still passes
- Add timestamped raw snapshot archiving (`frigate_snapshot_archive_directory` /
  `Monitor:Frigate:SnapshotArchiveDirectory`): every fetched snapshot is kept as
  `yyyyMMdd_HHmmssfff.jpg` for offline analysis
- Add a `replay` CLI (`dotnet run -- replay <folder>`): batch-analyze an exported snapshot
  week through the live detector and analyzer into per-frame `readings.csv`, `summary.json`
  and a self-contained `report.html` with the rise curve, outcome timeline and annotated
  frames
- Record frame lighting statistics (mean/median/P10/P90) in the detection diagnostics, the
  debug MQTT payload and the replay CSV
- Stabilize jar geometry: the wall pair's outer (union) extents define the column and the
  bottom edge only refines it within a band above the wall bound, so heights no longer
  wobble with lighting-induced Hough segment changes; removed the unconstrained
  `FindJarBottomFromEnergy` pick that could collapse the dough height onto its own surface
  edge
- Constrain the sigmoid peak prediction physically: the fitted plateau must exceed
  everything observed and the predicted peak must lie in the future, so the ETA leads the
  rise instead of reporting "peak just happened"
- Fix debug-image filename collisions (mtime-derived timestamps) and let the archived
  snapshot directory coexist with `latest_snapshot.jpg` without clobbering either

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
