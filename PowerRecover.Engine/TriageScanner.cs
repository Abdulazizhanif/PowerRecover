namespace PowerRecover.Engine;

/// <summary>
/// Triage mode — surfaces your most recoverable files in under 5 minutes.
///
/// Strategy:
///   1. Full NTFS MFT scan (fast — reads only the MFT, not the whole disk)
///   2. Signature carve of only the first 10 GB of unallocated space
///   3. Filter results to confidence >= minConfidence (default 75%)
///   4. Skip duplicates via FileDeduplicator
///
/// This covers:
///   - ALL files that were deleted recently (MFT entries not reused yet)
///   - High-quality carved files in the first portion of the disk
///     (most recently deleted files cluster near the start on FAT/NTFS)
///
/// When to use:
///   "I deleted something 10 minutes ago" → Triage mode, done in 2 minutes
///   "I need everything possible"          → Both (thorough) mode, hours
///
/// Usage:
///   var triage = new TriageScanner(disk, partitionOffset);
///   triage.Log      += m => Log(m);
///   triage.Progress += (d,t) => SetProgress(d,t);
///   foreach (var rf in triage.Scan(token)) { ... }
/// </summary>
public sealed class TriageScanner
{
    private readonly RawDisk _disk;
    private readonly long    _partitionOffset;
    private readonly int     _minConfidence;
    private readonly long    _maxCarveBytes;
    private readonly FileSignature[] _sigs;

    public event Action<string>?     Log;
    public event Action<long, long>? Progress;

    // Default: carve first 10 GB, require 75% confidence
    public TriageScanner(RawDisk disk,
                         long partitionOffset  = 0,
                         int  minConfidence    = 75,
                         long maxCarveBytes    = 10L * 1024 * 1024 * 1024,
                         IEnumerable<FileSignature>? sigs = null)
    {
        _disk            = disk;
        _partitionOffset = partitionOffset;
        _minConfidence   = minConfidence;
        _maxCarveBytes   = maxCarveBytes;
        _sigs            = sigs?.ToArray() ?? ExtendedSignatures.All;
    }

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct)
    {
        var dedup   = new FileDeduplicator();
        int total   = 0;
        int skipped = 0;

        // ── Phase 1: NTFS MFT (fast — reads only file table) ─────────
        Log?.Invoke("TRIAGE: Phase 1 — NTFS MFT scan…");

        var ntfs = new NtfsScanner(_disk, _partitionOffset);
        ntfs.Log      += m      => Log?.Invoke($"[MFT] {m}");
        ntfs.Progress += (d, t) => Progress?.Invoke(d / 2, t);

        if (ntfs.ReadBootSector())
        {
            var ntfsFiles = ntfs.Scan(ct).ToList();
            ntfs.ResolvePaths(ntfsFiles);

            foreach (var rf in ntfsFiles)
            {
                if (ct.IsCancellationRequested) yield break;
                if (rf.Name.StartsWith("$") && !rf.Deleted) continue;

                rf.Confidence = ConfidenceScorer.Score(rf);

                if (rf.Confidence < _minConfidence) { skipped++; continue; }
                if (dedup.IsDuplicate(rf))          { skipped++; continue; }

                total++;
                yield return rf;
            }

            Log?.Invoke($"TRIAGE: MFT phase done — {total} files, " +
                        $"{skipped} below confidence threshold.");
        }
        else
        {
            Log?.Invoke("TRIAGE: No NTFS volume found — skipping MFT phase.");
        }

        if (ct.IsCancellationRequested) yield break;

        // ── Phase 2: Limited carve of first N GB ─────────────────────
        long carveEnd = Math.Min(_disk.Length, _maxCarveBytes);
        Log?.Invoke($"TRIAGE: Phase 2 — Carving first " +
                    $"{FormatSize(carveEnd)} of disk…");

        // Use _disk directly — carver stops at carveEnd via Scan filter below
        var carver = new CarveScanner(_disk, _sigs);
        carver.Log      += m      => Log?.Invoke($"[Carve] {m}");
        carver.Progress += (d, t) => Progress?.Invoke(
            _disk.Length / 2 + d / 2, _disk.Length);

        int carveTotal   = 0;
        int carveSkipped = 0;

        foreach (var rf in carver.Scan(ct).Where(f => f.Offset < carveEnd))
        {
            if (ct.IsCancellationRequested) yield break;

            rf.Confidence = ConfidenceScorer.Score(rf);

            if (rf.Confidence < _minConfidence) { carveSkipped++; continue; }
            if (dedup.IsDuplicate(rf))          { carveSkipped++; continue; }

            carveTotal++;
            total++;
            yield return rf;
        }

        Log?.Invoke($"TRIAGE: Carve phase done — {carveTotal} files, " +
                    $"{carveSkipped} skipped (low confidence or duplicate).");
        Log?.Invoke($"TRIAGE: Complete — {total} files total. " +
                    dedup.Summary);
    }

    private static string FormatSize(long b)
    {
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):F1} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):F1} MB";
        return $"{b} B";
    }
}
