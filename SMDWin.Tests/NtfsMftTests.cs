using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // Reading the NTFS index (MFT) for Large files / Top folders: records built by hand, in the on-disk format.
    public class NtfsMftTests
    {
        private const uint System = 0x4, Reparse = 0x400;

        /// <summary>Builds a 1024-byte "FILE" record with the fixups applied, as on disk.</summary>
        private static byte[] Record(bool inUse = true, bool dir = false, long baseRecord = 0, uint attrs = 0x20,
                                     DateTime? modified = null, (long parent, string name, byte space)[]? names = null,
                                     long? residentSize = null, long? nonResidentSize = null, long startVcn = 0,
                                     byte[]? runs = null, bool noStdInfo = false)
        {
            var rec = new byte[1024];
            Encoding.ASCII.GetBytes("FILE").CopyTo(rec, 0);
            W16(rec, 4, 48);          // the fixup table
            W16(rec, 6, 3);           // the check number + 2 blocks of 512
            W16(rec, 20, 56);         // the first attribute
            W16(rec, 22, (ushort)((inUse ? 1 : 0) | (dir ? 2 : 0)));
            W32(rec, 28, 1024);
            if (baseRecord >= 0) W64(rec, 32, baseRecord == 0 ? 0 : baseRecord | (1L << 48));   // with a sequence number, as on disk
            int off = 56;

            if (!noStdInfo && baseRecord == 0)
            {
                W32(rec, off, 0x10); W32(rec, off + 4, 24 + 0x30); W32(rec, off + 16, 0x30); W16(rec, off + 20, 24);
                W64(rec, off + 24 + 8, (modified ?? new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc)).ToFileTimeUtc());
                W32(rec, off + 24 + 0x20, attrs);
                off += 24 + 0x30;
            }
            foreach (var (parent, name, space) in names ?? Array.Empty<(long, string, byte)>())
            {
                int valueLen = 0x42 + name.Length * 2, len = (24 + valueLen + 7) & ~7;
                W32(rec, off, 0x30); W32(rec, off + 4, (uint)len); W32(rec, off + 16, (uint)valueLen); W16(rec, off + 20, 24);
                W64(rec, off + 24, parent | (1L << 48));          // the parent number + the sequence number
                rec[off + 24 + 0x40] = (byte)name.Length; rec[off + 24 + 0x41] = space;
                Encoding.Unicode.GetBytes(name).CopyTo(rec, off + 24 + 0x42);
                off += len;
            }
            if (residentSize.HasValue)
            {
                W32(rec, off, 0x80); W32(rec, off + 4, 24); W32(rec, off + 16, (uint)residentSize.Value); W16(rec, off + 20, 24);
                off += 24;
            }
            if (nonResidentSize.HasValue)
            {
                byte[] r = runs ?? new byte[] { 0x11, 0x10, 0x20, 0x00 };
                int len = (64 + r.Length + 7) & ~7;
                W32(rec, off, 0x80); W32(rec, off + 4, (uint)len); rec[off + 8] = 1;
                W64(rec, off + 16, startVcn); W16(rec, off + 32, 64);
                W64(rec, off + 40, (nonResidentSize.Value + 4095) / 4096 * 4096); W64(rec, off + 48, nonResidentSize.Value);
                r.CopyTo(rec, off + 64);
                off += len;
            }
            W32(rec, off, 0xFFFFFFFF);
            W32(rec, 24, (uint)(off + 8));

            // fixup: the last 2 bytes of each 512 block are moved into the table and replaced with the check number
            W16(rec, 48, 0xBEEF);
            for (int i = 1; i <= 2; i++)
            {
                rec[48 + i * 2] = rec[i * 512 - 2]; rec[48 + i * 2 + 1] = rec[i * 512 - 1];
                W16(rec, i * 512 - 2, 0xBEEF);
            }
            return rec;
        }

        private static void W16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void W32(byte[] b, int o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
        private static void W64(byte[] b, int o, long v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }

        private static NtfsRecord Parse(byte[] rec)
        {
            Assert.True(NtfsRecordParser.TryParse(rec, out var r));
            return r;
        }

        [Fact]
        public void File_NameParentSizeAndDate_AreRead()
        {
            var when = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);
            var bytes = Record(modified: when, names: new[] { (30L, "movie.mkv", (byte)1) }, nonResidentSize: 5_000_000_000);
            var r = Parse(bytes);
            Assert.True(r.InUse); Assert.False(r.IsDirectory);
            Assert.Equal(30L, r.ParentRecord);                    // without the sequence number in the high bytes
            Assert.Equal("movie.mkv", Encoding.Unicode.GetString(bytes, r.NameOffset, r.NameChars * 2));
            Assert.True(r.HasDataStart);
            Assert.Equal(5_000_000_000L, r.DataSize);
            Assert.Equal(when.ToFileTimeUtc(), r.ModifiedFileTime);
        }

        [Fact]
        public void SmallFile_KeptInsideTheRecord_HasItsSize()
        {
            var r = Parse(Record(names: new[] { (5L, "notes.txt", (byte)3) }, residentSize: 321));
            Assert.True(r.HasDataStart);
            Assert.Equal(321L, r.DataSize);
        }

        [Fact]
        public void WindowsName_IsPreferredOverTheShortDosName()
        {
            var bytes = Record(names: new[] { (5L, "PROGRA~1", (byte)2), (5L, "Program Files", (byte)1) }, dir: true);
            var r = Parse(bytes);
            Assert.True(r.IsDirectory);
            Assert.Equal("Program Files", Encoding.Unicode.GetString(bytes, r.NameOffset, r.NameChars * 2));
            Assert.Equal((byte)3, r.NameRank);
        }

        [Fact]
        public void Fixups_RestoreTheBytesAtTheEndOfEachSector()
        {
            // a long name crosses the edge of the first 512 block → without fixup it would come out corrupted
            string longName = new string('a', 200) + ".bin";
            var bytes = Record(names: new[] { (5L, longName, (byte)1) }, residentSize: 1);
            var r = Parse(bytes);
            Assert.Equal(longName, Encoding.Unicode.GetString(bytes, r.NameOffset, r.NameChars * 2));
        }

        [Fact]
        public void TornRecord_IsRejected()
        {
            var bytes = Record(names: new[] { (5L, "a.txt", (byte)1) }, residentSize: 1);
            bytes[510] ^= 0xFF;                                   // the check number no longer matches
            Assert.False(NtfsRecordParser.TryParse(bytes, out _));
        }

        [Fact]
        public void NotAFileRecord_IsRejected()
        {
            Assert.False(NtfsRecordParser.TryParse(new byte[1024], out _));
            var bad = Record(); bad[0] = (byte)'B';
            Assert.False(NtfsRecordParser.TryParse(bad, out _));
        }

        [Fact]
        public void DataRuns_AreDecoded_IncludingBackwardJumps()
        {
            // 0x10 clusters from 0x20; then 0x300 clusters 0x1000 further on; then 5 clusters 0x100 back
            var runs = NtfsRecordParser.ParseRuns(new byte[] { 0x11, 0x10, 0x20, 0x22, 0x00, 0x03, 0x00, 0x10, 0x21, 0x05, 0x00, 0xFF, 0x00 });
            Assert.Equal(3, runs.Count);
            Assert.Equal((0x20L, 0x10L), runs[0]);
            Assert.Equal((0x1020L, 0x300L), runs[1]);
            Assert.Equal((0x1020L - 0x100L, 5L), runs[2]);
        }

        // ── the index ──
        // 5 = C:\ · 20 = Users · 21 = Paul · 22 = Windows · 23 = "System Volume Information" (System) · 24 = link
        private static MftIndex BuildIndex(long keepNamesFrom = 100)
        {
            var index = new MftIndex("C:\\", 64, keepNamesFrom);
            void Add(long no, byte[] rec) { var r = Parse(rec); index.Add(no, r, rec); }
            Add(5, Record(dir: true, names: new[] { (5L, ".", (byte)3) }, attrs: 0x6));
            Add(20, Record(dir: true, names: new[] { (5L, "Users", (byte)1) }));
            Add(21, Record(dir: true, names: new[] { (20L, "Paul", (byte)1) }));
            Add(22, Record(dir: true, names: new[] { (5L, "Windows", (byte)1) }));
            Add(23, Record(dir: true, names: new[] { (5L, "System Volume Information", (byte)1) }, attrs: 0x10 | System));
            Add(24, Record(dir: true, names: new[] { (5L, "Documents and Settings", (byte)1) }, attrs: 0x10 | Reparse));
            Add(30, Record(names: new[] { (21L, "movie.mkv", (byte)1) }, nonResidentSize: 5000));
            Add(31, Record(names: new[] { (21L, "small.txt", (byte)1) }, residentSize: 10));
            Add(32, Record(names: new[] { (22L, "big.dll", (byte)1) }, nonResidentSize: 900));
            Add(33, Record(names: new[] { (5L, "pagefile.sys", (byte)1) }, nonResidentSize: 9000, attrs: 0x20 | System));
            Add(34, Record(names: new[] { (23L, "restore.dat", (byte)1) }, nonResidentSize: 7000));
            Add(35, Record(names: new[] { (24L, "ghost.bin", (byte)1) }, nonResidentSize: 7000));
            Add(36, Record(inUse: false, names: new[] { (21L, "deleted.iso", (byte)1) }, nonResidentSize: 8000));
            Add(37, Record(names: new[] { (5L, "root.bin", (byte)1) }, nonResidentSize: 300));
            return index;
        }

        [Fact]
        public void WholeDrive_ListsLargeFiles_SkippingSystemAndLinks()
        {
            var index = BuildIndex();
            var files = index.LargeFiles(index.FindDirectory("C:\\"), 100, out long seen).OrderByDescending(f => f.Size).ToList();
            Assert.Equal("movie.mkv|big.dll|root.bin", string.Join("|", files.Select(f => f.Name)));
            Assert.Equal("C:\\Users\\Paul", files[0].Directory);
            Assert.Equal("C:\\Windows", files[1].Directory);
            Assert.Equal("C:\\", files[2].Directory);
            Assert.Equal(4L, seen);          // the 3 + small.txt; no pagefile (System), none from the skipped folders, not the deleted one
        }

        [Fact]
        public void Subfolder_OnlyContainsItsOwnFiles()
        {
            var index = BuildIndex();
            int users = index.FindDirectory("c:\\users");          // upper / lower case does not matter
            Assert.True(users > 0);
            var files = index.LargeFiles(users, 100, out long seen);
            Assert.Equal(2L, seen);                                // movie.mkv + small.txt (numarat, dar sub prag)
            Assert.Equal("movie.mkv", string.Join("|", files.Select(f => f.Name)));
            Assert.Equal("C:\\Users\\Paul", files[0].Directory);
        }

        [Fact]
        public void UnknownFolder_OrOtherDrive_IsNotFound()
        {
            var index = BuildIndex();
            Assert.Equal(-1, index.FindDirectory("C:\\Nope"));
            Assert.Equal(-1, index.FindDirectory("D:\\Users"));
        }

        [Fact]
        public void TopFolders_SumEverythingBelowEachFirstLevelFolder()
        {
            var index = BuildIndex();
            var totals = index.FirstLevelTotals(index.FindDirectory("C:\\"), out long seen).OrderByDescending(t => t.Size).ToList();
            Assert.Equal(2, totals.Count);
            Assert.Equal("C:\\Users", totals[0].Path); Assert.Equal(5010L, totals[0].Size); Assert.Equal(2, totals[0].Files);
            Assert.Equal("C:\\Windows", totals[1].Path); Assert.Equal(900L, totals[1].Size);
            Assert.Equal(4L, seen);
        }

        [Fact]
        public void SmallFiles_DoNotKeepTheirNames_ButStillCountInTotals()
        {
            var index = BuildIndex(keepNamesFrom: 1000);
            var files = index.LargeFiles(index.FindDirectory("C:\\"), 1, out long seen);
            Assert.Equal("movie.mkv", string.Join("|", files.Select(f => f.Name)));   // only the one above the threshold has a name
            Assert.Equal(4L, seen);
            Assert.Equal(5010L, index.FirstLevelTotals(index.FindDirectory("C:\\"), out _).First(t => t.Path == "C:\\Users").Size);
        }

        [Fact]
        public void SizeStoredInAContinuationRecord_ReachesTheFile()
        {
            // a very fragmented file: the base record has only the name, the start of the data is in another record
            foreach (bool continuationFirst in new[] { false, true })
            {
                var index = new MftIndex("C:\\", 64, 100);
                var steps = new List<(long no, byte[] rec)>
                {
                    (5, Record(dir: true, names: new[] { (5L, ".", (byte)3) })),
                    (40, Record(names: new[] { (5L, "huge.vhdx", (byte)1) })),
                    (41, Record(baseRecord: 40, nonResidentSize: 123_456_789)),
                };
                if (continuationFirst) steps.Reverse();
                foreach (var (no, rec) in steps) { var r = Parse(rec); index.Add(no, r, rec); }
                var files = index.LargeFiles(index.FindDirectory("C:\\"), 100, out _);
                Assert.Equal(1, files.Count);
                Assert.Equal("huge.vhdx", files[0].Name);
                Assert.Equal(123_456_789L, files[0].Size);
            }
        }

        [Fact]
        public void ContinuationOfTheMftItself_IsRecognized()
        {
            // the continuations of $MFT (record 0) have base number 0, but the full reference is not zero
            var rec = Record(noStdInfo: true, nonResidentSize: 1, startVcn: 0x4000, runs: new byte[] { 0x21, 0x40, 0x00, 0x10, 0x00 });
            rec[32 + 6] = 1;                                       // only the sequence number; the record number stays 0
            Assert.True(NtfsRecordParser.TryParse(rec, out var r));
            Assert.True(r.IsContinuation);
            Assert.Equal(0L, r.BaseRecord);
            Assert.Equal(0x4000L, r.DataStartVcn);
            Assert.False(r.HasDataStart);                          // the size is written only in the first fragment
            var runs = NtfsRecordParser.ParseRuns(rec.AsSpan(r.RunsOffset, r.RunsLength));
            Assert.Equal(1, runs.Count);
            Assert.Equal((0x1000L, 0x40L), runs[0]);
        }

        [Fact]
        public void FolderLoop_DoesNotHang()
        {
            // corrupted index: two folders that have each other as parent
            var index = new MftIndex("C:\\", 64, 1);
            void Add(long no, byte[] rec) { var r = Parse(rec); index.Add(no, r, rec); }
            Add(5, Record(dir: true, names: new[] { (5L, ".", (byte)3) }));
            Add(20, Record(dir: true, names: new[] { (21L, "a", (byte)1) }));
            Add(21, Record(dir: true, names: new[] { (20L, "b", (byte)1) }));
            Add(30, Record(names: new[] { (20L, "x.bin", (byte)1) }, nonResidentSize: 500));
            Assert.Equal(0, index.LargeFiles(index.FindDirectory("C:\\"), 1, out long seen).Count);
            Assert.Equal(0L, seen);
        }
    }
}
