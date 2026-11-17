# EventDB – sources

Reference material used to write and check `events.json` (199 entries).
Message texts were matched against Microsoft documentation where it exists, and otherwise against
well-known, repeated Microsoft Q&A / support threads quoting the real event text.

## Microsoft Learn / Microsoft Support

- Kernel-Power 41 (BugcheckCode 0 vs non-zero): https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart
- Kernel-Power 41 (TechNet wiki): https://learn.microsoft.com/en-us/archive/technet-wiki/14246.kernel-power-event-id-41
- Kernel-Power 137 (MTRR change across S4): https://learn.microsoft.com/en-us/answers/questions/4040469/event-id-137-kernel-power
- Storport / storahci / stornvme reset event 129: https://learn.microsoft.com/en-us/archive/blogs/ntdebugging/understanding-storage-timeouts-and-event-129-errors
- Disk event 153: https://learn.microsoft.com/en-us/archive/blogs/ntdebugging/interpreting-event-153-errors
- NTFS / disk corruption (Ntfs 55, 98, disk 7/11/51): https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/troubleshoot-data-corruption-and-disk-errors
- NTFS health model (event 98, online/offline chkdsk): https://learn.microsoft.com/en-us/archive/blogs/b8/redesigning-chkdsk-and-the-new-ntfs-health-model
- DCOM 10016 is expected and can be ignored: https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/event-10016-logged-when-accessing-dcom
- Secure Boot DB/DBX update events (TPM-WMI 1795/1796/1801/1808...): https://support.microsoft.com/en-us/topic/secure-boot-db-and-dbx-variable-update-events-37e47cf8-608b-4a87-8175-bdead630eb69
- Secure Boot certificate updates (2026 expiry): https://support.microsoft.com/en-us/topic/secure-boot-certificate-updates-guidance-for-it-professionals-and-organizations-e2b43f9f-b424-42df-bc6a-8476db65ab2f
- Windows Update event 20: https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-R2-and-2008/cc735642(v=ws.10)
- Hypervisor event 41: https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-R2-and-2008/dd582286(v=ws.10)
- DHCP Client events (Windows Server 2008 R2 event reference): https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-r2-and-2008/dd380209(v=ws.10)
- Microsoft Defender Antivirus event IDs: https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-microsoft-defender-antivirus
- Defender security intelligence update troubleshooting: https://learn.microsoft.com/en-us/microsoft-365/security/defender-endpoint/security-intelligence-update-tshoot
- Bluetooth BTHUSB 17 after resume: https://support.microsoft.com/en-us/help/982046
- Security auditing events (4624/4625/4720/4740): https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/basic-audit-logon-events

## Microsoft Q&A threads used to confirm message text

- WHEA-Logger 46/47 (memory): https://learn.microsoft.com/en-us/answers/questions/4144653/getting-event-47-whea-logger-and-event-46-whea-log
- WHEA-Logger 46 (memory machine check): https://learn.microsoft.com/en-us/answers/questions/1309174/how-to-fix-whea-logger-event-46-error-memory-machi
- WLAN-AutoConfig 4001 / 10002: https://learn.microsoft.com/en-us/answers/questions/2738492/wlan-autoconfig-critical-errors-4001-and-10002
- WLAN-AutoConfig 4003 (limited connectivity): https://learn.microsoft.com/en-us/answers/questions/3782217/warning-(error)-4003-wifi-automatically-disconnect
- Dhcp-Client 1001: https://learn.microsoft.com/en-us/answers/questions/2647826/eventid-1001-dhcp-your-computer-was-not-assigned-a
- Dhcp-Client 1002: https://learn.microsoft.com/en-us/answers/questions/2830503/dhcp-client-ip-lease-denied-event-id-1002
- Windows Memory Diagnostic 1101/1201 vs 1102/1202: https://learn.microsoft.com/en-us/answers/questions/4143975/memory-diagnostics-error-1202-and-1102
- Windows Update event 31: https://learn.microsoft.com/en-us/answers/questions/1148533/windows-update-failed-to-download-an-update
- Defender 5007 frequency: https://learn.microsoft.com/en-us/answers/questions/3969005/is-alot-of-event-id-5007s-normal-for-defender
- BTHUSB 17: https://learn.microsoft.com/en-us/answers/questions/4028251/bthusb-eventid-17-error

## Other well-known references

- Geoff Chappell, Kernel-Power event provider (message texts): https://www.geoffchappell.com/studies/windows/km/ntoskrnl/events/microsoft-windows-kernel-power.htm
- Kernel-EventTracing 3 / ReadyBoot 0xC0000188: https://mskb.pkisolutions.com/kb/2001347
- Security-SPP 1014: https://www.thewindowsclub.com/acquisition-of-end-user-license-failed-event-id-1014
- Kernel-Power 86 (critical thermal shutdown): https://answers.microsoft.com/en-us/surface/forum/all/event-86-kernel-power-randomly-shuts-down-surface/218c6772-1641-47f6-a39b-6e21ce91dac9
