# Changelog

## 0.4.3

- Rise rate: fitted over at least 60 minutes (new option `RateMinSpanMinutes`, default 60) instead of the
  shortest span that is statistically sufficient (about 30-40 minutes). One pixel step in 5 minutes read as a
  surge or stall; 60 minutes follow the real rate and read about 0 only when the dough has stopped rising.
- The log now says why a rise session started ("New rise session started: manual reset / session older than
  36 h / height fell ... / dough floor offset implausible / no active session after a restart").
- Classic detector: the `dark_mass` debug image draws the session-start line, so a session restart is visible
  in the diagnostics sidecar (`session_start_surface_px` was empty on these frames).

## 0.4.2

- Classic detector: the `dark_mass` method now also feeds the dough-floor offset to the daylight path, so the
  rise stays on the dough-floor basis when the camera switches from IR night to colour in the morning. Before,
  the first daylight frames carried no floor and the rise analyzer reported them as unavailable.

## 0.4.1

- Dashboard: target ETA tile and LLM detection switch; fix entity ids in docs
- Classic detector: IR night scene with a lamp behind the jar (dough = dark mass between the lamp glow and the
  saturated glass foot) is now measured (`dark_mass` method, dough floor held as a 12-frame median). Before, the
  glow path took the foot for the light source, lost the surface and the stale continuity fallback latched onto
  the glass base (rise stuck at 0 %). Light-source candidates are now tried largest first, and a failed glow
  path no longer ends the cycle but falls through to the dark-extent search.
- Replay: `--classic` replays the image-pattern detector (no LLM calls).

## 0.4.0

- New sensor "Target Rise ETA" (`sensor.sourdough_monitor_target_rise_eta`, with Earliest/Latest): when the rise
  is expected to reach `target_rise_percent` (default 100 %, "doubled", the rule the dough is judged
  by; add-on option). It carries the trailing hour's linear trend forward and holds only while the
  dough clearly rises (>= 3 %/h and 2 standard errors) and is still short of the target; a peak
  forecast ending below the target cancels it and one ending sooner caps it. On the bake build of
  2026-10-15 (used at +100 % after 7.3 h) it announced the doubling 2.5 h ahead with a median error
  of 15 min (35 min worst case in the last 2.5 h), 3.5 h ahead off by 47 min. Replay output is
  unchanged.
- Why: the peak itself stops abruptly and so can only be forecast 40-70 min ahead, but the rise to
  +100 % is linear and predictable hours ahead. The 2026-10-15 session had no ETA of any kind.

## 0.3.1

- Fix: on 2026-10-15/16 the peak ETA, expected height and range stayed empty although the dough
  peaked at 23:30 UTC (60 % rise). Cause: 0.2.4's forecast minimum of 70 % rise (a prior taken
  from two sessions peaking at 125 %); the evening re-feed peaked at only 60 %. The minimum
  (`Analysis:MinRisePercentForForecast`) is now 30 %: it still blocks the early stall of 2026-10-14
  (19 %), nothing above. A stall at 50 % can again produce a short-lived false ETA.
- The ETA no longer disappears when the rise has stopped: once a published forecast sees the
  rate gone (95 % of the fit, range within 1 h) it keeps stating the fitted peak time (now in the
  past) and rise, until the rate-based "peaked" flag follows (1.5 h after the stop on 2026-10-15,
  with an empty sensor in between). It only continues a running forecast, so a flat or creeping
  stretch cannot start it (2.6 %/h for 2.5 h on 2026-10-14 would have).
- The plausibility gate dropped 10 genuine readings during the fast rise of 2026-10-15 (steps of
  5-17 px, 9 px tolerance); `Analysis:OutlierTolerancePx` is 12 now. The two misdetections of
  2026-10-14 (20 px off) are still rejected.

## 0.3.0

- New Home Assistant switch "LLM Detection" (`switch.sourdough_monitor_llm_detection`, MQTT
  discovery, retained state): ON uses the vision LLM, OFF the classic image-pattern detector, at
  runtime and without a restart. The choice persists in `/data/detection_mode.txt`; with no file
  the LLM is used. This re-wires `JarLevelDetector` (new `IJarDetector`, `SwitchableDetector`).
- Both detectors feed one rise session on the glass-bottom basis: the classic detector's dough
  floor is dropped in the live path, and after a switch the new detector's jar bottom is replaced
  by the bottom the session was measured with (cleared by a camera move). Replay is unchanged
  (LLM only).
- The `llm_model` sensor reads `classic` while the classic detector is active.

## 0.2.5

- Fix: with a configured `llm_model` of `google/gemini-3.8-flash` (or any model whose reasoning is
  mandatory) 0.2.4 failed every cycle with `llm_error` (HTTP 400 "Reasoning is mandatory"), so no
  session start and no debug markup appeared. The client now answers that 400 by retrying once with
  reasoning `minimal` and remembers it per model; no option change is needed.

## 0.2.4

- LLM cost: reasoning is switched off and the default model is now `google/gemini-3-flash-preview`.
  `google/gemini-3.8-flash` cannot run without reasoning (OpenRouter answers HTTP 400 "Reasoning is
  mandatory"), and with `minimal` about 20 % of its draws spent 400-2000 hidden thinking tokens
  (19 % of the 2026-10-14 spend) although the visible answer is always about 250 characters.
- New add-on option `llm_reasoning_effort` (default `off`; `minimal`/`low`/... or empty for models
  that require reasoning, e.g. `minimal` with `google/gemini-3.8-flash`).
- Measured on the 10 labelled frames (3 samples): `gemini-3-flash-preview` without reasoning
  median surface error 5 px (3.8-flash: 4 px), jar bottom 6 px (5 px), one frame above 30 px (two);
  $0.0039 vs $0.0066 per 3-sample cycle. 2026-10-14 session replay (245 cycles, same pipeline):
  $1.00 vs $1.65, level difference median 1.2 px. `qwen3-vl-235b-instruct` ($0.0008 per cycle)
  has 26 px median error, `gemini-2.5-flash` 74 px: both unusable.
- Forecast needs a minimum rise (`Analysis:MinRisePercentForForecast`, default 70 %): a daily-fed
  jar rises 100-130 % before it peaks, while the 2026-10-14 session had mid-rise stalls (19 % for
  45 minutes, 53 % for 20 minutes) that the forecaster took for the turn, publishing ETAs 3-6 h
  too early on the 3.8-flash readings (6 cycles). "Peaked" is unaffected.
- New outcome `geometry_warmup`: no level is reported until the jar geometry window holds
  `Llm:MinGeometryFrames` (3) outlines. The first outline put the jar bottom at 677 px (real
  709 px) and so the session start height at 91 px instead of about 115-123 px, inflating every
  rise percentage (observed on a replay started mid-session; also applies after a restart).
- The session baseline is the median of the first 20 minutes of readings
  (`Analysis:BaselineSettleMinutes`) instead of the first reading alone, and the stored rise and
  rates are rescaled as it settles. One reading was 12 px low on 2026-10-14 (586 against 598 px),
  which showed the same dough as 105 % instead of 126 % rise. After the change the 14 Oct replays
  of 3.8-flash and 3-flash (3 and 1 samples) report a maximum of 123.5 / 126.2 / 129.2 %.

## 0.2.3

- Peak forecast (`peak_eta`, `predicted_peak_percent`, `peak_eta_low`/`peak_eta_high`) rebuilt;
  in the 2026-10-14 session none of the four was ever published. Causes: the forecaster needed
  windows of 1-minute rate samples, but the LLM detector delivers one reading per 5 min; the
  point ETA was dropped whenever its windows disagreed by more than 60 min (almost always); and a
  forecast had to survive a fixed "rate has fallen by 15 % of its maximum" gate every single cycle.
- New `PeakForecaster`: the rate ramps up, holds and falls to zero within 1.5-2 h (measured
  2026-10-13 and 10-14). That shape (rate maximum time, decline duration, three decline curvatures)
  is fitted on a grid to 10-minute medians of the unsmoothed rise of the last 4 h, independent of
  the sampling interval. The fit errors weight the grid; its weighted percentiles are the
  earliest/latest times and heights, its best fits the point ETA and height. No forecast is made
  until the data show the rate maximum already lies in the past (the steady 20-24 %/h phase of
  the 10-14 session correctly yields nothing), nor once the rise has stopped.
- A running forecast survives a cycle with a noisier fit (looser gates), the published values are
  the median of the last 15 min of forecasts and are kept up to 10 min through a missing one.
- On the two measured sessions the forecast appears 1 h before the rise stops (10-14: 22:30 UTC,
  forecast 22:28-22:59 with 121-126 % against 120-125 % reached); the range always held the stop.
  Noisy variants (3 px scatter, 0.75x-1.3x speed): median error 0 min, 90 % within 26 min, once a
  forecast exists; it exists in about 40 % of the last 90 minutes (the turn is statistically
  invisible earlier).
- Removed `RateTrendForecaster` and `MaxEtaSpreadMinutes`. `Sample` now stores the unsmoothed rise.
- False readings are now cleaned out with what is known about fed dough: it follows a smooth
  curve, rises at most ~26 %/h (measured) and falls at ~8 %/h. The plausibility gate compares each
  raw reading with the robust trend of the last 45 min (limits `MaxRisePercentPerHour` 40,
  `MaxFallPercentPerHour` 15, replacing the classic detector's `MaxRisePxPerMinute` of 4 px/min,
  i.e. 200 %/h) and rejects it when it is off by more than `OutlierTolerancePx` (9 px) or three
  times the recent scatter. A level that persists is still accepted after
  `MaxImplausibleJumpRejects` frames (handling). On the 10-14 session it rejects the two
  misdetections of 433 and 439 px at 23:57/00:22 UTC (20 px above the plateau, both with
  23-26 px disagreement between the model's three draws) and 5 smaller ones; the forecast timing
  is unchanged. Dropping cycles by sample disagreement instead was tested and made the forecast
  later (it removes good readings during the turn), so it is not used.

## 0.2.2

- Debug image reduced to three lines: the glass outline (green), the measured level (red) and the
  session's starting level (blue). Level and start line are clipped to the inside of the glass;
  the orange surface curve, the vertex dots and the crop frame are gone.

## 0.2.1

- New debug sensors `LLM Sample Spread` (px) and `LLM Samples Usable` (need `debug_enabled`).
- The debug image no longer has the outcome and model text burned in; the outcome stays in the
  log and the diagnostics JSONL.
- Compact Home Assistant view in `dashboard/brot.yaml`.

## 0.2.0

- Dough level from a vision LLM (OpenRouter) instead of the classic detector, which failed in
  low light by confusing the front and back of the jar. `LlmJarDetector` gates dark frames
  (P90 below `Vision:MinFrameIntensity`; contrast is no longer gated), crops to the ROI,
  downscales to 640 px and asks the model for the whole jar outline and the front dough surface as
  points on a 0-1000 grid. The level is the mean of the surface curve over the central 60 % of
  the jar width; jar top and bottom are the median of the last 15 outlines. A camera move is
  confirmed when the newest 5 outlines shift against the older median on two edges (or the bottom
  by 16 %), and resets the rise session. `JarLevelDetector` is disabled (not wired), not deleted.
  - The height basis is the glass bottom (no dough floor), so a rise session started with the
    classic detector on the dough-floor basis stays "unavailable" until it is reset.
  - New options `llm_api_key`, `llm_model` (default `google/gemini-3.8-flash`),
    `llm_max_calls_per_day` (hard cap, 4500), `llm_samples_per_cycle` (3) and `Monitor:Llm:*`
    (image size, JPEG quality, confidence floor, geometry window, sample temperature).
  - Home Assistant sensors for the LLM: cost today (UTC day) and in total, calls today and in
    total, and the model in use. Totals persist in `/data/llm_usage.json`; cached replay answers
    are never counted. The model can be switched in the add-on options (`llm_model`).
  - Median of 3 samples per cycle: the single-draw level wandered by about 40 px between frames
    on the overnight LED sequence. The sample spread is logged per cycle with running statistics
    and written to the diagnostics JSONL so the extra cost can be dropped when it stops mattering.
  - `replay` uses the LLM detector with an on-disk response cache (`--llm-cache`), `--model`,
    `--samples` and `--max-calls`; `tools/llm_benchmark.py` scores models on the labelled frames
    in `tools/llm_bench`.
  - Benchmark (10 labelled low-light/daylight frames, one sample): `google/gemini-3.8-flash`
    median surface error 4-7 px, jar bottom 6 px, no false positive, about $0.0024 per call;
    `google/gemini-3.1-flash-lite` 12 px at $0.0007; `google/gemini-3.5-flash-lite` similar at
    $0.0012; `google/gemini-2.5-flash-lite` 45 px with 5 of 10 unusable;
    `qwen/qwen3-vl-30b-a3b-instruct` poor jar bottom (median 58 px).
  - Overnight 2026-10-13 00:00-02:50 UTC with 3 samples: 18 of 18 cycles measured, level falls
    smoothly 511 to 463 px, sample spread mean 4.8 px, max 16.2 px, 1 of 18 cycles above 10 px.

## 0.1.52

- Jar detection from the glass walls and a consistent glass bottom, checked by replaying
  the whole archive (17,449 frames, Oct 1-13): detected frames 8,446 to 10,289, "no jar"
  1,310 to 52. A warm lit wall beside the jar was counted as dough and held the jar column at
  x 352..1203 for days (Oct 8: 65 % of detected frames had an absurd column; Oct 9-10 detected
  27 % / 4 %); black dough at night (LED jar) reported "no jar" for hours.
  - `JarWallTracker` fits the two glass walls as angled lines (about 0.12 px per row, the
    jar is about 60 px wider at the top than at the dough) from the persistent vertical edges
    of the last 24 frames. Only clearly lit models (`JarWallMinScore`, 0.75) are trusted and
    kept across weak daylight windows; flat daylight fits sit on reflections or the shelf.
  - The detector clamps a column that reaches past the walls, replaces a stale held column
    when this frame's own extent agrees with the walls, measures inside the walls when no
    dough extent exists, and drops a model its own columns contradict for
    `JarWallDistrustFrames` frames. The angle is applied at the row of the last surface.
  - `JarBottomEstimator` reads the outer glass bottom the same way in day and night; the
    detector reports the median of the last 45 frames for every method (night/continuity
    frames used to report 671 against 702 by day, which shifts the dough floor offset).
  - `replay --no-debug-images` (3x faster) and `tools/replay_metrics.py` for per-day
    before/after tables.

- Peak forecast from the rate trend instead of a sigmoid fit. Overnight data (2026-10-13,
  796 gap-free frames) showed that the rise does not saturate asymptotically: the rate
  ramps up for hours, peaks, and falls roughly linearly to zero within ~1.5 h. Richards,
  Gompertz and logistic fits all put the plateau 3–15 h too late (ETA 13:13 for a peak at
  02:35 UTC) and only published after the peak. `RateTrendForecaster` now extrapolates the
  falling rate to zero (robust Theil–Sen line over 75/90/120 min of rate samples): the
  zero crossing is the peak time, the area under the rate line the peak height. The
  range is the spread over the windows; the point ETA is published only while that spread
  is at most `MaxEtaSpreadMinutes` (now 60). Rate samples carry the centre of their fit span
  (a trailing fit describes the rate half a span ago), removing a ~30 min lag. "Peaked"
  is now purely rate-based (flat or falling for `PeakConfirmWindows`). Removed
  `SigmoidFitter`, `PeakFraction`, `MaxEtaRelativeStdError`, `EtaStabilityWindowMinutes`.

## 0.1.51

- Re-acquire the dough surface after a collapse or a long loss (#8): the continuity
  fallback used to give up for good once the surface was farther from the last good row
  than the rate-limited band allowed or the anchor was older than
  `SurfaceContinuityMinutes` — after a collapse every following frame read "no surface".
  The fallback now also runs a wide pass over the whole jar column (upward movement stays
  rate-limited, downward jumps are unbounded; a stale anchor may search anywhere) and
  accepts its surface only after `SurfaceReacquireFrames` agreeing frames (reported as
  method "reacquire", so a static artifact cannot latch). The wide pass runs on both the
  backlit and the ambient warm path; `SurfaceContinuityMinutes = 0` still disables the
  fallback entirely.
- Flag a peaked session whose surface stays lost (#9): `no_surface` for
  `SurfaceLossAfterPeakMinutes` (default 120) consecutive minutes after a confirmed peak
  logs one warning per outage and reports `surface_lost_after_peak_minutes` in the
  debug-mode MQTT diagnostics payload. Dark nights, missing jars and fresh measurements
  reset the streak — only an unbroken run of surface misses counts as a loss.
- Fit the rise with an asymmetric Richards curve (#10):
  `h(t) = L·(1 + ν·exp(-k(t-t0)))^(-1/ν)` with the shape ν fitted in [0.1, 5] (ν = 1 is
  the previous symmetric logistic, ν → 0 the Gompertz curve). The symmetric model biased
  the peak ETA by phase on asymmetric rises (measured −1.9 h on a truncated asymmetric
  synthetic rise); the practical-peak ETA and its uncertainty window now follow the
  measured asymmetry.

## 0.1.50

- Publish a peak time only once it is actually determined, and never as a bare point:
  `peak_eta` appears only after the rise rate has measurably turned (two rate-error
  margins below the session maximum) and the estimate has stopped moving (spread at most
  `MaxEtaSpreadMinutes` over `EtaStabilityWindowMinutes` of fits), while the new
  `peak_eta_low`/`peak_eta_high` timestamp sensors always show the current uncertainty
  window. The sigmoid is refitted from several seeds every cycle instead of warm-starting
  from the previous fit, so the prediction no longer depends on which fit preceded it.
- A peak or forecast now requires evidence of a real rise and a measured turn: the
  session's maximum rate must reach `MinSlopeEvidenceSigmas` (default 2) times the rate's
  error bound, the current rate must sit measurably below it (the margin grows with steep
  sessions — a wobbling 17 %/h dip under a 23.5 %/h spurt is not a turn), and the decline
  must hold across the confirming windows. Flat lag-phase data, a degenerate low-plateau
  fit and rate wobbles can no longer flag "Peaked" mid-rise (all three observed live on
  2026-10-06). With that protection in place, `MinRisePercentForPeak` drops from 25 % to
  5 % so small kept-starter sessions (a real +5.8 % rise on 2026-10-06) are no longer
  excluded from prediction and their peak is declared.
- Never show a stale prediction: after `mqtt.stale_after_minutes` (default 10) without a
  usable measurement the state payload clears the measured values and sets `data_stale=ON`
  (new "Data Stale" problem binary sensor, plus a "Last Reading" timestamp sensor). A
  retained peak ETA can no longer masquerade as current through a detection gap.
- Keep measuring when the LED glow sits directly above the dough surface. From ~17:30 on
  2026-10-06 the dough rose past the light source, the glow-based jar column pinched to
  half the jar width and every frame read "no surface" for 105 minutes — exactly the
  decisive final rise. The jar column must now plausibly match the established width
  (`ColumnWidthToleranceFraction`), and when the surface search fails a continuity search
  around the last known surface (`SurfaceContinuityMinutes`,
  `SurfaceContinuityMaxPxPerMinute`, reported as method "continuity") keeps the series
  going. A sustained width disagreement (`ColumnWidthReliefFrames`) hands the candidate to
  the normal move validation, so a real jar or camera change still resets the session.
- Publish a summary when a session ends (`session_summary` topic, "Last Session Ended"
  sensor with JSON attributes): session start/end, maximum rise and when it was reached,
  maximum rate, and the prediction error (predicted peak minus the measured maximum).
- Fix replay timestamps: archive file names are UTC but were parsed as local time, shifting
  every replay output by the machine's UTC offset. They are parsed as UTC now, reports
  render in local time and say so, and the replay CSV gains
  `predicted_peak_time_low`/`predicted_peak_time_high`.

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
