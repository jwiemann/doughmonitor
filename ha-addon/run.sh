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

# --- MQTT ---
export Monitor__Mqtt__DeviceId="$(jq -r '.mqtt_device_id' "$CONFIG_PATH")"
export Monitor__Mqtt__DiscoveryPrefix="homeassistant"

# --- Suppress console rendering (no terminal in HA add-on) ---
export Monitor__Vision__DebugSaveAnnotatedImages="false"

# --- OpenCvSharp native libraries ---
export LD_LIBRARY_PATH="/app/runtimes/linux-x64/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

cd /app
exec /app/SourdoughMonitor