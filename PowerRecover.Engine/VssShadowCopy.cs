using System.Management;
using System.Runtime.InteropServices;

namespace PowerRecover.Engine;

/// <summary>
/// Enumerates and accesses Windows Volume Shadow Copy Service (VSS)
/// snapshots — letting the user recover files from previous versions
/// of a volume without needing a backup.
///
/// VSS snapshots are accessible at:
///   \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\
///
/// We enumerate them via WMI (Win32_ShadowCopy), then the user can
/// pick a snapshot and we open it as a scan source — the existing
/// NTFS/FAT/carve pipeline works unchanged.
///
/// This is powerful for recovering files that were deleted days ago
/// and have since been overwritten on the live volume.
/// </summary>
public sealed class VssShadowCopy
{
    public event Action<string>? Log;

    // ── Enumeration ───────────────────────────────────────────────────

    /// <summary>List all available VSS shadow copies on this machine.</summary>
    public List<ShadowCopyInfo> GetShadowCopies()
    {
        var results = new List<ShadowCopyInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\cimv2",
                "SELECT * FROM Win32_ShadowCopy ORDER BY InstallDate DESC");

            foreach (ManagementObject obj in searcher.Get())
            {
                string? id           = obj["ID"]?.ToString();
                string? deviceObject = obj["DeviceObject"]?.ToString();
                string? volumeName   = obj["VolumeName"]?.ToString();
                string? installDate  = obj["InstallDate"]?.ToString();
                string? clientAcc   = obj["ClientAccessible"]?.ToString();

                if (string.IsNullOrEmpty(deviceObject)) continue;

                DateTime created = DateTime.MinValue;
                if (!string.IsNullOrEmpty(installDate))
                    ManagementDateTimeConverter.ToDateTime(installDate);

                results.Add(new ShadowCopyInfo
                {
                    Id           = id ?? "",
                    DevicePath   = deviceObject,
                    VolumeName   = volumeName ?? "",
                    CreatedAt    = created,
                    ScanPath     = deviceObject + "\\",
                    Description  = $"Shadow copy of {volumeName} " +
                                   $"({(created == DateTime.MinValue ? "unknown date" : created.ToString("yyyy-MM-dd HH:mm"))})",
                });
            }

            Log?.Invoke($"Found {results.Count} VSS shadow copy snapshot(s).");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"VSS enumeration error: {ex.Message} " +
                        "(requires admin and VSS service running)");
        }

        return results;
    }

    /// <summary>Get shadow copies for a specific volume (e.g. "C:\\").</summary>
    public List<ShadowCopyInfo> GetShadowCopiesForVolume(string volumePath)
    {
        return GetShadowCopies()
            .Where(s => s.VolumeName.StartsWith(
                volumePath.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ── Access ────────────────────────────────────────────────────────

    /// <summary>
    /// Open a shadow copy as a scan source.
    /// The returned path can be passed directly to RawDisk(string).
    ///
    /// Shadow copies expose the full NTFS volume at the snapshot time,
    /// so NtfsScanner works normally on the returned path.
    /// </summary>
    public string GetScanPath(ShadowCopyInfo snapshot)
    {
        // VSS device path format: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN
        // Adding \ at the end makes it accessible as a directory/volume
        return snapshot.DevicePath.TrimEnd('\\') + "\\";
    }

    /// <summary>
    /// Create a symbolic link to a shadow copy for easier access.
    /// Returns the link path (e.g. C:\VSS_Link\) or null on failure.
    /// Requires admin and mklink.
    /// </summary>
    public string? CreateAccessLink(ShadowCopyInfo snapshot, string linkPath)
    {
        try
        {
            // Remove existing link
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath);

            // mklink /d creates a directory symbolic link
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c mklink /d \"{linkPath}\" " +
                                  $"\"{snapshot.DevicePath}\\\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(5000);

            if (Directory.Exists(linkPath))
            {
                Log?.Invoke($"VSS link created: {linkPath}");
                return linkPath;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"VSS link error: {ex.Message}");
        }
        return null;
    }

    // ── VSS snapshot creation (for live recovery) ─────────────────────

    /// <summary>
    /// Create a new VSS snapshot of a live volume.
    /// Returns the shadow copy ID on success.
    /// Useful for scanning a live, in-use volume safely.
    /// </summary>
    public string? CreateSnapshot(string volumePath)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2");
            scope.Connect();

            var shadowClass = new ManagementClass(scope,
                new ManagementPath("Win32_ShadowCopy"), null);

            var inParams = shadowClass.GetMethodParameters("Create");
            inParams["Volume"]  = volumePath;
            inParams["Context"] = "ClientAccessible";

            var outParams = shadowClass.InvokeMethod("Create", inParams, null);
            uint result   = (uint)outParams["ReturnValue"];
            string? id    = outParams["ShadowID"]?.ToString();

            if (result == 0 && id != null)
            {
                Log?.Invoke($"VSS snapshot created: {id}");
                return id;
            }
            Log?.Invoke($"VSS create failed: error {result}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"VSS create error: {ex.Message}");
        }
        return null;
    }

    /// <summary>Delete a shadow copy by ID (cleanup after scanning).</summary>
    public void DeleteSnapshot(string shadowId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\cimv2",
                $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");

            foreach (ManagementObject obj in searcher.Get())
            {
                obj.Delete();
                Log?.Invoke($"VSS snapshot deleted: {shadowId}");
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"VSS delete error: {ex.Message}");
        }
    }
}

/// <summary>Describes one VSS shadow copy snapshot.</summary>
public sealed class ShadowCopyInfo
{
    public string   Id          { get; set; } = "";
    public string   DevicePath  { get; set; } = "";
    public string   VolumeName  { get; set; } = "";
    public DateTime CreatedAt   { get; set; }
    public string   ScanPath    { get; set; } = "";
    public string   Description { get; set; } = "";

    public override string ToString() => Description;
}
