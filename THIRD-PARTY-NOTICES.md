# Third-party notices

SMD Win uses the following open-source components and data. Each remains under its own license.

## Libraries (NuGet)

| Component | License | Used for |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | MPL-2.0 | Sensors: temperatures, voltages, fans, clocks, power |
| [WPF-UI](https://github.com/lepoco/wpfui) | MIT | Windows 11 style controls |
| [PDFsharp](https://github.com/empira/PDFsharp) | MIT | PDF reports |
| [SharpDX](https://github.com/sharpdx/SharpDX) | MIT | DirectX access for the graphics stress test |
| [Serilog](https://github.com/serilog/serilog) (+ Sinks.File, Sinks.Debug, Enrichers.Thread) | Apache-2.0 | Logging |
| System.Management, System.ServiceProcess.ServiceController, Microsoft.VisualBasic | MIT | WMI, services, helpers (.NET) |

## Bundled programs

### smartmontools — `Tools/smartctl.exe`, `Tools/drivedb.h`
- Version 7.5, unmodified. License: GPL-2.0-or-later (full text in `Tools/smartmontools-COPYING.txt`).
- Run as a separate process; it is not linked into SMD Win.
- Source code: https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5

### PawnIO — `Assets/PawnIO_setup.exe`
- Official signed installer, redistributed unmodified and run as a separate process.
- SMD Win talks to the driver through its standard interface.
- Project and license: https://github.com/namazso/PawnIO

## Icons

### Fluent UI System Icons — `Services/FluentIcons.cs`, `views/Icons*.xaml`, page icons

- Copyright (c) 2020 Microsoft Corporation — MIT License
- https://github.com/microsoft/fluentui-system-icons
- The "regular" 24 px icons are converted to WPF path data. The graphics card, fan and Windows logo icons are SMD Win's own drawings in the same style.

## Data

| Data | Source | License |
|---|---|---|
| Hardware database (`Data/HardwareDB`) | [buildcores-open-db](https://github.com/buildcores/buildcores-open-db), completed from manufacturer specifications | ODC-By 1.0 |
| IP to country (`Assets/geo-country-v4.bin`) | [DB-IP Lite](https://db-ip.com) "IP to Country", via [ip-location-db](https://github.com/sapics/ip-location-db); converted to a compact table | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) — "IP Geolocation by DB-IP" |
| Malware control server addresses (optional network alert, downloaded at run time to `Data\net-blocklist-feodo.txt`, not shipped) | [abuse.ch Feodo Tracker](https://feodotracker.abuse.ch/) IP blocklist | [CC0](https://creativecommons.org/publicdomain/zero/1.0/) |
| MAC vendor list (`Assets/oui_database.csv`) | IEEE OUI registry, via the Wireshark `manuf` file | Public IEEE data / Wireshark |
| Windows event explanations (`Data/EventDB`) | Written for SMD Win; sources listed in `Data/EventDB/SOURCES.md` | Same as SMD Win |
