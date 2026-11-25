# Changelog

## v1.1.0

- Stationary middle and back-button presses replay a click even after a long hold. Scrolling starts on movement.
- Added scroll speed, dominant-axis snapping, Pause, Reset, startup at sign-in, and tray status. The fork also includes smooth scrolling, whole-notch mode, reverse, and optional double-click locking.
- Moved the mouse hook to a dedicated thread. Added fail-open recovery, idle and lifecycle resets, checked input injection with bounded cleanup, safer delayed replay, settings recovery, and one instance per Windows session.
- Enabled PerMonitorV2 DPI awareness and expanded Windows tests to x64 and x86. CI pins actions and publishes tested builds with a release checksum.

## v1.0.0

- Baseline build of the forked upstream Windows code with middle- and back-button hold-to-scroll and tray controls.
