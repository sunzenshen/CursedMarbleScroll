# MarbleScroll

> I too was caught off guard when the original install of MarbleScroll became no longer available.
> Questionable sacrifices to the dark god Claude were made in this fork, so use it at your own risk.

MarbleScroll is a Windows tray app for scrolling with a mouse or trackball. Hold the middle button and move to scroll. Press and release it without crossing the movement threshold to open a link in a new tab, close a tab, or paste in an app that supports middle-click paste. The back side button also works by default. On Linux, the equivalent is [libinput's on-button-down scroll method](https://wayland.freedesktop.org/libinput/doc/latest/scrolling.html#on-button-scrolling).

## Tray menu

Run `MarbleScroll.exe` and right-click its tray icon. Only one instance runs per Windows session.

| Group | Option | Default and behavior |
| --- | --- | --- |
| Triggers | Scroll on middle button | On. Hold the middle button and move to scroll. |
| Triggers | Scroll on X-Button | On. Hold the back side button (XBUTTON1) and move to scroll. |
| Triggers | Emulate middle button on X click | Off. A stationary back-button press replays as a middle click when X-button scrolling is enabled. |
| Behavior | Smooth scrolling | On. Sends small wheel deltas. Turn it off for apps that expect whole 120-unit notches. |
| Behavior | Double-click to lock scrolling | Off. Double-click an enabled trigger to scroll hands-free. Click a trigger once to unlock. Left and right clicks still work while locked. A pending single click replays after the Windows double-click interval or when motion cancels lock recognition. A delayed replay is dropped if the window under the pointer changes or the click is older than the double-click interval plus 250 ms. |
| Behavior | Reverse scroll direction | Off. Reverses both axes. |
| Behavior | Snap to dominant axis | On. Chooses the axis with greater accumulated motion and switches only when the other axis exceeds three times the current axis's accumulated motion. |
| Behavior | Scroll speed | Normal (1×). Also offers Slow (0.5×), Fast (2×), and Very fast (3×). |
| Controls | Pause | Off. Passes mouse input through and cancels the current gesture. Double-click the tray icon with the left button to toggle it. |
| Controls | Reset | Cancels the gesture, discards pending output, and reinstalls the hook. |
| Controls | Start with Windows | Off. Toggles the `MarbleScroll` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The value contains the quoted executable path. |
| Exit | Exit | Closes MarbleScroll. |

Scrolling starts after 2 pixels of accumulated motion. Holding still preserves the click regardless of how long the button is held. At normal speed, a pixel of motion produces four vertical wheel units or one horizontal wheel unit. The tooltip reads **MarbleScroll**, **MarbleScroll (paused)**, or **MarbleScroll (scroll locked)**.

Tray choices are saved per Windows user. If user settings are corrupt, MarbleScroll starts with defaults and attempts to rename the file to `user.config.corrupt-<timestamp>`. A save failure displays a tray balloon once per run.

## Launch switches

Launch overrides apply to the current run. Use the tray menu to change saved choices.

| Switch | Effect |
| --- | --- |
| `--defaults` | Starts with factory gesture settings and leaves saved choices intact. |
| `--lock` | Enables double-click locking. |
| `--notches` | Sends only whole wheel notches. |
| `--paused` | Starts paused. |
| `--lock-idle-ms <milliseconds>` | Overrides the locked-scroll idle timeout for testing. |
| `--exit-after-ms <milliseconds>` | Runs the tray Exit action after a delay for testing. |
| `--check-native-layouts <32\|64>` | Checks the process bitness and native input struct sizes, then exits with status 0 on success or 1 on mismatch. For testing. |

For example, `MarbleScroll.exe --defaults --lock` starts with factory gesture settings and lock enabled.

## Recovery and limitations

An active hold resets after 30 seconds without mouse input. Locked scroll resets after 10 seconds without movement. Session lock and unlock, console connect and disconnect, suspend and resume, and display changes cancel gestures and discard pending output. MarbleScroll reinstalls the hook after unlock, console connect, and resume. Use **Reset** if scrolling stops responding, or **Pause** to restore ordinary input.

Windows User Interface Privilege Isolation (UIPI) can block synthetic wheel and click events over an elevated window. A failed wheel send cancels the gesture and restores pointer movement. MarbleScroll runs as the invoking user with UIAccess disabled.

## Build and test

Windows 10 or 11 with .NET Framework 4.8 is required. Install the .NET Framework 4.8 targeting pack to build from Visual Studio or a Developer Command Prompt.

```bat
msbuild MarbleScroll.sln /p:Configuration=Release /m
msbuild tests/ScrollEngineTests.csproj /p:Configuration=Release
tests\bin\Release\ScrollEngineTests.exe
```

The executable, configuration, and license are written to `MarbleScroll/bin/Release/`. Exit any running MarbleScroll instance before running the end-to-end test on an unlocked Windows desktop.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/e2e.ps1 -Exe MarbleScroll/bin/Release/MarbleScroll.exe
```

CI tests the AnyCPU build as x64 and an explicit x86 build as 32-bit. It uploads each build after its tests pass. For a local x86 test, build with `/p:PlatformTarget=x86` and pass `-ExpectedBits 32` to the e2e script.

## Releases

v1.0.0 is the baseline build of the forked upstream code; v1.1.0 is the hardened baseline.

Tags such as `vX.Y.Z` publish the tested AnyCPU build as `MarbleScroll-<tag>.zip`, with the executable, configuration, license, and README. The release also includes `SHA256SUMS.txt` for verifying the ZIP.

## Attribution

- Original MarbleScroll by fewprojects.com (site offline).
- The MIT-licensed [j-zero/MarbleScroll](https://github.com/j-zero/MarbleScroll) fork added the middle- and X-button options.
- The scroll engine is adapted from [kaushr/MarbleScroll for macOS](https://github.com/kaushr/MarbleScroll), MIT licensed, Copyright 2026 Kaushik Ruparel.
