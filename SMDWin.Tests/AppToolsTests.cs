using System;
using System.IO;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The Applications page: the icon with the initial, the program's commands, the prefetch traces, the notes.</summary>
    public class AppToolsTests
    {
        // ── the initial-letter icon ──
        [Fact]
        public void Initial_IsTheFirstLetterOrDigit_UpperCase()
        {
            Assert.Equal("M", AppTile.Initial("mozilla Firefox"));
            Assert.Equal("7", AppTile.Initial("7-Zip 23.01"));
            Assert.Equal("V", AppTile.Initial("  (µ) VLC"[5..]));
            Assert.Equal("N", AppTile.Initial("[.NET] nvidia"[7..]));
            Assert.Equal("Ș", AppTile.Initial("știri"));
            Assert.Equal("?", AppTile.Initial("---"));
            Assert.Equal("?", AppTile.Initial(""));
            Assert.Equal("?", AppTile.Initial(null));
        }

        [Fact]
        public void Colour_IsStable_AndIgnoresCaseAndOuterSpaces()
        {
            Assert.Equal(AppTile.ColorIndex("Mozilla Firefox"), AppTile.ColorIndex("  mozilla firefox "));
            Assert.Equal(AppTile.ColorHex("7-Zip"), AppTile.ColorHex("7-ZIP"));
            // the value does not depend on the run (FNV-1a): the same names → the same colours tomorrow too
            Assert.Equal(AppTile.ColorIndex("abc"), (int)(0x1A47E90B % (uint)AppTile.Palette.Length));
            foreach (var n in new[] { "", "a", "Zoom", "Adobe Acrobat", null })
                Assert.True(AppTile.ColorIndex(n) >= 0 && AppTile.ColorIndex(n) < AppTile.Palette.Length);
        }

        [Fact]
        public void Colours_SpreadOverThePalette()
        {
            var names = Enumerable.Range(0, 200).Select(i => "Program " + i);
            int used = names.Select(AppTile.ColorIndex).Distinct().Count();
            Assert.Equal(AppTile.Palette.Length, used);
            Assert.True(AppTile.Palette.All(p => p.Length == 7 && p[0] == '#'));
        }

        // ── commands ──
        [Fact]
        public void IconFilePath_StripsQuotesAndIconIndex()
        {
            Assert.Equal(@"C:\App\app.exe", AppCommands.IconFilePath("\"C:\\App\\app.exe\",0"));
            Assert.Equal(@"C:\App\app.exe", AppCommands.IconFilePath(@"C:\App\app.exe,-101"));
            Assert.Equal(@"C:\App\icon.ico", AppCommands.IconFilePath(@" C:\App\icon.ico "));
            Assert.Equal(@"C:\Prog, Inc\app.exe", AppCommands.IconFilePath(@"C:\Prog, Inc\app.exe"));   // the comma in the name stays
            Assert.Equal("", AppCommands.IconFilePath(null));
        }

        [Fact]
        public void ModifyCommand_PrefersWhatTheProgramDeclares_ThenMsi()
        {
            Assert.Equal("\"C:\\App\\setup.exe\" /modify", AppCommands.ModifyCommand(" \"C:\\App\\setup.exe\" /modify ", false, "App", false, false));
            string guid = "{90160000-008C-0000-1000-0000000FF1CE}";
            Assert.Equal("MsiExec.exe /I" + guid, AppCommands.ModifyCommand("", true, guid, false, false));
            Assert.Equal("MsiExec.exe /I" + guid, AppCommands.ModifyCommand(null, true, guid, noModify: true, noRepair: false));   // at least the repair
            Assert.Equal("", AppCommands.ModifyCommand("", true, guid, noModify: true, noRepair: true));
            Assert.Equal("", AppCommands.ModifyCommand("", true, "NotAGuid", false, false));
            Assert.Equal("", AppCommands.ModifyCommand("", false, guid, false, false));
        }

        [Fact]
        public void MsiArguments_DropTheProgramName_HoweverItIsWritten()
        {
            Assert.Equal("/X{90160000-008C-0000-1000-0000000FF1CE}", AppCommands.MsiArguments("MsiExec.exe /X{90160000-008C-0000-1000-0000000FF1CE}"));
            Assert.Equal("/I{ABC}", AppCommands.MsiArguments("msiexec.exe /I{ABC}"));                       // litere mici
            Assert.Equal("/x {ABC} /qb", AppCommands.MsiArguments("\"C:\\Windows\\System32\\MSIEXEC.EXE\" /x {ABC} /qb"));
            Assert.Equal("/I{ABC}", AppCommands.MsiArguments("  msiexec /I{ABC}"));
            Assert.Null(AppCommands.MsiArguments("\"C:\\App\\unins000.exe\" /SILENT"));
            Assert.Null(AppCommands.MsiArguments("C:\\App\\uninstall.exe /msiexec"));
            Assert.Null(AppCommands.MsiArguments(null));
        }

        [Fact]
        public void RegistryPath_IsWhatRegistryEditorShows()
        {
            Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
                AppCommands.RegistryPath("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "7-Zip"));
            Assert.Equal(@"HKEY_CURRENT_USER\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Zoom",
                AppCommands.RegistryPath("HKCU", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\", "Zoom"));
        }

        [Fact]
        public void OwnFolder_ExcludesRootsAndSharedFolders()
        {
            var shared = new[] { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Windows", @"C:\Users\Paul", "" };
            Assert.True(AppCommands.IsOwnFolder(@"C:\Program Files\7-Zip\", shared));
            Assert.True(AppCommands.IsOwnFolder(@"D:\Games\Game", shared));
            Assert.False(AppCommands.IsOwnFolder(@"C:\", shared));
            Assert.False(AppCommands.IsOwnFolder(@"C:", shared));
            Assert.False(AppCommands.IsOwnFolder(@"C:\Program Files\", shared));
            Assert.False(AppCommands.IsOwnFolder(@"c:\program files (x86)", shared));
            Assert.False(AppCommands.IsOwnFolder(@"C:\Users", shared));          // the parent of a shared folder
            Assert.False(AppCommands.IsOwnFolder("", shared));
            Assert.False(AppCommands.IsOwnFolder(null, shared));
        }

        [Fact]
        public void IsInside_NeedsARealSubPath()
        {
            Assert.True(AppCommands.IsInside(@"C:\Program Files\7-Zip\7zFM.exe", @"C:\Program Files\7-Zip\"));
            Assert.True(AppCommands.IsInside(@"c:\program files\7-zip\sub\x.exe", @"C:\Program Files\7-Zip"));
            Assert.False(AppCommands.IsInside(@"C:\Program Files\7-Zip2\x.exe", @"C:\Program Files\7-Zip"));
            Assert.False(AppCommands.IsInside(@"C:\Program Files\7-Zip", @"C:\Program Files\7-Zip"));
            Assert.False(AppCommands.IsInside(@"C:\x.exe", ""));
        }

        [Fact]
        public void Prefetch_MatchesTheProgramName_TruncatedLikeWindowsDoes()
        {
            Assert.Equal("CHROME.EXE", AppCommands.PrefetchName(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
            Assert.True(AppCommands.IsPrefetchOf("CHROME.EXE-D999B1BA.pf", "chrome.exe"));
            Assert.False(AppCommands.IsPrefetchOf("CHROME.EXE-D999B1BA.pf", "chrom.exe"));
            Assert.False(AppCommands.IsPrefetchOf("CHROMEDRIVER.EXE-11112222.pf", "chrome.exe"));
            Assert.False(AppCommands.IsPrefetchOf("CHROME.EXE-D999B1.pf", "chrome.exe"));              // code too short
            // long name: Windows keeps 29 characters
            string longExe = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
            Assert.Equal(29, AppCommands.PrefetchName(longExe).Length);
            Assert.True(AppCommands.IsPrefetchOf(AppCommands.PrefetchName(longExe) + "-0A1B2C3D.pf", longExe));
        }

        [Fact]
        public void MainExe_IsTheOneNamedLikeTheProgram()
        {
            Assert.Equal("firefox.exe", AppCommands.PickMainExe("Mozilla Firefox (x64 ro)", new[] { "updater.exe", "crashreporter.exe", "firefox.exe", "pingsender.exe" }));
            // names too short to be compared ("7z"): the first non-generic one stays (the program icon takes priority anyway)
            Assert.Equal("7z.exe", AppCommands.PickMainExe("7-Zip 23.01", new[] { "Uninstall.exe", "7z.exe", "7zFM.exe", "7zG.exe" }));
            Assert.Equal("app.exe", AppCommands.PickMainExe("Something Else", new[] { "uninstall.exe", "app.exe" }));    // none is similar → the first non-generic one
            Assert.Equal("", AppCommands.PickMainExe("X", new[] { "setup.exe", "unins000.exe" }));
            Assert.True(AppCommands.IsGenericExeName(@"C:\App\Unins000.EXE"));
            Assert.False(AppCommands.IsGenericExeName("firefox.exe"));
        }

        [Fact]
        public void ClipboardText_SkipsWhatIsMissing()
        {
            string text = AppCommands.ClipboardText("7-Zip", "23.01", "Igor Pavlov", "—", @"C:\Program Files\7-Zip", @"HKEY_LOCAL_MACHINE\…\7-Zip", "");
            Assert.Equal("7-Zip|Version: 23.01|Publisher: Igor Pavlov|Location: C:\\Program Files\\7-Zip|Registry: HKEY_LOCAL_MACHINE\\…\\7-Zip",
                string.Join("|", text.Split(Environment.NewLine)));
        }

        [Fact]
        public void Ago_ReadsNaturally()
        {
            var now = new DateTime(2026, 10, 3, 9, 0, 0);
            Assert.Equal("today", AppCommands.Ago(new DateTime(2026, 10, 3, 1, 0, 0), now));
            Assert.Equal("yesterday", AppCommands.Ago(new DateTime(2026, 10, 2, 23, 0, 0), now));
            Assert.Equal("5 days ago", AppCommands.Ago(now.AddDays(-5), now));
            Assert.Equal("3 months ago", AppCommands.Ago(now.AddDays(-95), now));
            Assert.Equal("2 years ago", AppCommands.Ago(now.AddDays(-800), now));
        }

        // ── notes ──
        [Fact]
        public void Notes_AreSavedPerProgram_AndSurviveARestart()
        {
            string file = Path.Combine(Path.GetTempPath(), "smdwin-notes-" + Guid.NewGuid().ToString("N"), "app-notes.json");
            try
            {
                var store = new AppNotesStore(file);
                Assert.Equal("", store.Get("7-Zip", "Igor Pavlov"));
                Assert.True(store.Set("7-Zip", "Igor Pavlov", "  lasat la cererea clientului  "));
                Assert.False(store.Set("7-zip ", "igor pavlov", "lasat la cererea clientului"));     // same note, same key
                Assert.True(store.Has("7-ZIP", "Igor Pavlov"));
                Assert.False(store.Has("7-Zip", "Altcineva"));

                var again = new AppNotesStore(file);                                               // "after restart"
                Assert.Equal("lasat la cererea clientului", again.Get("7-Zip", "Igor Pavlov"));
                Assert.Equal(1, again.Count);

                Assert.True(again.Set("7-Zip", "Igor Pavlov", "   "));                              // an empty note deletes it
                Assert.Equal(0, new AppNotesStore(file).Count);
                Assert.False(File.Exists(file + ".tmp"));
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch (IOException) { } }
        }

        [Fact]
        public void Notes_AreCutToTheLimit_AndABrokenFileStartsEmpty()
        {
            string dir = Path.Combine(Path.GetTempPath(), "smdwin-notes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "app-notes.json");
            try
            {
                File.WriteAllText(file, "{ this is not json");
                var store = new AppNotesStore(file);
                Assert.Equal(0, store.Count);
                store.Set("App", "", new string('x', AppNotesStore.MaxNoteLength + 50));
                Assert.Equal(AppNotesStore.MaxNoteLength, new AppNotesStore(file).Get("App", "").Length);
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
