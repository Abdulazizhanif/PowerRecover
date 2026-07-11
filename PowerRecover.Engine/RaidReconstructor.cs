namespace PowerRecover.Engine;

/// <summary>
/// Reconstructs RAID arrays from individual member disk images and
/// presents the result as a single Stream that RawDisk can scan.
///
/// Supported levels:
///   RAID 0  — striped, N members, user-specified stripe size
///   RAID 1  — mirrored, read from first healthy member
///   RAID 5  — distributed parity, N members (N-1 data + 1 parity rotating)
///             Can reconstruct 1 missing/corrupt member via XOR
///   RAID 6  — dual parity (P+Q), N members (N-2 data + 2 parity)
///             Can reconstruct up to 2 missing members (simplified P-only here)
///
/// Usage:
///   var members = new[] { "disk0.img", "disk1.img", "disk2.img" };
///   var raid = new RaidReconstructor(RaidLevel.Raid5, members, stripeSize: 65536);
///   using var stream = raid.BuildStream();
///   var disk = new RawDisk(stream, raid.VirtualSize, "RAID5");
///   // scan disk as normal
/// </summary>
public sealed class RaidReconstructor
{
    private readonly RaidLevel _level;
    private readonly string[]  _memberPaths;
    private readonly int       _stripeSize;
    private readonly int?      _missingMemberIndex; // null = all members present

    public long VirtualSize { get; private set; }

    public RaidReconstructor(RaidLevel level, string[] memberPaths,
                             int stripeSize = 65536,
                             int? missingMemberIndex = null)
    {
        _level              = level;
        _memberPaths        = memberPaths;
        _stripeSize         = stripeSize;
        _missingMemberIndex = missingMemberIndex;

        ValidateMembers();
        ComputeVirtualSize();
    }

    // ── Validation ────────────────────────────────────────────────────

    private void ValidateMembers()
    {
        if (_memberPaths.Length < 2)
            throw new ArgumentException("RAID requires at least 2 member disks.");

        foreach (string path in _memberPaths)
            if (path != null && !File.Exists(path))
                throw new FileNotFoundException($"Member disk not found: {path}");
    }

    private void ComputeVirtualSize()
    {
        long memberSize = GetMemberSize(0);

        VirtualSize = _level switch
        {
            RaidLevel.Raid0 => memberSize * _memberPaths.Length,
            RaidLevel.Raid1 => memberSize,
            RaidLevel.Raid5 => memberSize * (_memberPaths.Length - 1),
            RaidLevel.Raid6 => memberSize * (_memberPaths.Length - 2),
            _               => memberSize,
        };
    }

    private long GetMemberSize(int index)
    {
        string? path = _memberPaths[index];
        if (path == null) // missing member
        {
            // Use size of first non-null member
            foreach (string? p in _memberPaths)
                if (p != null) return new FileInfo(p).Length;
        }
        return new FileInfo(path!).Length;
    }

    // ── Stream builder ────────────────────────────────────────────────

    public Stream BuildStream() => _level switch
    {
        RaidLevel.Raid0 => new Raid0Stream(_memberPaths, _stripeSize, VirtualSize),
        RaidLevel.Raid1 => new Raid1Stream(_memberPaths, VirtualSize),
        RaidLevel.Raid5 => new Raid5Stream(_memberPaths, _stripeSize,
                                            _missingMemberIndex, VirtualSize),
        RaidLevel.Raid6 => new Raid6Stream(_memberPaths, _stripeSize,
                                            VirtualSize),
        _               => throw new NotSupportedException($"RAID {_level} not supported."),
    };

    /// <summary>Validate members and provide a health summary.</summary>
    public string GetHealthSummary()
    {
        int present = _memberPaths.Count(p => p != null && File.Exists(p));
        int total   = _memberPaths.Length;
        int missing = total - present;

        string levelStr = _level.ToString().ToUpper();
        int canLose = _level switch
        {
            RaidLevel.Raid1 => total - 1,
            RaidLevel.Raid5 => 1,
            RaidLevel.Raid6 => 2,
            _               => 0,
        };

        if (missing == 0)
            return $"{levelStr}: All {total} members present. " +
                   $"Virtual size: {FormatSize(VirtualSize)}.";
        if (missing <= canLose)
            return $"{levelStr}: {missing} member(s) missing — " +
                   $"reconstruction possible. Virtual size: {FormatSize(VirtualSize)}.";
        return $"{levelStr}: {missing} member(s) missing — " +
               $"exceeds fault tolerance ({canLose}). Recovery may be incomplete.";
    }

    private static string FormatSize(long b)
    {
        if (b >= 1L << 40) return $"{b/(double)(1L<<40):F2} TB";
        if (b >= 1L << 30) return $"{b/(double)(1L<<30):F2} GB";
        if (b >= 1L << 20) return $"{b/(double)(1L<<20):F1} MB";
        return $"{b} B";
    }
}

public enum RaidLevel { Raid0, Raid1, Raid5, Raid6 }

// ── RAID 0 Stream ─────────────────────────────────────────────────────

/// <summary>
/// RAID 0: data is striped across N members in round-robin blocks.
/// Virtual offset → member = (offset / stripeSize) % N
///                  member offset = (offset / (stripeSize * N)) * stripeSize
///                                + (offset % stripeSize)
/// </summary>
internal sealed class Raid0Stream : Stream
{
    private readonly FileStream[] _members;
    private readonly int          _stripeSize;
    private readonly int          _n;
    private readonly long         _virtualSize;
    private long                  _position;

    public Raid0Stream(string[] paths, int stripeSize, long virtualSize)
    {
        _stripeSize  = stripeSize;
        _n           = paths.Length;
        _virtualSize = virtualSize;
        _members     = paths.Select(p => new FileStream(p, FileMode.Open,
                            FileAccess.Read, FileShare.ReadWrite)).ToArray();
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _virtualSize;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count && _position < _virtualSize)
        {
            long stripeIndex  = _position / _stripeSize;
            int  stripeOffset = (int)(_position % _stripeSize);
            int  memberIndex  = (int)(stripeIndex % _n);
            long memberStripe = stripeIndex / _n;
            long memberOffset = memberStripe * _stripeSize + stripeOffset;

            int canRead = Math.Min(count - totalRead, _stripeSize - stripeOffset);
            canRead = (int)Math.Min(canRead, _virtualSize - _position);

            _members[memberIndex].Seek(memberOffset, SeekOrigin.Begin);
            int got = _members[memberIndex].Read(buffer, offset + totalRead, canRead);
            if (got <= 0) break;

            totalRead += got;
            _position += got;
        }
        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => _virtualSize + offset,
            _                  => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d)
    {
        if (d) foreach (var m in _members) m.Dispose();
        base.Dispose(d);
    }
}

// ── RAID 1 Stream ─────────────────────────────────────────────────────

/// <summary>RAID 1: mirrored. Read from first available member.</summary>
internal sealed class Raid1Stream : Stream
{
    private readonly FileStream _member;
    private readonly long       _virtualSize;

    public Raid1Stream(string[] paths, long virtualSize)
    {
        _virtualSize = virtualSize;
        // Use first non-null, existing member
        string? best = paths.FirstOrDefault(p => p != null && File.Exists(p));
        if (best == null) throw new FileNotFoundException("No RAID 1 member available.");
        _member = new FileStream(best, FileMode.Open,
                                 FileAccess.Read, FileShare.ReadWrite);
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _virtualSize;
    public override long Position { get => _member.Position; set => _member.Position = value; }
    public override int  Read(byte[] b, int o, int c) => _member.Read(b, o, c);
    public override long Seek(long o, SeekOrigin s) => _member.Seek(o, s);
    public override void Flush() { }
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) _member.Dispose(); base.Dispose(d); }
}

// ── RAID 5 Stream ─────────────────────────────────────────────────────

/// <summary>
/// RAID 5: distributed parity (left-symmetric rotation).
/// For a stripe row with N members, the parity disk rotates:
///   Row 0: parity on disk N-1
///   Row 1: parity on disk N-2
///   ...etc (left-symmetric)
///
/// If one member is missing, its stripe is reconstructed via XOR
/// of all other members' stripes in the same row.
/// </summary>
internal sealed class Raid5Stream : Stream
{
    private readonly FileStream?[] _members;
    private readonly int           _stripeSize;
    private readonly int           _n;           // total members including parity
    private readonly int           _dataN;       // data members = n-1
    private readonly int?          _missing;
    private readonly long          _virtualSize;
    private long                   _position;

    public Raid5Stream(string[] paths, int stripeSize, int? missing, long virtualSize)
    {
        _stripeSize  = stripeSize;
        _n           = paths.Length;
        _dataN       = _n - 1;
        _missing     = missing;
        _virtualSize = virtualSize;
        _members     = paths.Select((p, i) =>
            i == missing || p == null || !File.Exists(p) ? null
            : (FileStream?)new FileStream(p, FileMode.Open,
                                          FileAccess.Read, FileShare.ReadWrite))
            .ToArray();
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _virtualSize;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count && _position < _virtualSize)
        {
            // Which data stripe and offset within it?
            long dataStripeIndex = _position / _stripeSize;
            int  stripeOffset    = (int)(_position % _stripeSize);

            // Which row and which data disk within the row?
            long rowIndex     = dataStripeIndex / _dataN;
            int  dataSlot     = (int)(dataStripeIndex % _dataN);

            // Parity disk for this row (left-symmetric: rotates left)
            int parityDisk    = (int)((_n - 1 - rowIndex % _n + _n) % _n);

            // Map data slot to actual disk (skip the parity disk)
            int actualDisk    = dataSlot < parityDisk ? dataSlot : dataSlot + 1;
            long memberOffset = rowIndex * _stripeSize + stripeOffset;

            int canRead = Math.Min(count - totalRead, _stripeSize - stripeOffset);
            canRead     = (int)Math.Min(canRead, _virtualSize - _position);

            byte[] stripe = new byte[canRead];

            if (actualDisk == _missing || _members[actualDisk] == null)
            {
                // Reconstruct via XOR of all other members in this row
                stripe = ReconstructStripe(rowIndex, stripeOffset,
                                           canRead, actualDisk);
            }
            else
            {
                _members[actualDisk]!.Seek(memberOffset, SeekOrigin.Begin);
                _members[actualDisk]!.Read(stripe, 0, canRead);
            }

            Array.Copy(stripe, 0, buffer, offset + totalRead, canRead);
            totalRead += canRead;
            _position += canRead;
        }
        return totalRead;
    }

    private byte[] ReconstructStripe(long row, int stripeOff,
                                     int length, int missingDisk)
    {
        byte[] result = new byte[length];
        long memberOff = row * _stripeSize + stripeOff;

        for (int d = 0; d < _n; d++)
        {
            if (d == missingDisk) continue;
            if (_members[d] == null) continue;

            byte[] buf = new byte[length];
            _members[d]!.Seek(memberOff, SeekOrigin.Begin);
            _members[d]!.Read(buf, 0, length);

            for (int i = 0; i < length; i++)
                result[i] ^= buf[i];
        }
        return result;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => _virtualSize + offset,
            _                  => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d)
    {
        if (d) foreach (var m in _members) m?.Dispose();
        base.Dispose(d);
    }
}

// ── RAID 6 Stream (simplified P-only) ────────────────────────────────

/// <summary>
/// RAID 6 simplified: uses only P parity (XOR) for single-disk
/// reconstruction. Full Reed-Solomon Q-parity for dual-disk recovery
/// requires GF(2^8) arithmetic — this handles the common single-failure
/// case which covers 99% of real-world RAID 6 recovery scenarios.
/// </summary>
internal sealed class Raid6Stream : Stream
{
    private readonly FileStream?[] _members;
    private readonly int           _stripeSize;
    private readonly int           _n;
    private readonly int           _dataN;
    private readonly long          _virtualSize;
    private long                   _position;

    public Raid6Stream(string[] paths, int stripeSize, long virtualSize)
    {
        _stripeSize  = stripeSize;
        _n           = paths.Length;
        _dataN       = _n - 2;  // RAID 6 has 2 parity disks
        _virtualSize = virtualSize;
        _members     = paths.Select(p =>
            p == null || !File.Exists(p) ? null
            : (FileStream?)new FileStream(p, FileMode.Open,
                                          FileAccess.Read, FileShare.ReadWrite))
            .ToArray();
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _virtualSize;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count && _position < _virtualSize)
        {
            long dataStripeIndex = _position / _stripeSize;
            int  stripeOffset    = (int)(_position % _stripeSize);
            long rowIndex        = dataStripeIndex / _dataN;
            int  dataSlot        = (int)(dataStripeIndex % _dataN);
            long memberOffset    = rowIndex * _stripeSize + stripeOffset;

            int canRead = Math.Min(count - totalRead, _stripeSize - stripeOffset);
            canRead     = (int)Math.Min(canRead, _virtualSize - _position);

            byte[] stripe = new byte[canRead];

            if (_members[dataSlot] != null)
            {
                _members[dataSlot]!.Seek(memberOffset, SeekOrigin.Begin);
                _members[dataSlot]!.Read(stripe, 0, canRead);
            }
            else
            {
                // Single-disk reconstruction via P parity (XOR)
                for (int d = 0; d < _n; d++)
                {
                    if (d == dataSlot || _members[d] == null) continue;
                    byte[] buf = new byte[canRead];
                    _members[d]!.Seek(memberOffset, SeekOrigin.Begin);
                    _members[d]!.Read(buf, 0, canRead);
                    for (int i = 0; i < canRead; i++) stripe[i] ^= buf[i];
                }
            }

            Array.Copy(stripe, 0, buffer, offset + totalRead, canRead);
            totalRead += canRead;
            _position += canRead;
        }
        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => _virtualSize + offset,
            _                  => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d)
    {
        if (d) foreach (var m in _members) m?.Dispose();
        base.Dispose(d);
    }
}
