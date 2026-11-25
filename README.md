# MarbleScroll

MarbleScroll is a Windows tray app for scrolling with a mouse or trackball. It is especially useful with wheel-less trackballs like the Logitech Trackman Marble. Hold the middle button or the back side button (X button), then move the mouse or roll the trackball to scroll vertically or horizontally. A quick press and release still acts as a click.

On Linux, the equivalent is [libinput's on-button-down scroll method](https://wayland.freedesktop.org/libinput/doc/latest/scrolling.html#on-button-scrolling).

## Usage

Run `MarbleScroll.exe` and right-click the MarbleScroll tray icon.

- **Scroll on middle Button** enables scrolling while the middle button is held. On by default.
- **Scroll on X-Button** enables scrolling while the back side button is held. On by default.
- **Emulate middle button on X click** turns a quick back-button click into a middle click while X-button scrolling is enabled. Off by default.
- **Smooth scrolling** sends small wheel deltas as you move. On by default. Turn it off for apps that expect whole 120-unit wheel notches.
- **Double-click to lock scrolling** lets you double-click either enabled scroll button, then scroll without holding it. Click an enabled scroll button once to unlock. Off by default. When on, a stationary single click is replayed after the Windows double-click interval. With lock off, a short click is replayed on release.
- **Reverse scroll direction** flips both axes. Off by default.
- **Exit** closes MarbleScroll.

Tray options are saved per Windows user. Scrolling begins after 2 pixels of accumulated movement or a 300 ms hold. By default, one pixel of motion produces four vertical wheel units or one horizontal wheel unit.

Launch switches apply to the current run. Tray changes still save normally.

- `--defaults` starts with factory defaults without overwriting saved choices.
- `--lock` enables double-click lock at startup.
- `--notches` starts in whole-notch mode.

For example, `MarbleScroll.exe --defaults --lock` starts with factory defaults and lock enabled.

To start MarbleScroll at login, press Win+R, enter `shell:startup`, and place a shortcut to `MarbleScroll.exe` in the folder that opens.

## Limitations

MarbleScroll runs without administrator rights, so Windows User Interface Privilege Isolation (UIPI) blocks its synthetic scroll and click events from reaching windows running as administrator. The upstream build used `uiAccess`, which requires a trusted code signature and installation under Program Files. This build runs with `uiAccess` disabled.

## Requirements

Windows 10 or 11 with .NET Framework 4.8.

## Build

With the .NET Framework 4.8 targeting pack installed, open `MarbleScroll.sln` in Visual Studio and build the Release configuration, or run this command from a Developer Command Prompt.

```bat
msbuild MarbleScroll.sln /p:Configuration=Release /m
```

The executable, configuration, and license are written to `MarbleScroll/bin/Release/`. CI uploads them in the **MarbleScroll** artifact before running the tests, so they are available even if a test fails.

## Testing

CI runs the pure scroll engine tests before the Windows end-to-end test. The end-to-end test injects mouse input into a WinForms window and checks wheel direction and totals, smooth and notch modes, click replay, locking, and cursor behavior.

```bat
msbuild tests/ScrollEngineTests.csproj /p:Configuration=Release
tests\bin\Release\ScrollEngineTests.exe
```

Exit any running MarbleScroll instance, then run the test from the repository root on an unlocked Windows desktop.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/e2e.ps1 -Exe MarbleScroll/bin/Release/MarbleScroll.exe
```

## Releases

Tags such as `vX.Y.Z` publish a GitHub Release with a `MarbleScroll-<tag>.zip` asset after CI passes.

## Attribution

- Original MarbleScroll by [fewprojects.com](https://www.fewprojects.com/marblescroll-for-logitech-trackman-marble/) (site offline).
- The MIT-licensed [j-zero/MarbleScroll](https://github.com/j-zero/MarbleScroll) fork added the middle- and X-button options.
- The scroll engine is adapted from [kaushr/MarbleScroll for macOS](https://github.com/kaushr/MarbleScroll), MIT licensed, Copyright 2026 Kaushik Ruparel.
- This fork is maintained by sunzenshen.
