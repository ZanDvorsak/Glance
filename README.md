# Glance

A modern, open replacement for AIDA64's LCD customizer, for small USB displays that use the AX206-style protocol
(USB ID `1908:0102`, 480×320). Design your own screens with live hardware data, backgrounds, gauges, graphs and pages,
and keep them on the display from a small background app.

> Status: early. Windows first.

## What it does

- **Editor** (Avalonia): live preview, drag and resize, undo/redo, pages, premade widgets, a virtual display for demos without the hardware.
- **Widgets**: text, sensor values, clock, bars, gauges, history graphs, images, GIFs, video (needs FFmpeg), panels. Colour thresholds.
- **Sensors**: everything LibreHardwareMonitor can read (CPU, GPU, motherboard, fans, voltages, drives), plus drive space. Searchable by group and type.
- **AIDA64 import**: loads `.axlcd` themes with a preview, maps their items to your sensors.
- **Runner** (tray app): shows your theme on the display without the editor, rotates pages, reloads when you save, blanks the screen on exit.

## Requirements

- Windows 10/11, .NET 8 SDK
- The display must use the **libusb-win32** driver (install it with [Zadig](https://zadig.akeo.ie/), choose `libusb-win32`)
- [PawnIO](https://pawnio.eu/) for CPU and motherboard sensors (LibreHardwareMonitor needs it)
- Optional: FFmpeg for video backgrounds

## Build and run

```powershell
cd src
dotnet build Glance.sln -c Release
```

Run `Glance.Runner` (asks for administrator rights) and `Glance.App` (the editor) from their `bin\Release` folders.

## Layout

| Folder | Purpose |
|---|---|
| `src/Glance.Core` | Display driver, renderer, themes, sensors, presets, importer |
| `src/Glance.App` | Avalonia editor |
| `src/Glance.Runner` | Elevated tray app that owns the display |
| `src/Glance.SensorHost` | Standalone elevated sensor feed |
| `src/UsbProbe` | Small diagnostic tool for the USB protocol |
| `docs/PROTOCOL.md` | The display's USB protocol, reverse-engineered from a capture |
