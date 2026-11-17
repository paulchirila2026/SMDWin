smartmontools 7.5 (Windows, 64-bit) — https://www.smartmontools.org/

Continut folder Tools\
  smartctl.exe   — din pachetul oficial smartmontools-7.5.win32-setup.exe (GitHub: smartmontools/smartmontools,
                   tag RELEASE_7_5), copiat NESCHIMBAT din folderul bin\ (varianta pe 64 de biti).
  drivedb.h      — baza de date de discuri a smartmontools (nume si unitati ale atributelor SMART, pe model);
                   smartctl o citeste de langa el. Poate fi inlocuita cu una mai noua fara rebuild.
  smartmontools-COPYING.txt — licenta GNU GPL (v2 sau mai noua) a smartmontools.

Verificare: MD5 smartctl.exe = c1d1d016a33b09014517636c61d30947 (identic cu doc\checksums64.txt din pachetul oficial);
MD5 installer = bb1e199ad6a3db3e1c27ae54b835cbd5 (identic cu fisierul .md5 publicat langa el pe GitHub).

SMDWin ruleaza smartctl.exe ca PROCES SEPARAT si ii citeste doar output-ul JSON — nu il leaga in cod.
Codul sursa al smartmontools: https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5
Daca SMDWin este distribuit cu acest folder, licenta si adresa sursei trebuie sa ramana alaturi.
