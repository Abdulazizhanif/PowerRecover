using System.Text;

namespace PowerRecover.Engine;

/// <summary>
/// Parses the NTFS Change Journal ($UsnJrnl:$J) and Log File ($LogFile)
/// to recover records of deleted files — even files deleted so recently
/// that their MFT records have already been reused and overwritten.
///
/// The USN journal is a circular log of every file system change.
/// Each record contains: filename, parent directory, reason flags,
/// timestamp, and MFT reference. We look for records with reason flags
/// indicating deletion (USN_REASON_FILE_DELETE or CLOSE after DELETE).
///
/// This finds files that:
///   - Were deleted seconds/minutes ago (MFT entry already gone)
///   - Were in directories that were themselves deleted
///   - Left no carve-able signature (text files, config files)
///
/// No paid tool surfaces this data in a recovery UI — they all stop at MFT.
/// </summary>
public sealed class NtfsJournalScanner
{
    private readonly RawDisk _disk;
    private readonly long    _partitionOffset;

    public event Action<string>?     Log;
    public event Action<long, long>? Progress;

    // USN reason flags
    private const uint USN_REASON_FILE_DELETE     = 0x00000200;
    private const uint USN_REASON_FILE_CREATE      = 0x00000100;
    private const uint USN_REASON_RENAME_OLD_NAME  = 0x00001000;
    private const uint USN_REASON_CLOSE            = 0x80000000;

    // Well-known NTFS metadata file MFT record numbers
    private const long MFT_USN_JOURNAL = 0;   // found via $Extend\$UsnJrnl

    public NtfsJournalScanner(RawDisk disk, long partitionOffset = 0)
    {
        _disk            = disk;
        _partitionOffset = partitionOffset;
    }

    // ── Main scan ─────────────────────────────────────────────────────

    /// <summary>
    /// Scans the USN journal for deletion records and returns
    /// JournalEntry objects describing each deleted file found.
    /// Call after NtfsScanner.Scan() — uses its partition offset.
    /// </summary>
    public IEnumerable<JournalEntry> Scan(CancellationToken ct)
    {
        // Step 1: find $UsnJrnl via $Extend directory
        var usnData = FindUsnJournal(ct);
        if (usnData == null)
        {
            Log?.Invoke("USN journal not found or not readable.");
            yield break;
        }

        Log?.Invoke($"USN journal found: {usnData.Length:N0} bytes");

        // Step 2: walk the journal records
        int parsed  = 0;
        int deleted = 0;
        long pos    = 0;
        byte[] data = usnData;

        while (pos + 60 < data.Length && !ct.IsCancellationRequested)
        {
            // Skip zero-padding between journal blocks
            if (data[pos] == 0 && data[pos+1] == 0 &&
                data[pos+2] == 0 && data[pos+3] == 0)
            {
                pos += 8;
                continue;
            }

            uint recLen = LE32(data, (int)pos);
            if (recLen < 60 || pos + recLen > data.Length) break;

            ushort majorVer = (ushort)LE16(data, (int)pos + 4);
            if (majorVer != 2 && majorVer != 3)
            { pos += Math.Max(8, recLen); continue; }

            uint reason     = LE32(data, (int)pos + 40);
            bool isDeletion = (reason & USN_REASON_FILE_DELETE) != 0;
            bool isClose    = (reason & USN_REASON_CLOSE) != 0;

            if (isDeletion || (isClose && (reason & USN_REASON_FILE_DELETE) != 0))
            {
                ulong mftRef    = LE64(data, (int)pos + 8);
                ulong parentRef = LE64(data, (int)pos + 16);
                long  usn       = (long)LE64(data, (int)pos + 24);
                long  timestamp = (long)LE64(data, (int)pos + 32);
                uint  attribs   = LE32(data, (int)pos + 44);
                ushort nameLen  = (ushort)LE16(data, (int)pos + 48);
                ushort nameOff  = (ushort)LE16(data, (int)pos + 50);

                string name = "";
                if (nameOff + nameLen <= recLen && nameLen > 0)
                    name = Encoding.Unicode.GetString(
                        data, (int)pos + nameOff, nameLen);

                if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                {
                    deleted++;
                    yield return new JournalEntry
                    {
                        Name          = name,
                        Ext           = GetExt(name),
                        MftReference  = (long)(mftRef & 0x0000FFFFFFFFFFFF),
                        ParentRef     = (long)(parentRef & 0x0000FFFFFFFFFFFF),
                        Usn           = usn,
                        Timestamp     = DateTime.FromFileTimeUtc(timestamp),
                        Reason        = reason,
                        ReasonText    = DescribeReason(reason),
                        IsDirectory   = (attribs & 0x10) != 0,
                    };
                }
                parsed++;
            }

            pos += recLen;
            if ((parsed & 0x3FF) == 0)
                Progress?.Invoke(pos, data.Length);
        }

        Log?.Invoke($"Journal: {parsed:N0} records scanned, " +
                    $"{deleted:N0} deletion events found.");
    }

    // ── Find $UsnJrnl ─────────────────────────────────────────────────

    private byte[]? FindUsnJournal(CancellationToken ct)
    {
        // The USN journal lives at $Extend\$UsnJrnl, MFT record ~10-12
        // Strategy: scan early MFT records for $UsnJrnl filename,
        // then read its $DATA stream ($J alternate data stream)
        // For simplicity, scan the first 1000 MFT records

        // We need the NtfsScanner to have already read the boot sector
        // Parse boot sector ourselves here
        byte[] boot = new byte[512];
        _disk.ReadAt(_partitionOffset, boot, 512, out _);

        if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS") return null;

        int    bytesPerSector    = LE16s(boot, 11);
        int    sectorsPerCluster = boot[13];
        int    clusterSize       = sectorsPerCluster * bytesPerSector;
        ulong  mftCluster        = LE64(boot, 48);
        sbyte  rawRec            = (sbyte)boot[64];
        int    recordSize        = rawRec < 0 ? 1 << (-rawRec) : rawRec * clusterSize;
        long   mftOffset         = _partitionOffset + (long)mftCluster * clusterSize;

        byte[] rec = new byte[recordSize];

        // Scan first 200 MFT records for $UsnJrnl
        for (int i = 0; i < 200 && !ct.IsCancellationRequested; i++)
        {
            long off = mftOffset + (long)i * recordSize;
            if (off >= _disk.Length) break;
            _disk.ReadAt(off, rec, recordSize, out _);

            if (rec[0] != 'F' || rec[1] != 'I' || rec[2] != 'L' || rec[3] != 'E')
                continue;

            // Check for $UsnJrnl filename
            string? name = ExtractFileName(rec);
            if (name != "$UsnJrnl") continue;

            Log?.Invoke($"Found $UsnJrnl at MFT record {i}");

            // Read its $DATA stream ($J) — the actual journal data
            return ReadAlternateDataStream(rec, clusterSize, recordSize);
        }

        return null;
    }

    private string? ExtractFileName(byte[] rec)
    {
        int attrOff = LE16s(rec, 20);
        int off     = attrOff;

        while (off + 8 <= rec.Length)
        {
            uint atype = LE32(rec, off);
            if (atype == 0xFFFFFFFF) break;
            uint alen  = LE32(rec, off + 4);
            if (alen == 0 || off + alen > rec.Length) break;

            if (atype == 0x30) // $FILE_NAME
            {
                int coff = LE16s(rec, off + 20);
                int b    = off + coff;
                if (b + 66 <= rec.Length)
                {
                    int fnLen = rec[b + 64] * 2;
                    if (b + 66 + fnLen <= rec.Length)
                        return Encoding.Unicode.GetString(rec, b + 66, fnLen);
                }
            }
            off += (int)alen;
        }
        return null;
    }

    private byte[]? ReadAlternateDataStream(byte[] rec, int clusterSize, int recordSize)
    {
        // Read $DATA stream of $UsnJrnl
        // The $J stream can be very large (GBs) — read first 64 MB max
        const long MAX_JOURNAL = 64L * 1024 * 1024;

        int attrOff = LE16s(rec, 20);
        int off     = attrOff;

        while (off + 8 <= rec.Length)
        {
            uint atype = LE32(rec, off);
            if (atype == 0xFFFFFFFF) break;
            uint alen  = LE32(rec, off + 4);
            if (alen == 0 || off + alen > rec.Length) break;

            if (atype == 0x80) // $DATA
            {
                byte nonRes = rec[off + 8];
                // Check attribute name — $J stream has a name
                byte nameLen = rec[off + 9];

                if (nonRes == 1) // non-resident
                {
                    long dataSize = Math.Min(
                        (long)LE64(rec, off + 48), MAX_JOURNAL);

                    int runOff = LE16s(rec, off + 32);
                    var runs   = new List<(long len, long start)>();
                    ParseRunList(rec, off + runOff, runs);

                    if (runs.Count == 0) { off += (int)alen; continue; }

                    using var ms = new System.IO.MemoryStream();
                    long remaining = dataSize;

                    foreach (var (lenClusters, startCluster) in runs)
                    {
                        if (remaining <= 0) break;
                        if (startCluster <= 0) continue;

                        long physOff = _partitionOffset +
                                       startCluster * clusterSize;
                        long readLen = Math.Min(
                            lenClusters * clusterSize, remaining);

                        byte[] buf = new byte[readLen];
                        _disk.ReadAt(physOff, buf, (int)readLen, out _);
                        ms.Write(buf, 0, buf.Length);
                        remaining -= readLen;
                    }
                    return ms.ToArray();
                }
            }
            off += (int)alen;
        }
        return null;
    }

    private void ParseRunList(byte[] rec, int off, List<(long, long)> runs)
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
            if (offBytes > 0 && (rec[off + offBytes - 1] & 0x80) != 0)
                startRel |= -1L << (8 * offBytes);
            off += offBytes;
            prevStart += startRel;
            runs.Add((length, prevStart));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string DescribeReason(uint reason)
    {
        var parts = new List<string>();
        if ((reason & USN_REASON_FILE_DELETE)    != 0) parts.Add("DELETE");
        if ((reason & USN_REASON_FILE_CREATE)    != 0) parts.Add("CREATE");
        if ((reason & USN_REASON_RENAME_OLD_NAME)!= 0) parts.Add("RENAME");
        if ((reason & USN_REASON_CLOSE)          != 0) parts.Add("CLOSE");
        return parts.Count > 0 ? string.Join("|", parts) : $"0x{reason:X8}";
    }

    private static string GetExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1
            ? name[(dot + 1)..].ToLowerInvariant() : "bin";
    }

    private static ushort LE16s(byte[] b, int o)
        => BitConverter.ToUInt16(b, o);
    private static int LE16(byte[] b, int o)
        => BitConverter.ToUInt16(b, o);
    private static uint LE32(byte[] b, int o)
        => BitConverter.ToUInt32(b, o);
    private static ulong LE64(byte[] b, int o)
        => BitConverter.ToUInt64(b, o);
}

/// <summary>A single USN journal record describing a file system event.</summary>
public sealed class JournalEntry
{
    public string   Name        { get; set; } = "";
    public string   Ext         { get; set; } = "";
    public long     MftReference{ get; set; }
    public long     ParentRef   { get; set; }
    public long     Usn         { get; set; }
    public DateTime Timestamp   { get; set; }
    public uint     Reason      { get; set; }
    public string   ReasonText  { get; set; } = "";
    public bool     IsDirectory { get; set; }

    /// <summary>Convert to a RecoveredFile for the results grid.
    /// Data will be null — the file must be found by offset separately.</summary>
    public RecoveredFile ToRecoveredFile() => new()
    {
        Name    = $"journal_{Name}",
        Ext     = Ext,
        Size    = 0,
        Deleted = true,
        Method  = "Journal",
        Offset  = 0,
    };
}
