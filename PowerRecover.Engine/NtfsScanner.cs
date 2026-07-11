using System.Text;

namespace PowerRecover.Engine;

/// <summary>
/// Parses an NTFS Master File Table to recover files WITH their original
/// names, sizes, and folder paths - including deleted files whose MFT
/// records have not yet been overwritten.
///
/// NEW in this version:
///   - Extracts ParentFileReferenceNumber from $FILE_NAME attribute
///   - Caches MFT record# → (name, parentRef) in _mftNames
///   - ResolvePaths() does a second pass to build full folder paths
/// </summary>
public sealed class NtfsScanner
{
    private readonly RawDisk _disk;
    private readonly long    _partitionOffset;

    public event Action<string>?    Log;
    public event Action<long, long>? Progress;

    // Geometry
    private int  _bytesPerSector;
    private int  _clusterSize;
    private long _mftOffset;
    private int  _recordSize;

    // NEW: MFT record# → (filename, parentMftRecord#) — used to rebuild paths
    private readonly Dictionary<long, (string name, long parentRef)> _mftNames = new();

    public NtfsScanner(RawDisk disk, long partitionOffset = 0)
    {
        _disk            = disk;
        _partitionOffset = partitionOffset;
    }

    private static ushort U16(ReadOnlySpan<byte> b, int o)
        => BitConverter.ToUInt16(b.Slice(o, 2));
    private static uint U32(ReadOnlySpan<byte> b, int o)
        => BitConverter.ToUInt32(b.Slice(o, 4));
    private static ulong U64(ReadOnlySpan<byte> b, int o)
        => BitConverter.ToUInt64(b.Slice(o, 8));

    public bool ReadBootSector()
    {
        byte[] boot = new byte[512];
        _disk.ReadAt(_partitionOffset, boot, 512, out _);

        if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS")
        {
            Log?.Invoke("Not an NTFS volume at this offset.");
            return false;
        }

        _bytesPerSector    = U16(boot, 11);
        int sectorsPerCluster = boot[13];
        ulong mftCluster   = U64(boot, 48);
        _clusterSize       = sectorsPerCluster * _bytesPerSector;

        sbyte raw = (sbyte)boot[64];
        _recordSize = raw < 0 ? 1 << (-raw) : raw * _clusterSize;

        _mftOffset = _partitionOffset + (long)mftCluster * _clusterSize;
        Log?.Invoke($"NTFS: cluster={_clusterSize} MFT@{_mftOffset:N0} " +
                    $"recordSize={_recordSize}");
        return true;
    }

    private static void ApplyFixup(byte[] rec, int sectorSize)
    {
        int usaOff = U16(rec, 4);
        int usaCnt = U16(rec, 6);
        if (usaCnt == 0) return;
        for (int i = 1; i < usaCnt; i++)
        {
            int src = usaOff + i * 2;
            int pos = i * sectorSize - 2;
            if (pos + 2 <= rec.Length && src + 2 <= rec.Length)
            {
                rec[pos]     = rec[src];
                rec[pos + 1] = rec[src + 1];
            }
        }
    }

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct,
                                           long maxRecords = 200_000)
    {
        if (_recordSize == 0 && !ReadBootSector())
            yield break;

        byte[] rec = new byte[_recordSize];
        for (long i = 0; i < maxRecords && !ct.IsCancellationRequested; i++)
        {
            long off = _mftOffset + i * _recordSize;
            if (off >= _disk.Length) break;

            int got = _disk.ReadAt(off, rec, _recordSize, out _);
            if (got < _recordSize) break;

            if (rec[0] != (byte)'F' || rec[1] != (byte)'I' ||
                rec[2] != (byte)'L' || rec[3] != (byte)'E')
                continue;

            byte[] work = (byte[])rec.Clone();
            ApplyFixup(work, _bytesPerSector);

            // FIX: pass record index i so ParseRecord can cache it
            var rf = ParseRecord(work, i);
            if (rf != null) yield return rf;

            if ((i & 0x3FF) == 0) Progress?.Invoke(i, maxRecords);
        }
    }

    // FIX: added recordIndex parameter
    private RecoveredFile? ParseRecord(byte[] rec, long recordIndex)
    {
        ushort flags  = U16(rec, 22);
        bool deleted  = (flags & 0x01) == 0;
        bool isDir    = (flags & 0x02) != 0;
        if (isDir) return null;

        int attrOff   = U16(rec, 20);
        string? name  = null;
        long parentRef = 5;          // default to root directory (MFT record 5)
        long size      = 0;
        var  runs      = new List<(long len, long start)>();
        byte[]? resident = null;

        int off = attrOff;
        while (off + 4 <= rec.Length)
        {
            uint atype = U32(rec, off);
            if (atype == 0xFFFFFFFF) break;
            uint alen  = U32(rec, off + 4);
            if (alen == 0 || off + alen > rec.Length) break;

            byte nonResident = rec[off + 8];
            byte nameLen     = rec[off + 9];

            if (atype == 0x30) // $FILE_NAME
            {
                int contentOff = U16(rec, off + 20);
                int b = off + contentOff;
                if (b + 66 <= rec.Length)
                {
                    // FIX: extract ParentFileReferenceNumber (bytes 0-7 of $FILE_NAME content)
                    // Low 48 bits = MFT record number; high 16 bits = sequence number (ignore)
                    ulong parentFrn = U64(rec, b);
                    parentRef = (long)(parentFrn & 0x0000FFFFFFFFFFFF);

                    int  fnLen = rec[b + 64];
                    byte ns    = rec[b + 65];
                    int  bytes = fnLen * 2;
                    if (b + 66 + bytes <= rec.Length)
                    {
                        string cand = Encoding.Unicode.GetString(rec, b + 66, bytes);
                        if (name == null || ns != 2) name = cand;
                    }
                }
            }
            else if (atype == 0x80 && nameLen == 0) // unnamed $DATA
            {
                if (nonResident == 0)
                {
                    uint clen  = U32(rec, off + 16);
                    int  coff  = U16(rec, off + 20);
                    size = clen;
                    if (off + coff + clen <= rec.Length)
                    {
                        resident = new byte[clen];
                        Array.Copy(rec, off + coff, resident, 0, clen);
                    }
                }
                else
                {
                    size = (long)U64(rec, off + 48);
                    int runOff = U16(rec, off + 32);
                    ParseRunList(rec, off + runOff, runs);
                }
            }
            off += (int)alen;
        }

        if (name == null) return null;

        // FIX: cache this record's name + parent so ResolvePaths() can walk upward
        _mftNames[recordIndex] = (name, parentRef);

        return new RecoveredFile
        {
            Name            = name,
            Ext             = GetExt(name),
            Size            = size,
            Deleted         = deleted,
            Method          = "NTFS-MFT",
            Runs            = runs.Count > 0 ? runs : null,
            ResidentData    = resident,
            ClusterSize     = _clusterSize,
            PartitionOffset = _partitionOffset,
            Offset          = runs.Count > 0
                              ? _partitionOffset + runs[0].start * _clusterSize : 0,
            // NEW fields
            MftRecordIndex  = recordIndex,
            ParentMftRef    = parentRef,
            // FolderPath is left empty here — call ResolvePaths() after scan completes
        };
    }

    /// <summary>
    /// Second pass: resolves FolderPath for every file in the list by
    /// walking the cached MFT name→parent map upward to the root.
    /// Call once after Scan() has finished collecting all results.
    /// </summary>
    public void ResolvePaths(IEnumerable<RecoveredFile> files)
    {
        foreach (var rf in files)
        {
            if (rf.MftRecordIndex < 0) continue;
            rf.FolderPath = BuildPath(rf.ParentMftRef, 0);
        }
    }

    private string BuildPath(long mftRef, int depth)
    {
        if (depth > 64)  return "\\...";     // loop/corruption guard
        if (mftRef <= 5) return "\\";        // reached root directory
        if (!_mftNames.TryGetValue(mftRef, out var entry)) return "\\?";

        string parent = BuildPath(entry.parentRef, depth + 1);
        return parent == "\\" ? $"\\{entry.name}" : $"{parent}\\{entry.name}";
    }

    private void ParseRunList(byte[] rec, int off,
                              List<(long, long)> runs)
    {
        long prevStart = 0;
        while (off < rec.Length)
        {
            byte header = rec[off];
            if (header == 0) break;
            int lenBytes = header & 0x0F;
            int offBytes = (header >> 4) & 0x0F;
            off++;
            if (lenBytes == 0 || off + lenBytes + offBytes > rec.Length) break;

            long length = 0;
            for (int i = 0; i < lenBytes; i++)
                length |= (long)rec[off + i] << (8 * i);
            off += lenBytes;

            long startRel = 0;
            for (int i = 0; i < offBytes; i++)
                startRel |= (long)rec[off + i] << (8 * i);
            // sign-extend
            if (offBytes > 0 && (rec[off + offBytes - 1] & 0x80) != 0)
                startRel |= -1L << (8 * offBytes);
            off += offBytes;

            prevStart += startRel;
            runs.Add((length, prevStart));
        }
    }

    private static string GetExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1
            ? name[(dot + 1)..].ToLowerInvariant() : "bin";
    }
}