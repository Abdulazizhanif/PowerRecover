// CarveTests.cs  —  PowerRecover.Tests
// Verifies signature carving against carve_test.img and ntfs_test.img.
// Place test images in PowerRecover.Tests/test_images/

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using PowerRecover.Engine;

namespace PowerRecover.Tests;

public class CarveTests : IDisposable
{
    private static string TestImagesDir =>
        Path.Combine(AppContext.BaseDirectory, "test_images");

    private static string CarveImg =>
        Path.Combine(TestImagesDir, "carve_test.img");

    private static string NtfsImg =>
        Path.Combine(TestImagesDir, "ntfs_test.img");

    private readonly string _outputDir;

    public CarveTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"pr_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static CarveScanner MakeCarver(RawDisk disk) =>
        new CarveScanner(disk, FileSignature.All);

    private static bool ImgMissing(string path) => !File.Exists(path);

    // ── Carve tests ───────────────────────────────────────────────────────

    [Fact]
    public void CarveImg_FileExists()
    {
        Assert.True(File.Exists(CarveImg),
            $"Missing: {CarveImg} — copy carve_test.img into PowerRecover.Tests/test_images/");
    }

    [Fact]
    public void CarveImg_FindsExactlyFourFiles()
    {
        if (ImgMissing(CarveImg)) return; // skip gracefully if no image

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        Assert.Equal(4, results.Count);
    }

    [Fact]
    public void CarveImg_FindsJpg()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        Assert.Contains(results,
            r => r.Ext.Equals("jpg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CarveImg_FindsPng()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        Assert.Contains(results,
            r => r.Ext.Equals("png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CarveImg_FindsPdf()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        Assert.Contains(results,
            r => r.Ext.Equals("pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CarveImg_FindsZip()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        Assert.Contains(results,
            r => r.Ext.Equals("zip", StringComparison.OrdinalIgnoreCase)
              || r.Ext.Equals("docx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CarveImg_NoFalsePositives()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        Assert.True(results.Count <= 4,
            $"Expected ≤4 files (no false positives), got {results.Count}");
    }

    [Fact]
    public void CarveImg_AllFilesHaveNonZeroSize()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        foreach (var rf in results)
            Assert.True(rf.Size > 0, $"File has zero size: {rf.Name}");
    }

    [Fact]
    public void CarveImg_AllFilesHaveDataBytes()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var results = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        foreach (var rf in results)
            Assert.NotNull(rf.Data);
    }

    [Fact]
    public void CarveImg_ExtractedFilesAreNonEmpty()
    {
        if (ImgMissing(CarveImg)) return;

        using var disk = new RawDisk(CarveImg);
        var extractor  = new FileExtractor(disk);
        var results    = MakeCarver(disk).Scan(CancellationToken.None).ToList();

        int i = 0;
        foreach (var rf in results)
        {
            var outPath = extractor.Save(rf, _outputDir, ++i);
            Assert.True(File.Exists(outPath),
                $"Extracted file not found: {rf.Name}");
            Assert.True(new FileInfo(outPath).Length > 0,
                $"Extracted file is empty: {rf.Name}");
        }
    }

    // ── NTFS tests ────────────────────────────────────────────────────────

    [Fact]
    public void NtfsImg_FileExists()
    {
        Assert.True(File.Exists(NtfsImg),
            $"Missing: {NtfsImg} — copy ntfs_test.img into PowerRecover.Tests/test_images/");
    }

    [Fact]
    public void NtfsImg_FindsBothFiles()
    {
        if (ImgMissing(NtfsImg)) return;

        using var disk = new RawDisk(NtfsImg);
        var parts  = PartitionTable.Read(disk);
        var pOff   = parts.Count > 0 ? parts[0].OffsetBytes : 0L;
        var scanner = new NtfsScanner(disk, pOff);
        if (!scanner.ReadBootSector()) throw new Exception("Not an NTFS image");

        var results = scanner.Scan(CancellationToken.None)
                             .Where(r => !r.Name.StartsWith("$"))
                             .ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void NtfsImg_FindsReportTxt()
    {
        if (ImgMissing(NtfsImg)) return;

        using var disk = new RawDisk(NtfsImg);
        var parts  = PartitionTable.Read(disk);
        var pOff   = parts.Count > 0 ? parts[0].OffsetBytes : 0L;
        var scanner = new NtfsScanner(disk, pOff);
        scanner.ReadBootSector();

        var results = scanner.Scan(CancellationToken.None).ToList();

        Assert.Contains(results,
            r => r.Name.Equals("report.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NtfsImg_FindsPhotoJpg()
    {
        if (ImgMissing(NtfsImg)) return;

        using var disk = new RawDisk(NtfsImg);
        var parts  = PartitionTable.Read(disk);
        var pOff   = parts.Count > 0 ? parts[0].OffsetBytes : 0L;
        var scanner = new NtfsScanner(disk, pOff);
        scanner.ReadBootSector();

        var results = scanner.Scan(CancellationToken.None).ToList();

        Assert.Contains(results,
            r => r.Name.Equals("photo.jpg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NtfsImg_ReportTxtCorrectSize()
    {
        if (ImgMissing(NtfsImg)) return;

        using var disk = new RawDisk(NtfsImg);
        var parts  = PartitionTable.Read(disk);
        var pOff   = parts.Count > 0 ? parts[0].OffsetBytes : 0L;
        var scanner = new NtfsScanner(disk, pOff);
        scanner.ReadBootSector();

        var results = scanner.Scan(CancellationToken.None).ToList();
        var report  = results.First(r =>
            r.Name.Equals("report.txt", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(67L, report.Size);
    }

    [Fact]
    public void NtfsImg_PhotoJpgCorrectSize()
    {
        if (ImgMissing(NtfsImg)) return;

        using var disk = new RawDisk(NtfsImg);
        var parts  = PartitionTable.Read(disk);
        var pOff   = parts.Count > 0 ? parts[0].OffsetBytes : 0L;
        var scanner = new NtfsScanner(disk, pOff);
        scanner.ReadBootSector();

        var results = scanner.Scan(CancellationToken.None).ToList();
        var photo   = results.First(r =>
            r.Name.Equals("photo.jpg", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2014L, photo.Size);
    }

    [Fact]
    public void NtfsImg_NoCarvedNamesInMftMode()
    {
        if (ImgMissing(NtfsImg)) return;

        using var disk = new RawDisk(NtfsImg);
        var parts  = PartitionTable.Read(disk);
        var pOff   = parts.Count > 0 ? parts[0].OffsetBytes : 0L;
        var scanner = new NtfsScanner(disk, pOff);
        scanner.ReadBootSector();

        var results = scanner.Scan(CancellationToken.None)
                             .Where(r => !r.Name.StartsWith("$"))
                             .ToList();

        foreach (var r in results)
            Assert.False(r.Name.StartsWith("carved_"),
                $"Got carved name instead of real MFT name: {r.Name}");
    }
}
