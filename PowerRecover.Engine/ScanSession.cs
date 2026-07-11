using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerRecover.Engine;

/// <summary>
/// Saves and restores scan progress so a long carve scan (hours on a 2TB
/// drive) can survive a power cut, crash, or deliberate stop and be resumed
/// exactly where it left off.
///
/// Persistence format: one .prsession JSON file alongside the output folder.
/// Uses System.Text.Json — no NuGet dependencies.
///
/// Usage:
///   // Before scan:
///   var session = ScanSession.LoadOrCreate(source, outputDir);
///   long resumeOffset = session.CarveOffset;   // 0 for new scan
///
///   // During scan (call every ~512 MB in CarveScanner):
///   session.CarveOffset = currentPos;
///   session.Save();
///
///   // When a file is found:
///   session.AddFile(recoveredFile);
///   session.Save();
///
///   // On completion:
///   session.MarkComplete();
///   session.Save();
/// </summary>
public sealed class ScanSession
{
    // ── Persisted fields ─────────────────────────────────────────────
    public string  Source       { get; set; } = "";
    public string  OutputDir    { get; set; } = "";
    public long    CarveOffset  { get; set; } = 0;
    public long    MftRecordsDone { get; set; } = 0;
    public bool    MftComplete  { get; set; } = false;
    public bool    CarveComplete { get; set; } = false;
    public bool    IsComplete   { get; set; } = false;
    public int     BadSectors   { get; set; } = 0;
    public DateTime StartedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? ResumedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<SessionFile> Files { get; set; } = new();

    // ── Runtime-only (not serialized) ────────────────────────────────
    [JsonIgnore] public string SessionPath { get; private set; } = "";

    // ─────────────────────────────────────────────────────────────────
    //  Load / create
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Load an existing session for this source+output pair, or
    /// create a new one. Returns (session, isResume).</summary>
    public static (ScanSession session, bool isResume) LoadOrCreate(
        string source, string outputDir)
    {
        string path = GetSessionPath(outputDir, source);

        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<ScanSession>(json,
                    SerializerOptions);
                if (loaded != null && loaded.Source == source)
                {
                    loaded.SessionPath = path;
                    loaded.ResumedAt   = DateTime.UtcNow;
                    return (loaded, true);
                }
            }
            catch { /* corrupt session file → start fresh */ }
        }

        var session = new ScanSession
        {
            Source      = source,
            OutputDir   = outputDir,
            StartedAt   = DateTime.UtcNow,
            SessionPath = path,
        };
        return (session, false);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Persistence
    // ─────────────────────────────────────────────────────────────────

    public void Save()
    {
        if (string.IsNullOrEmpty(SessionPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        string json = JsonSerializer.Serialize(this, SerializerOptions);
        // Write to .tmp then rename — atomic replace, never corrupt
        string tmp = SessionPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, SessionPath, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(SessionPath)) File.Delete(SessionPath);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Mutation helpers
    // ─────────────────────────────────────────────────────────────────

    public void AddFile(RecoveredFile rf, string savedPath)
    {
        Files.Add(new SessionFile
        {
            Name        = rf.Name,
            Ext         = rf.Ext,
            Size        = rf.Size,
            Offset      = rf.Offset,
            Method      = rf.Method,
            Deleted     = rf.Deleted,
            SavedPath   = savedPath,
        });
    }

    public void MarkComplete()
    {
        IsComplete    = true;
        CompletedAt   = DateTime.UtcNow;
    }

    public void IncrementBadSectors(int count) => BadSectors += count;

    // ─────────────────────────────────────────────────────────────────
    //  Stats helpers
    // ─────────────────────────────────────────────────────────────────

    [JsonIgnore]
    public int FilesFound => Files.Count;

    [JsonIgnore]
    public int DeletedCount => Files.Count(f => f.Deleted);

    [JsonIgnore]
    public long TotalRecoveredBytes => Files.Sum(f => f.Size);

    [JsonIgnore]
    public TimeSpan ElapsedSinceStart =>
        (CompletedAt ?? DateTime.UtcNow) - StartedAt;

    [JsonIgnore]
    public string SummaryLine =>
        $"{FilesFound} files ({FormatSize(TotalRecoveredBytes)}) — " +
        $"{DeletedCount} deleted — {BadSectors} bad sectors — " +
        $"{ElapsedSinceStart:hh\\:mm\\:ss}";

    private static string FormatSize(long b)
    {
        if (b >= 1L << 30) return $"{b / (1L << 30):F2} GB";
        if (b >= 1L << 20) return $"{b / (1L << 20):F1} MB";
        if (b >= 1L << 10) return $"{b / (1L << 10):F0} KB";
        return $"{b} B";
    }

    // ─────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string GetSessionPath(string outputDir, string source)
    {
        // Hash the source path to a short ID so sessions are unique per drive
        int hash = Math.Abs(source.GetHashCode());
        return Path.Combine(outputDir, $"powerrecover_{hash:X8}.prsession");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Lightweight serializable summary of one recovered file,
/// stored in the session. Not the full RecoveredFile (which holds Data[]
/// and run lists that are too large to persist).</summary>
public sealed class SessionFile
{
    public string Name      { get; set; } = "";
    public string Ext       { get; set; } = "";
    public long   Size      { get; set; }
    public long   Offset    { get; set; }
    public string Method    { get; set; } = "";
    public bool   Deleted   { get; set; }
    public string SavedPath { get; set; } = "";
}
