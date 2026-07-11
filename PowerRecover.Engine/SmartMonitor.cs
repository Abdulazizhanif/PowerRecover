using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerRecover.Engine;

/// <summary>
/// Reads S.M.A.R.T. attribute data from a physical drive using the
/// IOCTL_ATA_PASS_THROUGH_DIRECT DeviceIoControl call.
///
/// Key attributes monitored:
///   ID 05  — Reallocated Sectors Count  (non-zero = drive remapping bad sectors)
///   ID 09  — Power-On Hours
///   ID C2  — Temperature (Celsius)
///   ID C5  — Current Pending Sectors    (sectors waiting to be reallocated)
///   ID C6  — Offline Uncorrectable      (sectors that CANNOT be read at all)
///   ID BB  — Reported Uncorrectable     (ECC couldn't fix it)
///
/// A drive with non-zero C5 or C6 is ACTIVELY DYING. Image it immediately.
///
/// Usage:
///   var monitor = new SmartMonitor(@"\\.\PhysicalDrive0");
///   var result  = monitor.Read();
///   if (result.IsCritical) Log("DRIVE FAILING — image immediately");
/// </summary>
public sealed class SmartMonitor
{
    private readonly string _drivePath;

    public SmartMonitor(string drivePath) => _drivePath = drivePath;

    // ─────────────────────────────────────────────────────────────────
    //  P/Invoke
    // ─────────────────────────────────────────────────────────────────

    private const uint GENERIC_READ    = 0x80000000;
    private const uint GENERIC_WRITE   = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE= 0x02;
    private const uint OPEN_EXISTING   = 3;
    private const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x4D030;
    private const byte ATA_SMART_CMD   = 0xB0;
    private const byte SMART_READ_DATA = 0xD0;
    private const byte SMART_LBA_MID   = 0x4F;
    private const byte SMART_LBA_HIGH  = 0xC2;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecA, uint dwCreationDisp,
        uint dwFlagsAndAttr, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        [In] ref ATA_PASS_THROUGH_DIRECT lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct ATA_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId, TargetId, Lun, ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public uint ReservedAsUlong;
        public IntPtr DataBuffer;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PreviousTaskFile;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] CurrentTaskFile;
    }

    private const ushort ATA_FLAGS_DATA_IN = 0x02;
    private const ushort ATA_FLAGS_48BIT   = 0x04;

    // ─────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────

    public SmartResult Read()
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFile(_drivePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
                return SmartResult.Unavailable(_drivePath,
                    "Cannot open drive handle (needs admin).");

            byte[] smartData = new byte[512];
            GCHandle pinned = GCHandle.Alloc(smartData, GCHandleType.Pinned);
            try
            {
                var cmd = BuildSmartReadCommand(pinned.AddrOfPinnedObject());
                bool ok = DeviceIoControl(handle, IOCTL_ATA_PASS_THROUGH_DIRECT,
                    ref cmd, Marshal.SizeOf<ATA_PASS_THROUGH_DIRECT>(),
                    smartData, smartData.Length, out _, IntPtr.Zero);

                if (!ok)
                    return SmartResult.Unavailable(_drivePath,
                        "ATA passthrough failed (NVMe or USB adaptor may not support SMART).");

                return ParseSmartData(smartData);
            }
            finally { pinned.Free(); }
        }
        catch (Exception ex)
        {
            return SmartResult.Unavailable(_drivePath, ex.Message);
        }
        finally { handle?.Dispose(); }
    }

    private static ATA_PASS_THROUGH_DIRECT BuildSmartReadCommand(IntPtr buf)
    {
        return new ATA_PASS_THROUGH_DIRECT
        {
            Length             = (ushort)Marshal.SizeOf<ATA_PASS_THROUGH_DIRECT>(),
            AtaFlags           = ATA_FLAGS_DATA_IN,
            DataTransferLength = 512,
            TimeOutValue       = 10,
            DataBuffer         = buf,
            PreviousTaskFile   = new byte[8],
            CurrentTaskFile    = new byte[]
            {
                0,              // Features: SMART_READ_DATA
                SMART_READ_DATA,
                0,              // Sector Count
                0,              // LBA Low
                SMART_LBA_MID,  // LBA Mid = 0x4F (SMART magic)
                SMART_LBA_HIGH, // LBA High = 0xC2 (SMART magic)
                0xA0,           // Device
                ATA_SMART_CMD,  // Command = 0xB0
            },
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  Data parsing
    // ─────────────────────────────────────────────────────────────────

    private SmartResult ParseSmartData(byte[] data)
    {
        // SMART attribute table: starts at byte 2, each entry is 12 bytes
        // Format: [id, flags_lo, flags_hi, current, worst, raw[6], 0]
        var attrs = new List<SmartAttribute>();
        for (int i = 2; i + 12 <= 362; i += 12)
        {
            byte id = data[i];
            if (id == 0) continue;

            byte current = data[i + 3];
            byte worst   = data[i + 4];
            long raw     = 0;
            for (int b = 0; b < 6; b++)
                raw |= (long)data[i + 5 + b] << (8 * b);

            attrs.Add(new SmartAttribute(id, current, worst, raw));
        }

        return new SmartResult
        {
            Drive       = _drivePath,
            Available   = true,
            Attributes  = attrs,
            // Pre-extract the ones we care most about
            ReallocatedSectors  = GetRaw(attrs, 0x05),
            PowerOnHours        = GetRaw(attrs, 0x09),
            TemperatureCelsius  = (int)GetRaw(attrs, 0xC2),
            PendingSectors      = GetRaw(attrs, 0xC5),
            UncorrectableSectors= GetRaw(attrs, 0xC6),
        };
    }

    private static long GetRaw(List<SmartAttribute> attrs, byte id)
        => attrs.FirstOrDefault(a => a.Id == id)?.Raw ?? -1;
}

// ── Result types ──────────────────────────────────────────────────────

public sealed class SmartResult
{
    public string Drive      { get; init; } = "";
    public bool   Available  { get; init; }
    public string? Error     { get; init; }
    public List<SmartAttribute> Attributes { get; init; } = new();

    public long ReallocatedSectors   { get; init; }
    public long PowerOnHours         { get; init; }
    public int  TemperatureCelsius   { get; init; }
    public long PendingSectors       { get; init; }
    public long UncorrectableSectors { get; init; }

    /// <summary>True if ANY data-loss indicator is non-zero.</summary>
    public bool IsCritical =>
        Available && (PendingSectors > 0 || UncorrectableSectors > 0);

    /// <summary>True if reallocation has occurred (drive is remapping sectors).</summary>
    public bool IsWarning  =>
        Available && ReallocatedSectors > 0;

    public HealthStatus HealthStatus => !Available   ? HealthStatus.Unknown
                                      : IsCritical   ? HealthStatus.Critical
                                      : IsWarning    ? HealthStatus.Warning
                                                     : HealthStatus.Good;

    public string HealthSummary => HealthStatus switch
    {
        HealthStatus.Good     => $"Good — {PowerOnHours}h on, {TemperatureCelsius}°C",
        HealthStatus.Warning  => $"Warning — {ReallocatedSectors} reallocated sectors. Image drive now.",
        HealthStatus.Critical => $"CRITICAL — {PendingSectors} pending, {UncorrectableSectors} uncorrectable. Drive is failing.",
        _                     => Error ?? "SMART unavailable (USB/NVMe or unsupported)",
    };

    public static SmartResult Unavailable(string drive, string reason)
        => new() { Drive = drive, Available = false, Error = reason };
}

public sealed record SmartAttribute(byte Id, byte Current, byte Worst, long Raw)
{
    public string Name => Id switch
    {
        0x01 => "Raw Read Error Rate",
        0x05 => "Reallocated Sectors",
        0x09 => "Power-On Hours",
        0x0C => "Power Cycle Count",
        0xBB => "Reported Uncorrectable",
        0xC0 => "Unsafe Shutdown Count",
        0xC2 => "Temperature",
        0xC5 => "Current Pending Sectors",
        0xC6 => "Offline Uncorrectable",
        0xC7 => "Ultra DMA CRC Error",
        0xF1 => "Total LBAs Written",
        0xF2 => "Total LBAs Read",
        _    => $"Attr 0x{Id:X2}",
    };
}

public enum HealthStatus { Unknown, Good, Warning, Critical }
