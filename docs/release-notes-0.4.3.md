# SMD Win 0.4.3 Beta

A round of polish after 0.4.2, based on testing on real PCs: a new Wi-Fi channel analysis, a calmer Settings page, a more useful Network section, and many smaller fixes.

**Download:** `SMDWin.exe` below — one portable file for Windows 10/11 x64, no installation, no .NET needed.

### New and changed
- **Wi-Fi › Channel analysis:** every access point in range on one full-width map, with the connection details each router announces (channel width, Wi-Fi standard, streams, security, clients), interference and recommendations. Names show on hover only.
- **Network › Apps:** a "Group by program" button puts all processes of the same program (for example several ffmpeg) in one row.
- **Network › Overview:** the "Active connections" card now looks like the Adapters card; the ping chart glides smoothly.
- **Event Viewer:** a "Last year" calendar of errors and blue screens; the level buttons (Critical, Error, Warning) filter the map.
- **Space usage:** uses the NTFS index again, so scans are fast.
- **Settings:** one page with compact cards; the rarely used options moved to the developer window; the sensor driver state is a simple LED.
- **Dashboard:** calmer look, a steady LIVE indicator showing the sensor driver state, "Health" instead of "PC health".
- **Storage, Benchmark, Temp & Stress:** new drawings, a stronger GPU test, one health verdict everywhere, and more readable charts.
- **Windows on another drive:** Event Viewer and BSOD analysis can read a Windows that is not running (for example from a rescue stick).

### Fixed
- Many texts that stayed in English are now translated (Romanian, German, French).
- The full video memory of cards with 4 GB or more is shown correctly.
- Samsung SSD attribute 183, USB drive error counts, "CPU + GPU" stress test and other diagnostics fixes — see the changelog.

Full list: [CHANGELOG](../CHANGELOG.md)

### Good to know
- The exe is **not code-signed yet**, so Windows SmartScreen may warn on first run: **More info → Run anyway**.
- This is a **pre-release**: the in-app update check ignores it until it is marked as final.
- This exe was **built on GitHub Actions** directly from the public source code (the commit is listed at the end of these notes).

SHA-256 (SMDWin.exe): <to be filled in at release>
