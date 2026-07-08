# Changelog

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
