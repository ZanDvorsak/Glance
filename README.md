# Glance

A modern, open replacement for AIDA64's LCD customizer, for small USB displays that use the AX206-style protocol
(USB ID `1908:0102`, 480×320). Design your own screens with live hardware data, backgrounds, gauges, graphs and pages,
and keep them on the display from a small background app.

> Status: early. Windows first. New here? Read the [tutorial](docs/TUTORIAL.md) (with screenshots).

![Glance editor](docs/images/02-editor.png)

## What it does

- **Editor** (Avalonia): live preview, drag and resize, undo/redo, pages, premade widgets, a virtual display for demos without the hardware.
- **Widgets**: text, sensor values, clock, bars, gauges, history graphs, images, GIFs, video (needs FFmpeg), panels. Colour thresholds.
- **Sensors**: everything LibreHardwareMonitor can read (CPU, GPU, motherboard, fans, voltages, drives), plus drive space. Searchable by group and type.
- **Starter themes and premade widgets**: five ready-made themes (minimal, dashboard, gaming, a two-page overview, desk clock) and a gallery of about 30 cards, gauges, rings and graphs that bind to your own sensors.
- **AIDA64 import**: loads `.axlcd` themes with a preview, maps their items to your sensors.
- **Sharing**: export a theme as one `.glance` file (images and videos included) and open it on another PC.
- **Runner** (tray app): shows your theme on the display without the editor, rotates pages, reloads when you save, blanks the screen on exit.

## Requirements

- Windows 10/11, .NET 8 SDK
- The display must use the **libusb-win32** driver (install it with [Zadig](https://zadig.akeo.ie/), choose `libusb-win32`)
- [PawnIO](https://pawnio.eu/) for CPU and motherboard sensors (LibreHardwareMonitor needs it)
- Optional: FFmpeg for video backgrounds. The background runner runs as administrator, so it only uses an FFmpeg that
  only administrators can change: install it for all users with **Install for all users** in Setup check. A per-user
  install (for example plain `winget install Gyan.FFmpeg`) works in the editor but not in the runner; the reason is
  written to `runner.log`.

## Install

Download `Glance-<version>-win-x64.zip` (no .NET needed), unzip it somewhere permanent, and run `Glance.App.exe`.
Start the background runner from the editor (**Background service**), or run `Glance.Runner.exe`; it asks for administrator rights.
There is also an installer (`Glance-Setup-<version>.exe`) that puts Glance in Program Files with Start menu shortcuts.

## Build and run

```powershell
cd src
dotnet build Glance.sln -c Release
```

Run `Glance.Runner` (asks for administrator rights) and `Glance.App` (the editor) from their `bin\Release` folders.

## Tests

```powershell
dotnet test src/Glance.Tests -c Release
```

About 240 unit tests cover the logic that does not need the display. They also run on GitHub for every push.

## Package and installer

```powershell
.\build\publish.ps1                      # dist\Glance and a self-contained zip (~215 MB folder, ~90 MB zip)
.\build\publish.ps1 -FrameworkDependent  # much smaller; needs the .NET 8 Desktop Runtime on the target PC
.\build\make-installer.ps1               # dist\Glance-Setup-<version>.exe (needs Inno Setup 6)
```

Bump the version in `src/Directory.Build.props` for a release. The editor and the runner live side by side in one folder
and find each other there.

## Layout

| Folder | Purpose |
|---|---|
| `src/Glance.Core` | Display driver, renderer, themes, sensors, presets, importer |
| `src/Glance.App` | Avalonia editor |
| `src/Glance.Runner` | Elevated tray app that owns the display |
| `src/Glance.SensorHost` | Standalone elevated sensor feed |
| `src/Glance.Tests` | Unit tests |
| `src/UsbProbe` | Small diagnostic tool for the USB protocol |
| `build/` | Publish and installer scripts |
| `installer/` | Inno Setup definition |
| `assets/` | Application icon |
| `docs/PROTOCOL.md` | The display's USB protocol, reverse-engineered from a capture |
| `AGENTS.md` | Notes for AI coding agents working on this repo |
