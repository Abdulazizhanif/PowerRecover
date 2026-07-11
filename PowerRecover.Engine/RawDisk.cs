using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerRecover.Engine;

/// <summary>
/// Provides raw, READ-ONLY access to a physical drive, a volume, or a
/// disk-image file. On Windows a physical drive is opened with
/// CreateFile(@"\\.\PhysicalDrive0"). Image files use a normal FileStream.
/// </summary>
public sealed class RawDisk : IDisposable
{
    private readonly FileStream _stream;
    private readonly SafeFileHandle? _handle;

    public long Length { get; }
    public int SectorSize { get; }
    public string Source { get; }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        out DISK_GEOMETRY lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        out long lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_GEOMETRY
    {
        public long Cylinders;
        public uint MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x70000;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x7405C;

    public RawDisk(string source)
    {
        Source = source;

        // Image file path -> open normally
        if (File.Exists(source))
        {
            _stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                                     FileShare.ReadWrite);
            Length = _stream.Length;
            SectorSize = 512;
            return;
        }

        // Physical device -> CreateFile. NO write flags = read-only.
        _handle = CreateFile(source, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, 0, IntPtr.Zero);

        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Cannot open '{source}'. Run as Administrator.");

        // Query geometry for sector size
        if (DeviceIoControl(_handle, IOCTL_DISK_GET_DRIVE_GEOMETRY,
                IntPtr.Zero, 0, out DISK_GEOMETRY geo,
                (uint)Marshal.SizeOf<DISK_GEOMETRY>(),
                out _, IntPtr.Zero))
        {
            SectorSize = (int)geo.BytesPerSector;
        }
        else SectorSize = 512;

        // Query total length
        if (DeviceIoControl(_handle, IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero, 0, out long len, sizeof(long),
                out _, IntPtr.Zero))
        {
            Length = len;
        }

        _stream = new FileStream(_handle, FileAccess.Read);
    }

    /// <summary>
    /// Reads exactly count bytes at the given absolute offset into buffer.
    /// On an I/O error (bad sector) it retries sector-by-sector and fills
    /// unreadable sectors with zero, returning how many bytes are "good".
    /// Never throws on bad sectors - that's the whole point.
    /// </summary>
    public int ReadAt(long offset, byte[] buffer, int count,
                      out int badSectors)
    {
        badSectors = 0;
        try
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            return ReadFull(buffer, count);
        }
        catch (IOException)
        {
            // Fall back to sector-granular reads
            int done = 0;
            int ss = SectorSize;
            byte[] sec = new byte[ss];
            while (done < count)
            {
                long pos = offset + done;
                int want = Math.Min(ss, count - done);
                try
                {
                    _stream.Seek(pos, SeekOrigin.Begin);
                    int got = ReadFull(sec, ss);
                    Array.Copy(sec, 0, buffer, done, Math.Min(got, want));
                    if (got == 0) break;
                }
                catch (IOException)
                {
                    Array.Clear(buffer, done, want); // zero-fill bad sector
                    badSectors++;
                }
                done += want;
            }
            return done;
        }
    }

    private int ReadFull(byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = _stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _handle?.Dispose();
    }
}
