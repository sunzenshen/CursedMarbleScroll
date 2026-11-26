# Changelog

## Unreleased

- A left or right click while scroll-locked now unlocks instead of clicking. The click and its release are not passed to the app.
- Optional momentum: with **Momentum after a flick** enabled, releasing a hold-to-scroll while still moving keeps scrolling and decays to a stop. Clicks, wheel turns, and moving to another window stop it. Off by default.
- A click followed quickly by motion now always replays at the press position. Previously the motion that ended the lock wait raced the replay, and if the output thread lost, the click could land on a neighboring link or tab.
- The hook path is compiled before the hook is installed, removing a stall of about 8 ms on the first mouse event after launch.
- Pressing an enabled button while scroll-locked always unlocks, even if the release of the locking click was missed.
- Remote Desktop connect and disconnect now cancel gestures, and connect reinstalls the hook, matching console session switches.

## v1.1.1

- Back and forward buttons both act as middle by default. Hold and move to scroll, or click without moving to replay a middle click. One tray toggle restores normal back and forward input.
- Double-click locking is on by default with a 300 ms window, independent of Windows double-click settings. The window is saved per user and bounded to 150–1000 ms. Locked scrolling still expires after 10 seconds of inactivity.
- Dominant-axis snapping is off by default, allowing free two-axis scrolling.
- A one-time settings migration applies these defaults and preserves other choices.

## v1.1.0

- Stationary middle and back-button presses replay a click even after a long hold. Scrolling starts on movement.
- Added scroll speed, dominant-axis snapping, Pause, Reset, startup at sign-in, and tray status. The fork also includes smooth scrolling, whole-notch mode, reverse, and optional double-click locking.
- Moved the mouse hook to a dedicated thread. Added fail-open recovery, idle and lifecycle resets, checked input injection with bounded cleanup, safer delayed replay, settings recovery, and one instance per Windows session.
- Enabled PerMonitorV2 DPI awareness and expanded Windows tests to x64 and x86. CI pins actions and publishes tested builds with a release checksum.

## v1.0.0

- Baseline build of the forked upstream Windows code with middle- and back-button hold-to-scroll and tray controls.
