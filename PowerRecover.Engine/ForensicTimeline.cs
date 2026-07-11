// ForensicTimeline.cs  —  PowerRecover.Engine
// Merges MFT MACE timestamps and $UsnJrnl change records into a single
// chronological event list.  Requires NtfsScanner + NtfsJournalScanner.
//
// Usage:
//   var timeline = ForensicTimeline.Build(ntfsFiles, journalRecords);
//   // Bind timeline.Events to a DataGrid or write to ReportExporter

using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerRecover.Engine
{
    // ────────────────────────────────────────────────────────────────────────
    // Data model
    // ────────────────────────────────────────────────────────────────────────

    public enum TimelineEventKind
    {
        Created,            // MFT $STANDARD_INFORMATION created time
        Modified,           // MFT $STANDARD_INFORMATION modified time
        Accessed,           // MFT $STANDARD_INFORMATION last-access time
        MetadataChanged,    // MFT $STANDARD_INFORMATION MFT-entry changed time
        Deleted,            // $UsnJrnl USN_REASON_FILE_DELETE record
        Renamed,            // $UsnJrnl USN_REASON_RENAME_NEW_NAME record
        Overwritten,        // $UsnJrnl USN_REASON_DATA_OVERWRITE record
        Truncated,          // $UsnJrnl USN_REASON_DATA_TRUNCATION record
        HardLinkChange,     // $UsnJrnl USN_REASON_HARD_LINK_CHANGE record
        AttributeChange     // $UsnJrnl USN_REASON_BASIC_INFO_CHANGE record
    }

    public sealed class TimelineEvent
    {
        /// When this event occurred (UTC).
        public DateTime Timestamp   { get; init; }

        /// Event category.
        public TimelineEventKind Kind { get; init; }

        /// Filename at the time of the event.
        public string FileName      { get; init; } = string.Empty;

        /// Full reconstructed path, if known.
        public string FolderPath    { get; init; } = string.Empty;

        /// MFT record number that owns this event (-1 if from journal only).
        public long MftRecord       { get; init; } = -1;

        /// USN sequence number (0 if from MFT timestamps).
        public long Usn             { get; init; }

        /// Human-readable one-liner for display.
        public string Description   { get; init; } = string.Empty;

        /// Source tag shown in the UI column.
        public string Source        =>
            Usn > 0 ? "$UsnJrnl" : "MFT";

        /// Full path for display (combines folder + name).
        public string FullPath      =>
            string.IsNullOrEmpty(FolderPath)
                ? FileName
                : FolderPath.TrimEnd('\\') + "\\" + FileName;

        public string KindText      => Kind.ToString();
    }

    // ────────────────────────────────────────────────────────────────────────
    // MFT timestamps — lightweight container for what NtfsScanner must supply
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents the four MACE timestamps from MFT $STANDARD_INFORMATION
    /// plus the file's identity.  NtfsScanner should populate these when it
    /// parses attribute 0x10.
    /// </summary>
    public sealed class MftTimestamps
    {
        public long     MftRecord   { get; init; }
        public string   FileName    { get; init; } = string.Empty;
        public string   FolderPath  { get; init; } = string.Empty;
        public DateTime Created     { get; init; }
        public DateTime Modified    { get; init; }
        public DateTime Accessed    { get; init; }
        public DateTime EntryChanged{ get; init; }
        public bool     IsDeleted   { get; init; }
    }

    // ────────────────────────────────────────────────────────────────────────
    // $UsnJrnl record — lightweight container (NtfsJournalScanner fills this)
    // ────────────────────────────────────────────────────────────────────────

    public sealed class UsnRecord
    {
        public long     Usn         { get; init; }
        public DateTime Timestamp   { get; init; }
        public long     FileRef     { get; init; }   // low 48 bits = MFT record
        public string   FileName    { get; init; } = string.Empty;
        public uint     Reason      { get; init; }   // USN_REASON_* flags
    }

    // USN_REASON flag constants (Win32)
    public static class UsnReason
    {
        public const uint DataOverwrite     = 0x00000001;
        public const uint DataExtend        = 0x00000002;
        public const uint DataTruncation    = 0x00000004;
        public const uint BasicInfoChange   = 0x00008000;
        public const uint RenameOldName     = 0x00001000;
        public const uint RenameNewName     = 0x00002000;
        public const uint FileCreate        = 0x00000100;
        public const uint FileDelete        = 0x00000200;
        public const uint HardLinkChange    = 0x00010000;
    }

    // ────────────────────────────────────────────────────────────────────────
    // The timeline builder
    // ────────────────────────────────────────────────────────────────────────

    public sealed class ForensicTimeline
    {
        public IReadOnlyList<TimelineEvent> Events { get; }

        private ForensicTimeline(List<TimelineEvent> events)
        {
            Events = events.AsReadOnly();
        }

        /// <summary>
        /// Merge MFT timestamps and $UsnJrnl records into one sorted list.
        /// Either parameter may be empty — the method handles partial data.
        /// </summary>
        public static ForensicTimeline Build(
            IEnumerable<MftTimestamps> mftEntries,
            IEnumerable<UsnRecord>    journalRecords)
        {
            var events = new List<TimelineEvent>();

            // ── 1. Expand MFT MACE timestamps ────────────────────────────
            foreach (var m in mftEntries)
            {
                // Skip obviously invalid timestamps (pre-NTFS era or far future)
                if (!IsValidTimestamp(m.Created))     continue;

                void Add(DateTime ts, TimelineEventKind kind, string desc) =>
                    events.Add(new TimelineEvent
                    {
                        Timestamp   = ts,
                        Kind        = kind,
                        FileName    = m.FileName,
                        FolderPath  = m.FolderPath,
                        MftRecord   = m.MftRecord,
                        Description = desc
                    });

                if (IsValidTimestamp(m.Created))
                    Add(m.Created, TimelineEventKind.Created,
                        $"{m.FileName} — created");

                if (IsValidTimestamp(m.Modified) && m.Modified != m.Created)
                    Add(m.Modified, TimelineEventKind.Modified,
                        $"{m.FileName} — last modified");

                if (IsValidTimestamp(m.Accessed) && m.Accessed != m.Modified)
                    Add(m.Accessed, TimelineEventKind.Accessed,
                        $"{m.FileName} — last accessed");

                if (IsValidTimestamp(m.EntryChanged) && m.EntryChanged != m.Modified)
                    Add(m.EntryChanged, TimelineEventKind.MetadataChanged,
                        $"{m.FileName} — MFT entry changed");

                // If the MFT marks the file as deleted, emit a deletion event
                // at the EntryChanged timestamp (best proxy we have from MFT alone)
                if (m.IsDeleted && IsValidTimestamp(m.EntryChanged))
                    Add(m.EntryChanged, TimelineEventKind.Deleted,
                        $"{m.FileName} — deleted (MFT $STANDARD_INFORMATION)");
            }

            // ── 2. Expand $UsnJrnl records ───────────────────────────────
            foreach (var u in journalRecords)
            {
                if (!IsValidTimestamp(u.Timestamp)) continue;

                // A single USN record can have multiple reason flags set —
                // emit one event per relevant flag.
                EmitIfSet(u, UsnReason.FileCreate,
                    TimelineEventKind.Created,    "created via journal");
                EmitIfSet(u, UsnReason.FileDelete,
                    TimelineEventKind.Deleted,    "deleted via journal");
                EmitIfSet(u, UsnReason.RenameNewName,
                    TimelineEventKind.Renamed,    "renamed (new name)");
                EmitIfSet(u, UsnReason.DataOverwrite,
                    TimelineEventKind.Overwritten,"data overwritten");
                EmitIfSet(u, UsnReason.DataTruncation,
                    TimelineEventKind.Truncated,  "data truncated");
                EmitIfSet(u, UsnReason.HardLinkChange,
                    TimelineEventKind.HardLinkChange, "hard link changed");
                EmitIfSet(u, UsnReason.BasicInfoChange,
                    TimelineEventKind.AttributeChange, "attributes changed");

                void EmitIfSet(UsnRecord rec, uint flag,
                               TimelineEventKind kind, string verb)
                {
                    if ((rec.Reason & flag) == 0) return;
                    events.Add(new TimelineEvent
                    {
                        Timestamp   = rec.Timestamp,
                        Kind        = kind,
                        FileName    = rec.FileName,
                        MftRecord   = rec.FileRef & 0x0000_FFFF_FFFF_FFFF,
                        Usn         = rec.Usn,
                        Description = $"{rec.FileName} — {verb} (USN {rec.Usn})"
                    });
                }
            }

            // ── 3. Sort chronologically ──────────────────────────────────
            events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            return new ForensicTimeline(events);
        }

        // ── Statistics helpers ────────────────────────────────────────────

        public int CountByKind(TimelineEventKind kind) =>
            Events.Count(e => e.Kind == kind);

        public IEnumerable<TimelineEvent> FilterByKind(TimelineEventKind kind) =>
            Events.Where(e => e.Kind == kind);

        public IEnumerable<TimelineEvent> FilterByFile(string fileName) =>
            Events.Where(e => e.FileName.Equals(
                fileName, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<TimelineEvent> FilterByRange(DateTime from, DateTime to) =>
            Events.Where(e => e.Timestamp >= from && e.Timestamp <= to);

        /// <summary>
        /// Returns distinct filenames that appear in both Created and Deleted
        /// events — files that existed and were then removed.
        /// </summary>
        public IEnumerable<string> DeletedFiles() =>
            Events.Where(e => e.Kind == TimelineEventKind.Deleted)
                  .Select(e => e.FileName)
                  .Distinct(StringComparer.OrdinalIgnoreCase);

        // ── Private helpers ───────────────────────────────────────────────

        private static readonly DateTime NtfsEpoch =
            new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime FutureLimit =
            new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static bool IsValidTimestamp(DateTime ts) =>
            ts.Kind == DateTimeKind.Utc
            && ts > NtfsEpoch
            && ts < FutureLimit;
    }
}
