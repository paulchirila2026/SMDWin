# SMD Win 0.4.2 Beta

A bigger update after 0.4.1: deeper storage and memory diagnostics, a new Benchmark page, reworked Settings and software pages, safer clean-up, and fixes from the reports on Reddit. Thank you to everyone who sent logs and screenshots.

**Download:** `SMDWin.exe` below — one portable file for Windows 10/11 x64, no installation, no .NET needed.

### New and changed
- **Storage checks** on every drive: SATA link below the drive's speed, NVMe PCIe lanes and generation, the disk errors Windows logged per drive (30 days), unaligned partitions, TRIM off, SMR hard disks, Samsung firmware with the known wear bug, NVMe thermal throttling, SMART through more USB cases.
- **More drive tests:** Real capacity (finds fake USB sticks and memory cards), Sustained write (the fast cache of an SSD and the speed after it), Access time (the seek time of a hard disk).
- **RAM:** XMP / EXPO on or off from the module part number, mixed modules, the last Windows Memory Diagnostic result, corrected memory errors, "is the RAM enough for what runs now", bandwidth vs. the theoretical peak, and a full scan with a second pass on all threads.
- **New drawings** of hard disks, SSDs, USB drives and memory modules.
- **Benchmark:** a line drawing of this computer (desktop, laptop, all-in-one or mini PC) that follows the test and the real readings, a short check before the test (power, Turbo, power plan, free space), and the tests in the same order as the drawing.
- **Settings:** eight tabs at the top; every option that differs from its default shows a dot and a "Default" link.
- **Start cards:** Disk benchmark, Surface scan, Speed test and Ping monitor show what the test does and how long it takes.
- **Applications:** updates through winget, an "Unused" filter, and a clean-up of what an uninstall leaves behind.
- **Services:** a "Non-Microsoft" view, recent crashes, and every change made from SMD Win can be undone.
- **Startup Manager:** the delay each program adds (measured by Windows), a "New" badge, and suggestions.
- **Tools:** SFC and DISM inside the page, plus Reset Windows Update, Reset print queue, restore point, driver back-up, BitLocker key, memory test and battery report; a History with Undo.
- **Small windows:** the less important columns step out instead of everything being cut.

### Fixed
- **Clear temporary files / Auto-Clean** now shows what it will delete first and cleans only the two real Temp folders (it followed the TEMP/TMP variables, which on some PCs point at C:\); Windows Update downloads only when no update is installing.
- **Surface scan** stops with "Drive disconnected" when the drive is unplugged, instead of racing on and switching to another disk.
- **RAM:** XMP showed as on for kits running at the standard speed.
- **Applications** installed years ago no longer show as "New" after an update.
- **Command Timeout (SMART 188)** on Seagate drives showed billions; now the real count (thanks for the report and the CrystalDiskInfo comparison).
- **Startup Manager:** Edge and other entries Windows does not let a switch control no longer have a switch that jumps back on.
- **Boot time** no longer counts the time spent at the sign-in screen.
- **Benchmark:** Start no longer ends in an error with Turbo ticked.
- **About → Check for updates** really checks GitHub.

Full list: [CHANGELOG](../CHANGELOG.md)

### Good to know
- The exe is **not code-signed yet**, so Windows SmartScreen may warn on first run: **More info → Run anyway**.
- This exe was **built on GitHub Actions** directly from the public source code (the commit is listed at the end of these notes).

SHA-256 (SMDWin.exe): <to be filled in at release>
