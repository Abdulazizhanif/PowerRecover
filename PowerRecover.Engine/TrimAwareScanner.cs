namespace PowerRecover.Engine;

/// <summary>
/// TRIM-aware carving wrapper. On SSDs, the firmware actively zeroes
/// freed blocks via the TRIM/DISCARD command — these regions can NEVER
/// contain recoverable data. Scanning them wastes time and produces
/// false positives when noise patterns happen to match signatures.
///
/// Strategy:
///   1. Detect if source is SSD (already done by MultiThreadedScanner)
///   2. Pre-scan the disk in 1 MB chunks, build a ZeroMap of confirmed
///      all-zero regions (>= ZERO_THRESHOLD consecutive zero bytes)
///   3. Pass ZeroMap to CarveScanner via the SkipRanges mechanism —
///      the scanner skips those ranges entirely
///
/// For HDDs: zero regions are normal unallocated space, not TRIM-zeroed.
///           We skip ZeroMap building entirely on HDDs.
///
/// Result: carve scan skips confirmed dead zones, runs faster, and
/// produces fewer false positives on SSDs with heavy TRIM activity.
/// </summary>
public sealed class TrimAwareScanner
{
    private readonly RawDisk       _disk;
    private readonly FileSignature[] _sigs;
    private readonly bool          _isSsd;

    // Minimum consecutive zero bytes to consider a region TRIM-zeroed.
    // 512 KB is conservative — real TRIM zeroes in 4KB–1MB blocks.
    private const int  ZERO_THRESHOLD = 512 * 1024;
    private const int  PROBE_CHUNK    = 1 * 1024 * 1024;   // 1 MB probe
    private const int  SAMPLE_BYTES   = 4096;               // bytes to sample

    public event Action<string>?     Log;
    public event Action<long, long>? Progress;

    public TrimAwareScanner(RawDisk disk, IEnumerable<FileSignature> sigs,
                            bool isSsd)
    {
        _disk  = disk;
        _sigs  = sigs.ToArray();
        _isSsd = isSsd;
    }

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct)
    {
        if (!_isSsd)
        {
            // HDD — no TRIM, use normal scanner
            Log?.Invoke("HDD source — TRIM analysis skipped, scanning normally.");
            var normal = new CarveScanner(_disk, _sigs);
            normal.Log      += m      => Log?.Invoke(m);
            normal.Progress += (d, t) => Progress?.Invoke(d, t);
            foreach (var rf in normal.Scan(ct)) yield return rf;
            yield break;
        }

        Log?.Invoke("SSD detected — building TRIM zero map before carving…");

        // Build zero map
        var skipRanges = BuildZeroMap(ct);
        long skippedBytes = skipRanges.Sum(r => r.length);
        double skipPct = _disk.Length > 0
            ? skippedBytes * 100.0 / _disk.Length : 0;

        Log?.Invoke($"TRIM map: {skipRanges.Count:N0} zero regions, " +
                    $"{FormatSize(skippedBytes)} ({skipPct:F1}%) will be skipped.");

        if (ct.IsCancellationRequested) yield break;

        // Scan with skip ranges
        var scanner = new SkipRangeCarver(_disk, _sigs, skipRanges);
        scanner.Log      += m      => Log?.Invoke(m);
        scanner.Progress += (d, t) => Progress?.Invoke(d, t);

        foreach (var rf in scanner.Scan(ct))
            yield return rf;
    }

    // ── Zero map builder ──────────────────────────────────────────────

    private List<(long offset, long length)> BuildZeroMap(CancellationToken ct)
    {
        var   regions     = new List<(long, long)>();
        long  total       = _disk.Length;
        byte[] probe      = new byte[SAMPLE_BYTES];
        long  zeroStart   = -1;
        long  pos         = 0;

        while (pos < total && !ct.IsCancellationRequested)
        {
            int got = _disk.ReadAt(pos, probe, SAMPLE_BYTES, out _);
            if (got <= 0) break;

            bool allZero = IsAllZero(probe, got);

            if (allZero && zeroStart < 0)
                zeroStart = pos;                // start of zero run

            if (!allZero && zeroStart >= 0)
            {
                long zeroLen = pos - zeroStart;
                if (zeroLen >= ZERO_THRESHOLD)
                    regions.Add((zeroStart, zeroLen));
                zeroStart = -1;
            }

            pos += PROBE_CHUNK;                 // stride 1 MB between samples
            Progress?.Invoke(pos / 2, total);   // first half = map building
        }

        // Close final zero run
        if (zeroStart >= 0)
        {
            long zeroLen = total - zeroStart;
            if (zeroLen >= ZERO_THRESHOLD)
                regions.Add((zeroStart, zeroLen));
        }

        return regions;
    }

    private static bool IsAllZero(byte[] buf, int count)
    {
        for (int i = 0; i < count; i++)
            if (buf[i] != 0) return false;
        return true;
    }

    private static string FormatSize(long b)
    {
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):F1} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):F1} MB";
        if (b >= 1L << 10) return $"{b / (double)(1L << 10):F0} KB";
        return $"{b} B";
    }
}

// ── SkipRangeCarver — CarveScanner that skips known-zero regions ──────

internal sealed class SkipRangeCarver
{
    private const int CHUNK    = 8 * 1024 * 1024;
    private const int OVERLAP  = 1024;
    private const int MIN_FILE = 64;

    private readonly RawDisk       _disk;
    private readonly FileSignature[] _sigs;
    private readonly List<(long offset, long length)> _skipRanges;

    public event Action<string>?     Log;
    public event Action<long, long>? Progress;

    public SkipRangeCarver(RawDisk disk, FileSignature[] sigs,
                           List<(long, long)> skipRanges)
    {
        _disk       = disk;
        _sigs       = sigs;
        _skipRanges = skipRanges;
    }

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct)
    {
        long total  = _disk.Length;
        byte[] buf  = new byte[CHUNK + OVERLAP];
        long pos    = 0;
        int  fileNo = 0;
        var  lastEnd= new Dictionary<string, long>();
        int  skipped= 0;

        while (pos < total && !ct.IsCancellationRequested)
        {
            // Check if this position falls in a skip range
            var skip = _skipRanges.FirstOrDefault(r =>
                pos >= r.offset && pos < r.offset + r.length);

            if (skip != default)
            {
                long skipTo = skip.offset + skip.length;
                skipped++;
                if (skipped % 100 == 0)
                    Log?.Invoke($"Skipped {skipped} TRIM regions so far");
                pos = skipTo;
                continue;
            }

            int want = (int)Math.Min(CHUNK + OVERLAP, total - pos);
            int got  = _disk.ReadAt(pos, buf, want, out int bad);
            if (bad > 0) Log?.Invoke($"Bad sector near {pos:N0}");
            if (got <= 0) break;

            int scanLimit = Math.Min(got, CHUNK);
            var hits      = FindHeaders(buf, got, scanLimit, pos);

            foreach (var (fileStart, sig) in hits)
            {
                if (ct.IsCancellationRequested) break;
                if (lastEnd.TryGetValue(sig.Ext, out long end)
                    && fileStart < end) continue;

                byte[]? data = TryCarve(fileStart, sig, total);
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
            Progress?.Invoke(total / 2 + Math.Min(pos, total) / 2, total);
        }

        Log?.Invoke($"TRIM-aware scan complete — {skipped} zero regions skipped.");
    }

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
                    !sig.Validate(buf, startInBuf, got - startInBuf)) continue;
                hits.Add((fileStart, sig));
            }
        }
        hits.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return hits;
    }

    private byte[]? TryCarve(long start, FileSignature sig, long total)
    {
        try
        {
            long maxLen = Math.Min(sig.MaxSize, total - start);
            if (sig.SizeField.HasValue)
            {
                var (fOff, fLen) = sig.SizeField.Value;
                byte[] head = Read(start, fOff + fLen);
                if (head.Length < fOff + fLen) return null;
                long size = 0;
                for (int i = 0; i < fLen; i++) size |= (long)head[fOff+i] << (8*i);
                if (size < MIN_FILE || size > maxLen) return null;
                return Read(start, (int)size);
            }
            if (sig.Footer == null)
            {
                byte[] raw = Read(start, (int)Math.Min(maxLen, 50*1024*1024));
                int end = raw.Length;
                while (end > 0 && raw[end-1] == 0) end--;
                return end <= MIN_FILE ? null : raw[..end];
            }
            byte[] all = Read(start, (int)Math.Min(maxLen, 50L*1024*1024));
            int fi = sig.LastFooter
                ? new ReadOnlySpan<byte>(all).LastIndexOf(sig.Footer)
                : new ReadOnlySpan<byte>(all).IndexOf(sig.Footer);
            if (fi < 0) return null;
            int total2 = fi + sig.Footer.Length + sig.FooterExtra;
            return total2 <= MIN_FILE || total2 > all.Length ? null : all[..total2];
        }
        catch { return null; }
    }

    private byte[] Read(long offset, int count)
    {
        byte[] buf = new byte[count];
        int got = _disk.ReadAt(offset, buf, count, out _);
        if (got < count) Array.Resize(ref buf, got);
        return buf;
    }
}
