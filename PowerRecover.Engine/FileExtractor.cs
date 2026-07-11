namespace PowerRecover.Engine;

/// <summary>Writes a RecoveredFile's bytes to the output folder.</summary>
public sealed class FileExtractor
{
    private readonly RawDisk _disk;
    public FileExtractor(RawDisk disk) => _disk = disk;

    public string Save(RecoveredFile rf, string outputDir, int index)
    {
        Directory.CreateDirectory(outputDir);
        string prefix = rf.Deleted ? "deleted_" : "";
        string safe = MakeSafe(rf.Name);
        string targetDir = outputDir;

        if (!string.IsNullOrWhiteSpace(rf.FolderPath))
        {
            string relativeFolder = MakeSafeRelativePath(rf.FolderPath);
            if (!string.IsNullOrWhiteSpace(relativeFolder))
                targetDir = Path.Combine(outputDir, "RecoveredFolders", relativeFolder);
        }
        else if (rf.Method.Contains("Carve", StringComparison.OrdinalIgnoreCase))
        {
            targetDir = Path.Combine(outputDir, "RecoveredByType", rf.Ext.ToLowerInvariant());
        }

        Directory.CreateDirectory(targetDir);
        string path = Path.Combine(targetDir, $"{index:D5}_{prefix}{safe}");

        byte[] data = Materialize(rf);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Turns run-lists / resident data / carved data into bytes.
    /// CRITICAL: cluster offsets from the MFT run-list are relative to the
    /// START OF THE PARTITION, not the physical disk start. We must add
    /// rf.PartitionOffset to every cluster read, otherwise we read from
    /// the wrong location on the disk and get empty/garbage data.</summary>
    public byte[] Materialize(RecoveredFile rf)
    {
        if (rf.Data != null) return rf.Data;                 // carved file
        if (rf.ResidentData != null) return rf.ResidentData; // small NTFS file

        if (rf.Runs != null && rf.ClusterSize > 0)           // non-resident NTFS
        {
            using var ms = new MemoryStream();
            byte[] cbuf = new byte[rf.ClusterSize];
            long remaining = rf.Size;
            foreach (var (lenClusters, startCluster) in rf.Runs)
            {
                if (startCluster <= 0) continue; // sparse / invalid run
                for (long c = 0; c < lenClusters && remaining > 0; c++)
                {
                    // *** THE FIX: add PartitionOffset so we read from the
                    // correct absolute position on the physical disk ***
                    long off = rf.PartitionOffset
                               + (startCluster + c) * rf.ClusterSize;
                    int got = _disk.ReadAt(off, cbuf, rf.ClusterSize, out _);
                    if (got <= 0) break;
                    int take = (int)Math.Min(remaining, got);
                    ms.Write(cbuf, 0, take);
                    remaining -= take;
                }
            }
            return ms.ToArray();
        }
        return Array.Empty<byte>();
    }

    private static string MakeSafe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "unnamed" : name;
    }

    private static string MakeSafeRelativePath(string folderPath)
    {
        string cleaned = folderPath.Trim().TrimStart('\\', '/');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "?") return "";

        string[] parts = cleaned
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(MakeSafe)
            .Where(p => p != "." && p != "..")
            .ToArray();

        return parts.Length == 0 ? "" : Path.Combine(parts);
    }
}

/// <summary>
/// Reads the MBR / GPT partition table to locate volumes on a whole disk,
/// so the NTFS scanner knows where each partition begins.
/// </summary>
public sealed class PartitionTable
{
    public readonly record struct Partition(
        long OffsetBytes, long SizeBytes, string Type);

    public static List<Partition> Read(RawDisk disk)
    {
        var list = new List<Partition>();
        byte[] sector = new byte[512];
        disk.ReadAt(0, sector, 512, out _);

        // MBR signature
        if (sector[510] != 0x55 || sector[511] != 0xAA) return list;

        // GPT? protective MBR has a 0xEE partition type
        bool gpt = false;
        for (int i = 0; i < 4; i++)
            if (sector[446 + i * 16 + 4] == 0xEE) gpt = true;

        if (gpt)
        {
            disk.ReadAt(512, sector, 512, out _);
            if (System.Text.Encoding.ASCII.GetString(sector, 0, 8)
                == "EFI PART")
            {
                long partEntryLba = BitConverter.ToInt64(sector, 72);
                int numEntries = BitConverter.ToInt32(sector, 80);
                int entrySize = BitConverter.ToInt32(sector, 84);
                byte[] table = new byte[numEntries * entrySize];
                disk.ReadAt(partEntryLba * 512, table, table.Length, out _);
                for (int i = 0; i < numEntries; i++)
                {
                    int b = i * entrySize;
                    long firstLba = BitConverter.ToInt64(table, b + 32);
                    long lastLba = BitConverter.ToInt64(table, b + 40);
                    if (firstLba == 0 && lastLba == 0) continue;
                    list.Add(new Partition(firstLba * 512,
                        (lastLba - firstLba + 1) * 512, "GPT"));
                }
                return list;
            }
        }

        // Classic MBR (4 primary entries)
        for (int i = 0; i < 4; i++)
        {
            int b = 446 + i * 16;
            byte type = sector[b + 4];
            if (type == 0) continue;
            uint startLba = BitConverter.ToUInt32(sector, b + 8);
            uint sectors = BitConverter.ToUInt32(sector, b + 12);
            if (sectors == 0) continue;
            list.Add(new Partition((long)startLba * 512,
                (long)sectors * 512, $"MBR-0x{type:X2}"));
        }
        return list;
    }
}
