# 🥖 Sourdough Monitor

[![Open your Home Assistant instance and open a repository inside the Home Assistant Community Store.](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https://github.com/jwiemann/doughmonitor)

Monitor sourdough starter rise from Frigate camera snapshots, publish readings to Home Assistant via MQTT discovery.

## How it works

1. Periodically fetches a camera snapshot from [Frigate](https://frigate.video/) (or any URL that returns a JPEG).
2. Uses computer vision to detect the jar and dough surface inside a configurable region of interest.
3. Tracks growth over time, computes rise %, rise rate, and predicts peak ETA using sigmoid curve fitting.
4. Publishes sensors to Home Assistant via MQTT discovery (auto-creates sensors, binary sensor, and a reset button).
5. Shows a live ASCII preview in the console.

## Sensors created

| Sensor | Type | Description |
|---|---|---|
| `sourdough_monitor_rise_percent` | `sensor` | Current rise % from baseline |
| `sourdough_monitor_rise_rate` | `sensor` | Rise rate in % per hour |
| `sourdough_monitor_predicted_peak_percent` | `sensor` | Predicted rise % at peak |
| `sourdough_monitor_peak_eta` | `sensor` (timestamp) | Estimated time of peak |
| `sourdough_monitor_peaked` | `binary_sensor` | ON when rise has peaked |
| `sourdough_monitor_reset` | `button` | Button to reset the current session |

## Installation (HA add-on)

1. Add this repository to your Home Assistant add-on store:
   - **Settings → Add-ons → Add-on store → ⋮ → Repositories**
   - Paste `https://github.com/jwiemann/doughmonitor`
   - Or click the badge above.

2. Install the **Sourdough Monitor** add-on.

3. Configure the following options:

| Option | Description |
|---|---|
| `frigate_base_url` | Your Home Assistant/Frigate base URL |
| `frigate_camera` | Camera entity name in Frigate |
| `frigate_access_token` | Long-lived access token (create in HA profile) |
| `frigate_sample_interval_minutes` | Interval between snapshots (0.25–60 min, default 1) |
| `mqtt_host` | MQTT broker hostname |
| `mqtt_port` | MQTT broker port |
| `mqtt_username` | MQTT username |
| `mqtt_password` | MQTT password |
| `mqtt_device_id` | MQTT device ID prefix (default: `sourdough_monitor`) |

## Docker (standalone)

```bash
docker build -t sourdough-monitor .
docker run -d \
  --restart unless-stopped \
  -v /path/to/config:/app/config \
  sourdough-monitor
```

Configure via `appsettings.json` or environment variables (e.g. `Monitor__Frigate__BaseUrl`).

## Snapshot archiving & offline replay (debugging)

To tune the detector on real footage, point the add-on at an archive directory — every
fetched snapshot is then written there timestamped (`yyyyMMdd_HHmmssfff.jpg`), giving you a
complete week of raw frames to replay:

| Option | Description |
|---|---|
| `frigate_snapshot_archive_directory` | Directory for timestamped raw snapshots (empty = off) |

Set it in the add-on options or via `Monitor:Frigate:SnapshotArchiveDirectory` in
`appsettings.json`. Copy the folder off the box after a bake and run the replay CLI:

```bash
cd ha-addon
dotnet run -- replay <absolute-image-folder> --out <new-output-folder> [--config appsettings.json] [--roi x,y,w,h] [--stride N | --interval-minutes N] [--timestamps-utc]
```

The replay runs every frame through the live detector (same code path as the add-on) and
writes to the output directory:

- `readings.csv` — one row per frame: outcome (`detected` / `dark_frame` / `no_jar` /
  `no_surface` / `decode_failed`), method (`warm` / `backlit` / `band` / `edge`), lighting
  statistics, jar bounds and geometry kind, dough top/base/height, analyzer gate, rise,
  rate, and predicted practical maximum
- `summary.json` — aggregate counts, method distribution, final growth/reading state
- `report.html` — rise curve with day markers, outcome timeline (dark frames marked), anomaly
  table and every annotated debug frame inline

Frames are timestamped from their file names (addon archive, Frigate/HA exports and typical
camera names like `IMG_2026-09-28_12-00-00.jpg` all parse); files without a parseable name
fall back to modification time and are flagged in the CSV.

Use `--timestamps-utc` for the gathered HA archive (its diagnostics carry `+00:00`).
The default interprets filename clocks in the replay host's local timezone. Use
`--interval-minutes 10` to emulate a ten-minute sampling cycle even when archive
cadence varies; `--stride` samples file indices instead. Replay disables persistent
geometry/session state and leaves the translucent dough overlay off.

## Session resets and lighting

A confirmed feeding-related height drop starts a new baseline. Missing snapshots,
an absent jar, darkness and ambiguous dawn/glare frames leave the feeding session
intact; a wooden stand is not accepted as dough merely because it is warm-coloured.

Uniform snapshot resizes are processed in the established image coordinate system,
shared by measurements and debug overlays. Reference dimensions and the supported
jar base are persisted with geometry, so a startup thumbnail does not redefine an
existing session. Actual aspect changes invalidate the coordinate system; other
geometry changes require consistent, valid jar/base/surface evidence before reset.

The published rise rate (%/h) is the slope of the shortest recent span whose standard
error is at most `MaxRateStdErrPercentPerHour` (default 2 %/h), looking back at most
`SlopeWindowMinutes`. The error follows from the sampling (longer or denser spans pin the
slope better) and the noise of a height reading (`RateNoiseFloorPx`, or the scatter around
the fit if larger), so it needs more time on a short jar or a slow cadence than on a tall
jar or a fast one. After a feeding reset, from a few jittery frames, or across a step in
the data the error is large and no rate is published, instead of a clipped value such as
-100 %/h. The trend uses the true rise, not its display clamp at 0 %.


## Expensive local calibration loop

The implementation plan and acceptance gates are in [DETECTION_PLAN.md](DETECTION_PLAN.md).
The offline model uses scene-wide geometry, dynamic-programming contour optimization,
robust curve fitting, and bidirectional temporal support. It is deliberately non-causal;
the add-on remains a causal, lighter C# detector.

From the repository root:

```bash
python -m pip install -r tools/requirements.txt
python tools/oracle.py sync
python tools/oracle.py segment
python tools/oracle.py geometry
python tools/oracle.py tune
dotnet build ha-addon
python tools/oracle.py run --parameters corpus/tuned_parameters.json --every-frame --out tmp-calibration-new
```

`sync` freezes raw images and diagnostics in private `corpus/`, with SHA-256 hashes and
a timestamped manifest. `--source` and `--corpus` select different archives. On a new
corpus, review full frames and create `references.json` before `tune`/`compare`; `infer`
and `preview` can propose contours without reviewed accuracy references. Reference
entries record `file`, `split` (`calibration` or `validation`), `regime`, annotation
`source`, a `surface_interval: [low, high]`, and optional `jar` bounds/tolerance. Empty
or unlit frames use `detectable: false`. Only calibration entries seed geometry/tuning.

Outputs:

- `corpus/predictions.jsonl`: offline predictions, raw levels, uncertainty, and support.
  **Not ground truth**; independently reviewed intervals are the accuracy reference.
- `corpus/preview/index.html`: full-frame annotated previews with links to raw images.
- `corpus/tuning.json`: calibration-selected parameters and separate holdout results.
- `<output>/evaluation.json`: reviewed-reference errors/misses/false positives/geometry
  errors, plus offline/live agreement explicitly labeled as **not accuracy**.
- `<output>/report.html`: the actual C# replay and causal growth series.

For a live-cadence run, omit `--every-frame` and select `--interval-minutes 10` (default).
Each replay uses a new output directory; old evidence is retained. Raw images and
generated artifacts are ignored by Git. Curated real-photo regressions cover backlight,
glare, pale dough, smaller jars, base edges, empty shelves/wooden stands, dawn recovery,
sampling-resolution changes and persisted-session recovery.

The current corpus validates one physical jar across several camera positions,
ambient/backlit day/night, and geometric stress transforms. It does not certify
unseen vessels, materials, recipes, or arbitrary viewpoints. Add reviewed examples
for new setups before claiming their accuracy. Deployment remains approval-gated;
reset the old growth session when changing the measurement basis.

## Maximum prediction confidence

A flat lag phase or accelerating segment alone does not identify a maximum. Predictions
remain unavailable until meaningful rise and sustained slowdown are observed. The
sigmoid may reach its practical maximum in the past; forcing a future ETA would invent
continued growth after the plateau. Once the practical peak is confirmed, the peaked
sensor is set and the ETA is withdrawn. Sustained renewed growth beyond the remembered
peak plus measurement jitter clears the flag without discarding the feeding history.
A recovered session must show a new observed plateau before confirming another peak;
a fit to the earlier plateau alone is insufficient.

Controlled growth-curve verification is not a backtest of actual fermentation timing.
That requires a camera-stable recorded rise and an independently observed peak.


## Camera load & live stream

Every sampling cycle fetches one snapshot. Connection reuse is built in (pooled HTTP
client), retries are limited to failed fetches (default 1 extra attempt), and detection
misses are simply re-sampled by the next cycle — so the load on the camera is one request
per interval, nothing more.

If the **live stream in the dashboard breaks** while the add-on runs, the bottleneck is
usually CPU on the box hosting Frigate (a Raspberry Pi serving stream, motion detection
and snapshots at once). Remedies, in order of effect:

1. Fetch snapshots **directly from Frigate** instead of the HA camera-proxy hop: set
   `frigate_base_url` to `http://<frigate-host>:5000` (leave `frigate_snapshot_url`
   empty). Frigate serves `latest.jpg` from memory — no HA round trip.
2. Lower `frigate_snapshot_quality` (75) / `frigate_snapshot_height` (720): a smaller
   JPEG costs much less encode CPU per request, and the detection works fine on smaller
   frames (all thresholds are relative).
3. Raise `frigate_sample_interval_minutes` (e.g. 5) — fewer requests per hour.
4. Keep the retry count low (`Frigate:SnapshotRetryCount`, default 1).

## Dark-frame gate

Without a recognized light source, frames whose P90 intensity is below
`Vision:MinFrameIntensity` (25) or P90−P10 spread below `Vision:MinFrameContrast` (30)
are rejected. A compact LED-lit jar can occupy less than ten percent of a dark frame;
its source and separate glass foot are checked before this global gate. Detection
then follows the lower-connected dough body, not the hotspot's fade. Insufficient
boundary evidence still returns no measurement rather than inventing a surface.

## Configuration (appsettings.json)

```json
{
  "Monitor": {
    "Frigate": {
      "BaseUrl": "http://homeassistant.local:8123",
      "Camera": "battery_cam",
      "AccessToken": "",
      "SampleIntervalMinutes": 1
    },
    "Mqtt": {
      "Host": "core-mosquitto",
      "Port": 1883,
      "Username": "",
      "Password": "",
      "DeviceId": "sourdough_monitor"
    },
    "Vision": {
      "RoiX": null,
      "RoiY": null,
      "RoiWidth": null,
      "RoiHeight": null,
      "MinJarWallFraction": 0.08,
      "MinJarWidthFraction": 0.04,
      "MinFrameIntensity": 25.0,
      "MinFrameContrast": 30.0,
      "DebugSaveAnnotatedImages": false
    },
    "Analysis": {
      "SlopeWindowMinutes": 120,
      "RateNoiseFloorPx": 2.0,
      "MaxRateStdErrPercentPerHour": 2.0,
      "ResetDropFraction": 0.25,
      "MinSamplesForFit": 8,
      "MaxEtaRelativeStdError": 0.15,
      "PeakConfirmWindows": 3,
      "MaxSessionHours": 36
    }
  }
}
```

## Build from source

```bash
dotnet build
```

Requires .NET 8 SDK and OpenCV dependencies.

## License

GNU General Public License v3.0
