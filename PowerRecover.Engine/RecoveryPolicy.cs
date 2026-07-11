namespace PowerRecover.Engine;

public enum RecoveryRejectReason
{
    None,
    SystemPath,
    LowValueExtension,
    TooSmall,
    LowConfidence,
    InvalidContent,
}

public readonly record struct RecoveryDecision(
    bool Accepted,
    RecoveryRejectReason Reason,
    string Message)
{
    public static RecoveryDecision Accept() => new(true, RecoveryRejectReason.None, "");
    public static RecoveryDecision Reject(RecoveryRejectReason reason, string message)
        => new(false, reason, message);
}

public sealed class RecoveryPolicy
{
    private static readonly HashSet<string> ValuableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "rtf", "csv",
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "psd",
        "mp3", "wav", "flac", "ogg", "mp4", "mov", "avi", "mkv",
        "zip", "rar", "7z", "gz", "tar",
        "db", "sqlite", "msg", "pst", "ost", "xml",
    };

    private static readonly HashSet<string> LowValueExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ani", "bak", "bin", "cab", "cat", "chk", "com", "cpl", "cur", "dat",
        "dll", "dmp", "drv", "efi", "exe", "fon", "ico", "idx", "inf", "ini",
        "lnk", "log", "manifest", "mui", "ocx", "pf", "scr", "sys", "tmp",
        "ttf", "woff", "woff2",
    };

    private static readonly string[] SystemPathMarkers =
    {
        "\\$recycle.bin\\",
        "\\$windows.~bt\\",
        "\\$windows.~ws\\",
        "\\boot\\",
        "\\config.msi\\",
        "\\msocache\\",
        "\\pagefile.sys",
        "\\perflogs\\",
        "\\program files\\",
        "\\program files (x86)\\",
        "\\programdata\\microsoft\\windows\\",
        "\\recovery\\",
        "\\system volume information\\",
        "\\windows\\",
    };

    private static readonly string[] NoisyNameMarkers =
    {
        "thumbs.db",
        "desktop.ini",
        "iconcache",
        "ntuser.dat",
        "usrclass.dat",
    };

    public int MinFilesystemConfidence { get; init; } = 70;
    public int MinCarveConfidence { get; init; } = 88;
    public bool KeepOnlyUserValueFiles { get; init; } = true;
    public bool ExcludeWindowsAndAppFiles { get; init; } = true;
    public bool RequireValidCarvedContent { get; init; } = true;

    public RecoveryDecision Evaluate(RecoveredFile file)
    {
        NormalizeOfficeContainer(file);

        string ext = NormalizeExt(file.Ext);
        string fullPath = NormalizePath(file.FullPath);
        string name = file.Name.ToLowerInvariant();

        if (ExcludeWindowsAndAppFiles && LooksLikeSystemFile(fullPath, name))
            return RecoveryDecision.Reject(RecoveryRejectReason.SystemPath,
                "Windows/application/cache file skipped");

        if (KeepOnlyUserValueFiles &&
            (LowValueExtensions.Contains(ext) || !ValuableExtensions.Contains(ext)))
            return RecoveryDecision.Reject(RecoveryRejectReason.LowValueExtension,
                $"Low-value file type skipped: .{ext}");

        if (IsTooSmall(file, ext))
            return RecoveryDecision.Reject(RecoveryRejectReason.TooSmall,
                "Tiny placeholder/cache-sized file skipped");

        int minConfidence = IsCarved(file) ? MinCarveConfidence : MinFilesystemConfidence;
        if (file.Confidence >= 0 && file.Confidence < minConfidence)
            return RecoveryDecision.Reject(RecoveryRejectReason.LowConfidence,
                $"Confidence below {minConfidence}%");

        if (RequireValidCarvedContent && IsCarved(file) && file.Data is { Length: > 0 } data &&
            !HasValidContent(file, data))
        {
            return RecoveryDecision.Reject(RecoveryRejectReason.InvalidContent,
                "Carved content failed file-structure validation");
        }

        return RecoveryDecision.Accept();
    }

    private static bool IsCarved(RecoveredFile file)
        => file.Method.Contains("Carve", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExt(string ext)
        => ext.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpeg" => "jpg",
            "tiff" => "tif",
            "sqlite" => "db",
            var value => value,
        };

    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('/', '\\').ToLowerInvariant();
        return normalized.StartsWith('\\') ? normalized : "\\" + normalized;
    }

    private static bool LooksLikeSystemFile(string fullPath, string name)
    {
        if (name.StartsWith('$')) return true;
        if (NoisyNameMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;
        return SystemPathMarkers.Any(fullPath.Contains);
    }

    private static bool IsTooSmall(RecoveredFile file, string ext)
    {
        if (file.Size <= 0) return true;

        long min = ext switch
        {
            "pdf" => 512,
            "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" => 512,
            "jpg" or "png" or "gif" or "bmp" or "tif" or "webp" or "heic" => 256,
            "mp3" or "wav" or "flac" or "ogg" or "mp4" or "mov" or "avi" or "mkv" => 4096,
            _ => 64,
        };

        return file.Size < min;
    }

    private static void NormalizeOfficeContainer(RecoveredFile file)
    {
        if (!file.Ext.Equals("zip", StringComparison.OrdinalIgnoreCase) ||
            file.Data is not { Length: > 0 } data)
        {
            return;
        }

        string? ext = DetectOfficeOpenXml(data);
        if (ext == null) return;

        file.Ext = ext;
        int dot = file.Name.LastIndexOf('.');
        file.Name = dot >= 0 ? file.Name[..dot] + "." + ext : file.Name + "." + ext;
        file.Method = "Office-Carve";
    }

    private static string? DetectOfficeOpenXml(byte[] data)
    {
        if (ContainsAscii(data, "word/document.xml")) return "docx";
        if (ContainsAscii(data, "xl/workbook.xml")) return "xlsx";
        if (ContainsAscii(data, "ppt/presentation.xml")) return "pptx";
        return null;
    }

    private static bool HasValidContent(RecoveredFile file, byte[] data)
    {
        string ext = NormalizeExt(file.Ext);
        return ext switch
        {
            "jpg" => data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8 &&
                     data[^2] == 0xFF && data[^1] == 0xD9,
            "png" => data.Length > 33 &&
                     data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) &&
                     ContainsAscii(data, "IEND"),
            "gif" => data.Length > 16 &&
                     data[0] == 'G' && data[1] == 'I' && data[2] == 'F' &&
                     data[^1] == 0x3B,
            "pdf" => StartsWithAscii(data, "%PDF-") && ContainsAscii(data, "%%EOF"),
            "docx" or "xlsx" or "pptx" => StartsWithZip(data) && DetectOfficeOpenXml(data) == ext,
            "zip" => StartsWithZip(data) && Contains(data, 0x50, 0x4B, 0x05, 0x06),
            "doc" or "xls" or "ppt" => data.Length > 512 &&
                     data.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
            _ => true,
        };
    }

    private static bool StartsWithZip(byte[] data)
        => data.Length > 4 && data[0] == 0x50 && data[1] == 0x4B &&
           data[2] == 0x03 && data[3] == 0x04;

    private static bool StartsWithAscii(byte[] data, string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        return data.Length >= bytes.Length && data.AsSpan(0, bytes.Length).SequenceEqual(bytes);
    }

    private static bool ContainsAscii(byte[] data, string value)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
        return data.AsSpan().IndexOf(bytes) >= 0;
    }

    private static bool Contains(byte[] data, byte b0, byte b1, byte b2, byte b3)
    {
        for (int i = 0; i + 3 < data.Length; i++)
            if (data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3)
                return true;
        return false;
    }
}
