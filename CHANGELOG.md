# Changelog

All notable changes to SMD Win are listed here, newest first.
Dates are in day.month.year format.

## [0.4.2 Beta] — November 2026

### Changed
- Temp & Stress › GPU test: works the whole card, closer to OCCT. The old test repeated one chain of calculations (part of the units waited for the previous result) and painted with one-colour fills, which modern cards do almost for free — on a GTX 1080 Ti it drew 191 W against 274 W in OCCT. Now each step runs 8 independent calculation chains with a read and a write in a large buffer spread over the video memory (64–512 MB), plus a full-screen draw that samples a texture, so the texture units, the pixel writers and the memory work too. The work per frame still starts small and grows only while frames are short, far from the 2 s after which Windows resets the driver.
- Temp & Stress › CPU test: the heavy vector work (AVX2) runs all the time; the lighter integer check now takes 2 s of every 30 s instead of every other 20 s, so the power and the temperature no longer drop in waves (34 W average against 40 W in OCCT on a Xeon E5-2680 v3). 12 parallel calculation chains instead of 8 keep all the units busy.
- Temp & Stress: the graphics card's power (W) has its own row in the sensor list, right after load, with its lowest and highest value (shown when the card reports it).
- Benchmark: the drawing shows the computer's real parts — the memory modules that are fitted (empty slots faint; 2 or 4 slots on a desktop, 2 on the others), the M.2 drive on the board, the 2.5" / 3.5" drives in their bays (up to 3 on a desktop, one 2.5" bay on a laptop, all-in-one or mini PC) and the optical drive when Windows sees one; the storage line points at the drive Windows runs from.
- Benchmark drawings, more realistic: the blowers of laptops, all-in-ones and mini PCs sit right under their fin stacks inside a faint housing, and the heat pipes go round the fan and run along the fins (in the all-in-one they used to end in the fan); 7-blade fans; square memory chips; a shorter, wider M.2 drive; screws, the Wi-Fi card with its antenna wires, the coin battery, speakers with their membranes, smaller laptop hinges, a recognisable power board in the all-in-one, the processor and graphics power plugs and SATA plugs on a desktop; the desktop feet sit under the case.
- Benchmark drawings: the air is drawn as a faint path with short glowing pulses — cool and straight going in, warm and slightly rippling going out — faster with the fans, and about 30 % brighter while the processor or graphics card works. The processor line no longer repeats the thread count shown under it.
- Storage › Overview: the drive cards fill the visible height. With one drive its activity chart grows into the free space; with two or more, the first two fit the screen without scrolling — a taller chart when there is room, otherwise a tighter card (smaller picture, the activity values on two columns, the peak in the chart's tooltip, a chart between 48 and 96 px). Further drives scroll.
- Benchmark: the test threads run below the window's priority — during the all-threads test on a 4-core laptop the window froze for up to 4.6 s. The score is the same (the threads still get all the free processor time). The processor name is shortened ("Intel Core i7-6700HQ" instead of "Intel(R) Core(TM) i7-6700HQ CPU @ 2.60GHz").
- Dashboard: a laptop graphics card that Windows switched off shows "Asleep" (its temperature is the last reading) instead of a frozen value read as live.
- Memory: after the memory test and the benchmark the freed memory is given back to Windows (before, .NET kept it reserved: 2.8 GB held with only 111 MB in use). The self-test lists the memory after each running tool.
- Dark background: only one alert state left — a faint red while the processor or graphics card is above 90 °C, fading back to blue as soon as it drops below (no amber state, no saved Health check result).
- Settings: Advanced is a card on the same page (the sensor read rate is now a compact 3 s / 1 s / 0.5 s choice); the Advanced and About links and the second page are gone (About opens from the logo in the sidebar). The "Data folder" and "Reports folder" rows are removed (both folders are next to SMDWin.exe), so all settings fit on one page. The sensor driver state uses the Windows mark.
- Storage › Surface scan: the progress and the read-speed chart are side by side with "Response time" at the top, and the sector map spans the whole width below, filling the rest of the page.
- Cards no longer light up under the mouse (Dashboard and everywhere else); list rows still do. The remaining coloured pills and dots (Applications, Windows Services, Space usage, Event Viewer, SMART attributes, the sensor banner) are Windows-style state marks with plain words; "Do not disable" is a small lock with neutral text instead of a red pill.
- Page action buttons are 8 px apart with more room inside. Storage: Disk Management and Diskpart are in one "Windows tools" menu button.
- Dark background: three subtle states — blue when all is well (a little stronger than before), a faint amber for warnings, a faint red when something is critical.
- Windows Services: "Quick switches" is one compact row (the explanations are in the tooltips); "Block Win version" is now "Pause feature updates".
- Applications: programs that declare no size get one measured from their folder (shown as an estimate, in the background, remembered between runs). Driver packages have their own "Drivers" filter, a common driver icon, their date under "Installed" and a clean version. No empty size bar for programs without a size; "possibly unwanted" is orange only when there are some.
- Space usage: "yesterday" instead of "1 days ago" (also in System Info and Power), "Open folder" and "Copy path" as icon buttons so the actions fit on one row, finer hatching on "safe to remove" blocks.
- Storage: the shared "Disk activity · All drives" card is gone from every tab, which frees space for the content. Surface scan shows its speed (now, average, min, max) above its own chart again. The last coloured status dots (RAM, Dashboard battery, SMART verdict, disk cards, power checks) are now Windows-style state marks, and the words are in the text colour unless they are a warning. RAM chart labels use sentence case.
- Storage › Overview: every disk card's activity chart has the same height, and each card is as tall as its content. The shared "Disk activity · All drives" chart is shown only on SMART, Surface scan and Benchmark, because on Overview each disk already has its own chart. The activity charts start at 1 minute instead of 5.
- Charts show only the horizontal grid lines by default; the vertical ones can be turned on with "Gridlines" in the chart menu. Storage partition and temperature bars are thin solid bars, as on the Dashboard. Temp & Stress: chart labels in sentence case ("Temperature · highest 73 °C"), values in the normal text colour unless they need attention, and Windows-style state marks.
- Settings: redesigned as one page. All categories (General, Appearance, Alerts, Widget & tray, Sensors, Data & reports) are visible at once, in compact cards on two columns, or one column on a narrow window. Advanced and About open on a separate page with a back button. The tab bar and the coloured icon in front of every row are gone; each card has one simple icon in its title.
- Charts: one grid style everywhere (Dashboard, Temp & Stress, Storage, Network, Benchmark, Power, battery and drive tests). There is no frame around the chart, only thin horizontal and vertical lines in a neutral colour that fade out towards their ends. The Storage "Disk activity" chart is taller and the empty space above and below it is gone; only cards that open something light up under the mouse.
- Dashboard: calmer look. Values are shown in the normal text colour; red is kept for errors and orange for warnings (Secure Boot off is now a warning, not an error). Partition bars are thin solid bars with "free of total" text. Chart labels use sentence case and consistent units (for example "2560 × 1440, 59 Hz"). The LIVE dot pulses gently, cards are highlighted under the mouse instead of showing arrows, and the background lights are about half as strong.
- A more Windows-like look, keeping the SMDWin colours and dials: states are shown the Windows way (a small tick / ! / i mark in a filled circle and the coloured word) instead of coloured pills — on the Dashboard, in System Info, PC health, the Storage and RAM checks and the labels of Applications, Services and Startup; the component icons no longer sit on tinted tiles; cards use the Windows 11 corner radius (8 px) and buttons and lists 4 px; the active page in the menu has a faint neutral background and the short accent bar on the left, as in Windows apps. The Dashboard charts look like Task Manager: a thin frame and a fine grid in the colour of the component.
- Benchmark drawing: the lines from the callouts to the parts no longer cross — the callouts follow the order in which the lines leave the drawing, and each line goes through a free gap (on the laptop the processor line passes above the chips, the graphics line under its chip). On the laptop the two memory modules sit side by side, as in most laptops, instead of stacked; the second cooler and the graphics chip are drawn when the laptop has a dedicated graphics card.
- Developer window: tidied up — five tabs (Status with the settings and the log below it, Self-Test, Design, Experimental, Databases); pages are built when first opened, so the window opens at once. Design keeps the choices still open (About animation, sidebar task indicator, battery style, processor picture) and shows the illustration gallery only on request, with still Benchmark drawings, so the page scrolls smoothly. Removed: the accent colour pickers, the old System Info layouts, the old power map, the PDF style choice, the monospace numbers toggle, and the old CPU / GPU Benchmark, System Diagnostic, Health Check and unfinished Manual entries (all replaced by pages of the app); the removed variants fall back to the defaults.
- System Info: while the hardware is read (5–10 s the first time), the page shows the drawing of this computer (laptop, all-in-one, mini PC or tower) with a magnifying glass passing over it, a progress bar and the list of the readings (processor and board, memory, storage, graphics, displays, network, audio, USB and Bluetooth, Windows), each ticked when it really finishes. A fast load shows nothing; a reading still running after 8 s says so.
- Benchmark drawing: the case has volume (a soft gradient and a still reflection on the glass / screen / cover), finer detail lines, memory chips on the modules and the M.2 drive, a grille on the power supply; the heat on the tested part is paler and subtler; small dots flow from the tested part to its callout and a small live chart follows its value (GHz, FPS, MB/s…); a finished part gets a short ring in the colour of its mark and then keeps a quieter tone of it.
- The "running" comet on the general mark dial (and on the other dials that show work in progress) goes round a full circle — it passes the gap at the bottom instead of jumping from the end straight back to the start.
- Storage → Benchmark: the chart under the dials shows the whole run — sequential write and read, then 4K read, 4K write, latency and the queue-depth tests each in their own section, with the disk speed while they run and their live value (IOPS, latency) on top; it used to stop at about half of the test. The section names under the chart show what is done (✓), what runs now and what follows. The chart is larger and grows with the window (the dials give it room); the 4K and latency steps update several times a second, also on slow hard disks.
- Storage checks on every drive card, as small chips (the explanation in the tooltip): the SATA link speed when it runs below what the drive supports (SATA II instead of III — an older port or a poor cable), the PCIe link of an NVMe SSD (generation and lanes, warned when fewer lanes than the SSD can use), the disk errors Windows logged for this drive in the last 30 days (bad blocks, controller errors, retried operations, file system errors — each mapped to its own drive), partitions not aligned to 4 KB, TRIM turned off, SMR hard disks, and Samsung 980 PRO / 990 PRO firmware with the known fast-wear bug.
- Storage → Benchmark → "More tests": Real capacity (fills the free space of a USB stick or memory card with known data and reads it back — finds a fake capacity; the test files are always deleted, the existing files stay), Sustained write (up to 80 GB / 4 minutes, shows how big the fast cache of an SSD is and its speed after it, with a chart) and Access time (300 random reads — the seek time of a hard disk, the sign of worn mechanics; read only).
- SMART: NVMe drives show how many times and how long they slowed down because of heat (thermal throttling); drives in USB cases with JMicron, ASMedia and Realtek bridges are probed for SMART with the matching method.
- RAM: XMP / EXPO is now read from the module part number (Kingston, G.Skill, Corsair, Crucial, Micron, Samsung, SK hynix, Patriot, ADATA…) — "Off — runs at 2666 of 3600" with what to do, or normal for a laptop. New chips under the configuration: mixed modules (size, speed or model), the last Windows Memory Diagnostic result, corrected memory errors (ECC) in the last 30 days, and whether the RAM is enough for what runs now (10 seconds of measuring how often Windows reads memory back from the disk, and how full the commit limit is).
- RAM test: the bandwidth result shows how much of the theoretical peak of the modules and channels it reached. The Advanced (full) scan adds a second pass on all processor threads at once — own-address, random data and moving inversions over the whole tested memory — which finds addressing faults and errors that show only under load and heat.
- New drive and memory drawings: hard disks drawn open (platter, hub, head arm, voice-coil magnet), SSDs with brushed aluminium and a real board (controller, flash, small parts), USB stick and external drives with their cable, memory modules with a dark board, chips with a sheen and a label sticker (DDR5 shows its power chip). The state shows as an LED or a coloured label band.
- Drive status: "Watch" is now "Needs attention".
- Tools → Clear temporary files: first shows the folders, their size and the largest items (with "only files older than 7 days", on by default, like Windows' Disk Cleanup), and deletes only after confirmation, in the app; SMD Win's own folder is never touched. Old Windows components: first asks Windows how much can be freed and whether a clean-up is recommended, and explains what happens (updates older than about 30 days can no longer be uninstalled).
- Settings: on a wide window the card lines up with the tabs on the left (it sat centered); the Close button and Language rows have their own icons.
- Self-test: the speed test runs to the end and the finished Benchmark is captured after its final count-up, so the pictures show real results.
- README: new screenshots (Benchmark, SMART, surface scan, Applications, Services, Tools).
- Benchmark: while the test runs, its button in the sidebar shows the running-test animation, like Storage and RAM.
- Benchmark: the drawing comes first — it is larger (a wider column, a smaller general-mark dial) and on a laptop screen the page fits without scrolling, before and after the test (the test descriptions under the bars step out into tooltips when the drawing would get less than about 380 px), the texts next to the drawing are larger on a small drawing, and the checks before the test follow the PC while the page is open (charger, processor load).
- Settings: the search box next to the tabs is gone — the search in the sidebar finds the options too and marks them on the Settings page; the note on Advanced is a light band instead of a dark one.
- Start cards: they only explain the test (what it does, how long, what it needs); Start stays the button at the top right, as everywhere else.
- Tools: History shows the last 3 entries, with "Show all" for the rest, so the page fits on a laptop screen.
- Applications: the program names line up on the left, and Uninstall in the details card is an outlined red button like the others.
- Settings, reorganized: eight tabs at the top (General, Appearance, Alerts, Widget & tray, Sensors, Data & reports, Advanced, About) with a search box next to them; one category is shown at a time. Every row has an icon; an option that differs from its default shows a small dot and a "Default" link that puts it back; a few options are marked "Recommended". Advanced starts with a short note, and About has the version, the update check and the links.
- Tests start from a large card: while there are no results, the empty area of Disk benchmark, Surface scan, Internet speed test and Ping monitor shows what the test does, how long it takes, what it needs and a big Start button (the small button at the top stays). When the test starts, the live view takes the same place.
- Applications: an "Updates" filter (the programs with a newer version, found with winget — update one from the right-click menu or all with one button; hidden when winget is not on the PC), an "Unused" filter (not started for more than 90 days, as recorded by Windows), and after an uninstall SMD Win looks for the folders and registry keys left behind and offers to remove them (folders to the Recycle Bin, keys saved first).
- Tools, grouped by what has to be done: SFC, DISM and the new "Old Windows components" clean-up run inside the page, with progress, time left and the result in plain words. New: Reset Windows Update, Reset print queue, a restore point in one click (with the date of the last one), driver back-up to a folder, BitLocker state and recovery key, Windows memory test and battery report. "Recent activity" became the History: everything run from Tools plus the service changes, each with Undo.
- Applications, Services, Startup: while nothing is selected, the card on the right shows a summary instead of one sentence — the largest, recently installed and possibly unwanted programs; the services that crashed or did not start in the last 7 days, the changes made from SMD Win (each with Undo) and the services of other makers; what Windows measured as slowing the start, suggestions (with a one-click clean-up of broken entries) and the entries added recently. A click on a row selects it in the list.
- Services: a new "Non-Microsoft" view (the services installed by other programs, with their maker), and every start-type change made from SMD Win is remembered so it can be undone.
- Startup Manager: a "Measured delay" column (how much later the start ended because of a program, measured by Windows) and a "New" badge on entries whose program appeared in the last 14 days.
- Applications, Services, Startup, Process Monitor: in a small window the less important columns step out instead of every column being cut ("Stoppe", "4.2 (", "Installe"), the details card gets narrower, and the program name no longer draws its badges over the next columns. The Startup switches are no longer squeezed, the quick-switch titles of Services wrap instead of being cut, and Tools shows 2 cards per row on a narrow page.
- Ctrl+F now also jumps to the search box on Services and Startup Manager.
- Benchmark: "Copy results" confirms on the button itself ("✓ Copied" for a moment).
- Benchmark drawing: it now follows the real readings — the fans turn with the processor and graphics load, the processor and graphics glow with their temperature (reddish when very hot), the disk LED blinks with the disk activity. A part that finishes turns from orange into the colour of its mark, a dot runs along its line and its ring fills while the number counts up; at the end every part pulses in turn and the general mark counts up. The mouse over a card lights up its part in the drawing, and the mouse over a part lights up its card.
- Benchmark: the graphics card is now tested before the storage, the same order as the drawing, the cards and the steps. The weakest card has a faint outline instead of a strong frame. The banner after a test shows the temperatures as text and everything else as the same soft pills, with a small Turbo switch. The drawing and the card rings animate with the screen refresh (the fans no longer jerk).
- Benchmark: before the test, a short check as tiles (power, Turbo, power plan, free space, processor load), each green when fine or amber with what to do. The Turbo tile is also its switch: a click turns Turbo on for the test and the tile turns green. After the test, the conditions it ran in are soft pills.
- Benchmark: a line drawing of this computer (desktop, laptop, all-in-one or mini PC) replaces the four big dials. Each component has a callout with its mark ring, hardware and value; the part being tested lights up while it runs (fans speed up, memory and disk activity blink), and after the test each part takes the colour of its mark. The general mark and the performance class sit next to the drawing; the component cards keep their values and tests, with a small mark ring in the header. Before the first run the page shows the same layout with Start in the top bar. The drawing grows with the window, each test shows what it measures under its bar, and the large cards no longer light up under the mouse. The warnings before a test are small chips on one row (the full advice in the tooltip), and the drawing shrinks to fit so the whole page fits on a laptop screen.

### Fixed
- Storage › SMART: on Samsung SSDs attribute 183 (0xB7) counts blocks that went bad in use ("Runtime Bad Block"), not SATA speed drops — the advice to check the cable was wrong. Health also counts the raw number of replaced blocks when the maker's value is still far from its threshold, and a drive with uncorrectable errors is never shown as healthy (a Samsung 870 EVO with 130 replaced blocks and 122 uncorrectable errors read 88 %).
- Dashboard: the storage card showed the health of the weakest drive under the name of the system drive (82 % of an HDD on a Samsung SSD card); it now shows the named drive, with all drives in the tooltip. A 99 % battery was shown in red. "Connected" on the Wi-Fi card was invisible in the Dark theme after a theme switch.
- Temp & Stress: the graphics card no longer reads "Idle" while its stress test runs.
- Texts left in English: the Network tool buttons (Start Test, Scan, Start…), Surface scan (Scanning…, Start Scan, Stopped), "Show all" links, "no volumes", the slowdown reason in the Health window. Long findings in the Health window wrap instead of being cut; the XMP / EXPO state on the RAM page wraps; better contrast for the iGPU row and the battery test percentage.
- Self-test: an idle graphics card keeping the same values is no longer reported as a stuck sensor.
- Temp & Stress: "CPU + GPU" could end up running only the CPU test. The GPU start waited in a queue behind the processor load, and the one-second check took it for a GPU test that had stopped by itself. The GPU test now starts right away (before the CPU load), and a GPU test that really stops by itself is written to the log with its reason.
- Storage: USB and other external drives no longer show "bad-block errors" from the Windows log as a critical problem. Windows numbers removable disks in the order they are connected, and removing a stick without "Eject" also leaves such entries, so these are now shown as a warning about read / write errors in the log.
- Dashboard: "Secure Boot: Disabled" was shown in green; it is now orange (a warning).
- Benchmark drawing: every memory module has its chips (on the all-in-one only one of the two had them); the mini PC modules sit side by side too.
- Benchmark drawing: on Dark the case of the computer was drawn as a white slab (the translucent card colour was mixed as an opaque one); it keeps the card transparency again.
- RAM: the XMP state was guessed from the speed Windows reports as "rated" — for XMP kits that is the standard speed, so XMP showed as on when it was off. It now comes from the part number (see above).
- Clean-up safety (Clear temporary files and Auto-Clean): only the two real Temp folders are cleaned (%LOCALAPPDATA%\Temp and %WINDIR%\Temp) — no longer the TEMP/TMP variables, which some PCs point at C:\ or a data folder. Links are never followed, files a pending restart still needs and System files are kept, folders are removed only when empty. Windows Update downloads are cleaned only when no update is being installed and no restart is pending, with the update services stopped meanwhile. The event logs are no longer offered (they are the history SMD Win diagnoses from). The Storage page's Auto Clean uses the same code. Uninstall leftovers never include anything inside the Windows folder.
- Surface scan: when the drive was unplugged or lost power, the scan raced through the rest in seconds, called the drive clean, and the drive list jumped to the first disk. Now the scan stops with "Drive disconnected" and shows only the part read, and the drive list does not change during a scan.
- Applications: programs installed long ago (Edge, Chrome, other browsers, VS Code) showed as "New" because their updates rewrite the install date. The date now comes from the program's folder when it is older; the details card shows "updated …" next to it.
- Startup Manager: entries from places Windows does not let a switch control (Active Setup, Winlogon, shell extensions) no longer have a switch that jumped back on (reported with Edge); Active Setup entries, which run once per new user and not at every start, are listed only in the advanced view. When a program turns its start back on by itself, SMD Win now says so — for Edge with the setting to change.
- Startup Manager: the boot time no longer counts the time the sign-in screen waited for the password (a PC left at the sign-in screen showed boots of several minutes); the wait is shown in the tooltip.
- Process Monitor: the first reading no longer shows 0.0 % and 0.0 GB at the top while the list has values, and a process without a signature reading shows its description instead of a dash.
- Benchmark: Start no longer ends in an error window after ticking "Turn on Turbo for the test".
- About: "Check for updates" asks GitHub whether a newer version exists and opens its download page (it only opened the project page before).
- Command Timeout (SMART 188): on drives that pack three counters into the value (Seagate), the count read through Windows is decoded correctly; it turns yellow only above 5 timeouts.

## [0.4.1 Beta] — November 2026

### Changed
- Windows Update check counts only the updates Windows would install, not the optional ones.
- Health check: the "Run missing tests" button fits its card; the "not tested" lines are fully translated; the top-bar button reads "PC health".
- Dashboard temperature charts: a steady scale in 5 °C steps that follows the readings, with both ends written on the chart; the chart is taller.
- Dialogs follow the Light theme; every dialog and modal window dims the window behind it.
- Translated texts left in English on Storage (SMART verdict), Power history and Processes.
- Firewall rule colours follow a theme change; clearer labels on the speed test in the Light theme.
- AMD processors show one clock per core (the "effective" and "average" clocks were listed as extra cores).
- A real graphics card is always shown instead of "Microsoft Basic Display Adapter" when Windows lists both.
- The battery drawing on System info is centred.
- Added Ryzen 7 5825U, WD PC SN5000S and Samsung M471A1G44CB0-CWE to the hardware database.
- Safe sensor start: if the app closes while loading the sensor libraries, the next start opens without them and explains why, with an option to try again.
- Laptop processors with a digit at the end of the name (i5-1035G1, i7-1165G7) and GeForce MX cards are now found in the hardware database.
- Disk health reasons and signature tooltips are translated in every language (they were in Romanian).
- A slow graphics card reading is logged once, then summarised every 10 minutes.

## [0.4 Beta] — November 2026

### Added
- Network › Apps: traffic per program now counts TCP and UDP over IPv4 and IPv6 (video calls, games, QUIC/HTTP3 were missing before), using Windows' own network events. Programs that only use UDP or IPv6 now appear too.
- Network › Apps: a "Site / service" column — the site name taken from the DNS answers and what the port is for (Web, DNS, Remote Desktop, VPN…) — plus Total and Protocol columns.
- Network › Apps: double-click opens the program's traffic: a 5-minute download/upload chart and every address it talked to, with site, service, country and bytes each way; exportable as CSV.
- Network › Apps › Export: the list of programs and their traffic as CSV.
- Network alerts (off by default, Network › Apps › Alerts): a program uses the internet for the first time on this PC; a program moves more than a chosen amount of data in a minute; contact with a known malware control server (abuse.ch Feodo Tracker list, downloaded only when this alert is on). Alerts appear in the notification centre.
- Process Monitor and Process Analyzer use the same per-program network counts.
- Network › Apps: the country of each remote address, from an offline database included in the program (IP to country data by DB-IP, CC BY 4.0) — nothing is sent anywhere.
- Tools › USB monitor: a live log of USB devices connecting and disconnecting, reconnections since Windows started, and the two power-saving settings that most often make a mouse drop out, each with a one-click fix.
- Screenshot button (camera icon next to Health): saves a picture of the program window only to Data\Screenshots and copies it to the clipboard; right click opens the folder.
- Dark theme: the soft background light slowly turns red while a temperature stays over its limit or the health check finds a critical problem, and fades back just as slowly; it can be switched off in Settings.
- New logo: a gauge with a pulse line, used for the program icon, sidebar, splash, About and the tray. A simpler drawing is used at small sizes.
- About window: the logo's segments light up one by one and the pulse line is drawn, then a small light runs along it every few seconds (when animations are on).
- Splash screen: the logo fills smoothly with the real start-up progress and gives one short pulse when the program is ready.
- Sidebar logo: lit up to orange at rest, rising to red while a test, benchmark or scan is running.
- Taskbar button: shows a moving progress band while a test is running.
- Tray icon: a small live gauge showing how busy the PC is (the highest of processor load, memory use and processor temperature relative to its limit).
- Loading messages and Network › Diagnostics checks use the logo as a spinner.
- Self-test: the result file now also holds screenshots on Light and Dark — every page with its tabs (Storage, Settings), every Network tool and the separate windows (Health, power history, battery test, benchmark, clean-up, notifications, manual, features, licenses, About, sensor driver). Only SMD Win's own windows are captured.
- Turbo Boost: every change (Turbo button, Processor boost mode) asks whether it should last only while SMD Win is open or stay permanently.
- The Turbo button reads the real state from Windows at start.
- Temporary Turbo changes are restored when SMD Win closes, or at the next start if it did not close normally.
- Turbo indicator: the Turbo button and the Power page show whether the current setting lasts until SMD Win closes or stays permanently.
- The processor range slider on the Power page asks the same question as Turbo (only while open / permanent) and shows when a change is temporary.
- Paused processes are resumed automatically when SMD Win closes, or at the next start if it did not close normally.
- Stress tests stop automatically when the processor or graphics card stays more than 10 °C above its limit from Settings.

### Changed
- First start: a one-time notice (beta, no warranty, what SMD Win can change and how to undo it, stress tests heat the hardware, logs contain hardware details).
- Driver search opens a Google search by default (driverpack.io is still available in Settings).
- README: screenshots, the Benchmark page, a complete list of internet connections, known limitations; the bug report template warns that logs contain serial numbers and network details.
- Benchmark: the start page shows the memory (size, type, modules) and no longer turns dark on hover; a warning and a clear note when there is not enough free space for the storage test; the graphics memory test frees the other tests' resources first.
- Status colours (green / orange / red texts) follow a theme change everywhere; a WMI timeout is no longer counted as an unsigned driver.
- Benchmark: the graphics compute test can no longer be shortened by the driver (Intel showed impossible values); Turbo off is detected, with an option to turn it on for the test; recalibrated compression and memory latency marks; advice for a single memory module; the memory card shows size, type and modules.
- Faster: the Network page and Tools page no longer block when they open; the app gives memory back after the memory test and the benchmark; it no longer checks its own signature (10 s).
- Fixes: base clock of Intel processors in System Info, "1 × 8 GB" for a single module, the uptime message names the real cause, "Turbo off" on Temp & Stress, power button choices no longer cut, longer disk names, "idle" instead of "—".
- Benchmark: warnings before the test (on battery, Power saver plan, busy processor); choice of graphics card on laptops with two (the dedicated one by default).
- Benchmark: new tests — processor compression, storage write; "keeps X% of its speed under load"; a short advice per component; values that look wrong are marked "?" and not counted; tooltips on the class names; Esc stops the test.
- Benchmark: a start card before the first run (components of this PC, their tests, Start); no numbers under the class bar; smaller dials with the main value under them.
- Benchmark: processor test fixed (the all-cores result was inflated); graphics memory now also measured with a read test; marks recalibrated.
- Fixed build errors on the Benchmark page (CS0102, CS0104, CS0664, CS1061).
- Benchmark: fixed the processor and graphics memory measurements; new performance class bar (Entry level … Workstation) with "suitable for"; animated scenes per component (processor engraving read from the real model, HDD / SATA SSD / M.2, dedicated or integrated graphics); Copy results; drive selector; thermal slowdown note.
- Storage: disk cards one under another at full width; interface, serial and capacity moved to the top of the card.
- Power: "What keeps the PC awake" uses three tabs and a full-width list; start-up rows show only the title and the switch, details on hover.
- The build number keeps counting when the project is replaced by a new copy, and one publish counts once.
- System Info › Protection: disabled protections are shown by colour and ✕ only, without strikethrough.
- Benchmark: every run is written in full to the log and to a file (the last 10 runs) — raw values, how each mark was computed, the graphics test details and the temperatures / clocks second by second — and the self-test zip includes them. The long self-test now also runs the Benchmark page.
- New Benchmark page (under Temp & Stress): marks from 0 to 10 for the processor, memory, storage and graphics card, and a general mark (their mean) with what the PC is good for. While it runs, an animated banner shows what is being tested, with the total progress.
- Dashboard: on Intel integrated graphics, which do not report their frequency, the graphics card shows its power draw instead of "—".
- Fewer cut-off texts on small screens: the speed test chart stays inside its card on 768 px screens, the frequency under load in Temp & Stress is shorter, wider traffic columns in Network › Apps, the Traceroute / Ports hints wrap, shorter uptime advice in the Health window.
- Drivers: no more "?" chip for drivers without a date. Network: opening the page no longer freezes the window while the Wi-Fi details are read.
- Self-test: a choice between short and long. The long one also starts every tool for about 10 seconds (speed test, ping, traceroute, DNS, ports, LAN, Wi-Fi Live, surface scan, RAM test, CPU + GPU stress) and captures it while it runs and after.
- Wi-Fi › Saved networks: read through the Windows Wi-Fi interface, so the list works on any Windows language (it stayed empty on non-English Windows); the security type has clear names (WPA2-Personal, WPA3-Personal…).
- RAM: memory soldered on the board (LPDDR) is shown as chips on the board with its channels, not as slots and sticks; the speed shows the configured value (the rated one in the tooltip) and no longer gets cut off.
- Storage: every disk card has its own activity chart (read / write), with the response time, how busy the disk is and the peak, over the same time window as the "All drives" chart. From two disks up the cards sit two per row.
- Translations: the System Health window and its report, every health-check result, Storage / SMART, Surface scan, Drivers, Processes, Services, Speed test, Ping, Power history, the Manual tabs and the crash legend now follow the app language (Romanian, German, French).
- Power: every dial looks like the Dashboard dials; the power button and lid choices always fit; no empty space under the plans; on batteries that report no power, the time left comes from Windows and the power is worked out from the charge drop; tighter layout on short screens.
- System Info: performance + efficiency cores on hybrid processors, base / turbo frequency, a cleaner battery with the charge next to it, protections that are on or off are clearly different (not only by colour), the storage total no longer counts USB sticks, the battery shows only its health.
- AMD processors with Radeon graphics: the graphics temperature is shown; no more sensor restarts at start-up when the sensor driver is not installed (they kept the processor busy).
- Processor core voltages under 0.3 V are not shown as readings. The self-test ends with a clear message and opens the folder.
- Dashboard: the network dial shows its unit (KB/s, MB/s) next to the figure on every screen size; the drive's health cell shows only the figure.
- Network › Wi-Fi: the Live tab no longer freezes the window while Windows waits for the location question; the tab is translated.
- Power: the recent starts and stops no longer push the page beyond the screen; small text fixes.
- Storage: the drive card is centred again (anchoring it at the top left a gap in the middle).
- Power page, after the first test: "Power use now" has a larger chart and keeps a decimal for small monthly amounts; Battery saver explains what it does; Battery health shows all its details on any screen; start-up and shut-down settings take one line each, so the page fits a 1080p screen; every power button and lid choice has its label; 1080p screens also show the recent starts and stops.
- Storage: the drive card starts at the top; the dial shows the life left reported by the drive (the "health" figure next to it is gone); healthy drives no longer show a green dot in the picture.
- The main window no longer flashes black when it opens: it appears with a short fade.
- Power page redesigned. A laptop now has two tabs, Battery and Power & sleep; a desktop PC keeps everything on one page:
  - The power plan is shown on a dial made of blocks, with a needle. On a laptop, the plan and the processor mode can be different when plugged in and on battery; SMD Win switches the plan when the charger is plugged in or out, while it runs.
  - "When the PC is not used": each time has its own slider, with separate columns for plugged in and on battery on a laptop, plus "Then hibernate", the power button and the lid.
  - What keeps the PC awake: the apps that stop sleep, what wakes the PC and the devices that can wake it, shown as three lists side by side.
  - Battery tab: the charge ring, the main figures, battery health, a battery saver threshold (with lower brightness on/off), the apps using the battery and the power used now.
  - Power use now: on battery it is the laptop's own reading; on a desktop the processor and graphics card are measured and the rest is estimated, with a monthly estimate in kWh and cost.
  - Large screens also show the recent starts and stops and open the advanced processor settings directly in the plan card.
  - "Reset to defaults" returns to the Balanced plan, the Normal processor mode and the recommended times. It asks first.
  - The Battery test button is hidden until the test is finished.
- New Power page, meant to fit on one screen and be changed in place:
  - "When the PC is not used": screen off, sleep, hard disks, power button and (laptop) the lid, each changed with one click; a laptop shows plugged in and on battery side by side.
  - The processor in three steps: Cool & quiet, Normal, Full speed. The detailed controls are under "Advanced settings".
  - A hidden "Ultimate Performance" plan can be added on desktops.
  - "Sleep" is one row of labels: what stops sleep, what wakes the PC, which devices can wake it, and the last wake-up. A label opens its list with actions: ignore an app or driver, close an app, stop a scheduled task from waking the PC, allow or block wake-ups and devices.
  - "Start-up and shut-down": Fast Startup (with the days since the last full restart and a Restart button), Hibernate (with the space it uses), the sleep type and a reset of the power plans.
  - "Keep awake" keeps the PC and the screen on for a while (30 min to "until turned off", or while a test runs) without changing the plan.
  - "Power check" lists what wastes power or stops sleep, each item with its fix; the full Windows report is still one click away.
- Disks show their exact type and form, read from the disk itself: "M.2 SATA SSD", "M.2 NVMe SSD", "2.5" SATA SSD", "3.5" HDD · 7200 rpm". This appears on the Storage page, in System Info, on the PDF sheet and in the self-test.
- USB disks and sticks show their USB link ("USB 3.0 · 5 Gb/s"), read from the USB hub. A USB 3 device running at USB 2 speed is flagged (USB 2 port, cable or old hub).
- External disks: when the enclosure lets it, the disk inside is read (HDD or SSD, form, rpm).
- Every published build now has a number that grows by one ("Build 57"). It is shown in About, Settings, the splash, the PDF report, the first line of the log and the self-test report and zip name, so a log tells which build it came from. The "v0.4 Beta" label is no longer shown.
- New splash screen: the logo fills up as the app loads, then its pulse is drawn and beats once; the empty row at the bottom is gone.
- Dialog windows have a fine border and a visible shadow, so they stand out from the page on the Light theme.
- The red background on Dark no longer stays after an old stress test reached 95 °C; it shows while the temperature is high right now.
- System Info no longer shows a "Nothing needs attention" box when there is nothing to check.
- The temperature check in the self-test reads the sensors again if the first reading is empty, and says the real reason when no temperature can be read.
- Uptime now counts from when the PC was switched on. With Fast Startup, Windows' own counter keeps running across shut-downs and showed weeks right after power-on.
- The red background on Dark appears only for physical danger (overheating, failing disk, RAM or stress-test errors, repeated blue screens, worn-out battery). Items such as activation, updates or a long time without restart stay in the Health check list only.
- Health check: battery health is orange under 70 % and red under 40 %; disk health red under 50 %; Windows not activated, missing drivers, network driver errors and a long time without restart are warnings; a fixed clock or charger throttling is a warning, only overheating is critical.
- The welcome window that offers the sensor driver now looks like About: the live logo with its animation, the name, three chips with what the driver brings and one line about PawnIO. It follows the Light/Dark theme.
- Dark background lights: wider and softer (about 30 % larger, 80 % of the strength), a wash over the page instead of a patch in the corner; the same for the red tint.
- Network › Apps: the columns fit — current upload and download share one column, the local address moved into the Remote tooltip, states are short words ("Connected", "Listening"), countries without the code; every cut-off text shows in full as a tooltip; short-lived programs are shown as "(ended)".
- Taskbar and tray icon: the ring and pulse are 30 % thicker, so the icon no longer looks thin next to other apps (the logo inside the program is unchanged).
- About window: five logo animations to choose from in the developer window (Design): Heartbeat, Rev, Monitor, Glint, Live gauge.
- About window: the pulse line now beats like a heart (a strong beat, a smaller one, a rest) instead of a dot running along it.
- Tray icon: the unlit segments follow the taskbar's light/dark colour, so they no longer look black on a dark taskbar.
- Network › Firewall: the Allow / Block state is a flat coloured chip (dot and word) instead of an outlined button; the tooltip says what a click does and whether the rule is turned off.
- Sidebar: the selected page is a flat tinted pill (no glass, shadow or outline) with a blue icon and bold text; the other pages are in a secondary grey, with a very faint grey on hover.
- Windows location prompt: SMD Win no longer asks for location at start. The Dashboard and tray read the Wi-Fi name from the network profile and the signal strength without it; only Network › Wi-Fi › Live may ask, with a note explaining why (Windows treats Wi-Fi details as location data) and a link to the setting.
- Taskbar button: the surface scan shows its real progress (red when bad sectors are found).
- Windows version, edition, build, architecture and install date are read from the registry instead of WMI: instant, and in English on every Windows language.
- The disk list is read while the splash screen is showing, so the Dashboard and Storage open faster.
- Older processors that report the temperature in whole degrees: the frozen-sensor checks are more tolerant, so the sensors are no longer restarted needlessly.
- Light theme: darker colours for the Features headers and Power History events, and visible hour lines and labels.
- The line under "Win" in the program name and the line under the System Information header were removed.
- Light theme: a softer blue-grey page, two very faint blue-grey lights in the corners, and lighter blue-grey card shadows.
- Sidebar: the selected page on Light is a pale blue pill with a thin line instead of a white pill with a blurred shadow.
- The program name is drawn the same way everywhere (sidebar, splash, About, Features): "SMD" light, "Win" bold.
- Self-test: the interface checks give fewer false alarms (table data, short words common to several languages, a few pixels of measuring noise).
- Self-test: a progress card with the current step, percentage and time left. The result file now opens with report.html: interface problems found on every screenshot (cut-off or hard-to-read text, overlaps, untranslated texts, empty values), page times, memory and resources across two tours, ignored errors, readings compared between two sources and sensors watched for 20 s.
- Licenses window: refreshed look; the SMD Win license text now matches GPL-3.0.
- Traceroute, Ports and DNS show a short hint instead of an empty card before the first run; Space usage keeps its recommendations while it refreshes them.
- Start-up no longer waits for the sensor driver question: the welcome window appears after the app has loaded and the app keeps working behind it.
- Processor clock: impossible readings (for example 9–15 GHz on some Ryzen 7020 laptops) are detected and replaced with the clock Windows reports.
- Soldered LPDDR memory is recognised as LPDDR (not DDR5), drawn as chips on the board, with no XMP/EXPO estimate; laptop modules are drawn with real SO-DIMM proportions.
- More texts translated (RAM, Storage, Temp & Stress, Event Viewer, Power) and dates use the interface language.
- First start: a new welcome window offers the sensor driver in plain words, installs it in place and confirms when sensors are active.
- System Information: on a low window (laptop) the rows get tighter and the header becomes a one-line "To check" strip, so everything fits without scrolling.
- Dashboard charts show the last minute by default; the time range (1 min – 1 h) is in the chart's right-click menu. Temp & Stress keeps 5 minutes.
- New About window: the logo in a slowly turning gradient ring, version · build · license at a glance, links to GitHub, licenses and updates.
- Buttons and input fields have softer corners (8 px), in line with the menu and the cards.
- Light theme: a slightly deeper page with a soft white glow behind the title, so the cards stand out.
- Menu: the selected page is a gently raised pill (white on Light, glass on Dark); Network moved under Hardware, the System group is now Software, Settings sits at the bottom; labels fade in when the menu expands; a quiet neutral hover.
- Temp & Stress: while a test runs, a progress line and the time left under the temperature dial.
- Dial arcs move to a new value in a steady sweep; the figure inside changes directly; animations are on by default.
- Charts: a soft pulse at the end of the line, and a lighter fill on Dark; dial figures turn orange / red smoothly when they pass a limit.
- Dials fill up from zero when the app opens.
- Storage › Overview: the drive cards use the full page height, with larger text and pictures.
- New look, step 3: System Information, Space usage and benchmark cards follow the new style; the selected page in the sidebar is a blue pill; dial figures roll to their new value and cards fade in one after another when a page opens (both follow the animations setting).
- New look, step 2: each component has its own colour (icon tile on the Dashboard, temperature line), dials fade from blue to violet along the arc, charts fill with a soft gradient, larger figures in the dials.
- New look, step 1: deeper Dark theme with soft coloured lights behind the pages, glass cards on Dark and clean white cards on Light, rounder corners, coloured status pills on the Dashboard.
- New icon set: Fluent UI System Icons (the icons of Windows 11) across the whole app — pages, buttons, dialogs, System Information and network tools.
- An unexpected error no longer closes SMD Win right away: it shows where the log is and offers to keep working; repeated errors still close it cleanly.
- Dialog and notification messages are translated (English, Romanian, German, French).
- Power page: the Plugged in / On battery choice uses the app's standard selector (sharp text), and the page is slightly more compact so it fits without scrolling on Full HD laptops.
- Log messages are now in English.
- Unused code removed (about 230 methods, including the old needle dial of the storage benchmark).
- Build warnings cleaned up (possible missing values, unused variables and leftovers).
- Project prepared for public release: single changelog, English build scripts and code comments, project renamed to `SMDWin.csproj`, README and third-party notices added.
- SMD Win is now free and open source under the GNU GPL v3 (or later); the About window shows the new license.

### Fixed
- Lower CPU and GPU use while the window is open: all animations run at most at 30 frames per second, and the slow decorative pulses (chart end points, card glow) at 15.
- Sidebar: the collapse button lines up with the menu icons.
- High CPU use: the GPU-per-process reading re-read Windows' whole GPU counter list once per process every 2 seconds; it now reads it once. The Dashboard and Stress cards no longer collect GPU and network data per process, and the window check no longer scans every window once per process.
- Dashboard: the Wi-Fi network name is read again (it failed on Windows 11).
- Storage: Seagate laptop and desktop HDDs (e.g. ST500LM021) were not recognised by name; when Windows cannot tell the disk type, smartctl's rotation rate and the SMART table decide, and an unknown disk is no longer drawn as an SSD.
- SMART: values that some makers pack with extra counters (Seagate power-on hours, command timeouts) now show the real number instead of a huge one.
- A USB stick on a system where Windows' storage information is unavailable is shown as a USB stick, not an SSD.
- Network › Apps: the program name column had shrunk to nothing on a 1680 px screen; the Blocked column (shown by the Firewall button anyway) was removed to make room, and totals now show KB / MB / GB instead of "0.0 MB".
- Desktops that hold their all-core turbo (e.g. a steady 4.0–4.1 GHz): the frequency sensor is no longer restarted because the clock does not change.
- Missing Romanian, German and French texts for the Network tabs and the USB monitor.
- A 3.5-second freeze right after the window appeared (Windows update, TPM and licence details were read a second time on the interface thread).
- Network › Apps: Send / Recv always showed "—" (the wrong TCP statistics were requested); the page also kept refreshing in the background after leaving it.
- The CPU clock from Windows counters is now the real clock (nominal × % Processor Performance) instead of the fixed nominal value.
- The sensors are no longer rebuilt when the CPU simply stays on its turbo step under varying load.
- Every WMI query now has a time limit, so a broken WMI on a client's PC cannot freeze a card.
- Process Monitor reads each process's path directly from Windows (much faster for explorer and similar processes).
- The computer name could be empty in the system summary.
- Process Monitor: memory is now the private working set (the same value as Task Manager; before, shared memory was counted too and values were about twice as high).
- Process Monitor: the Apps tab adds each app's helper processes to its row (Edge's many msedge processes, a program's child processes), like Task Manager.
- Process Monitor: the Network column and card were always empty; they now show TCP traffic per process and the real traffic of the network adapters.
- Process Monitor: the Disk card shows the real disk throughput; the per-process column is renamed I/O, because it also counts network and pipe transfers.
- Process Monitor: CPU uses the same measure as Task Manager (time weighted by the real clock speed).
- Process Analyzer: CPU and I/O were read by process name, so with several processes of the same name they could belong to another one; everything is now read by PID. Network showed the whole PC's traffic; it now shows only this process.
- The sidebar search box kept its highlighted border; a click outside it or Esc now releases it.
- Primary buttons on Dark (Start test, Start Scan, Short self-test…) had white text on a light blue at 2.9:1 contrast; they now use the base blue (about 4.7:1).
- Menu group titles (MAIN, HARDWARE…) on Dark were too faint.
- Applications: the Version column cut long version numbers; the SMART "What it means" column now wraps instead of cutting the text.
- When the sensor driver is installed but the processor sensors do not answer, SMD Win now says so (Settings, sidebar, install window) instead of "active", and shows "—" rather than an approximate temperature that has not been seen to change.
- Laptops without the sensor driver: a firmware temperature that never moves (e.g. a fixed 24 °C) is no longer shown as real, and the processor clock no longer stays frozen on the value read during a stress test.
- Network page: opening it (and the firewall list) no longer freezes the window for several seconds — long lists now draw only the visible rows.
- Light theme: status colours used as text (Allow, Review, Block…) are darkened so they stay readable; sidebar group titles have more contrast.
- Translations: Network tools and their tabs, the sidebar search hint and the "Health" badge in the title bar now follow the chosen language.
- Sensor driver window, Health cards, SMART headers and System info status pills no longer cut or overlap their texts in Romanian.
- Opening Network after a theme change could freeze the app for several seconds.
- Dashboard: the figures inside the dials could stay dark (nearly invisible on Dark) after a value crossed a limit.
- Disabled blue buttons (Clean up, Clean Now) no longer turn blank on Light; the Block buttons in Network › Apps follow the theme.
- Port scanner shows Scan (not Stop) when idle; the LAN scan no longer restarts on its own; Wi-Fi button text is no longer cut.
- Health check: the card headers no longer overlap on narrow cards; Temp & Stress says "Not reported" for a GPU clock that is never read.
- About: five clicks on the version, the build or the author open the developer panel again.
- Dark theme: soft background lights with no visible steps on any monitor, the same every time.
- Switching from Light back to Dark no longer paints the menu, the title bar buttons and the pages over the background.
- Dark and Light: the desktop wallpaper no longer tints the window, so the app looks the same on every monitor.
- Dashboard dials: the unlit part of the dial is visible again on Dark (e.g. on the Storage card at low activity).
- Space usage: the drive selector showed a small box over the drive name; the scan now shows a progress bar in the map card.
- Internet speed test chart starts at 100 Mbps; storage benchmark dials and chart start at 200 MB/s with round grid values.
- System Information: a narrow partition shows only its free space instead of a cut-off label; the benchmark rating no longer shows an empty box.
- Blurry text on selected buttons and tabs (Event Viewer, Drivers, Apps, Storage, Settings, Process Analyzer) and in several dialogs.
- Storage benchmark: pressing Stop restarted the test instead of stopping it.
- The bundled smartctl is checked against the copy inside SMDWin.exe and locked while the app runs.
- External drives were labelled "(extern)" instead of "(external)".
- Sharper text on cards, right-click menus, tooltips, drop-down lists, dialogs and the floating widget (shadows no longer blur the text).
- RAM page: the Configuration and Diagnostics cards no longer leave an empty band under the title.
- Drivers: the Problems / Not installed / Important filters have their own icons; the System Information PDF uses the same icons as the app.

## [0.3 Beta] — October 2026

### Added
- Illustration library: desktops, laptops, all-in-ones, mini PCs, motherboards, drives, memory modules, displays and peripherals drawn from the detected hardware.
- System Information page: the whole machine on one screen (processor, memory, storage, graphics, displays, network, peripherals, Windows), with exportable PDF.
- Network page with built-in tools: diagnosis, Wi-Fi with router view, ping monitor, traceroute, DNS, port scanner, LAN scanner, LAN speed, speed test with several connections, firewall rules and network apps.
- Settings in two tabs (Settings and Advanced), with an optional computer-service mode that puts the workshop name on reports.
- One `Data` folder next to the exe for settings, logs, reports and history (portable, nothing written to the Desktop).
- Hardware database for processors, graphics cards, memory modules and drives, plus a report for hardware it does not recognise yet.
- Windows event explanations (199 common events) in English and Romanian.
- Stress test bar: what to test, intensity (Standard or Long), stop after, start.

### Changed
- Dashboard redesigned: one row per component with current value, short history, details and status.
- Storage: Overview with space, health, SMART and temperature; Space usage tab; redesigned benchmark with live dials.
- Power page: plans as cards, sleep and wake timeline, battery health and history.
- RAM page: memory slots drawn as they are on the board, including empty slots.
- Single choice-button style and a fixed blue data colour across the app; contrast checked in Light and Dark.
- Faster start: language packs and tools are extracted on demand instead of unpacking the whole runtime.

### Fixed
- Freeze during the large-files scan.
- False throttling warnings on some Ryzen systems.
- Interface freeze with the maximum stress mode (mode removed; Standard and Long remain).

## [0.2 Beta] — 26.09.2026 – 03.10.2026

Version numbering restarted at 0.2 to mark the complete interface redesign.

### Added
- Background recording of sensors, with charts and markers for stress tests.
- Throttling detection and BSOD analysis with plain-language advice.
- Notification centre, Event Viewer redesign, installed apps with categories.
- Self-test that visits every page and writes a report.
- Splash screen showing the real start-up steps.

### Changed
- Windows 11 style (Mica, rounded cards, Fluent controls) applied page by page.
- Compact widget with three styles (Gauges, Pill, Minimal).
- English, Romanian, German and French throughout the interface.

## [0.9.x] — August – September 2026

### Added
- Storage health with smartctl as the main SMART source, SMART history, NVMe diagnostics and surface scan.
- Processor and graphics stress tests with a second workload each, and Max Load (processor, graphics, memory and disk together).
- Fan speed readings, graphics memory temperature, live battery power.
- Driver backup, Windows Update and driver-update blocking with restore scripts.
- Optimize and Auto-Clean with saved original values and revert.
- Battery report viewer independent of the Windows language.

### Fixed
- Stale sensor values shown as current; sensors validated before display.
- Orderly shutdown: sensor driver closed and stress tests stopped on exit.
- Processor temperature freezes, graphics card detection on systems with integrated and dedicated graphics.
- Several handle leaks and slow pages (Startup, Drivers).

## [0.8.x and earlier] — until August 2026

- First versions: live monitoring of processor, graphics, memory, disk and network; temperatures through LibreHardwareMonitor; floating widgets; process monitor; tray icon; PDF and HTML reports; light and dark themes.
