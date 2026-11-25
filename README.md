# MarbleScroll

MarbleScroll is a Windows tray app for scrolling with a mouse or trackball. It is especially useful with wheel-less trackballs like the Logitech Trackman Marble. Hold the middle button or the back side button (X button), then move the mouse or roll the trackball to scroll vertically or horizontally. A quick press and release still acts as a click.

On Linux, the equivalent is [libinput's on-button-down scroll method](https://wayland.freedesktop.org/libinput/doc/latest/scrolling.html#on-button-scrolling).

## Usage

Run `MarbleScroll.exe` and right-click the MarbleScroll tray icon.

- **Scroll on middle Button** enables scrolling while the middle button is held. On by default.
- **Scroll on X-Button** enables scrolling while the back side button is held. On by default.
- **Emulate middle button on X click** turns a quick back-button click into a middle click while X-button scrolling is enabled. Off by default.
- **Exit** closes MarbleScroll.

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

The executable and its configuration file are written to `MarbleScroll/bin/Release/`. CI uploads both files in the **MarbleScroll** artifact before running the tests, so they are available even if a test fails.

## Testing

CI runs a Windows end-to-end test against the Release executable. The test injects mouse input into a WinForms window and checks wheel direction, click replay, cursor suppression, and normal mouse behavior after MarbleScroll exits.

Exit any running MarbleScroll instance, then run the test from the repository root on an unlocked Windows desktop.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/e2e.ps1 -Exe MarbleScroll/bin/Release/MarbleScroll.exe
```

## Releases

Tags such as `vX.Y.Z` publish a GitHub Release with a `MarbleScroll-<tag>.zip` asset after CI passes.

## Attribution

- Original MarbleScroll by [fewprojects.com](https://www.fewprojects.com/marblescroll-for-logitech-trackman-marble/) (site offline).
- The MIT-licensed [j-zero/MarbleScroll](https://github.com/j-zero/MarbleScroll) fork added the middle- and X-button options.
- This fork is maintained by sunzenshen.
