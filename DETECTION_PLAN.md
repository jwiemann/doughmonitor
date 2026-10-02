# Local detection and calibration plan

## Goal and measurement contract

- Recognize the jar's usable interior and visible base, then measure the dough's front-glass boundary.
- The red curve and reported level use the same contour; lighting must not switch the measurement to a glow, smear, or wall edge.
- Preserve geometry when illumination changes. Reset the growth baseline after a confirmed camera move or resolution change.
- Return no measurement when the boundary is ambiguous; never turn a detection miss into invented growth.
- Predict a practical maximum from meaningful rise and sustained slowdown, not from a flat lag phase or an accelerating segment alone.

## 1. Freeze the complete image corpus

`tools/oracle.py sync` copies the HA snapshot archive and diagnostics locally, verifies SHA-256 hashes, and records a cutoff in `corpus/manifest.json`. Later acquisitions do not change that evaluation run. Raw images and generated reports remain private and ignored by Git.

The gathered HA diagnostics carry UTC timestamps. Replay uses `--timestamps-utc`; other exports can retain the CLI's local-time interpretation. Do not infer camera liveness by comparing a UTC filename to the workstation's local clock.

## 2. Build the expensive offline reference model

- Segment structural camera scenes independently of lighting regimes.
- Estimate geometry from multiple frames, a visible glass foot, and reviewed calibration examples. Short camera-handling transitions remain unmeasured rather than acquiring arbitrary walls.
- Localize the lower-connected content boundary using the jar's own intensity distribution, not global room brightness.
- Optimize the contour across columns with dynamic programming and robust curve fitting.
- Use bidirectional temporal support within the same scene and lighting regime. Preserve raw estimates and uncertainty; never enforce monotonic growth because feeding and collapse are real transitions.

This model is non-causal and allowed to spend more computation locally. Its output is `predictions.jsonl`, **not ground truth**.

## 3. Establish independent review references

Review raw and annotated **full frames** side by side. Record surface intervals, usable column/base geometry, source of the annotation, and separate calibration/validation splits in `corpus/references.json`.

The existing 12:51 surface reference around row 475 is user-confirmed. Other current visual intervals are assistant-reviewed and approximate; do not claim pixel-perfect accuracy from them. Do not train or score against unreviewed predictions, and do not randomly split adjacent near-identical frames into training and validation.

## 4. Tune the live detector in a local loop

1. Run offline parameter candidates against calibration references; inspect validation results separately.
2. Apply justified live changes: light-source/rim geometry, body-connected surface tracing, ambient warm-body geometry, and jar-relative search windows.
3. Replay the actual C# detector over every archived image.
4. Compare independently reviewed accuracy separately from offline/live agreement.
5. Inspect full-frame previews, false positives, missing measurements, geometry errors, and abrupt series changes.
6. Replay at timestamp-based live cadence (`--interval-minutes`), not just every N files; archive cadence changes within this corpus.

Use a fresh output directory for each iteration so before/after evidence is not overwritten.

## 5. Verify size, perspective, and maximum prediction

- Exercise different image resolutions, smaller jars within a fixed frame, and mild projective camera skew using transformed real images and transformed reference coordinates.
- Keep real-photo regressions for the LED hotspot/wall failure, smaller-object detection, visible jar base, empty shelf, and coordinate-system changes.
- Feed the real analyzer controlled growth curves with a known practical peak. Validate early unavailability, stable forecasts after slowdown, plateau confirmation, and no invented forecast during flat lag.
- Backtest archived sessions only when a camera-stable rising segment and an independently observed peak exist. Camera handling and already-settling dough do not provide an actual peak-timing ground truth.

## 6. Acceptance and deployment gate

Required evidence: per-lighting reference errors, missing/false measurements, jar geometry errors, annotated full frames, size/perspective stress results, and causal peak-forecast behavior.

The current archive covers one physical jar and several camera positions. It can validate those conditions and geometric stress cases, but cannot certify every unseen vessel material, shape, content, or light placement. New physical setups require additional full-frame review references and another loop before making an accuracy claim.

Release publication is approved after full-frame review. After installing the update, reset the old session baseline: measurements from the old detection basis are not interchangeable with the new contour basis. Future deployments remain approval-gated.

## Verified local results

Frozen corpus: **1,968 images**, about 30 hours, with checksums and three diagnostic sidecars. Current artifacts are indexed by `corpus/active-artifacts.json`; early prototype `labels.jsonl` files are not the reference dataset.

| Lighting class | Measured / frames | Raw detection coverage |
|---|---:|---:|
| Ambient daylight | 545 / 610 | 89.3% |
| Backlit daylight | 160 / 162 | 98.8% |
| Backlit night | 1,100 / 1,100 | 100% |
| Dim ambient | 20 / 84 | 23.8% |
| Unlit / empty | 0 / 12 | 0% (intentionally unavailable) |

Coverage is **not accuracy**. The 12 independently reviewed references include ten detectable examples and two negative examples: nine detectable examples measured inside their review intervals, one conservative daytime miss, both negatives rejected, and no wrong accepted jar geometry. Most intervals are approximate assistant visual reviews; the established noon surface basis is the prior user-confirmed reference.

- Full replay: 1,825 measurements, 12 dark-frame rejections, 131 missing/ambiguous surfaces, no decode failures. The analyzer withheld 63 measured samples at its plausibility/session gates.
- Clock-bucket replays: 1,795 frames at one-minute cadence and 181 at ten-minute cadence. Fractional-timestamp jitter no longer drops every other sample.
- Integration: **54 tests passed**; **28/28** transformed real-image cases fell inside their transformed review intervals (resolution, smaller object, mild perspective skew).
- Controlled growth: after observed slowdown, ETA errors were −6.16, −0.77, and +2.23 minutes at hours 8, 9, and 10; practical peak confirmed by hour 11. Flat lag never invented a prediction. These are controlled-curve results, not a backtest of actual fermentation timing.
- Full-frame previews were inspected directly and the review gallery/raw-image navigation exercised in Chromium. Color versions of the actual C# contours are in `corpus/preview/verified-*.jpg` and the HA share's preview folder.

Release **0.1.46** contains these verified changes following user approval. Publishing the repository does not install the update on Home Assistant; install/restart it and press **Reset** once. Glare/dim-light misses and unseen physical jars remain explicit limits, not hidden fallbacks.


Supplementary capture: **101 subsequently arriving raw images** were preserved separately in `corpus/new-arrivals/`, bringing local raw preservation to **2,069 images** without altering the frozen calibration cohort. With unchanged parameters/code, the C# detector and offline model both measured **101/101** holdout frames, with no decode failures or analyzer-gated samples. Their mean agreement was 0.50 px (P90 1 px), explicitly not an independently labeled accuracy estimate. The newest raw/annotated full frame was inspected.

Older evidence is also retained separately: **3,495 live-debug files** (annotated images and diagnostics) and **109 preview-history files**. None of these overlays is an inference input. See `corpus/supplementary-evidence.json` and `corpus/new-arrivals/holdout-result.json` for capture cutoffs and provenance.

