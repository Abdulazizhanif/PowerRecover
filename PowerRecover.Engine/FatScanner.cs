using System.Text;

namespace PowerRecover.Engine;

/// <summary>
/// Parses FAT32 and exFAT filesystems to recover files with their original
/// names — including deleted entries (first byte of dir entry = 0xE5).
/// Most USB drives, SD cards, and camera memory use these filesystems, so
/// this is the biggest single coverage gap vs paid tools.
///
/// FAT32:  boot sector → FAT chain → directory walk → VFAT long names
/// exFAT:  VBR → allocation bitmap → directory tree (entry sets)
///
/// Wired into MainWindow via the same RecoveredFile pipeline as NTFS.
/// </summary>
public sealed class FatScanner
{
    private readonly RawDisk _disk;
    private readonly long _partitionOffset;

    public event Action<string>? Log;
    public event Action<long, long>? Progress;

    // ── FAT32 geometry (populated by ReadBootSector) ──────────────────
    private int _bytesPerSector;
    private int _sectorsPerCluster;
    private int _clusterSize;
    private long _fatOffset;        // byte offset of FAT #1 from disk start
    private long _dataOffset;       // byte offset of first data cluster
    private long _rootCluster;      // FAT32: cluster# of root dir
    private uint _totalClusters;
    private bool _isExFat;
    private bool _isFat32;

    // exFAT-specific
    private int _exFatClusterHeapOffset; // sector LBA of cluster heap
    private long _exFatFatOffset;        // byte offset of FAT

    private const uint FAT_EOC = 0x0FFFFFF8;
    private const uint FAT_FREE = 0x00000000;
    private const byte ENTRY_DELETED = 0xE5;
    private const byte ENTRY_END = 0x00;

    public FatScanner(RawDisk disk, long partitionOffset = 0)
    {
        _disk = disk;
        _partitionOffset = partitionOffset;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Boot sector parsing
    // ─────────────────────────────────────────────────────────────────

    public bool ReadBootSector()
    {
        byte[] boot = new byte[512];
        _disk.ReadAt(_partitionOffset, boot, 512, out _);

        string oemId = Encoding.ASCII.GetString(boot, 3, 8).TrimEnd();

        if (oemId == "EXFAT")
        {
            _isExFat = true;
            return ParseExFatBoot(boot);
        }

        // FAT32 OEM IDs: "FAT32   " or various vendor strings
        // Confirm by checking the FAT32 signature at 0x52
        string fat32sig = Encoding.ASCII.GetString(boot, 82, 8).TrimEnd();
        string fat16sig = Encoding.ASCII.GetString(boot, 54, 8).TrimEnd();

        if (fat32sig == "FAT32" || fat16sig.StartsWith("FAT"))
        {
            _isFat32 = true;
            return ParseFat32Boot(boot);
        }

        Log?.Invoke("Not a FAT32 or exFAT volume at this offset.");
        return false;
    }

    private bool ParseFat32Boot(byte[] b)
    {
        _bytesPerSector = LE16(b, 11);
        _sectorsPerCluster = b[13];
        if (_bytesPerSector == 0 || _sectorsPerCluster == 0) return false;

        _clusterSize = _bytesPerSector * _sectorsPerCluster;

        int reservedSectors = LE16(b, 14);
        int numFats = b[16];
        uint sectorsPerFat32 = LE32(b, 36);
        _rootCluster = LE32(b, 44);

        long fatLba = reservedSectors;
        _fatOffset = _partitionOffset + fatLba * _bytesPerSector;
        long dataLba = fatLba + (long)numFats * sectorsPerFat32;
        _dataOffset = _partitionOffset + dataLba * _bytesPerSector;

        // Total data clusters
        uint totalSectors = LE32(b, 32);
        if (totalSectors == 0) totalSectors = LE32(b, 19);
        _totalClusters = (uint)((totalSectors - dataLba) / _sectorsPerCluster);

        Log?.Invoke($"FAT32: {_bytesPerSector}B/sec, {_sectorsPerCluster}sec/cluster" +
                    $" ({_clusterSize}B), root@cluster {_rootCluster}, " +
                    $"{_totalClusters:N0} clusters");
        return true;
    }

    private bool ParseExFatBoot(byte[] b)
    {
        // exFAT BPB layout (all fields little-endian)
        _bytesPerSector  = 1 << b[108];   // BytesPerSectorShift
        _sectorsPerCluster = 1 << b[109]; // SectorsPerClusterShift
        _clusterSize     = _bytesPerSector * _sectorsPerCluster;

        long fatLba      = LE32(b, 80);   // FatOffset (sectors)
        _exFatFatOffset  = _partitionOffset + fatLba * _bytesPerSector;

        _exFatClusterHeapOffset = (int)LE32(b, 88); // ClusterHeapOffset (sectors)
        _dataOffset = _partitionOffset + (long)_exFatClusterHeapOffset * _bytesPerSector;

        _rootCluster  = LE32(b, 96);      // FirstClusterOfRootDirectory
        _totalClusters = LE32(b, 92);

        _fatOffset = _exFatFatOffset;

        Log?.Invoke($"exFAT: {_bytesPerSector}B/sec, {_sectorsPerCluster}sec/cluster" +
                    $" ({_clusterSize}B), root@cluster {_rootCluster}, " +
                    $"{_totalClusters:N0} clusters");
        return true;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Public scan entry point
    // ─────────────────────────────────────────────────────────────────

    public IEnumerable<RecoveredFile> Scan(CancellationToken ct)
    {
        if (!_isFat32 && !_isExFat && !ReadBootSector())
            yield break;

        if (_isExFat)
        {
            foreach (var f in ScanExFatDirectory(_rootCluster, ct))
                yield return f;
        }
        else
        {
            foreach (var f in ScanFat32Directory(_rootCluster, ct))
                yield return f;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  FAT32 directory walking
    // ─────────────────────────────────────────────────────────────────

    private IEnumerable<RecoveredFile> ScanFat32Directory(
        long startCluster, CancellationToken ct, string parentPath = "")
    {
        // Collect all directory entries from the cluster chain
        var entries = new List<byte[]>();
        foreach (long cluster in WalkFatChain(startCluster))
        {
            if (ct.IsCancellationRequested) yield break;
            byte[] clusterData = ReadCluster(cluster);
            for (int i = 0; i + 32 <= clusterData.Length; i += 32)
            {
                byte[] entry = new byte[32];
                Array.Copy(clusterData, i, entry, 0, 32);
                entries.Add(entry);
            }
        }

        // Walk entries, assembling VFAT long-name sequences
        string longName = "";
        int idx = 0;
        while (idx < entries.Count)
        {
            if (ct.IsCancellationRequested) yield break;
            byte[] e = entries[idx++];

            if (e[0] == ENTRY_END) break;

            byte attr = e[11];
            bool isLfn = (attr & 0x0F) == 0x0F;

            if (isLfn)
            {
                // VFAT long filename entry — prepend to accumulator
                longName = ParseVfatEntry(e) + longName;
                continue;
            }

            bool deleted = e[0] == ENTRY_DELETED;
            if (deleted) e[0] = 0x5F; // restore first char placeholder

            bool isDir = (attr & 0x10) != 0;
            bool isSystem = (attr & 0x04) != 0;
            bool isVolLabel = (attr & 0x08) != 0;
            if (isVolLabel) { longName = ""; continue; }

            string name83 = Parse83Name(e);
            string name = !string.IsNullOrEmpty(longName) ? longName : name83;
            longName = "";

            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                continue;

            uint clusterHi = LE16(e, 20);
            uint clusterLo = LE16(e, 26);
            long cluster = (clusterHi << 16) | clusterLo;
            long size = LE32(e, 28);

            string fullPath = string.IsNullOrEmpty(parentPath)
                ? name : $"{parentPath}\\{name}";

            if (isDir && cluster >= 2 && !deleted)
            {
                // Recurse into subdirectory
                foreach (var sub in ScanFat32Directory(cluster, ct, fullPath))
                    yield return sub;
            }
            else if (!isDir && cluster >= 2 && size > 0)
            {
                yield return MakeFatFile(name, fullPath, cluster, size, deleted);
            }

            Progress?.Invoke(cluster, _totalClusters);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  exFAT directory walking
    // ─────────────────────────────────────────────────────────────────

    private IEnumerable<RecoveredFile> ScanExFatDirectory(
        long startCluster, CancellationToken ct, string parentPath = "")
    {
        // exFAT uses "entry sets": one File Entry + Stream Extension + File Name(s)
        var rawEntries = new List<byte[]>();
        foreach (long cluster in WalkFatChain(startCluster))
        {
            if (ct.IsCancellationRequested) yield break;
            byte[] clusterData = ReadCluster(cluster);
            for (int i = 0; i + 32 <= clusterData.Length; i += 32)
            {
                byte[] entry = new byte[32];
                Array.Copy(clusterData, i, entry, 0, 32);
                rawEntries.Add(entry);
            }
        }

        int i2 = 0;
        while (i2 < rawEntries.Count)
        {
            if (ct.IsCancellationRequested) yield break;
            byte[] primary = rawEntries[i2++];
            byte entryType = primary[0];

            // 0x85 = File entry, 0x05 = deleted File entry
            bool deleted = entryType == 0x05;
            if (entryType != 0x85 && entryType != 0x05) continue;

            int secondaryCount = primary[1];
            if (i2 + secondaryCount > rawEntries.Count) break;

            // Collect secondary entries
            byte[]? streamEntry = null;
            var nameEntries = new List<byte[]>();
            for (int s = 0; s < secondaryCount && i2 < rawEntries.Count; s++)
            {
                byte[] sec = rawEntries[i2++];
                byte secType = sec[0];
                if (secType == 0xC0 || secType == 0x40) streamEntry = sec;       // Stream Extension
                else if (secType == 0xC1 || secType == 0x41) nameEntries.Add(sec); // File Name
            }

            if (streamEntry == null) continue;

            // Stream Extension: valid data length at offset 8, first cluster at 20
            long validDataLen = BitConverter.ToInt64(streamEntry, 8);
            uint firstCluster = LE32(streamEntry, 20);
            bool isDir = (primary[4] & 0x10) != 0; // GeneralSecondaryFlags

            // Reconstruct name from File Name entries
            var sb = new StringBuilder();
            foreach (var ne in nameEntries)
            {
                for (int c = 2; c + 1 < 32; c += 2)
                {
                    ushort ch = (ushort)(ne[c] | (ne[c + 1] << 8));
                    if (ch == 0) break;
                    sb.Append((char)ch);
                }
            }
            string name = sb.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            string fullPath = string.IsNullOrEmpty(parentPath)
                ? name : $"{parentPath}\\{name}";

            if (isDir && firstCluster >= 2 && !deleted)
            {
                foreach (var sub in ScanExFatDirectory(firstCluster, ct, fullPath))
                    yield return sub;
            }
            else if (!isDir && firstCluster >= 2 && validDataLen > 0)
            {
                yield return MakeFatFile(name, fullPath, firstCluster,
                                         validDataLen, deleted);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Build a RecoveredFile from FAT cluster info
    // ─────────────────────────────────────────────────────────────────

    private RecoveredFile MakeFatFile(string name, string fullPath,
                                      long firstCluster, long size, bool deleted)
    {
        // Build run list from FAT chain so FileExtractor.Materialize works
        var runs = new List<(long len, long start)>();

        if (deleted)
        {
            // Deleted entry: FAT chain is free — we only have the first cluster.
            // Estimate length from reported size and assume contiguous layout,
            // which is true for most small files on flash media.
            long clustersNeeded = (size + _clusterSize - 1) / _clusterSize;
            runs.Add((clustersNeeded, firstCluster));
        }
        else
        {
            // Active file: walk the FAT chain
            // Compress contiguous runs for FileExtractor efficiency
            long prev = -1;
            long runLen = 0;
            long runStart = -1;
            foreach (long c in WalkFatChain(firstCluster))
            {
                if (c == prev + 1)
                {
                    runLen++;
                }
                else
                {
                    if (runStart >= 0) runs.Add((runLen, runStart));
                    runStart = c;
                    runLen = 1;
                }
                prev = c;
            }
            if (runStart >= 0) runs.Add((runLen, runStart));
        }

        // Calculate absolute disk offset of first cluster for display
        long diskOffset = ClusterToDiskOffset(firstCluster);

        return new RecoveredFile
        {
            Name         = deleted ? $"deleted_{name}" : name,
            Ext          = GetExt(name),
            Size         = size,
            Deleted      = deleted,
            Method       = _isExFat ? "exFAT" : "FAT32",
            Offset       = diskOffset,
            Runs         = runs,
            ClusterSize  = _clusterSize,
            PartitionOffset = _partitionOffset,
            FolderPath   = Path.GetDirectoryName(fullPath) ?? "",
        };
    }

    // ─────────────────────────────────────────────────────────────────
    //  FAT chain walking
    // ─────────────────────────────────────────────────────────────────

    private IEnumerable<long> WalkFatChain(long startCluster)
    {
        if (startCluster < 2 || startCluster > _totalClusters + 1)
            yield break;

        // Guard against infinite loops (corrupt FAT or bad cluster)
        var visited = new HashSet<long>();
        long cluster = startCluster;
        const int MAX_CLUSTERS = 2_000_000;
        int count = 0;

        while (cluster >= 2 && cluster < FAT_EOC && count++ < MAX_CLUSTERS)
        {
            if (!visited.Add(cluster)) break; // loop detected
            yield return cluster;
            cluster = ReadFatEntry(cluster);
        }
    }

    private uint ReadFatEntry(long cluster)
    {
        long fatByteOffset = _fatOffset + cluster * 4;
        byte[] entry = new byte[4];
        _disk.ReadAt(fatByteOffset, entry, 4, out _);
        return LE32(entry, 0) & 0x0FFFFFFF;
    }

    private byte[] ReadCluster(long cluster)
    {
        long offset = ClusterToDiskOffset(cluster);
        byte[] buf = new byte[_clusterSize];
        _disk.ReadAt(offset, buf, _clusterSize, out _);
        return buf;
    }

    private long ClusterToDiskOffset(long cluster)
        => _dataOffset + (cluster - 2) * _clusterSize;

    // ─────────────────────────────────────────────────────────────────
    //  FAT32 name parsing
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Reconstruct one segment of a VFAT long-name entry.</summary>
    private static string ParseVfatEntry(byte[] e)
    {
        // Each VFAT entry contributes 13 UCS-2 characters spread across
        // offsets 1..10, 14..23, 28..31.
        var chars = new char[13];
        int[] offsets = { 1,3,5,7,9, 14,16,18,20,22, 28,30 };
        for (int i = 0; i < offsets.Length && i < 13; i++)
        {
            ushort ch = (ushort)(e[offsets[i]] | (e[offsets[i] + 1] << 8));
            if (ch == 0 || ch == 0xFFFF) { return new string(chars, 0, i); }
            chars[i] = (char)ch;
        }
        // 13th char at offset 30 already handled above; handle the last
        if (offsets.Length < 13)
        {
            ushort ch = (ushort)(e[30] | (e[31] << 8));
            chars[12] = (ch == 0 || ch == 0xFFFF) ? '\0' : (char)ch;
        }
        int len = Array.IndexOf(chars, '\0');
        return new string(chars, 0, len < 0 ? 13 : len);
    }

    /// <summary>Parse 8.3 short filename from a directory entry.</summary>
    private static string Parse83Name(byte[] e)
    {
        string basePart = Encoding.ASCII.GetString(e, 0, 8).TrimEnd();
        string extPart  = Encoding.ASCII.GetString(e, 8, 3).TrimEnd();
        if (e[0] == ENTRY_DELETED) basePart = "_" + basePart[1..];
        return string.IsNullOrEmpty(extPart)
            ? basePart
            : $"{basePart}.{extPart}";
    }

    private static string GetExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1
            ? name[(dot + 1)..].ToLowerInvariant() : "bin";
    }

    // ─────────────────────────────────────────────────────────────────
    //  Little-endian helpers (mirrors NtfsScanner style)
    // ─────────────────────────────────────────────────────────────────

    private static ushort LE16(byte[] b, int o)
        => BitConverter.ToUInt16(b, o);
    private static uint LE32(byte[] b, int o)
        => BitConverter.ToUInt32(b, o);
}
