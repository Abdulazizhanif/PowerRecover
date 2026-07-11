namespace PowerRecover.Engine;

/// <summary>Validates that bytes at a header position look like a REAL
/// file of this type, not a random signature match in noise.</summary>
public delegate bool HeaderValidator(byte[] buf, int fileStartInBuf, int avail);

public sealed class FileSignature
{
    public required string Name { get; init; }
    public required string Ext { get; init; }
    public required byte[] Header { get; init; }
    public byte[]? Footer { get; init; }
    public int FooterExtra { get; init; }
    public int HeaderOffset { get; init; }
    public long MaxSize { get; init; } = 50 * 1024 * 1024;
    public bool LastFooter { get; init; }

    /// <summary>Exact size stored in the file header: (offset, byteLen),
    /// little-endian. Used by BMP.</summary>
    public (int Offset, int Length)? SizeField { get; init; }

    /// <summary>Extra structural check to reject false positives.
    /// Short signatures (2-4 bytes) match constantly in random data,
    /// so they MUST validate surrounding structure.</summary>
    public HeaderValidator? Validate { get; init; }

    public static readonly FileSignature[] All =
    {
        new()
        {
            Name="JPEG image", Ext="jpg",
            Header=new byte[]{0xFF,0xD8,0xFF},
            Footer=new byte[]{0xFF,0xD9}, MaxSize=30L*1024*1024,
            Validate=(b,p,n) => n > 3 && b[p+3] is
                (>= 0xC0 and <= 0xCF) or 0xDB or 0xDD
                or (>= 0xE0 and <= 0xEF) or 0xFE,
        },
        new()
        {
            Name="PNG image", Ext="png",
            Header=new byte[]{0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A},
            Footer=new byte[]{0x49,0x45,0x4E,0x44}, FooterExtra=8,
            MaxSize=40L*1024*1024,
        },
        new()
        {
            Name="GIF image", Ext="gif",
            Header=new byte[]{0x47,0x49,0x46,0x38,0x39,0x61},
            Footer=new byte[]{0x00,0x3B}, MaxSize=20L*1024*1024,
        },
        new()
        {
            Name="BMP image", Ext="bmp",
            Header=new byte[]{0x42,0x4D},
            SizeField=(2, 4),
            MaxSize=60L*1024*1024,
            Validate=(b,p,n) =>
            {
                if (n < 18) return false;
                uint dib = BitConverter.ToUInt32(b, p + 14);
                return dib is 12 or 40 or 52 or 56 or 64 or 108 or 124;
            },
        },
        new()
        {
            Name="PDF document", Ext="pdf",
            Header=new byte[]{0x25,0x50,0x44,0x46,0x2D},
            Footer=new byte[]{0x25,0x25,0x45,0x4F,0x46},
            LastFooter=true, MaxSize=100L*1024*1024,
        },
        new()
        {
            Name="ZIP / Office", Ext="zip",
            Header=new byte[]{0x50,0x4B,0x03,0x04},
            Footer=new byte[]{0x50,0x4B,0x05,0x06}, FooterExtra=22,
            MaxSize=200L*1024*1024,
        },
        new()
        {
            Name="RAR archive", Ext="rar",
            Header=new byte[]{0x52,0x61,0x72,0x21,0x1A,0x07},
            MaxSize=200L*1024*1024,
        },
        new()
        {
            Name="7-Zip archive", Ext="7z",
            Header=new byte[]{0x37,0x7A,0xBC,0xAF,0x27,0x1C},
            MaxSize=200L*1024*1024,
        },
        new()
        {
            Name="MS Office legacy", Ext="doc",
            Header=new byte[]{0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1},
            MaxSize=50L*1024*1024,
        },
        new()
        {
            Name="MP3 audio", Ext="mp3",
            Header=new byte[]{0x49,0x44,0x33},
            MaxSize=30L*1024*1024,
            Validate=(b,p,n) => n > 9
                && b[p+3] is 2 or 3 or 4 && b[p+4] == 0
                && b[p+6] < 0x80 && b[p+7] < 0x80
                && b[p+8] < 0x80 && b[p+9] < 0x80,
        },
        new()
        {
            Name="MP4 video", Ext="mp4",
            Header=new byte[]{0x66,0x74,0x79,0x70},
            HeaderOffset=4, MaxSize=500L*1024*1024,
            Validate=(b,p,n) =>
            {
                if (n < 8) return false;
                uint box = (uint)(b[p] << 24 | b[p+1] << 16
                                | b[p+2] << 8 | b[p+3]);
                return box >= 8 && box <= 1024;
            },
        },
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// RecoveredFile — single definition, updated with new fields
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RecoveredFile
{
    // Original fields (unchanged)
    public required string Name { get; set; }
    public required string Ext  { get; set; }
    public long   Offset        { get; set; }
    public long   Size          { get; set; }
    public string Method        { get; set; } = "Carve";
    public bool   Deleted       { get; set; }
    public byte[]? Data         { get; set; }

    // NTFS run-list / resident data (null for carved files)
    public List<(long lengthClusters, long startCluster)>? Runs { get; set; }
    public byte[]? ResidentData   { get; set; }
    public int    ClusterSize     { get; set; }
    public long   PartitionOffset { get; set; }

    // NEW: folder path rebuilt from MFT parent references or FAT directory tree
    // Empty string for carved files where the path is unknown.
    public string FolderPath     { get; set; } = "";

    // NEW: MFT record index — used by NtfsScanner.ResolvePaths(). -1 = not an NTFS file.
    public long   MftRecordIndex { get; set; } = -1;

    // NEW: parent MFT record number from $FILE_NAME attribute.
    public long   ParentMftRef   { get; set; } = 5;

    // NEW: 0-100 recovery quality score. -1 = not yet computed.
    // Set by ConfidenceScorer.Score(rf) after the file is found.
    public int    Confidence     { get; set; } = -1;

    // Computed display properties (used by DataGrid bindings)
    public string FullPath => string.IsNullOrEmpty(FolderPath)
        ? Name : $"{FolderPath}\\{Name}";

    public string SizeText
    {
        get
        {
            if (Size >= 1L << 30) return $"{Size / (double)(1L << 30):F2} GB";
            if (Size >= 1L << 20) return $"{Size / (double)(1L << 20):F1} MB";
            if (Size >= 1L << 10) return $"{Size / (double)(1L << 10):F0} KB";
            return $"{Size} B";
        }
    }

    public string Status          => Deleted ? "deleted" : "ok";
    public string ConfidenceText  => Confidence >= 0 ? $"{Confidence}%" : "—";
}

// ─────────────────────────────────────────────────────────────────────────────
// CarveScanner
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// High-performance signature carver with structural validation.
/// Reads in large chunks with a small overlap so signatures straddling a
/// chunk boundary are still found; scans with Span&lt;byte&gt; for speed.
/// </summary>
public sealed class CarveScanner
{
    private const int CHUNK   = 32 * 1024 * 1024;
    private const int OVERLAP = 1024;
    private const int MIN_FILE = 64;

    private readonly RawDisk _disk;
    private readonly FileSignature[] _sigs;

    public event Action<string>? Log;
    public event Action<long, long>? Progress;

    // FIX: parameter type is IEnumerable<FileSignature>, NOT IEnumerable<ExtendedSignatures.All>
    // Pass ExtendedSignatures.All (the array property) at the call site in MainWindow.
    public CarveScanner(RawDisk disk, IEnumerable<FileSignature> sigs)
    {
        _disk = disk;
        _sigs = sigs.ToArray();
    }

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct)
    {
        long total = _disk.Length;
        byte[] buf = new byte[CHUNK + OVERLAP];
        long pos    = 0;
        int  fileNo = 0;

        var lastEnd = new Dictionary<string, long>();

        while (pos < total && !ct.IsCancellationRequested)
        {
            int want = (int)Math.Min(CHUNK + OVERLAP, total - pos);
            int got  = _disk.ReadAt(pos, buf, want, out int bad);
            if (bad > 0)
                Log?.Invoke($"Skipped {bad} bad sector(s) near {pos:N0}");
            if (got <= 0) break;

            int scanLimit = Math.Min(got, CHUNK);
            var hits = FindHeaders(buf, got, scanLimit, pos);
            if (hits.Count > 0)
                Log?.Invoke($"Chunk @{pos:N0}: {hits.Count} candidate(s)");

            foreach (var (fileStart, sig) in hits)
            {
                if (ct.IsCancellationRequested) break;
                if (lastEnd.TryGetValue(sig.Ext, out long end)
                    && fileStart < end) continue;

                byte[]? data;
                try
                {
                    data = Carve(fileStart, sig, total);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Carve failed ({sig.Ext} @ {fileStart:N0})"
                                + $": {ex.GetType().Name} - {ex.Message}");
                    continue;
                }
                if (data == null || data.Length <= MIN_FILE) continue;

                lastEnd[sig.Ext] = fileStart + data.Length;
                fileNo++;
                yield return new RecoveredFile
                {
                    Name   = $"{fileNo:D5}_{fileStart:X12}.{sig.Ext}",
                    Ext    = sig.Ext,
                    Offset = fileStart,
                    Size   = data.Length,
                    Data   = data,
                    Method = "Carve",
                };
            }

            pos += CHUNK;
            Progress?.Invoke(Math.Min(pos, total), total);
        }
    }

    /// <summary>Finds + VALIDATES signature headers in one buffer.
    /// All Span work happens here, safely outside the iterator (CS4007).</summary>
    private List<(long, FileSignature)> FindHeaders(
        byte[] buf, int got, int scanLimit, long pos)
    {
        var hits = new List<(long, FileSignature)>();
        ReadOnlySpan<byte> span = buf.AsSpan(0, got);

        foreach (var sig in _sigs)
        {
            int from = 0;
            while (from < scanLimit)
            {
                int idx = span.Slice(from).IndexOf(sig.Header);
                if (idx < 0) break;
                idx += from;
                if (idx >= scanLimit) break;
                from = idx + 1;

                int startInBuf = idx - sig.HeaderOffset;
                if (startInBuf < 0) continue;
                long fileStart = pos + startInBuf;

                if (sig.Validate != null &&
                    !sig.Validate(buf, startInBuf, got - startInBuf))
                    continue;

                hits.Add((fileStart, sig));
            }
        }
        hits.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return hits;
    }

    private byte[]? Carve(long start, FileSignature sig, long total)
    {
        long maxLen = Math.Min(sig.MaxSize, total - start);

        if (sig.SizeField.HasValue)
        {
            var (fOff, fLen) = sig.SizeField.Value;
            byte[] head = ReadRange(start, fOff + fLen);
            if (head.Length < fOff + fLen) return null;
            long size = 0;
            for (int i = 0; i < fLen; i++)
                size |= (long)head[fOff + i] << (8 * i);
            if (size < MIN_FILE || size > maxLen) return null;
            return ReadRange(start, (int)size);
        }

        if (sig.Footer == null)
        {
            const long HARD_CAP = 50 * 1024 * 1024;
            int allocLen = (int)Math.Min(maxLen, HARD_CAP);
            byte[] raw = ReadRange(start, allocLen);
            int end = raw.Length;
            while (end > 0 && raw[end - 1] == 0) end--;
            if (end <= MIN_FILE) return null;
            Array.Resize(ref raw, end);
            return raw;
        }

        byte[] footer = sig.Footer;
        using var ms  = new MemoryStream();
        byte[] tail   = Array.Empty<byte>();
        long p        = start;
        long bestEnd  = -1;
        long scanMax  = Math.Min(maxLen, total - start);

        while (ms.Length < scanMax)
        {
            int want  = (int)Math.Min(CHUNK, scanMax - ms.Length);
            if (want <= 0) break;
            byte[] block = ReadRange(p, want);
            if (block.Length == 0) break;

            byte[] window;
            long windowBase;
            if (tail.Length > 0)
            {
                window = new byte[tail.Length + block.Length];
                Buffer.BlockCopy(tail,  0, window, 0,           tail.Length);
                Buffer.BlockCopy(block, 0, window, tail.Length, block.Length);
                windowBase = ms.Length - tail.Length;
            }
            else
            {
                window     = block;
                windowBase = ms.Length;
            }

            ReadOnlySpan<byte> wspan = window;
            int idx = sig.LastFooter
                ? wspan.LastIndexOf(footer)
                : wspan.IndexOf(footer);
            if (idx >= 0)
            {
                bestEnd = windowBase + idx + footer.Length + sig.FooterExtra;
                if (!sig.LastFooter)
                {
                    ms.Write(block, 0, block.Length);
                    break;
                }
            }

            ms.Write(block, 0, block.Length);
            p += block.Length;
            int keep = Math.Min(footer.Length - 1, block.Length);
            tail = keep > 0 ? block[^keep..] : Array.Empty<byte>();
            if (block.Length < want) break;
        }

        if (bestEnd > ms.Length && bestEnd <= maxLen)
        {
            int need = (int)(bestEnd - ms.Length);
            byte[] extraBytes = ReadRange(start + ms.Length, need);
            ms.Write(extraBytes, 0, extraBytes.Length);
        }

        if (bestEnd < 0 || bestEnd > ms.Length) return null;
        byte[] result = new byte[bestEnd];
        ms.Position   = 0;
        int read = ms.Read(result, 0, (int)bestEnd);
        return read == bestEnd ? result : null;
    }

    private byte[] ReadRange(long offset, int count)
    {
        byte[] buf = new byte[count];
        int got = _disk.ReadAt(offset, buf, count, out _);
        if (got < count) Array.Resize(ref buf, got);
        return buf;
    }
}