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
| `frigate_sample_interval_minutes` | Interval between snapshots (1–60 min) |
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

## Configuration (appsettings.json)

```json
{
  "Monitor": {
    "Frigate": {
      "BaseUrl": "http://homeassistant.local:8123",
      "Camera": "battery_cam",
      "AccessToken": "",
      "SampleIntervalMinutes": 10
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
      "DebugSaveAnnotatedImages": false
    },
    "Analysis": {
      "SlopeWindowMinutes": 40,
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
