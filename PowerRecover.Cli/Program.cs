using System.Diagnostics;
using PowerRecover.Engine;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string command = args[0].ToLowerInvariant();

switch (command)
{
    case "drives":
        return RunDrives();
    case "scan":
        return RunScan(args);
    default:
        Console.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
}

void PrintUsage()
{
    Console.WriteLine("PowerRecover CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  PowerRecover.Cli drives");
    Console.WriteLine("  PowerRecover.Cli scan <source> <output> [--mode ntfs|carve|both] [--types ext1,ext2,...]");
    Console.WriteLine("  Professional filtering is enabled by default: system junk, tiny placeholders,");
    Console.WriteLine("  low-confidence carves, icons, executables, and Windows app files are skipped.");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine(@"  PowerRecover.Cli scan \\.\PhysicalDrive1 D:\Recovered --mode both");
    Console.WriteLine(@"  PowerRecover.Cli scan C:\images\disk.img E:\Out --mode carve --types jpg,png,pdf");
}

int RunDrives()
{
    Console.WriteLine("Probing physical drives (PhysicalDrive0 .. PhysicalDrive15)...");
    Console.WriteLine();
    int found = 0;
    for (int i = 0; i <= 15; i++)
    {
        string path = $@"\\.\PhysicalDrive{i}";
        try
        {
            using var disk = new RawDisk(path);
            double gb = disk.Length / (1024.0 * 1024 * 1024);
            Console.WriteLine($"  {path,-22} {gb,10:F1} GB   sector={disk.SectorSize}");
            found++;
        }
        catch
        {
            // not present, or not accessible without admin - skip silently
        }
    }
    Console.WriteLine();
    Console.WriteLine(found == 0
        ? "No accessible physical drives found. Are you running as Administrator?"
        : $"{found} accessible drive(s) found.");
    return 0;
}

int RunScan(string[] cliArgs)
{
    if (cliArgs.Length < 3)
    {
        Console.WriteLine("Error: scan requires <source> and <output>.");
        PrintUsage();
        return 1;
    }

    string source = cliArgs[1];
    string output = cliArgs[2];
    string mode = "both";
    string[]? types = null;

    for (int i = 3; i < cliArgs.Length; i++)
    {
        if (cliArgs[i] == "--mode" && i + 1 < cliArgs.Length)
        {
            mode = cliArgs[++i].ToLowerInvariant();
        }
        else if (cliArgs[i] == "--types" && i + 1 < cliArgs.Length)
        {
            types = cliArgs[++i]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant())
                .ToArray();
        }
    }

    if (mode != "ntfs" && mode != "carve" && mode != "both")
    {
        Console.WriteLine($"Error: --mode must be ntfs, carve, or both (got '{mode}').");
        return 1;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("Stopping... (finishing current chunk)");
        cts.Cancel();
    };

    Console.WriteLine($"Source: {source}");
    Console.WriteLine($"Output: {output}");
    Console.WriteLine($"Mode:   {mode}");
    Console.WriteLine();

    RawDisk disk;
    try
    {
        disk = new RawDisk(source);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error opening source: {ex.Message}");
        return 1;
    }

    int totalFound = 0;
    int badSectorTotal = 0;
    int skippedQuality = 0;
    var policy = new RecoveryPolicy();
    var sw = Stopwatch.StartNew();

    using (disk)
    {
        Directory.CreateDirectory(output);
        var extractor = new FileExtractor(disk);

        if (mode is "ntfs" or "both")
            totalFound += RunNtfs(disk, extractor, output, policy, cts.Token,
                ref badSectorTotal, ref skippedQuality);

        if (!cts.IsCancellationRequested && mode is "carve" or "both")
            totalFound += RunCarve(disk, extractor, output, policy, types, cts.Token,
                ref badSectorTotal, ref skippedQuality);
    }

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine("-----------------------------------------");
    Console.WriteLine($"Done. {totalFound} file(s) recovered to: {output}");
    Console.WriteLine($"Junk/system/low-quality candidates skipped: {skippedQuality:N0}");
    Console.WriteLine($"Bad sectors encountered: {badSectorTotal}");
    Console.WriteLine($"Elapsed: {sw.Elapsed:hh\\:mm\\:ss}");
    if (cts.IsCancellationRequested)
        Console.WriteLine("(scan was stopped early by user)");

    return 0;
}

int RunNtfs(RawDisk disk, FileExtractor extractor, string output,
            RecoveryPolicy policy, CancellationToken ct,
            ref int badSectorTotal, ref int skippedQuality)
{
    Console.WriteLine("=== NTFS MFT Deep Scan ===");

    var partitions = PartitionTable.Read(disk);
    var offsetsToTry = new List<long>();
    if (partitions.Count > 0)
    {
        Console.WriteLine($"Found {partitions.Count} partition(s).");
        offsetsToTry.AddRange(partitions.Select(p => p.OffsetBytes));
    }
    else
    {
        Console.WriteLine("No partition table found - trying offset 0 (raw volume / image).");
        offsetsToTry.Add(0);
    }

    int savedCount = 0;
    int fileIndex = 0;

    foreach (long partOffset in offsetsToTry)
    {
        if (ct.IsCancellationRequested) break;

        var scanner = new NtfsScanner(disk, partOffset);
        scanner.Log += msg => Console.WriteLine($"  [ntfs] {msg}");
        scanner.Progress += (done, total) =>
            Console.Write($"\r  Scanning MFT records: {done:N0} / {total:N0}      ");

        if (!scanner.ReadBootSector())
            continue; // not NTFS at this offset

        var files = scanner.Scan(ct).ToList();
        Console.WriteLine();
        Console.WriteLine($"  {files.Count} entr(y/ies) found at partition offset {partOffset:N0}.");

        scanner.ResolvePaths(files);

        foreach (var rf in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                rf.Confidence = ConfidenceScorer.Score(rf);
                if (!ShouldKeep(policy, rf, ref skippedQuality)) continue;
                string savedPath = extractor.Save(rf, output, fileIndex++);
                savedCount++;
                Console.WriteLine($"  [{(rf.Deleted ? "DEL" : "OK ")}] {rf.FullPath}  ({rf.SizeText}) -> {savedPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Failed to save '{rf.Name}': {ex.Message}");
            }
        }
    }

    return savedCount;
}

int RunCarve(RawDisk disk, FileExtractor extractor, string output,
             RecoveryPolicy policy, string[]? types, CancellationToken ct,
             ref int badSectorTotal, ref int skippedQuality)
{
    Console.WriteLine();
    Console.WriteLine("=== Signature Carve ===");

    IEnumerable<FileSignature> sigs = types is { Length: > 0 }
        ? ExtendedSignatures.Filtered(types)
        : ExtendedSignatures.All;

    var carver = new CarveScanner(disk, sigs);
    carver.Log += msg => Console.WriteLine($"  [carve] {msg}");
    carver.Progress += (done, total) =>
    {
        double pct = total > 0 ? done * 100.0 / total : 0;
        Console.Write($"\r  Carving: {pct,5:F1}%  ({done:N0} / {total:N0} bytes)      ");
    };

    int savedCount = 0;
    int fileIndex = 0;

    foreach (var rf in carver.Scan(ct))
    {
        if (ct.IsCancellationRequested) break;
        try
        {
            rf.Confidence = ConfidenceScorer.Score(rf);
            if (!ShouldKeep(policy, rf, ref skippedQuality)) continue;
            string savedPath = extractor.Save(rf, output, fileIndex++);
            savedCount++;
            Console.WriteLine();
            Console.WriteLine($"  [{rf.Ext.ToUpperInvariant()}] {rf.Name}  ({rf.SizeText}) @ 0x{rf.Offset:X} -> {savedPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to save carved file: {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  {savedCount} file(s) carved.");
    return savedCount;
}

bool ShouldKeep(RecoveryPolicy policy, RecoveredFile rf, ref int skippedQuality)
{
    var decision = policy.Evaluate(rf);
    if (decision.Accepted) return true;

    skippedQuality++;
    return false;
}
