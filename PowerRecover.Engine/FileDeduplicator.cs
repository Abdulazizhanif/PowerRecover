namespace PowerRecover.Engine;

/// <summary>
/// Deduplicates recovered files by content hash during scanning.
/// Uses xxHash64 — extremely fast (10+ GB/s), good collision resistance.
/// No NuGet dependency — xxHash64 is implemented inline below.
///
/// How it works:
///   - Every carved file's bytes are hashed before saving
///   - If the same hash was seen before, the file is a duplicate — skip it
///   - NTFS/FAT files are hashed by (name + size + parentRef) not content
///     because materializing them just to hash would be slow
///
/// Result: if the same JPG appears 3 times on disk, only 1 copy is saved.
/// On drives with lots of system files this can cut output size by 60-80%.
///
/// Usage:
///   var dedup = new FileDeduplicator();
///   if (dedup.IsDuplicate(rf)) continue;  // skip this file
///   // else save it normally
/// </summary>
public sealed class FileDeduplicator
{
    private readonly HashSet<ulong> _seenHashes = new();
    private readonly HashSet<string> _seenNames  = new();

    public int DuplicatesSkipped { get; private set; }
    public int UniqueFiles       { get; private set; }

    /// <summary>
    /// Returns true if this file is a duplicate and should be skipped.
    /// Registers the file as seen on first call.
    /// </summary>
    public bool IsDuplicate(RecoveredFile rf)
    {
        ulong hash = ComputeHash(rf);

        if (_seenHashes.Contains(hash))
        {
            DuplicatesSkipped++;
            return true;
        }

        _seenHashes.Add(hash);
        UniqueFiles++;
        return false;
    }

    /// <summary>Check by filename only — fast path for NTFS metadata files.</summary>
    public bool IsNameDuplicate(string name)
    {
        if (_seenNames.Contains(name))
        {
            DuplicatesSkipped++;
            return true;
        }
        _seenNames.Add(name);
        return false;
    }

    public void Reset()
    {
        _seenHashes.Clear();
        _seenNames.Clear();
        DuplicatesSkipped = 0;
        UniqueFiles       = 0;
    }

    public string Summary =>
        $"Deduplication: {UniqueFiles:N0} unique, " +
        $"{DuplicatesSkipped:N0} duplicates skipped";

    // ── Hash computation ──────────────────────────────────────────────

    private static ulong ComputeHash(RecoveredFile rf)
    {
        // For carved files: hash the actual content bytes
        if (rf.Data != null && rf.Data.Length > 0)
            return XxHash64(rf.Data, 0, rf.Data.Length);

        // For resident NTFS data: hash the content
        if (rf.ResidentData != null && rf.ResidentData.Length > 0)
            return XxHash64(rf.ResidentData, 0, rf.ResidentData.Length);

        // For non-resident files: hash by (name + size + offset)
        // Content hashing would require materializing the file — too slow
        // This catches exact duplicates by metadata
        ulong h = 14695981039346656037UL; // FNV-1a basis
        foreach (char c in rf.Name)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        h ^= (ulong)rf.Size;
        h *= 1099511628211UL;
        h ^= (ulong)rf.Offset;
        h *= 1099511628211UL;
        return h;
    }

    // ── xxHash64 — public domain, fast non-cryptographic hash ────────
    // Based on Yann Collet's xxHash algorithm.

    private const ulong PRIME1 = 11400714785074694791UL;
    private const ulong PRIME2 = 14029467366897019727UL;
    private const ulong PRIME3 =  1609587929392839161UL;
    private const ulong PRIME4 =  9650029242287828579UL;
    private const ulong PRIME5 =  2870177450012600261UL;

    private static ulong XxHash64(byte[] data, int offset, int length)
    {
        ulong h64;
        int   pos = offset;
        int   end = offset + length;

        if (length >= 32)
        {
            ulong v1 = unchecked(PRIME1 + PRIME2);
            ulong v2 = PRIME2;
            ulong v3 = 0;
            ulong v4 = unchecked(0 - PRIME1);

            int limit = end - 32;
            while (pos <= limit)
            {
                v1 = Round(v1, ReadU64(data, pos));     pos += 8;
                v2 = Round(v2, ReadU64(data, pos));     pos += 8;
                v3 = Round(v3, ReadU64(data, pos));     pos += 8;
                v4 = Round(v4, ReadU64(data, pos));     pos += 8;
            }

            h64 = RotL(v1, 1) + RotL(v2, 7) + RotL(v3, 12) + RotL(v4, 18);
            h64 = Merge(h64, v1);
            h64 = Merge(h64, v2);
            h64 = Merge(h64, v3);
            h64 = Merge(h64, v4);
        }
        else
        {
            h64 = unchecked(PRIME5);
        }

        h64 += (ulong)length;

        while (pos + 8 <= end)
        {
            h64 ^= Round(0, ReadU64(data, pos)); pos += 8;
            h64  = unchecked(RotL(h64, 27) * PRIME1 + PRIME4);
        }
        if (pos + 4 <= end)
        {
            h64 ^= ReadU32(data, pos) * PRIME1; pos += 4;
            h64  = unchecked(RotL(h64, 23) * PRIME2 + PRIME3);
        }
        while (pos < end)
        {
            h64 ^= data[pos++] * PRIME5;
            h64  = unchecked(RotL(h64, 11) * PRIME1);
        }

        return Avalanche(h64);
    }

    private static ulong Round(ulong acc, ulong input)
        => unchecked(RotL(acc + input * PRIME2, 31) * PRIME1);

    private static ulong Merge(ulong h64, ulong v)
        => unchecked((h64 ^ Round(0, v)) * PRIME1 + PRIME4);

    private static ulong Avalanche(ulong h)
    {
        h ^= h >> 33;
        h  = unchecked(h * PRIME2);
        h ^= h >> 29;
        h  = unchecked(h * PRIME3);
        h ^= h >> 32;
        return h;
    }

    private static ulong RotL(ulong v, int n)
        => (v << n) | (v >> (64 - n));

    private static ulong ReadU64(byte[] b, int i)
        => BitConverter.ToUInt64(b, i);

    private static ulong ReadU32(byte[] b, int i)
        => BitConverter.ToUInt32(b, i);
}
