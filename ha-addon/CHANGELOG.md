# Changelog

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
