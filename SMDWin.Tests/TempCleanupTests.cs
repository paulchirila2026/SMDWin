using System;
using System.Collections.Generic;
using System.IO;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>
    /// The temp clean-up must never reach outside a real "Temp" folder, never delete fresh files, SMD Win's own tools or
    /// files a pending restart still needs. Each rule is checked here on folders made for the test.
    /// </summary>
    public class TempCleanupTests : IDisposable
    {
        private readonly string _base;

        public TempCleanupTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "smdwin-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_base);
        }

        public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

        [Fact]
        public void RootMustBeNamedTempAndSitInItsParent()
        {
            string parent = Path.Combine(_base, "Local");
            Directory.CreateDirectory(Path.Combine(parent, "Temp"));
            Directory.CreateDirectory(Path.Combine(parent, "Data"));
            Assert.True(TempCleanup.IsSafeRoot(Path.Combine(parent, "Temp"), parent));
            Assert.False(TempCleanup.IsSafeRoot(Path.Combine(parent, "Data"), parent));          // not a Temp folder
            Assert.False(TempCleanup.IsSafeRoot(Path.Combine(parent, "Temp"), _base));           // not where it should be
            Assert.False(TempCleanup.IsSafeRoot(Path.Combine(parent, "Missing"), parent));       // does not exist
            Assert.False(TempCleanup.IsSafeRoot(Path.GetPathRoot(_base)!, Path.GetPathRoot(_base)!));   // a drive root
        }

        [Fact]
        public void SafeRootsAreOnlyTheTwoKnownFolders()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            string win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            foreach (var r in TempCleanup.SafeRoots())
                Assert.True(r.Equals(local, StringComparison.OrdinalIgnoreCase) || r.Equals(win, StringComparison.OrdinalIgnoreCase), r);
            Assert.True(TempCleanup.SafeRoots().Count <= 2);
        }

        private FileInfo MakeFile(string name, int daysOld)
        {
            string p = Path.Combine(_base, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
            var t = DateTime.Now.AddDays(-daysOld);
            File.SetCreationTime(p, t); File.SetLastWriteTime(p, t);
            return new FileInfo(p);
        }

        [Fact]
        public void FreshFilesAreKeptByDefault()
        {
            var rules = new TempCleanup.Rules { Cutoff = DateTime.Now.AddDays(-TempCleanup.DefaultAgeDays), Own = new List<string>(), Pending = new HashSet<string>() };
            Assert.False(TempCleanup.MayDelete(MakeFile("new.tmp", 1), rules));
            Assert.True(TempCleanup.MayDelete(MakeFile("old.tmp", 30), rules));
        }

        [Fact]
        public void OwnFolderAndPendingFilesAreKept()
        {
            var old1 = MakeFile(Path.Combine("SMDWin", "bundle", "smartctl.exe"), 30);
            var old2 = MakeFile("pending.dll", 30);
            var rules = new TempCleanup.Rules
            {
                Cutoff = null,
                Own = new List<string> { Path.Combine(_base, "SMDWin") },
                Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { old2.FullName },
            };
            Assert.False(TempCleanup.MayDelete(old1, rules));
            Assert.False(TempCleanup.MayDelete(old2, rules));
        }

        [Fact]
        public void SystemFilesAreKept()
        {
            var f = MakeFile("sys.dat", 30);
            File.SetAttributes(f.FullName, FileAttributes.System);
            var rules = new TempCleanup.Rules { Cutoff = null, Own = new List<string>(), Pending = new HashSet<string>() };
            Assert.False(TempCleanup.MayDelete(new FileInfo(f.FullName), rules));
            File.SetAttributes(f.FullName, FileAttributes.Normal);
        }

        [Fact]
        public void CleanRefusesAFolderThatIsNotACheckedRoot()
        {
            var f = MakeFile(Path.Combine("Temp", "old.tmp"), 30);
            var rules = new TempCleanup.Rules { Cutoff = null, Own = new List<string>(), Pending = new HashSet<string>() };
            var r = TempCleanup.Clean(Path.Combine(_base, "Temp"), rules);
            Assert.Equal(0, r.Deleted);
            Assert.True(File.Exists(f.FullName));
        }
    }
}
