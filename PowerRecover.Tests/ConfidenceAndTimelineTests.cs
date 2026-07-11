// ConfidenceAndTimelineTests.cs  —  PowerRecover.Tests
// Pure unit tests — no disk images required.
// Tests ConfidenceScorer and ForensicTimeline using real RecoveredFile field names.

using System;
using System.Linq;
using Xunit;
using PowerRecover.Engine;

namespace PowerRecover.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// ConfidenceScorer tests
// ─────────────────────────────────────────────────────────────────────────────

public class ConfidenceScorerTests
{
    // Helper: make a minimal valid RecoveredFile (Name + Ext are required)
    private static RecoveredFile Make(string name, string ext,
        long size = 50_000, string method = "Carve",
        bool deleted = false) =>
        new RecoveredFile
        {
            Name    = name,
            Ext     = ext,
            Size    = size,
            Method  = method,
            Deleted = deleted,
        };

    [Fact]
    public void Score_ActiveNtfsFile_Returns100()
    {
        var rf = Make("document.docx", "docx", method: "NTFS-MFT", deleted: false);
        int score = ConfidenceScorer.Score(rf);
        Assert.Equal(100, score);
    }

    [Fact]
    public void Score_DeletedNtfsFile_AtLeast70()
    {
        var rf = Make("old_photo.jpg", "jpg", size: 200_000,
                      method: "NTFS-MFT", deleted: true);
        rf.ClusterSize = 4096;
        rf.Runs = new() { (49, 100) };

        int score = ConfidenceScorer.Score(rf);
        Assert.True(score >= 70,
            $"Deleted NTFS file should score >= 70, got {score}");
    }

    [Fact]
    public void Score_CarvedFile_ReturnsInRange()
    {
        var rf = Make("carved_00001.jpg", "jpg", size: 500_000);
        int score = ConfidenceScorer.Score(rf);
        Assert.InRange(score, 0, 100);
    }

    [Fact]
    public void Score_TinyCarvedFile_LowScore()
    {
        var rf = Make("carved_00002.jpg", "jpg", size: 50); // suspiciously tiny
        int score = ConfidenceScorer.Score(rf);
        Assert.True(score < 70,
            $"Tiny carved file should score < 70, got {score}");
    }

    [Fact]
    public void Score_ZeroSize_VeryLow()
    {
        var rf = Make("carved_00003.pdf", "pdf", size: 0);
        int score = ConfidenceScorer.Score(rf);
        Assert.True(score < 50,
            $"Zero-size file should score < 50, got {score}");
    }

    [Fact]
    public void Score_AlwaysNonNegative()
    {
        var rf = Make("x.png", "png", size: 1);
        Assert.True(ConfidenceScorer.Score(rf) >= 0);
    }

    [Fact]
    public void Score_NeverExceeds100()
    {
        var rf = Make("big.mp4", "mp4", size: 500_000_000,
                      method: "NTFS-MFT", deleted: false);
        Assert.True(ConfidenceScorer.Score(rf) <= 100);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ForensicTimeline tests
// ─────────────────────────────────────────────────────────────────────────────

public class ForensicTimelineTests
{
    private static DateTime Ts(string iso) =>
        DateTime.Parse(iso, null,
            System.Globalization.DateTimeStyles.RoundtripKind);

    private static MftTimestamps MakeMft(string name,
        string created, string modified, string accessed, string changed,
        bool deleted = false, long rec = 1) =>
        new MftTimestamps
        {
            MftRecord    = rec,
            FileName     = name,
            Created      = Ts(created),
            Modified     = Ts(modified),
            Accessed     = Ts(accessed),
            EntryChanged = Ts(changed),
            IsDeleted    = deleted,
        };

    // ── Build / empty ─────────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyInputs_ReturnsEmpty()
    {
        var tl = ForensicTimeline.Build(
            Array.Empty<MftTimestamps>(),
            Array.Empty<UsnRecord>());
        Assert.Empty(tl.Events);
    }

    [Fact]
    public void Build_SingleMftEntry_EmitsFourEvents()
    {
        var m = MakeMft("test.txt",
            "2023-06-01T10:00:00Z",
            "2023-06-02T10:00:00Z",
            "2023-06-03T10:00:00Z",
            "2023-06-04T10:00:00Z");

        var tl = ForensicTimeline.Build(new[] { m }, Array.Empty<UsnRecord>());

        // Created + Modified + Accessed + MetadataChanged
        Assert.Equal(4, tl.Events.Count);
    }

    [Fact]
    public void Build_DeletedMftEntry_EmitsDeletedEvent()
    {
        var m = MakeMft("gone.docx",
            "2023-01-01T00:00:00Z",
            "2023-01-02T00:00:00Z",
            "2023-01-02T00:00:00Z",
            "2023-06-15T00:00:00Z",
            deleted: true);

        var tl = ForensicTimeline.Build(new[] { m }, Array.Empty<UsnRecord>());

        Assert.Contains(tl.Events, e => e.Kind == TimelineEventKind.Deleted);
    }

    // ── Sort order ────────────────────────────────────────────────────────

    [Fact]
    public void Build_EventsAreSortedChronologically()
    {
        var entries = new[]
        {
            MakeMft("b.txt",
                "2023-03-01T00:00:00Z","2023-04-01T00:00:00Z",
                "2023-05-01T00:00:00Z","2023-06-01T00:00:00Z", rec: 1),
            MakeMft("a.txt",
                "2022-01-01T00:00:00Z","2022-02-01T00:00:00Z",
                "2022-03-01T00:00:00Z","2022-04-01T00:00:00Z", rec: 2),
        };

        var tl = ForensicTimeline.Build(entries, Array.Empty<UsnRecord>());

        for (int i = 1; i < tl.Events.Count; i++)
            Assert.True(tl.Events[i].Timestamp >= tl.Events[i - 1].Timestamp,
                "Events are not in chronological order");
    }

    // ── USN records ───────────────────────────────────────────────────────

    [Fact]
    public void Build_UsnDeleteRecord_EmitsDeletedEvent()
    {
        var usn = new UsnRecord
        {
            Usn       = 123456,
            Timestamp = Ts("2023-09-10T08:30:00Z"),
            FileRef   = 500,
            FileName  = "important.xlsx",
            Reason    = UsnReason.FileDelete,
        };

        var tl = ForensicTimeline.Build(
            Array.Empty<MftTimestamps>(), new[] { usn });

        Assert.Single(tl.Events);
        Assert.Equal(TimelineEventKind.Deleted, tl.Events[0].Kind);
        Assert.Equal("important.xlsx", tl.Events[0].FileName);
        Assert.Equal(123456L, tl.Events[0].Usn);
        Assert.Equal("$UsnJrnl", tl.Events[0].Source);
    }

    [Fact]
    public void Build_UsnMultipleFlags_EmitsMultipleEvents()
    {
        var usn = new UsnRecord
        {
            Usn       = 999,
            Timestamp = Ts("2023-10-01T12:00:00Z"),
            FileRef   = 777,
            FileName  = "updated.docx",
            Reason    = UsnReason.DataOverwrite | UsnReason.BasicInfoChange,
        };

        var tl = ForensicTimeline.Build(
            Array.Empty<MftTimestamps>(), new[] { usn });

        Assert.Equal(2, tl.Events.Count);
    }

    [Fact]
    public void Build_UsnCreateRecord_EmitsCreatedEvent()
    {
        var usn = new UsnRecord
        {
            Usn       = 1,
            Timestamp = Ts("2024-01-01T00:00:00Z"),
            FileRef   = 100,
            FileName  = "newfile.txt",
            Reason    = UsnReason.FileCreate,
        };

        var tl = ForensicTimeline.Build(
            Array.Empty<MftTimestamps>(), new[] { usn });

        Assert.Contains(tl.Events, e => e.Kind == TimelineEventKind.Created);
    }

    // ── Statistics helpers ────────────────────────────────────────────────

    [Fact]
    public void CountByKind_ReturnsCorrectCount()
    {
        var m = MakeMft("a.txt",
            "2023-01-01T00:00:00Z",
            "2023-01-02T00:00:00Z",
            "2023-01-02T00:00:00Z",
            "2023-01-03T00:00:00Z",
            deleted: true);

        var tl = ForensicTimeline.Build(new[] { m }, Array.Empty<UsnRecord>());

        Assert.Equal(1, tl.CountByKind(TimelineEventKind.Created));
        Assert.Equal(1, tl.CountByKind(TimelineEventKind.Deleted));
    }

    [Fact]
    public void FilterByFile_ReturnsOnlyMatchingFile()
    {
        var entries = new[]
        {
            MakeMft("target.jpg",
                "2023-05-01T00:00:00Z","2023-05-02T00:00:00Z",
                "2023-05-02T00:00:00Z","2023-05-03T00:00:00Z", rec: 1),
            MakeMft("other.png",
                "2023-06-01T00:00:00Z","2023-06-02T00:00:00Z",
                "2023-06-02T00:00:00Z","2023-06-03T00:00:00Z", rec: 2),
        };

        var tl   = ForensicTimeline.Build(entries, Array.Empty<UsnRecord>());
        var hits = tl.FilterByFile("target.jpg").ToList();

        Assert.All(hits, e => Assert.Equal("target.jpg", e.FileName));
        Assert.DoesNotContain(hits, e => e.FileName == "other.png");
    }

    [Fact]
    public void DeletedFiles_ReturnsDeletedFilenames()
    {
        var usns = new[]
        {
            new UsnRecord { Usn=1, Timestamp=Ts("2023-01-01T00:00:00Z"),
                FileName="removed.docx", Reason=UsnReason.FileDelete },
            new UsnRecord { Usn=2, Timestamp=Ts("2023-01-02T00:00:00Z"),
                FileName="kept.xlsx", Reason=UsnReason.DataOverwrite },
        };

        var tl      = ForensicTimeline.Build(Array.Empty<MftTimestamps>(), usns);
        var deleted = tl.DeletedFiles().ToList();

        Assert.Contains("removed.docx", deleted);
        Assert.DoesNotContain("kept.xlsx", deleted);
    }

    // ── Edge cases ────────────────────────────────────────────────────────

    [Fact]
    public void InvalidTimestamps_AreSkipped()
    {
        var m = new MftTimestamps
        {
            MftRecord    = 1,
            FileName     = "ancient.txt",
            Created      = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Modified     = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Accessed     = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EntryChanged = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var tl = ForensicTimeline.Build(new[] { m }, Array.Empty<UsnRecord>());
        Assert.Empty(tl.Events);
    }

    [Fact]
    public void FilterByRange_ReturnsOnlyEventsInWindow()
    {
        var entries = new[]
        {
            MakeMft("old.txt",
                "2020-01-01T00:00:00Z","2020-01-02T00:00:00Z",
                "2020-01-02T00:00:00Z","2020-01-03T00:00:00Z", rec: 1),
            MakeMft("new.txt",
                "2024-01-01T00:00:00Z","2024-01-02T00:00:00Z",
                "2024-01-02T00:00:00Z","2024-01-03T00:00:00Z", rec: 2),
        };

        var tl   = ForensicTimeline.Build(entries, Array.Empty<UsnRecord>());
        var from = Ts("2023-01-01T00:00:00Z");
        var to   = Ts("2025-01-01T00:00:00Z");
        var hits = tl.FilterByRange(from, to).ToList();

        Assert.All(hits, e => Assert.Equal("new.txt", e.FileName));
        Assert.DoesNotContain(hits, e => e.FileName == "old.txt");
    }
}
