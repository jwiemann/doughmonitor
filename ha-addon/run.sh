#!/bin/bash
# ==============================================================================
# Sourdough Monitor HA Add-on
# Maps add-on config options to .NET environment variables and starts the app.
# ==============================================================================

set -e

CONFIG_PATH=/data/options.json

# --- Frigate ---
export Monitor__Frigate__Camera="$(jq -r '.frigate_camera' "$CONFIG_PATH")"
export Monitor__Frigate__SampleIntervalMinutes="$(jq -r '.frigate_sample_interval_minutes' "$CONFIG_PATH")"
# Direct Frigate snapshot source (skips the HA camera-proxy hop). Empty = use the
# supervisor proxy. Recommended when Frigate runs on a small box (e.g. a Raspberry Pi):
# the snapshot is served from Frigate's memory at a reduced size instead of re-encoding
# a full-res frame per request.
export Monitor__Frigate__BaseUrl="$(jq -r '.frigate_base_url // ""' "$CONFIG_PATH")"
# Explicit override; empty falls through to BaseUrl (or the supervisor proxy). This must
# be exported even when empty: the appsettings.json ships a camera-proxy SnapshotUrl that
# would otherwise shadow the Frigate BaseUrl.
export Monitor__Frigate__SnapshotUrl="$(jq -r '.frigate_snapshot_url // ""' "$CONFIG_PATH")"
export Monitor__Frigate__SnapshotQuality="$(jq -r '.frigate_snapshot_quality // 75' "$CONFIG_PATH")"
export Monitor__Frigate__SnapshotHeight="$(jq -r '.frigate_snapshot_height // 720' "$CONFIG_PATH")"
# --- Raw snapshot archive (for offline replay/debug analysis; empty = off) ---
export Monitor__Frigate__SnapshotArchiveDirectory="$(jq -r '.snapshot_archive_directory // ""' "$CONFIG_PATH")"

# --- MQTT ---
export Monitor__Mqtt__DeviceId="$(jq -r '.mqtt_device_id' "$CONFIG_PATH")"
export Monitor__Mqtt__DiscoveryPrefix="homeassistant"

# --- Debug mode (annotated images + MQTT camera) ---
export Monitor__Vision__DebugSaveAnnotatedImages="$(jq -r '.debug_enabled' "$CONFIG_PATH")"
export Monitor__Vision__DebugHighlightDough="$(jq -r '.debug_highlight_dough // false' "$CONFIG_PATH")"
export Monitor__Mqtt__DebugMode="$(jq -r '.debug_enabled' "$CONFIG_PATH")"

# --- OpenCvSharp native libraries ---
export LD_LIBRARY_PATH="/app/runtimes/linux-x64/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

cd /app
exec /app/SourdoughMonitor