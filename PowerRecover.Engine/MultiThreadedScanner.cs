using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerRecover.Engine;

/// <summary>
/// Multi-threaded carve scanner. Splits the disk into N zones and scans
/// each zone in parallel using separate CarveScanner instances.
///
/// IMPORTANT: Multi-threading helps massively on SSDs (random read = fast).
/// On spinning HDDs the head must seek between zones — serial is faster.
/// We auto-detect SSD vs HDD via IOCTL_STORAGE_QUERY_PROPERTY and only
/// parallelise on solid-state media.
///
/// Usage — drop-in replacement for CarveScanner in MainWindow:
///   var scanner = new MultiThreadedScanner(disk, sigs);
///   scanner.Log      += m      => Ui(() => Log(m));
///   scanner.Progress += (d, t) => Ui(() => SetProgress(d, t));
///   foreach (var rf in scanner.Scan(token)) { ... }
/// </summary>
public sealed class MultiThreadedScanner
{
    private readonly RawDisk _disk;
    private readonly FileSignature[] _sigs;
    private readonly int _threadCount;

    public event Action<string>?     Log;
    public event Action<long, long>? Progress;

    // P/Invoke for SSD detection
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;   // StorageDeviceProperty = 0
        public uint QueryType;    // PropertyStandardQuery = 0
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint  Version;
        public uint  Size;
        public byte  DeviceType;
        public byte  DeviceTypeModifier;
        public byte  RemovableMedia;
        public byte  CommandQueueing;
        public uint  VendorIdOffset;
        public uint  ProductIdOffset;
        public uint  ProductRevisionOffset;
        public uint  SerialNumberOffset;
        public uint  BusType;          // 17 = NVMe, 11 = SATA SSD (reported as ATA)
        public uint  RawPropertiesLength;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecA, uint dwCreationDisp, uint dwFlags, IntPtr hTemplate);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDev, uint dwCode,
        ref STORAGE_PROPERTY_QUERY lpIn, uint nIn,
        ref STORAGE_DEVICE_DESCRIPTOR lpOut, uint nOut,
        out uint lpBytes, IntPtr lpOverlapped);

    public MultiThreadedScanner(RawDisk disk, IEnumerable<FileSignature> sigs,
                                int? threadCount = null)
    {
        _disk   = disk;
        _sigs   = sigs.ToArray();

        bool isSsd   = DetectSsd(disk.Source);
        _threadCount = threadCount ?? (isSsd
            ? Math.Min(Environment.ProcessorCount, 8)
            : 1);  // HDD: force single thread
    }

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct)
    {
        if (_threadCount <= 1)
        {
            Log?.Invoke("Sequential scan (HDD detected or single-thread mode)");
            var single = new CarveScanner(_disk, _sigs);
            single.Log      += m      => Log?.Invoke(m);
            single.Progress += (d, t) => Progress?.Invoke(d, t);
            foreach (var rf in single.Scan(ct))
                yield return rf;
            yield break;
        }

        Log?.Invoke($"Parallel scan: {_threadCount} threads on SSD");

        long total     = _disk.Length;
        long zoneSize  = total / _threadCount;
        var  bag       = new ConcurrentBag<RecoveredFile>();
        long processed = 0;

        // Each zone gets its own RawDisk instance (separate file handle/position)
        var tasks = new Task[_threadCount];
        for (int t = 0; t < _threadCount; t++)
        {
            int  zoneIndex = t;
            long zoneStart = zoneIndex * zoneSize;
            long zoneEnd   = zoneIndex == _threadCount - 1 ? total : zoneStart + zoneSize;

            tasks[t] = Task.Run(() =>
            {
                // Each thread opens its own handle to avoid seek contention
                using var zoneDisk = new RawDisk(_disk.Source);
                var zoneCarver     = new ZoneCarver(zoneDisk, _sigs,
                                                    zoneStart, zoneEnd);
                foreach (var rf in zoneCarver.Scan(ct))
                {
                    bag.Add(rf);
                    Interlocked.Add(ref processed, rf.Size);
                    Progress?.Invoke(Interlocked.Read(ref processed), total);
                }
                Log?.Invoke($"Zone {zoneIndex}: {zoneStart:N0}–{zoneEnd:N0} complete");
            }, ct);
        }

        try { Task.WaitAll(tasks, ct); }
        catch (OperationCanceledException) { }

        // Sort by disk offset so results come out in order
        foreach (var rf in bag.OrderBy(r => r.Offset))
            yield return rf;
    }

    private static bool DetectSsd(string source)
    {
        // Image files are always "SSD" equivalent — no seek penalty
        if (File.Exists(source)) return true;
        if (!source.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var handle = CreateFile(source, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            using (handle)
            {
                var query = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = 0,
                    QueryType  = 0,
                    AdditionalParameters = new byte[1],
                };
                var desc = new STORAGE_DEVICE_DESCRIPTOR();
                bool ok = DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    ref query, (uint)Marshal.SizeOf(query),
                    ref desc, (uint)Marshal.SizeOf(desc),
                    out _, IntPtr.Zero);

                if (!ok) return false;

                // BusType: 17 = NVMe, 11 = SATA (often SSD), 7 = USB
                // We treat NVMe and USB as SSD-equivalent
                return desc.BusType is 17 or 7;
            }
        }
        catch { return false; }
    }
}

/// <summary>Carves a specific byte range of a disk — used by each parallel zone.</summary>
internal sealed class ZoneCarver
{
    private const int CHUNK   = 8 * 1024 * 1024;
    private const int OVERLAP = 1024;
    private const int MIN_FILE = 64;

    private readonly RawDisk _disk;
    private readonly FileSignature[] _sigs;
    private readonly long _start;
    private readonly long _end;

    public ZoneCarver(RawDisk disk, FileSignature[] sigs, long start, long end)
    {
        _disk  = disk;
        _sigs  = sigs;
        _start = start;
        _end   = end;
    }

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct)
    {
        byte[] buf     = new byte[CHUNK + OVERLAP];
        long pos       = _start;
        int  fileNo    = 0;
        var  lastEnd   = new Dictionary<string, long>();

        while (pos < _end && !ct.IsCancellationRequested)
        {
            int want = (int)Math.Min(CHUNK + OVERLAP, _end - pos);
            int got  = _disk.ReadAt(pos, buf, want, out _);
            if (got <= 0) break;

            int scanLimit = Math.Min(got, CHUNK);
            var hits      = FindHeaders(buf, got, scanLimit, pos);

            foreach (var (fileStart, sig) in hits)
            {
                if (ct.IsCancellationRequested) break;
                if (lastEnd.TryGetValue(sig.Ext, out long end) && fileStart < end) continue;

                byte[]? data = TryCarve(fileStart, sig);
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
        }
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

    private byte[]? TryCarve(long start, FileSignature sig)
    {
        try
        {
            long maxLen = Math.Min(sig.MaxSize, _disk.Length - start);
            if (sig.SizeField.HasValue)
            {
                var (fOff, fLen) = sig.SizeField.Value;
                byte[] head = Read(start, fOff + fLen);
                if (head.Length < fOff + fLen) return null;
                long size = 0;
                for (int i = 0; i < fLen; i++) size |= (long)head[fOff + i] << (8 * i);
                if (size < MIN_FILE || size > maxLen) return null;
                return Read(start, (int)size);
            }
            if (sig.Footer == null)
            {
                byte[] raw = Read(start, (int)Math.Min(maxLen, 50 * 1024 * 1024));
                int end = raw.Length;
                while (end > 0 && raw[end - 1] == 0) end--;
                return end <= MIN_FILE ? null : raw[..end];
            }
            // Footer-based: simple bounded read
            byte[] all = Read(start, (int)Math.Min(maxLen, 50L * 1024 * 1024));
            int fi = sig.LastFooter
                ? new ReadOnlySpan<byte>(all).LastIndexOf(sig.Footer)
                : new ReadOnlySpan<byte>(all).IndexOf(sig.Footer);
            if (fi < 0) return null;
            int total = fi + sig.Footer.Length + sig.FooterExtra;
            return total <= MIN_FILE || total > all.Length ? null : all[..total];
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
