#!/usr/bin/with-contenv bashio
# ==============================================================================
# Sourdough Monitor HA Add-on
# Maps add-on config options to .NET environment variables and starts the app.
# ==============================================================================

set -e

CONFIG_PATH=/data/options.json

# --- Frigate ---
export Monitor__Frigate__BaseUrl="$(bashio::config 'frigate_base_url')"
export Monitor__Frigate__Camera="$(bashio::config 'frigate_camera')"
export Monitor__Frigate__AccessToken="$(bashio::config 'frigate_access_token')"
export Monitor__Frigate__SampleIntervalMinutes="$(bashio::config 'frigate_sample_interval_minutes')"

# --- MQTT ---
export Monitor__Mqtt__Host="$(bashio::config 'mqtt_host')"
export Monitor__Mqtt__Port="$(bashio::config 'mqtt_port')"
export Monitor__Mqtt__Username="$(bashio::config 'mqtt_username')"
export Monitor__Mqtt__Password="$(bashio::config 'mqtt_password')"
export Monitor__Mqtt__DeviceId="$(bashio::config 'mqtt_device_id')"
export Monitor__Mqtt__DiscoveryPrefix="homeassistant"

# --- Suppress console rendering (no terminal in HA add-on) ---
export Monitor__Vision__DebugSaveAnnotatedImages="false"

cd /app
exec dotnet SourdoughMonitor.dll