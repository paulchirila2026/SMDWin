# SMD Win 0.4 Beta

First public release. A portable Windows diagnostics tool: hardware, temperatures, storage health, stress tests, benchmark and Windows problems in one exe. No installation, no account, no telemetry.

## Highlights

- **Benchmark page** — marks from 0 to 10 for the processor, memory, storage and graphics card in about 2.5 minutes, a performance class, checks before the test (battery, Power saver, Turbo off, free space) and short advice per component.
- **Storage** — SMART health for SATA, NVMe and USB drives, a live activity chart per disk, surface scan, benchmark and space usage.
- **Temp & Stress** — live sensors, processor / graphics / full-system stress tests with integrity checks and automatic stop when a temperature stays too high.
- **Power** — battery health, sleep and wake history, what keeps the PC awake, Fast Startup, hibernation, power plans; Turbo changes can be temporary (only while SMD Win is open).
- **Network** — traffic per program (TCP and UDP, IPv4 and IPv6) with sites and countries, Wi-Fi, ping, traceroute, DNS, port and LAN scan, speed test.
- **Windows** — crashes with likely cause, Event Viewer with explanations, drivers, startup apps, services, Optimize and Auto-Clean with revert.
- **Reports** — PDF and HTML reports and a diagnostic package.
- Four languages: English, Romanian, German, French. Light and Dark themes.

## Before running

- Windows 10 / 11, 64-bit, administrator rights. The exe is self-contained (no .NET needed).
- The exe is **not code-signed yet**: SmartScreen shows "Windows protected your PC" → **More info → Run anyway**. Some antivirus programs may flag an unsigned exe that installs a sensor driver (PawnIO, optional). The SHA-256 checksum is below.
- This is a **beta**: benchmark marks are first estimates, and some sensors are missing on some models. Bug reports with the `Data\Logs` folder are welcome — remove serial numbers and network details first, issues are public.

## Checksum

```
SHA-256 (SMDWin.exe): f2c9997b2679ae85e29d50bd00edc7a9cb5b1ad2fd4bcad185fb6815621d4003
```

VirusTotal scan of this exe: [1 / 69](https://www.virustotal.com/gui/file/f2c9997b2679ae85e29d50bd00edc7a9cb5b1ad2fd4bcad185fb6815621d4003/detection) — one generic detection (Bkav Pro), all other engines report it clean.

Full list of changes: [CHANGELOG.md](../CHANGELOG.md).
