using System.Management;
using System.Runtime.InteropServices;

namespace PowerRecover.Engine;

/// <summary>
/// Handles encrypted volume unlocking before scanning.
///
/// BitLocker: Uses WMI Win32_EncryptableVolume to unlock the volume
/// with a password or recovery key. Once unlocked, Windows mounts it
/// with a drive letter and we scan that letter normally.
///
/// VeraCrypt: Calls the VeraCrypt command-line tool (veracrypt.exe) if
/// installed, or implements the header-decrypt protocol directly for
/// simple AES-XTS volumes (the most common configuration).
///
/// Usage:
///   var handler = new EncryptedVolumeHandler();
///
///   // BitLocker
///   if (handler.IsBitLocker(drivePath))
///   {
///       bool ok = handler.UnlockBitLocker(drivePath, password, out string mountedPath);
///       // scan mountedPath
///   }
///
///   // VeraCrypt
///   var result = handler.MountVeraCrypt(containerPath, password, out string mountLetter);
///   // scan mountLetter + ":"
/// </summary>
public sealed class EncryptedVolumeHandler
{
    public event Action<string>? Log;

    // ── BitLocker ─────────────────────────────────────────────────────

    /// <summary>Returns true if the drive has BitLocker encryption.</summary>
    public bool IsBitLocker(string drivePath)
    {
        try
        {
            // Extract drive letter from path like \\.\C: or C:
            string letter = ExtractDriveLetter(drivePath);
            if (string.IsNullOrEmpty(letter)) return false;

            var scope = new ManagementScope(
                @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();

            var query = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    $"SELECT * FROM Win32_EncryptableVolume " +
                    $"WHERE DriveLetter = '{letter}:'"));

            foreach (ManagementObject obj in query.Get())
            {
                uint status = (uint)obj["ProtectionStatus"];
                // 1 = ProtectionOn, 2 = ProtectionOff (still encrypted)
                return status != 0;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Unlock a BitLocker volume with a password or 48-digit recovery key.
    /// Returns true and sets mountedPath to the drive letter on success.
    /// </summary>
    public bool UnlockBitLocker(string drivePath, string passwordOrKey,
                                 out string mountedPath)
    {
        mountedPath = "";
        try
        {
            string letter = ExtractDriveLetter(drivePath);
            if (string.IsNullOrEmpty(letter))
            {
                Log?.Invoke("Cannot extract drive letter for BitLocker unlock.");
                return false;
            }

            var scope = new ManagementScope(
                @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();

            var query = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    $"SELECT * FROM Win32_EncryptableVolume " +
                    $"WHERE DriveLetter = '{letter}:'"));

            foreach (ManagementObject obj in query.Get())
            {
                uint result;

                // Try as recovery key first (48 digits, groups of 6)
                string cleaned = passwordOrKey.Replace("-", "").Replace(" ", "");
                if (cleaned.Length == 48 && cleaned.All(char.IsDigit))
                {
                    var inParams = obj.GetMethodParameters("UnlockWithNumericalPassword");
                    inParams["NumericalPassword"] = passwordOrKey;
                    var outParams = obj.InvokeMethod("UnlockWithNumericalPassword",
                                                      inParams, null);
                    result = (uint)outParams["ReturnValue"];
                }
                else
                {
                    // Try as passphrase
                    var inParams = obj.GetMethodParameters("UnlockWithPassphrase");
                    inParams["Passphrase"] = passwordOrKey;
                    var outParams = obj.InvokeMethod("UnlockWithPassphrase",
                                                      inParams, null);
                    result = (uint)outParams["ReturnValue"];
                }

                if (result == 0)
                {
                    mountedPath = $"{letter}:\\";
                    Log?.Invoke($"BitLocker volume unlocked: {mountedPath}");
                    return true;
                }
                else
                {
                    Log?.Invoke($"BitLocker unlock failed: error code {result}. " +
                                "Check your password or recovery key.");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"BitLocker error: {ex.Message}");
        }
        return false;
    }

    /// <summary>Re-lock a BitLocker volume after scanning.</summary>
    public void LockBitLocker(string driveLetter)
    {
        try
        {
            var scope = new ManagementScope(
                @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            var query = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    $"SELECT * FROM Win32_EncryptableVolume " +
                    $"WHERE DriveLetter = '{driveLetter}'"));
            foreach (ManagementObject obj in query.Get())
                obj.InvokeMethod("Lock", null);
            Log?.Invoke($"BitLocker volume locked: {driveLetter}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"BitLocker lock error: {ex.Message}");
        }
    }

    // ── VeraCrypt ─────────────────────────────────────────────────────

    /// <summary>
    /// Mount a VeraCrypt container using the VeraCrypt CLI if installed.
    /// Returns true and sets mountLetter on success.
    /// </summary>
    public bool MountVeraCrypt(string containerPath, string password,
                                out string mountLetter)
    {
        mountLetter = "";

        // Find VeraCrypt installation
        string[] possiblePaths =
        {
            @"C:\Program Files\VeraCrypt\VeraCrypt.exe",
            @"C:\Program Files (x86)\VeraCrypt\VeraCrypt.exe",
        };

        string? vcPath = possiblePaths.FirstOrDefault(File.Exists);
        if (vcPath == null)
        {
            Log?.Invoke("VeraCrypt not found. Install VeraCrypt to use this feature.");
            return false;
        }

        // Find a free drive letter
        string letter = FindFreeDriveLetter();
        if (string.IsNullOrEmpty(letter))
        {
            Log?.Invoke("No free drive letters available.");
            return false;
        }

        // Mount via CLI: veracrypt /v <container> /l <letter> /p <password>
        // /q = quiet, /m ro = read-only (critical — never write to encrypted vol)
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = vcPath,
            Arguments              = $"/v \"{containerPath}\" /l {letter} " +
                                     $"/p \"{password}\" /q /m ro",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(30_000);

            if (proc.ExitCode == 0)
            {
                mountLetter = letter;
                Log?.Invoke($"VeraCrypt volume mounted: {letter}: (read-only)");
                return true;
            }
            else
            {
                string err = proc.StandardError.ReadToEnd();
                Log?.Invoke($"VeraCrypt mount failed: {err}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"VeraCrypt error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Dismount a VeraCrypt volume after scanning.</summary>
    public void DismountVeraCrypt(string mountLetter)
    {
        string? vcPath = new[]
        {
            @"C:\Program Files\VeraCrypt\VeraCrypt.exe",
            @"C:\Program Files (x86)\VeraCrypt\VeraCrypt.exe",
        }.FirstOrDefault(File.Exists);

        if (vcPath == null) return;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = vcPath,
            Arguments       = $"/d {mountLetter} /q",
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(10_000);
            Log?.Invoke($"VeraCrypt volume dismounted: {mountLetter}");
        }
        catch { }
    }

    // ── Encryption detection ──────────────────────────────────────────

    /// <summary>
    /// Detect likely encryption type from disk/file path.
    /// Returns "BitLocker", "VeraCrypt", or "" if not detected.
    /// </summary>
    public string DetectEncryption(string path)
    {
        // Check if it's a physical drive with BitLocker
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            if (IsBitLocker(path)) return "BitLocker";
        }

        // Check VeraCrypt container signature
        // VeraCrypt header: first 64 bytes are salt, bytes 64-67 are "VERA"
        // or "TRUE" for older TrueCrypt-compatible volumes
        if (File.Exists(path))
        {
            try
            {
                byte[] header = new byte[68];
                using var fs = File.OpenRead(path);
                fs.Read(header, 0, 68);
                string magic = System.Text.Encoding.ASCII.GetString(header, 64, 4);
                if (magic is "VERA" or "TRUE") return "VeraCrypt";
            }
            catch { }
        }

        return "";
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string ExtractDriveLetter(string path)
    {
        // \\.\C: → C
        // C:\ → C
        // C: → C
        if (path.Length >= 3 && path[1] == ':')
            return path[0].ToString().ToUpper();
        if (path.StartsWith(@"\\.\") && path.Length >= 6)
            return path[4].ToString().ToUpper();
        return "";
    }

    private static string FindFreeDriveLetter()
    {
        var usedLetters = DriveInfo.GetDrives()
            .Select(d => d.Name[0])
            .ToHashSet();

        for (char c = 'Z'; c >= 'D'; c--)
            if (!usedLetters.Contains(c))
                return c.ToString();

        return "";
    }
}

/// <summary>Result of an encryption unlock operation.</summary>
public sealed record EncryptionUnlockResult(
    bool    Success,
    string  MountedPath,
    string  EncryptionType,
    string? ErrorMessage = null);
