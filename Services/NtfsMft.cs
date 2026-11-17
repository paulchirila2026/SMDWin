using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SMDWin.Services
{
    /// <summary>What matters from an MFT record (one per NTFS file or folder).</summary>
    public struct NtfsRecord
    {
        public bool InUse, IsDirectory;
        /// <summary>true = continuation record: its attributes belong to record <see cref="BaseRecord"/>
        /// (a file with many attributes or heavily fragmented takes several records).</summary>
        public bool IsContinuation;
        public long BaseRecord;

        public bool HasStdInfo;
        public uint Attributes;
        public long ModifiedFileTime;

        public bool HasName;
        public long ParentRecord;
        /// <summary>Where the name is in the record (UTF-16) and how many characters it has.</summary>
        public int NameOffset, NameChars;
        /// <summary>How good the name is: 3 = Windows name, 2 = POSIX, 1 = short DOS name (8.3).</summary>
        public byte NameRank;

        /// <summary>true if the record contains the start of the file data — only there is the size written.</summary>
        public bool HasDataStart;
        public long DataSize;

        /// <summary>Fragment list of the non-resident data attribute (used to read the MFT itself).</summary>
        public int RunsOffset, RunsLength;
        public long DataStartVcn;
    }

    /// <summary>
    /// Interpretation of the MFT of an NTFS volume, without disk access: takes the bytes of a record and extracts the name,
    /// parent folder, size and modification date. The format is the public NTFS one ("FILE" records of 1024 or 4096 bytes,
    /// with the attributes $STANDARD_INFORMATION 0x10, $FILE_NAME 0x30, $DATA 0x80).
    /// </summary>
    public static class NtfsRecordParser
    {
        private const uint FileSignature = 0x454C4946;   // „FILE”
        private const int FixupStride = 512;
        public const uint AttrSystem = 0x4, AttrReparsePoint = 0x400;

        /// <summary>
        /// Reads one record. First it repairs the "fixups": on disk the last 2 bytes of every 512-byte block are replaced by a
        /// check number, and the real values live in a table in the header. If the check number does not match, the record was
        /// half-written and is ignored. Modifies <paramref name="rec"/> in place.
        /// </summary>
        public static bool TryParse(Span<byte> rec, out NtfsRecord r)
        {
            r = default;
            if (rec.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(rec) != FileSignature) return false;

            int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(4));
            int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(6));
            if (usaCount > 1)
            {
                if (usaOffset + usaCount * 2 > rec.Length) return false;
                ushort check = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(usaOffset));
                for (int i = 1; i < usaCount; i++)
                {
                    int tail = i * FixupStride - 2;
                    if (tail + 2 > rec.Length) break;
                    if (BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(tail)) != check) return false;
                    rec[tail] = rec[usaOffset + i * 2];
                    rec[tail + 1] = rec[usaOffset + i * 2 + 1];
                }
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(22));
            r.InUse = (flags & 0x1) != 0;
            r.IsDirectory = (flags & 0x2) != 0;
            // reference = record number (48 bits) + sequence number; all 0 = base record.
            // $MFT continuations have number 0 but a non-zero sequence, which is why the whole value is checked.
            ulong baseRef = BinaryPrimitives.ReadUInt64LittleEndian(rec.Slice(32));
            r.IsContinuation = baseRef != 0;
            r.BaseRecord = (long)(baseRef & 0xFFFFFFFFFFFF);

            int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(24)), (uint)rec.Length);
            int off = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(20));
            while (off + 16 <= used)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(off));
                if (type == 0xFFFFFFFF) break;
                long len = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(off + 4));
                if (len < 16 || off + len > rec.Length) break;
                int alen = (int)len;
                bool nonResident = rec[off + 8] != 0;
                int attrNameLen = rec[off + 9];
                var attr = rec.Slice(off, alen);

                if (!nonResident && alen >= 24)
                {
                    int valueLen = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16)), int.MaxValue);
                    int valueOff = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(20));
                    bool valueOk = valueOff >= 24 && valueOff <= alen && valueLen <= alen - valueOff;

                    if (type == 0x10 && valueOk && valueLen >= 0x24)
                    {
                        r.HasStdInfo = true;
                        r.ModifiedFileTime = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(valueOff + 8));
                        r.Attributes = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(valueOff + 0x20));
                    }
                    else if (type == 0x30 && valueOk && valueLen >= 0x42)
                    {
                        int chars = attr[valueOff + 0x40];
                        byte space = attr[valueOff + 0x41];
                        byte rank = space == 2 ? (byte)1 : space == 0 ? (byte)2 : (byte)3;
                        if (0x42 + chars * 2 <= valueLen && rank > r.NameRank)
                        {
                            r.HasName = true;
                            r.NameRank = rank;
                            r.ParentRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(attr.Slice(valueOff)) & 0xFFFFFFFFFFFF);
                            r.NameOffset = off + valueOff + 0x42;
                            r.NameChars = chars;
                        }
                    }
                    else if (type == 0x80 && attrNameLen == 0)
                    {
                        // small data, kept right in the record
                        r.HasDataStart = true;
                        r.DataSize = BinaryPrimitives.ReadUInt32LittleEndian(attr.Slice(16));
                    }
                }
                else if (nonResident && type == 0x80 && attrNameLen == 0 && alen >= 64)
                {
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(16));
                    int runOff = BinaryPrimitives.ReadUInt16LittleEndian(attr.Slice(32));
                    if (startVcn == 0)
                    {
                        r.HasDataStart = true;
                        r.DataSize = BinaryPrimitives.ReadInt64LittleEndian(attr.Slice(48));
                    }
                    if (runOff >= 64 && runOff < alen && r.RunsLength == 0)
                    {
                        r.RunsOffset = off + runOff;
                        r.RunsLength = alen - runOff;
                        r.DataStartVcn = startVcn;
                    }
                }
                off += alen;
            }
            return true;
        }

        /// <summary>
        /// Fragment list ("data runs") of an attribute: pairs (start cluster, cluster count). Each entry has a header byte
        /// (how many bytes the length takes, how many the offset), and the offset is relative to the previous fragment and can
        /// be negative.
        /// </summary>
        public static List<(long Lcn, long Clusters)> ParseRuns(ReadOnlySpan<byte> runs)
        {
            var list = new List<(long, long)>();
            long lcn = 0;
            int i = 0;
            while (i < runs.Length)
            {
                byte header = runs[i++];
                if (header == 0) break;
                int lenSize = header & 0x0F, offSize = header >> 4;
                if (lenSize == 0 || lenSize > 8 || offSize > 8 || i + lenSize + offSize > runs.Length) break;
                long clusters = 0;
                for (int b = 0; b < lenSize; b++) clusters |= (long)runs[i + b] << (8 * b);
                i += lenSize;
                if (offSize == 0) continue;   // "sparse" fragment (no data on disk) — does not occur in the MFT
                long delta = 0;
                for (int b = 0; b < offSize; b++) delta |= (long)runs[i + b] << (8 * b);
                if ((runs[i + offSize - 1] & 0x80) != 0 && offSize < 8) delta |= -1L << (8 * offSize);   // numar negativ
                i += offSize;
                lcn += delta;
                if (clusters <= 0 || lcn < 0) break;
                list.Add((lcn, clusters));
            }
            return list;
        }
    }

    /// <summary>A file found in the index.</summary>
    public sealed class MftFile
    {
        public string Name = "";
        public string Directory = "";
        public long Size;
        public DateTime ModifiedLocal;
    }

    /// <summary>Total of a folder on the first level of the scanned path.</summary>
    public sealed class MftFolderTotal
    {
        public string Path = "";
        public long Size;
        public int Files;
    }

    /// <summary>
    /// Everything read from the MFT, kept compact (one slot per record): parent, size, date, attributes and — only where
    /// needed — the name. Large files and per-folder totals are produced from it without any other disk read.
    /// Same rules as the normal scan: folders and files with the "System" attribute or that are links (reparse points)
    /// are skipped, together with everything they contain.
    /// </summary>
    public sealed class MftIndex
    {
        public const int RootRecord = 5;
        private const int FirstUserRecord = 16;          // 0–15 are the internal NTFS files ($MFT, $LogFile…)
        private const byte FInUse = 1, FDir = 2, FName = 4, FSize = 8, RankShift = 5;

        private readonly string _driveRoot;              // „C:\”
        private readonly long _keepNamesFrom;
        private readonly int _count;
        private readonly uint[] _parent;
        private readonly long[] _size, _modified;
        private readonly uint[] _attrs;
        private readonly byte[] _flags;
        private readonly int[] _nameOff;
        private readonly byte[] _nameLen;
        private char[] _names = new char[1 << 16];
        private int _namesUsed;

        /// <param name="recordCount">How many records the table has.</param>
        /// <param name="keepFileNamesFrom">File names are kept only from this size up (folders always keep their name) —
        /// otherwise a disk with millions of files would take hundreds of MB of memory.</param>
        public MftIndex(string driveRoot, long recordCount, long keepFileNamesFrom)
        {
            _driveRoot = driveRoot.EndsWith('\\') ? driveRoot : driveRoot + "\\";
            _keepNamesFrom = keepFileNamesFrom;
            _count = (int)Math.Min(recordCount, int.MaxValue - 64);
            _parent = new uint[_count];
            _size = new long[_count];
            _modified = new long[_count];
            _attrs = new uint[_count];
            _flags = new byte[_count];
            _nameOff = new int[_count];
            _nameLen = new byte[_count];
        }

        public int RecordCount => _count;

        /// <summary>Adds a record that was read. The attributes of a continuation record are moved to the base record.</summary>
        public void Add(long recordNo, in NtfsRecord r, ReadOnlySpan<byte> recordBytes)
        {
            if (!r.InUse || recordNo < 0 || recordNo >= _count) return;
            long target = r.IsContinuation ? r.BaseRecord : recordNo;
            if (target < 0 || target >= _count) return;
            int t = (int)target;

            if (!r.IsContinuation)
            {
                _flags[t] |= FInUse;
                if (r.IsDirectory) _flags[t] |= FDir;
                if (r.HasStdInfo) { _attrs[t] = r.Attributes; _modified[t] = r.ModifiedFileTime; }
            }
            if (r.HasDataStart) { _size[t] = r.DataSize; _flags[t] |= FSize; }

            int rankNow = (_flags[t] >> RankShift) & 3;
            if (r.HasName && r.NameRank > rankNow)
            {
                _parent[t] = (uint)Math.Min(r.ParentRecord, uint.MaxValue);
                _flags[t] = (byte)((_flags[t] & ~(3 << RankShift)) | (r.NameRank << RankShift));
                // the name: always for folders; for files only if they are large enough or the size is not known yet
                // (it is in a continuation record, which may come later)
                bool isDir = r.IsContinuation ? (_flags[t] & FDir) != 0 : r.IsDirectory;
                bool sizeKnown = (_flags[t] & FSize) != 0;
                if (isDir || !sizeKnown || _size[t] >= _keepNamesFrom)
                {
                    int chars = Math.Min(r.NameChars, 255);
                    if (r.NameOffset + chars * 2 <= recordBytes.Length)
                    {
                        if (_namesUsed + chars > _names.Length) Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesUsed + chars));
                        MemoryMarshal.Cast<byte, char>(recordBytes.Slice(r.NameOffset, chars * 2)).CopyTo(_names.AsSpan(_namesUsed));
                        _nameOff[t] = _namesUsed;
                        _nameLen[t] = (byte)chars;
                        _namesUsed += chars;
                        _flags[t] |= FName;
                    }
                }
            }
        }

        private bool IsDir(int rec) => (_flags[rec] & (FInUse | FDir)) == (FInUse | FDir);
        private bool IsFile(int rec) => (_flags[rec] & (FInUse | FDir)) == FInUse;
        private bool HasName(int rec) => (_flags[rec] & FName) != 0;
        private ReadOnlySpan<char> NameOf(int rec) => _names.AsSpan(_nameOff[rec], _nameLen[rec]);
        private bool Skipped(int rec) => (_attrs[rec] & (NtfsRecordParser.AttrSystem | NtfsRecordParser.AttrReparsePoint)) != 0;

        /// <summary>Record of the folder at the given path, or −1 if it is not on this volume / does not exist.</summary>
        public int FindDirectory(string path)
        {
            string full = path.EndsWith('\\') ? path : path + "\\";
            if (!full.StartsWith(_driveRoot, StringComparison.OrdinalIgnoreCase)) return -1;
            int cur = RootRecord;
            foreach (string part in full.Substring(_driveRoot.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                int found = -1;
                for (int i = FirstUserRecord; i < _count; i++)
                    if (_parent[i] == (uint)cur && IsDir(i) && HasName(i) && NameOf(i).Equals(part, StringComparison.OrdinalIgnoreCase))
                    { found = i; break; }
                if (found < 0) return -1;
                cur = found;
            }
            return cur;
        }

        // For each folder: 0 = not computed, −1 = outside the scanned path (or skipped), −2 = the scanned folder itself,
        // otherwise the number of the first-level folder of the scanned path it lives in.
        private int[]? _top;
        private int _topRoot = -1;

        private int TopChildOf(int dir, int root)
        {
            if (_top == null || _topRoot != root) { _top = new int[_count]; _topRoot = root; }
            int answer = 0, steps = 0, d = dir, prev = -1;
            // walk up towards the root until a known answer
            while (true)
            {
                if (d == root) { answer = prev < 0 ? -2 : prev; break; }
                if (d < 0 || d >= _count || d < FirstUserRecord || !IsDir(d) || Skipped(d) || ++steps > 1024) { answer = -1; break; }
                if (_top[d] != 0) { answer = _top[d]; break; }
                prev = d;
                d = (int)_parent[d];
            }
            // record the answer along the whole chain walked
            int c = dir; steps = 0;
            while (c != d && c >= FirstUserRecord && c < _count && ++steps <= 1024)
            {
                _top[c] = answer;
                c = (int)_parent[c];
            }
            return dir == root ? -2 : answer;
        }

        private readonly Dictionary<int, string> _dirPaths = new();

        private string DirectoryPath(int dir)
        {
            if (dir == RootRecord) return _driveRoot;
            if (_dirPaths.TryGetValue(dir, out var known)) return known;
            var parts = new List<int>();
            int d = dir, steps = 0;
            string prefix = _driveRoot;
            while (d != RootRecord && d >= FirstUserRecord && d < _count && ++steps <= 1024)
            {
                if (_dirPaths.TryGetValue(d, out var p)) { prefix = p; break; }
                parts.Add(d);
                d = (int)_parent[d];
            }
            string path = prefix;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                path += new string(NameOf(parts[i])) + "\\";
                _dirPaths[parts[i]] = path;
            }
            return path;
        }

        /// <summary>Files of at least <paramref name="minBytes"/> in the given folder (including subfolders), plus how many
        /// files the folder contains in total.</summary>
        public List<MftFile> LargeFiles(int rootDir, long minBytes, out long filesSeen)
        {
            var list = new List<MftFile>();
            filesSeen = 0;
            for (int i = FirstUserRecord; i < _count; i++)
            {
                if (!IsFile(i) || Skipped(i)) continue;
                int parent = (int)Math.Min(_parent[i], int.MaxValue);
                if (((_flags[i] >> RankShift) & 3) == 0 || TopChildOf(parent, rootDir) == -1) continue;
                filesSeen++;
                if (_size[i] < minBytes || !HasName(i)) continue;
                DateTime modified;
                try { modified = DateTime.FromFileTime(_modified[i]); } catch (ArgumentOutOfRangeException) { modified = DateTime.MinValue; }
                list.Add(new MftFile
                {
                    Name = new string(NameOf(i)),
                    Directory = DirectoryPath(parent).TrimEnd('\\') is { Length: > 2 } dir ? dir : _driveRoot,
                    Size = _size[i],
                    ModifiedLocal = modified,
                });
            }
            return list;
        }

        /// <summary>
        /// The whole folder tree under the given folder, for the map in Storage › What uses space: each folder with its size,
        /// and files from <paramref name="bigFileBytes"/> up with name, size and date. Same skip rules as above (System, links).
        /// </summary>
        public SpaceDir BuildSpaceTree(int rootDir, string rootPath, long bigFileBytes, CancellationToken ct = default)
        {
            var root = new SpaceDir { Name = rootPath, RootPath = rootPath };
            var nodes = new Dictionary<int, SpaceDir> { [rootDir] = root };
            // folders in the scanned path (cancellation is checked every 64 Ki records)
            for (int i = FirstUserRecord; i < _count; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (i == rootDir || !IsDir(i) || Skipped(i) || !HasName(i) || ((_flags[i] >> RankShift) & 3) == 0) continue;
                if (TopChildOf(i, rootDir) == -1) continue;
                nodes[i] = new SpaceDir { Name = new string(NameOf(i)) };
            }
            foreach (var (rec, node) in nodes)
            {
                if (rec == rootDir) continue;
                if (nodes.TryGetValue((int)Math.Min(_parent[rec], int.MaxValue), out var parent) && !ReferenceEquals(parent, node))
                { node.Parent = parent; parent.Dirs.Add(node); }
            }
            // the files
            for (int i = FirstUserRecord; i < _count; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (!IsFile(i) || Skipped(i) || ((_flags[i] >> RankShift) & 3) == 0) continue;
                if (!nodes.TryGetValue((int)Math.Min(_parent[i], int.MaxValue), out var dir)) continue;
                if (dir != root && dir.Parent == null) continue;   // folder with no link to the root
                long size = _size[i];
                dir.OwnSize += size; dir.OwnFiles++;
                if (size >= bigFileBytes && HasName(i))
                {
                    DateTime modified;
                    try { modified = DateTime.FromFileTime(_modified[i]); } catch (ArgumentOutOfRangeException) { modified = DateTime.MinValue; }
                    dir.BigFiles.Add(new SpaceFile { Name = new string(NameOf(i)), Parent = dir, Size = size, Modified = modified });
                }
            }
            return root;
        }

        /// <summary>Size and file count for each direct subfolder of the given folder.</summary>
        public List<MftFolderTotal> FirstLevelTotals(int rootDir, out long filesSeen)
        {
            var size = new Dictionary<int, long>();
            var files = new Dictionary<int, int>();
            filesSeen = 0;
            for (int i = FirstUserRecord; i < _count; i++)
            {
                if (!IsFile(i) || Skipped(i) || ((_flags[i] >> RankShift) & 3) == 0) continue;
                int top = TopChildOf((int)Math.Min(_parent[i], int.MaxValue), rootDir);
                if (top == -1) continue;
                filesSeen++;
                if (top == -2) continue;          // a file directly in the scanned folder
                size[top] = size.GetValueOrDefault(top) + _size[i];
                files[top] = files.GetValueOrDefault(top) + 1;
            }
            var list = new List<MftFolderTotal>(size.Count);
            foreach (var (dir, bytes) in size)
                if (bytes > 0) list.Add(new MftFolderTotal { Path = DirectoryPath(dir).TrimEnd('\\'), Size = bytes, Files = files[dir] });
            return list;
        }
    }
}
