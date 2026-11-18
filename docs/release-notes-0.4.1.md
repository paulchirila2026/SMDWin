# SMD Win 0.4.1 Beta

Fixes from the first reports after the 0.4 release. Thank you to everyone who sent logs and screenshots.

**Download:** `SMDWin.exe` below — one portable file for Windows 10/11 x64, no installation, no .NET needed.

### What changed
- **Safe sensor start:** on some PCs the app closed right after the splash screen while loading the sensor libraries (a crash in a native library, or security software stopping an unsigned exe). The next start now opens without those libraries, explains why, and offers to try again.
- **AMD processors** show one clock per core (an 8-core Ryzen showed 18 "cores").
- **Graphics card:** a real card is shown instead of "Microsoft Basic Display Adapter" when Windows lists both.
- **Dashboard temperature charts:** a steady scale in 5 °C steps, the lowest and highest value written on the chart, and a taller chart.
- **Dialogs** follow the Light theme and dim the window behind them.
- **Windows Update check** no longer counts optional updates (it reported pending updates on an up-to-date PC).
- **Hardware database:** laptop processors such as i5-1035G1 / i7-1165G7 and GeForce MX cards are now recognised; new parts added.
- **Translations:** texts that appeared in English or Romanian in the wrong language (disk health, SMART verdict, Power history, Health check).

Full list: [CHANGELOG](../CHANGELOG.md)

### Good to know
- The exe is **not code-signed yet**, so Windows SmartScreen may warn on first run: **More info → Run anyway**.
- This exe was **built on GitHub Actions** directly from the public source code (the commit is listed at the end of these notes).

SHA-256 (SMDWin.exe): <to be filled in at release>
