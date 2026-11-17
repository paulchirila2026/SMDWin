using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SMDWin.Services
{
    /// <summary>
    /// Display-time translation for texts generated in code (Health, Event Viewer): exact match, then templates with numbers
    /// / names (e.g. "{n} update(s) pending"). Romanian only; otherwise the English stays. Idempotent: an already translated
    /// text no longer matches anything.
    /// </summary>
    public static class Tx
    {
        private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
        {
            // ── Health: titluri
            ["System Health Check"] = "Verificarea stării sistemului",
            ["System Health Check — SMD Win"] = "Verificarea stării sistemului — SMD Win",
            ["Windows activation"] = "Activarea Windows", ["Secure Boot & TPM"] = "Secure Boot și TPM", ["Firewall"] = "Paravan de protecție (firewall)",
            ["Antivirus"] = "Antivirus", ["Windows Update"] = "Windows Update", ["Drive health"] = "Starea unităților",
            ["Free space"] = "Spațiu liber", ["Drive temperature"] = "Temperatura unităților", ["Restore points"] = "Puncte de restaurare",
            ["Drivers"] = "Drivere", ["Processor under load"] = "Procesor sub sarcină", ["Processor slowdown"] = "Încetinirea procesorului",
            ["Graphics under load"] = "Placă video sub sarcină", ["Memory test"] = "Test de memorie", ["Windows Memory Diagnostic"] = "Diagnosticarea memoriei din Windows",
            ["Battery"] = "Baterie", ["Core services"] = "Servicii de bază", ["Uptime"] = "Timp de funcționare", ["Event log"] = "Jurnal de evenimente",
            ["Blue screens"] = "Ecrane albastre", ["Network adapter"] = "Placă de rețea", ["Clock"] = "Ceas",
            // ── Health: sfaturi
            ["Windows is not activated, so some settings stay locked."] = "Windows nu este activat, deci unele setări rămân blocate.",
            ["Secure Boot or TPM is off. The PC works normally — only Windows 11 upgrades and some games need them (turned on in the BIOS)."] = "Secure Boot sau TPM este oprit. Calculatorul funcționează normal — doar trecerea la Windows 11 și unele jocuri au nevoie de ele (se pornesc din BIOS).",
            ["The firewall is off, so the PC accepts connections it should block."] = "Paravanul de protecție este oprit, deci calculatorul acceptă conexiuni care ar trebui blocate.",
            ["No active antivirus. Turning Microsoft Defender back on is enough."] = "Nu există antivirus activ. Este suficientă repornirea Microsoft Defender.",
            ["Updates are waiting. Install them and restart when convenient."] = "Există actualizări în așteptare. Se instalează și se repornește când este potrivit.",
            ["A drive reports problems. Back up important files soon."] = "O unitate raportează probleme. Fișierele importante trebuie salvate cât mai curând.",
            ["The system drive is almost full — Windows slows down and updates can fail."] = "Unitatea de sistem este aproape plină — Windows încetinește, iar actualizările pot eșua.",
            ["A drive runs hot. Check the airflow around it."] = "O unitate se încălzește. Merită verificată circulația aerului în jurul ei.",
            ["System Restore is off, so there is no way back after a bad update or driver."] = "Restaurarea sistemului este oprită, deci nu există cale de întoarcere după o actualizare sau un driver defect.",
            ["A device has no working driver."] = "Un dispozitiv nu are driver funcțional.",
            ["The processor got too hot or made errors under load. Clean the cooler, renew the thermal paste."] = "Procesorul s-a încălzit prea tare sau a făcut erori sub sarcină. Se recomandă curățarea coolerului și schimbarea pastei termice.",
            ["The processor slows itself down — usually heat or a power limit."] = "Procesorul se încetinește singur — de obicei din cauza căldurii sau a unei limite de putere.",
            ["The graphics card got too hot or gave wrong results under load."] = "Placa video s-a încălzit prea tare sau a dat rezultate greșite sub sarcină.",
            ["The memory test found errors. Test each module on its own to find the bad one."] = "Testul de memorie a găsit erori. Fiecare modul se testează separat pentru a-l găsi pe cel defect.",
            ["Windows Memory Diagnostic found errors."] = "Diagnosticarea memoriei din Windows a găsit erori.",
            ["The battery has lost a large part of its capacity."] = "Bateria și-a pierdut o mare parte din capacitate.",
            ["An important Windows service is stopped."] = "Un serviciu important din Windows este oprit.",
            ["The PC hasn't restarted in a long time. A restart finishes updates and frees memory."] = "Calculatorul nu a fost repornit de mult timp. O repornire finalizează actualizările și eliberează memoria.",
            ["Windows logged serious errors recently."] = "Windows a înregistrat recent erori grave.",
            ["The PC crashed with a blue screen recently."] = "Calculatorul s-a blocat recent cu ecran albastru.",
            ["The network adapter's driver reported errors."] = "Driverul plăcii de rețea a raportat erori.",
            ["The clock is not synced — websites and updates can fail."] = "Ceasul nu este sincronizat — site-urile și actualizările pot da erori.",
            // ── Health: interface
            ["Copy report"] = "Copiază raportul", ["Copies every check and its result as text"] = "Copiază toate verificările și rezultatele, ca text",
            ["Re-run"] = "Rulează din nou", ["Run all the checks again"] = "Rulează din nou toate verificările",
            ["Starting…"] = "Se pornește…", ["Checking…"] = "Se verifică…", ["Waiting…"] = "În așteptare…",
            ["What to do, most important first"] = "Ce este de făcut, începând cu ce contează mai mult", ["NEEDS ATTENTION"] = "NECESITĂ ATENȚIE",
            ["Open"] = "Deschide", ["Open firewall"] = "Deschide firewall", ["Open security"] = "Deschide Securitate", ["Open updates"] = "Deschide actualizările",
            ["Turn on"] = "Pornește", ["Open Storage"] = "Deschide Stocare", ["Open Power"] = "Deschide Alimentare", ["Open Drivers"] = "Deschide Drivere", ["Open storage"] = "Deschide Stocare", ["Open power"] = "Deschide Alimentare", ["Open drivers"] = "Deschide Drivere",
            ["Open services"] = "Deschide Servicii", ["Open events"] = "Deschide Evenimente", ["Open crash"] = "Deschide Blocări", ["Open Temp & Stress"] = "Deschide Temperaturi și stres", ["Open RAM"] = "Deschide RAM",
            ["Checking… this takes a few seconds"] = "Se verifică… durează câteva secunde", ["Cancelled."] = "Anulat.",
            ["Critical"] = "Critic", ["Warning"] = "Avertisment", ["WARNING"] = "AVERTISMENT", ["CHECK"] = "DE VERIFICAT", ["Stale"] = "Vechi", ["OLD TEST"] = "TEST VECHI",
            ["Run missing tests"] = "Rulează testele lipsă", ["Opens the page of the first test that has not been run yet"] = "Deschide pagina primului test care nu a fost rulat încă", ["info"] = "informare", ["INFO"] = "INFO",
            ["Checking"] = "Se verifică", ["All clear"] = "Totul în regulă", ["not tested"] = "netestat", ["NOT TESTED"] = "NETESTAT",
            ["Reading Windows, the drives and the last test results."] = "Se citesc datele din Windows, unitățile și ultimele rezultate de test.",
            ["1 warning needs attention"] = "1 avertisment necesită atenție",
            ["1 thing to fix"] = "1 lucru de rezolvat",
            ["Nothing is changed on the PC. Right-click a row to copy it."] = "Nu se modifică nimic pe calculator. Clic dreapta pe un rând îl copiază.",
            ["Start with the first item on the right — it is the most important."] = "Cel mai important este primul element din dreapta.",
            ["Mostly healthy · 1 thing to check"] = "În mare parte sănătos · 1 lucru de verificat", ["Watch"] = "Atenție",
            ["Nothing is broken; a few things are worth a look when you have time."] = "Nimic nu este stricat; câteva lucruri merită verificate când există timp.",
            ["Healthy · nothing to fix"] = "Sănătos · nimic de reparat", ["Healthy"] = "Sănătos",
            ["Everything checked looks good. Run the missing tests for a complete picture."] = "Tot ce s-a verificat arată bine. Testele lipsă trebuie rulate pentru o imagine completă.",
            ["Everything checked looks good."] = "Tot ce s-a verificat arată bine.",
            ["Nothing needs your attention"] = "Nimic nu necesită atenție", ["Every check passed."] = "Toate verificările au trecut.",
            ["Report copied — paste it into an e-mail or a document."] = "Raport copiat — poate fi lipit într-un e-mail sau document.",
            // ── Health: rezultate
            ["Could not determine battery wear level"] = "Uzura bateriei nu a putut fi determinată",
            ["All drivers installed"] = "Toate driverele sunt instalate", ["Could not check driver status"] = "Starea driverelor nu a putut fi verificată",
            ["No antivirus product detected"] = "Nu s-a detectat niciun antivirus", ["Could not verify antivirus status"] = "Starea antivirusului nu a putut fi verificată",
            ["All core system services running"] = "Toate serviciile de bază rulează",
            ["Windows Update Agent is not available on this system"] = "Agentul Windows Update nu este disponibil pe acest sistem",
            ["No pending Windows updates"] = "Nu există actualizări Windows în așteptare", ["Could not check for Windows updates"] = "Actualizările Windows nu au putut fi verificate",
            ["Windows is activated"] = "Windows este activat", ["Windows is not activated — some features may be limited"] = "Windows nu este activat — unele funcții pot fi limitate",
            ["Could not verify Windows activation status"] = "Starea activării Windows nu a putut fi verificată",
            ["Legacy BIOS (Secure Boot not available on this firmware) — TPM not present"] = "BIOS clasic (Secure Boot nu este disponibil pe acest firmware) — TPM absent",
            ["Not applicable (Legacy BIOS)"] = "Nu se aplică (BIOS clasic)", ["Enabled"] = "Activat", ["Disabled"] = "Dezactivat", ["Not present"] = "Absent",
            ["Could not determine firewall status"] = "Starea firewall nu a putut fi determinată", ["Windows Firewall is active"] = "Windows Firewall este activ",
            ["Windows Firewall is disabled"] = "Windows Firewall este dezactivat", ["Could not verify firewall status"] = "Starea firewall nu a putut fi verificată",
            ["No system restore points found"] = "Nu s-au găsit puncte de restaurare", ["System Restore appears to be disabled"] = "Restaurarea sistemului pare dezactivată",
            ["No disks detected"] = "Nu s-au detectat discuri", ["Could not read disk health (SMART)"] = "Starea discurilor (SMART) nu a putut fi citită",
            ["No partitions detected"] = "Nu s-au detectat partiții", ["Could not read free disk space"] = "Spațiul liber nu a putut fi citit",
            ["Windows Time service is disabled — clock will not auto-sync"] = "Serviciul Windows Time este dezactivat — ceasul nu se sincronizează singur",
            ["Windows Time service not active right now (normal — starts on demand)"] = "Serviciul Windows Time nu este activ acum (normal — pornește la cerere)",
            ["Clock has never synced successfully"] = "Ceasul nu s-a sincronizat niciodată cu succes", ["Could not check clock synchronization"] = "Sincronizarea ceasului nu a putut fi verificată",
            ["No critical events in the last 7 days"] = "Niciun eveniment critic în ultimele 7 zile", ["Could not read Event Log"] = "Jurnalul de evenimente nu a putut fi citit",
            ["No network driver errors in the last 7 days"] = "Nicio eroare de driver de rețea în ultimele 7 zile", ["Could not check network driver errors"] = "Erorile driverului de rețea nu au putut fi verificate",
            ["No BSODs in the last 30 days"] = "Niciun ecran albastru în ultimele 30 de zile", ["Could not read crash history"] = "Istoricul blocărilor nu a putut fi citit",
            ["Not slowed down now · no stress test yet"] = "Nu încetinește acum · încă niciun test de stres",
            ["run a CPU stress test in Temp & Stress"] = "se rulează un test de stres al procesorului în Temperaturi și stres",
            ["run a GPU stress test in Temp & Stress"] = "se rulează un test de stres al plăcii video în Temperaturi și stres",
            ["run a memory test on the RAM page"] = "se rulează un test de memorie în pagina RAM",
            ["too hot"] = "prea cald", ["power limit"] = "limită de putere", ["the charger / board forced it"] = "încărcătorul sau placa de bază a forțat-o",
            ["battery saver"] = "economisire baterie", ["power circuit too hot"] = "circuitul de alimentare prea cald", ["fixed low speed"] = "viteză mică fixă", ["unknown reason"] = "motiv necunoscut",
            ["Advanced test"] = "test avansat", ["Basic test"] = "test de bază",
            ["GPU stress did not report any compute activity — driver or adapter issue"] = "Testul de stres video nu a raportat activitate de calcul — problemă de driver sau de adaptor",
            ["RAM integrity test could not complete"] = "Testul de integritate al memoriei RAM nu a putut fi finalizat",
            ["RAM stable — bandwidth measured, 256MB integrity test passed"] = "RAM stabil — lățimea de bandă a fost măsurată, testul de integritate pe 256 MB a trecut",
            // ── Scor sanatate
            ["Excellent"] = "Excelent", ["Good"] = "Bun", ["Fair"] = "Acceptabil", ["Poor"] = "Slab",
            ["All systems running optimally"] = "Totul funcționează optim", ["Minor issues detected"] = "Probleme minore",
            ["Some components need attention"] = "Unele componente necesită atenție", ["Multiple issues found — action needed"] = "Mai multe probleme — sunt necesare măsuri",
            ["Critical problems detected"] = "Probleme critice",
            // ── Event Viewer: details
            ["Event type:"] = "Tipul evenimentului:", ["Date / Time:"] = "Data / ora:", ["Duration:"] = "Durată:", ["Notes:"] = "Observații:",
            ["Copy"] = "Copiază", ["Search Google"] = "Caută pe Google", ["Close"] = "Închide",
            // ── Event Viewer: explanations (EventExplainer)
            ["Unexpected shutdown — power loss, a crash or a forced power-off (holding the power button)."] = "Oprire neașteptată — lipsă de curent, blocare sau oprire forțată (apăsare lungă pe butonul de pornire).",
            ["The previous shutdown was unexpected."] = "Oprirea anterioară a fost neașteptată.",
            ["Windows started (event log service started)."] = "Windows a pornit (serviciul de jurnal a pornit).",
            ["Windows shut down normally."] = "Windows s-a oprit normal.", ["Windows started."] = "Windows a pornit.", ["Windows shut down."] = "Windows s-a oprit.",
            ["The system crashed with a blue screen (BSOD). Details on the BSOD / Crash page."] = "Sistemul s-a blocat cu ecran albastru (BSOD). Detalii în pagina BSOD / Blocări.",
            ["Windows could not save the crash dump (memory dump) — often a too-small page file."] = "Windows nu a putut salva imaginea memoriei de la blocare — adesea fișierul de paginare este prea mic.",
            ["A hardware error was detected and corrected (often PCIe / graphics card link). Occasional ones are harmless."] = "O eroare hardware a fost detectată și corectată (adesea legătura PCIe / placa video). Cele izolate sunt inofensive.",
            ["Fatal hardware error (CPU / memory / bus). Usually causes a crash — check temperatures, RAM and overclock."] = "Eroare hardware gravă (procesor / memorie / magistrală). De obicei provoacă blocare — merită verificate temperaturile, RAM-ul și overclockul.",
            ["A corrected hardware error in the CPU / cache."] = "O eroare hardware corectată în procesor / cache.",
            ["A corrected memory (RAM) error."] = "O eroare corectată de memorie (RAM).",
            ["The CPU speed was limited by the firmware — usually overheating or a power limit (throttling)."] = "Viteza procesorului a fost limitată de firmware — de obicei supraîncălzire sau limită de putere (throttling).",
            ["Bad block on the disk — the drive could not read a sector. Check disk health (SMART / Surface Scan)."] = "Bloc defect pe disc — unitatea nu a putut citi un sector. Merită verificată starea discului (SMART / Scanare suprafață).",
            ["The disk controller reported an error — often a faulty cable or port."] = "Controllerul discului a raportat o eroare — adesea un cablu sau port defect.",
            ["An error occurred while writing to the disk (paging operation)."] = "A apărut o eroare la scrierea pe disc (operație de paginare).",
            ["A disk operation had to be retried — early sign of a slow or failing drive or cable."] = "O operație pe disc a trebuit repetată — semn timpuriu de unitate sau cablu lent ori defect.",
            ["The file system is corrupted — run chkdsk /f."] = "Sistemul de fișiere este corupt — se rulează chkdsk /f.",
            ["The volume needs a check (chkdsk) — possible file system damage."] = "Volumul necesită verificare (chkdsk) — posibile defecte ale sistemului de fișiere.",
            ["The display driver stopped responding and recovered (screen flicker / black screen for a moment)."] = "Driverul de afișare a încetat să răspundă și și-a revenit (clipire sau ecran negru pentru o clipă).",
            ["NVIDIA driver error — usually driver, overheating or unstable GPU overclock."] = "Eroare de driver NVIDIA — de obicei driver, supraîncălzire sau overclock instabil al plăcii video.",
            ["NVIDIA driver was reset after it stopped responding."] = "Driverul NVIDIA a fost resetat după ce a încetat să răspundă.",
            ["AMD display driver stopped responding and recovered."] = "Driverul de afișare AMD a încetat să răspundă și și-a revenit.",
            ["A driver for a device failed to load (device may not work)."] = "Driverul unui dispozitiv nu s-a încărcat (dispozitivul poate să nu funcționeze).",
            ["A device has a problem starting (driver or hardware issue)."] = "Un dispozitiv are probleme la pornire (driver sau hardware).",
            ["A service failed to start."] = "Un serviciu nu a putut porni.",
            ["A service could not start because a service it depends on failed."] = "Un serviciu nu a putut porni, deoarece un serviciu de care depinde a eșuat.",
            ["A service took too long to start (timeout) — often slow boot or a busy disk."] = "Un serviciu a pornit prea greu (timeout) — adesea pornire lentă sau disc ocupat.",
            ["A service did not respond in time."] = "Un serviciu nu a răspuns la timp.", ["A service stopped with an error."] = "Un serviciu s-a oprit cu eroare.",
            ["A service crashed and Windows restarted it."] = "Un serviciu s-a blocat, iar Windows l-a repornit.", ["A service crashed unexpectedly."] = "Un serviciu s-a blocat neașteptat.",
            ["A service's startup type was changed (normal — Windows and updates do this often)."] = "Tipul de pornire al unui serviciu a fost schimbat (normal — Windows și actualizările fac asta des).",
            ["A service started or stopped (normal activity)."] = "Un serviciu a pornit sau s-a oprit (activitate normală).",
            ["An application crashed."] = "O aplicație s-a blocat.", ["An application stopped responding and was closed."] = "O aplicație a încetat să răspundă și a fost închisă.",
            ["A crash report was saved (follows an application or system error)."] = "S-a salvat un raport de blocare (urmează unei erori de aplicație sau de sistem).",
            ["A .NET application crashed."] = "O aplicație .NET s-a blocat.",
            ["An application could not start — a Visual C++ runtime is missing or damaged."] = "O aplicație nu a putut porni — lipsește sau este deteriorat un runtime Visual C++.",
            ["Harmless permission warning, present on almost every Windows PC — can be ignored."] = "Avertisment inofensiv de permisiuni, prezent pe aproape orice calculator cu Windows — poate fi ignorat.",
            ["A background component did not register in time — usually harmless."] = "O componentă din fundal nu s-a înregistrat la timp — de obicei inofensiv.",
            ["Windows license service scheduled a check (normal)."] = "Serviciul de licențiere Windows a programat o verificare (normal).",
            ["Windows license service finished a check (normal)."] = "Serviciul de licențiere Windows a terminat o verificare (normal).",
            ["Shadow copy service stopped because it was idle (normal)."] = "Serviciul de copii shadow s-a oprit fiind inactiv (normal).",
            ["An installer started a restart session (normal during installs/updates)."] = "Un program de instalare a pornit o sesiune de repornire (normal la instalări/actualizări).",
            ["An installer finished a restart session (normal)."] = "Un program de instalare a terminat o sesiune de repornire (normal).",
            ["Network service registered a URL (normal background activity)."] = "Serviciul de rețea a înregistrat o adresă URL (activitate normală de fundal).",
            ["Network service released a URL (normal background activity)."] = "Serviciul de rețea a eliberat o adresă URL (activitate normală de fundal).",
            ["Profile notification issue — usually harmless."] = "Problemă la notificarea profilului — de obicei inofensivă.",
            ["IP address conflict — another device on the network has the same address."] = "Conflict de adresă IP — un alt dispozitiv din rețea are aceeași adresă.",
            ["Name resolution timed out — slow or unreachable DNS server."] = "Rezolvarea numelor a expirat — server DNS lent sau inaccesibil.",
            ["Could not reach the time server — clock may drift."] = "Serverul de timp nu a putut fi contactat — ceasul se poate decala.",
            ["A Windows update failed to install."] = "O actualizare Windows nu s-a putut instala.", ["A Windows update was installed."] = "O actualizare Windows a fost instalată.",
            ["Microsoft Defender detected a threat."] = "Microsoft Defender a detectat o amenințare.", ["Microsoft Defender took action against a threat."] = "Microsoft Defender a luat măsuri împotriva unei amenințări.",
            ["Winlogon notification (normal)."] = "Notificare Winlogon (normal).",
        };

        // (English template, Romanian replacement) — the groups {1}, {2} … are kept from the text
        private static readonly (Regex re, string ro)[] Patterns =
        {
            P(@"^1 driver missing or with errors$", "1 driver lipsă sau cu erori"),
            P(@"^1 update pending — includes a critical security update$", "1 actualizare în așteptare — inclusiv una critică de securitate"),
            P(@"^1 update pending installation$", "1 actualizare în așteptare pentru instalare"),
            P(@"^1 restore point — newest from (.*) \((\d+)d ago\)$", "1 punct de restaurare — cel mai nou din $1 (acum $2 zile)"),
            P(@"^1 restore point found$", "1 punct de restaurare găsit"),
            P(@"^1 critical event in the last 7 days$", "1 eveniment critic în ultimele 7 zile"),
            P(@"^1 network driver error in the last 7 days — (.*)$", "1 eroare de driver de rețea în ultimele 7 zile — $1"),
            P(@"^1 BSOD in the last 30 days$", "1 ecran albastru în ultimele 30 de zile"),
            P(@"^Secure Boot on · TPM not present — needed only for Windows 11 upgrades and some games$", "Secure Boot pornit · TPM absent — necesar doar pentru trecerea la Windows 11 și unele jocuri"),
            P(@"^Secure Boot on · TPM (.+) present — needed only for Windows 11 upgrades and some games$", "Secure Boot pornit · TPM $1 prezent — necesar doar pentru trecerea la Windows 11 și unele jocuri"),
            P(@"^Secure Boot off · TPM not present — needed only for Windows 11 upgrades and some games$", "Secure Boot oprit · TPM absent — necesar doar pentru trecerea la Windows 11 și unele jocuri"),
            P(@"^Secure Boot off · TPM (.+) present — needed only for Windows 11 upgrades and some games$", "Secure Boot oprit · TPM $1 prezent — necesar doar pentru trecerea la Windows 11 și unele jocuri"),
            P(@"^Last run (.+) · Took (\d+) s$", "Ultima verificare $1 · A durat $2 s"),
            P(@"^(\d+) OK · (\d+) not tested$", "$1 OK · $2 netestate"),
            P(@"^(\d+) things need attention · 1 warning$", "$1 lucruri necesită atenție · 1 avertisment"),
            P(@"^(\d+) things need attention · (\d+) warnings$", "$1 lucruri necesită atenție · $2 avertismente"),
            P(@"^(\d+) checks$", "$1 verificări"),
            P(@"^(\d+) checks · read-only$", "$1 verificări · doar citire"),
            P(@"^(\d+) things to fix$", "$1 lucruri de rezolvat"),
            P(@"^(\d+) checks · nothing is changed on the PC · right-click a row to copy it$", "$1 verificări · nu se modifică nimic pe calculator · clic dreapta pe un rând îl copiază"),
            P(@"^Something went wrong: (.*)$", "A apărut o eroare: $1"),
            P(@"^(\d+) warnings need attention$", "$1 avertismente necesită atenție"),
            P(@"^Mostly healthy · (\d+) things to check$", "În mare parte sănătos · $1 lucruri de verificat"),
            P(@"^(\d+) items?$", "$1 elemente"),
            P(@"^\+(\d+) more — see the cards below$", "+$1 în plus — vezi cardurile de mai jos"),
            P(@"^(\d+) tests? not run yet — use Run test in the Hardware card\.$", "$1 teste nerulate încă — se pot rula din Temperaturi și stres și din RAM."),
            P(@"^(\d+) (passed|warnings?|critical|not tested|checks?)$", "$1 $2"),
            P(@"^SMD Win — System Health Check — (.*)$", "SMD Win — Verificarea stării sistemului — $1"),
            P(@"^(\d+)% health( — (\d+) charge cycles)?$", "Sănătate $1%$2"),
            P(@" — (\d+) charge cycles$", " — $1 cicluri de încărcare"),
            P(@"^(\d+) drivers? missing or with errors$", "$1 drivere lipsă sau cu erori"),
            P(@"^Disk temperatures OK \(worst: (.*)\)$", "Temperaturile discurilor sunt în regulă (cea mai mare: $1)"),
            P(@"^(.*) running hot — (.*)$", "$1 se încălzește — $2"),
            P(@"^(.*) is active$", "$1 este activ"),
            P(@"^(.*) appears to be disabled$", "$1 pare dezactivat"),
            P(@"^Stopped: (.*)$", "Oprite: $1"),
            P(@"^System has been running for (\d+) days without a restart$", "Sistemul rulează de $1 zile fără repornire"),
            P(@"^(\d+) updates? pending — includes a critical security update$", "$1 actualizări în așteptare — inclusiv una critică de securitate"),
            P(@"^(\d+) updates? pending installation$", "$1 actualizări în așteptare pentru instalare"),
            P(@"^Legacy BIOS \(Secure Boot not available on this firmware\) — TPM (.*) present$", "BIOS clasic (Secure Boot nu este disponibil pe acest firmware) — TPM $1 prezent"),
            P(@"^Secure Boot on, TPM (.*) present$", "Secure Boot pornit, TPM $1 prezent"),
            P(@"^Secure Boot (on|off), TPM (.*)$", "Secure Boot $1, TPM $2"),
            P(@"^(\d+) restore points? — newest from (.*) \((\d+)d ago\)$", "$1 puncte de restaurare — cel mai nou din $2 (acum $3 zile)"),
            P(@"^(\d+) restore points? found$", "$1 puncte de restaurare găsite"),
            P(@"^All disks healthy \((\d+) checked, worst (\d+)%\)$", "Toate discurile sunt sănătoase ($1 verificate, cel mai slab $2%)"),
            P(@"^System drive (.+) — ([\d.,]+ [GT]B) free \((\d+)% used\)$", "Unitatea de sistem $1 — $2 liberi ($3% ocupat)"),
            P(@"^System drive OK — (.+) is nearly full \((\d+)% used, not critical\)$", "Unitatea de sistem este în regulă — $1 este aproape plină ($2% ocupat, nu e critic)"),
            P(@"^Free space OK on all (\d+) partition\(s\)$", "Spațiu liber suficient pe toate cele $1 partiții"),
            P(@"^Clock last synced (\d+)d ago$", "Ceasul s-a sincronizat ultima dată acum $1 zile"),
            P(@"^Clock has not synced in (\d+)d — check date/time settings$", "Ceasul nu s-a sincronizat de $1 zile — merită verificate setările de dată și oră"),
            P(@"^Clock synced \((.*)\)$", "Ceas sincronizat ($1)"),
            P(@"^(\d+) critical events? in the last 7 days$", "$1 evenimente critice în ultimele 7 zile"),
            P(@"^(\d+) network driver errors? in the last 7 days — (.*)$", "$1 erori de driver de rețea în ultimele 7 zile — $2"),
            P(@"^(\d+) BSODs? in the last 30 days$", "$1 ecrane albastre în ultimele 30 de zile"),
            P(@"^Test failed: (.*)$", "Testul a eșuat: $1"),
            P(@"^CPU stable — (\d+)s stress at 100% load, no computation errors$", "Procesor stabil — stres de $1 s la 100% sarcină, fără erori de calcul"),
            P(@"^(\d+) computation error\(s\) detected during (\d+)s stress — possible hardware instability \(not temperature-related\)$", "$1 erori de calcul în timpul stresului de $2 s — posibilă instabilitate hardware (nu din cauza temperaturii)"),
            P(@"^Not tested yet · (.*)$", "Netestat încă · $1"),
            P(@"^(\d+) calculation errors under load · (.*)$", "$1 erori de calcul sub sarcină · $2"),
            P(@"^(\d+) wrong results under load · (.*)$", "$1 rezultate greșite sub sarcină · $2"),
            P(@"^Reached (.*) under load — cooling can't keep up · (.*)$", "A ajuns la $1 sub sarcină — răcirea nu face față · $2"),
            P(@"^Reached (.*) under load · (.*)$", "A ajuns la $1 sub sarcină · $2"),
            P(@"^Hot under load: (.*) · (.*)$", "Cald sub sarcină: $1 · $2"),
            P(@"^Stable, max (.*) · (.*)$", "Stabil, maxim $1 · $2"),
            P(@"^(.*) test on (.*)$", "Test de $1 din $2"),
            P(@"^(\d+) days? ago$", "acum $1 zile"),
            P(@"^Slowed down right now: (.*) — (\d+) MHz \((\d+)% of max\)$", "Încetinit chiar acum: $1 — $2 MHz ($3% din maxim)"),
            P(@"^Slowed down (\d+)% of the last stress test \((.*)\) · (.*)$", "Încetinit $1% din ultimul test de stres ($2) · $3"),
            P(@"^No slowdown now or in the last stress test \((.*)\)$", "Fără încetinire acum sau în ultimul test de stres ($1)"),
            P(@"^Errors found \((.*)\) · (.*) on (.*)$", "Erori găsite ($1) · $2 din $3"),
            P(@"^(.*) could not finish · (.*)$", "$1 nu a putut termina · $2"),
            P(@"^No errors · (.*) on (.*)$", "Fără erori · $1 din $2"),
            P(@"^GPU stress failed: (.*)$", "Testul de stres video a eșuat: $1"),
            P(@"^GPU compute stable — (\d+)s D3D11 compute shader stress, no errors$", "Calcul video stabil — stres D3D11 de $1 s, fără erori"),
            P(@"^Windows Memory Diagnostic found errors on (.*)$", "Diagnosticarea memoriei din Windows a găsit erori la $1"),
            P(@"^Windows Memory Diagnostic ran on (.*), no errors found$", "Diagnosticarea memoriei din Windows a rulat la $1, fără erori"),
            P(@"^(\d+) RAM error\(s\) detected — possible faulty module or unstable overclock$", "$1 erori RAM — posibil modul defect sau overclock instabil"),
            P(@"^Network test failed: (.*)$", "Testul de rețea a eșuat: $1"),
            P(@"^Previous occurrences of ""(.*)"" \((\d+) shown\):$", "Apariții anterioare ale „$1” ($2 afișate):"),
            P(@"^Event Details — (.*)$", "Detalii eveniment — $1"),
            P(@"^(\d+) minutes?$", "$1 minute"),
        };

        private static (Regex, string) P(string pattern, string ro) => (new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant), ro);

        private static bool Active => LanguageService.CurrentCode == "ro";

        public static string T(string? text)
        {
            if (string.IsNullOrEmpty(text) || !Active) return text ?? "";
            if (Exact.TryGetValue(text, out var ro)) return ro;

            // "Name · summary" (attention list): each part is translated separately
            if (text.Contains(" · ", StringComparison.Ordinal))
            {
                foreach (var (re, rep) in Patterns) if (re.IsMatch(text)) return re.Replace(text, rep);
                var parts = text.Split(" · ");
                bool any = false;
                for (int i = 0; i < parts.Length; i++) { string t = T(parts[i]); if (!ReferenceEquals(t, parts[i]) && t != parts[i]) any = true; parts[i] = t; }
                return any ? string.Join(" · ", parts) : text;
            }
            foreach (var (re, rep) in Patterns)
                if (re.IsMatch(text)) return re.Replace(text, rep);
            return text;
        }

        /// <summary>Translates in place the texts in the visual tree (TextBlock, buttons, tooltips, the window title).</summary>
        public static void Tree(DependencyObject root)
        {
            if (!Active || root == null) return;
            try
            {
                if (root is Window w && !string.IsNullOrEmpty(w.Title)) w.Title = T(w.Title);
                Walk(root);
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }

        private static void Walk(DependencyObject d)
        {
            if (d is TextBlock tb && tb.Inlines.Count <= 1)
            {
                string t = T(tb.Text);
                if (t != tb.Text) tb.Text = t;
            }
            if (d is ContentControl cc && cc.Content is string s)
            {
                string t = T(s);
                if (t != s) cc.Content = t;
            }
            if (d is FrameworkElement fe && fe.ToolTip is string tip)
            {
                string t = T(tip);
                if (t != tip) fe.ToolTip = t;
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
    }
}
