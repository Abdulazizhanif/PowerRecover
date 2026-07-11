namespace PowerRecover.Engine;

/// <summary>
/// Clones a source drive or partition to a raw .img file sector-by-sector,
/// using the same bad-sector skip + zero-fill logic as RawDisk.ReadAt.
///
/// WHY THIS MATTERS: a physically failing drive degrades with every read.
/// You should NEVER run repeated scans on a failing drive. Clone it first,
/// then scan the .img file — which is safe, fast, and repeatable.
///
/// Usage pattern (mirrors NtfsScanner / CarveScanner):
///   var imager = new DiskImager(sourceDisk);
///   imager.Log     += msg  => AppendLog(msg);
///   imager.Progress += (d,t) => UpdateProgressBar(d, t);
///   await Task.Run(() => imager.Clone(outputPath, ct));
/// </summary>
public sealed class DiskImager
{
    private readonly RawDisk _source;

    public event Action<string>? Log;
    public event Action<long, long>? Progress;   // (bytesWritten, total)
    public event Action<long, int>? BadSector;   // (offset, count)

    private const int BLOCK_SIZE = 8 * 1024 * 1024; // 8 MB per read

    public DiskImager(RawDisk source) => _source = source;

    /// <summary>
    /// Clone source to outputPath. Returns stats on completion.
    /// Throws only on fatal I/O errors writing the output file.
    /// </summary>
    public ImagingResult Clone(string outputPath, CancellationToken ct)
    {
        long total   = _source.Length;
        int  sectors = _source.SectorSize;
        long written = 0;
        int  totalBad = 0;
        var  sw = System.Diagnostics.Stopwatch.StartNew();

        Log?.Invoke($"Imaging {_source.Source} → {outputPath}");
        Log?.Invoke($"  Source size: {FormatSize(total)} | Block: {FormatSize(BLOCK_SIZE)}");
        Log?.Invoke($"  Read-only source — never writing back to source drive.");

        byte[] buf = new byte[BLOCK_SIZE];

        using var output = new FileStream(outputPath, FileMode.Create,
                                          FileAccess.Write, FileShare.None,
                                          bufferSize: BLOCK_SIZE, useAsync: false);

        // Pre-allocate so the OS reserves contiguous space up front.
        // On SSDs this prevents fragmentation; on HDDs it reduces seeks.
        try { output.SetLength(total); }
        catch { /* non-fatal if sparse files aren't supported */ }

        long pos = 0;
        while (pos < total && !ct.IsCancellationRequested)
        {
            int want = (int)Math.Min(BLOCK_SIZE, total - pos);
            int got  = _source.ReadAt(pos, buf, want, out int bad);

            if (got <= 0) break;

            if (bad > 0)
            {
                totalBad += bad;
                BadSector?.Invoke(pos, bad);
                Log?.Invoke($"  [{FormatSize(pos)}] {bad} bad sector(s) — zero-filled");
            }

            output.Write(buf, 0, got);
            written += got;
            pos     += got;

            Progress?.Invoke(written, total);

            // Periodic throughput log every 512 MB
            if ((written & (512 * 1024 * 1024 - 1)) < BLOCK_SIZE)
            {
                double mb  = written / 1024.0 / 1024.0;
                double sec = sw.Elapsed.TotalSeconds;
                double mbps = sec > 0 ? mb / sec : 0;
                Log?.Invoke($"  {FormatSize(written)} / {FormatSize(total)} " +
                            $"— {mbps:F1} MB/s");
            }
        }

        output.Flush();
        sw.Stop();

        double elapsed = sw.Elapsed.TotalSeconds;
        double avgMbps = elapsed > 0 ? (written / 1024.0 / 1024.0) / elapsed : 0;
        string status  = ct.IsCancellationRequested ? "CANCELLED" : "COMPLETE";

        Log?.Invoke($"Imaging {status}: {FormatSize(written)} written in " +
                    $"{sw.Elapsed:hh\\:mm\\:ss} ({avgMbps:F1} MB/s avg), " +
                    $"{totalBad} bad sector(s).");

        return new ImagingResult
        {
            BytesWritten   = written,
            TotalBytes     = total,
            BadSectorCount = totalBad,
            Elapsed        = sw.Elapsed,
            AverageMBps    = avgMbps,
            OutputPath     = outputPath,
            Cancelled      = ct.IsCancellationRequested,
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (1L << 40):F2} TB";
        if (bytes >= 1L << 30) return $"{bytes / (1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (1L << 10):F0} KB";
        return $"{bytes} B";
    }
}

public sealed record ImagingResult
{
    public long     BytesWritten   { get; init; }
    public long     TotalBytes     { get; init; }
    public int      BadSectorCount { get; init; }
    public TimeSpan Elapsed        { get; init; }
    public double   AverageMBps    { get; init; }
    public string   OutputPath     { get; init; } = "";
    public bool     Cancelled      { get; init; }

    public double PercentComplete =>
        TotalBytes > 0 ? BytesWritten * 100.0 / TotalBytes : 0;
}
