namespace PowerRecover.Engine;

/// <summary>
/// Rates each recovered file 0–100% based on how likely it is to be
/// a real, intact, usable file vs a false-positive or partial fragment.
///
/// Scoring model:
///   Header valid       +30  (always true for carves — that's how we found it)
///   Footer found       +30  (PDF/ZIP/PNG have footers; JPEG has EOI)
///   Size reasonable    +20  (not 0, not truncated at MaxSize boundary)
///   Internal structure +20  (type-specific deep check)
///
/// For NTFS-MFT files:
///   Active (not deleted)  = 100%
///   Deleted, intact runs  = 85%
///   Deleted, sparse runs  = 60%
///   Resident (small file) = 95%
/// </summary>
public static class ConfidenceScorer
{
    public static int Score(RecoveredFile rf)
    {
        // ── NTFS paths ────────────────────────────────────────────────
        if (rf.Method == "NTFS-MFT")
        {
            if (rf.ResidentData != null)     return 95; // fully in MFT
            if (!rf.Deleted)                 return 100;

            // Deleted non-resident: check for sparse runs (gaps = overwritten)
            if (rf.Runs == null || rf.Runs.Count == 0) return 50;
            bool hasSparse = rf.Runs.Any(r => r.lengthClusters == 0
                                           || r.startCluster   <= 0);
            return hasSparse ? 60 : 85;
        }

        // ── FAT32 / exFAT paths ───────────────────────────────────────
        if (rf.Method is "FAT32" or "exFAT")
        {
            if (!rf.Deleted) return 100;
            // Deleted FAT: we assume contiguous layout — may be overwritten
            if (rf.Size < 512)  return 40; // tiny files often get reused fast
            if (rf.Size < 4096) return 65;
            return 75;
        }

        // ── Carved files ──────────────────────────────────────────────
        if (rf.Data == null || rf.Data.Length == 0) return 0;

        int score = 30; // header matched (always true here)
        byte[] d = rf.Data;
        int len  = d.Length;

        score += ScoreFooter(rf.Ext, d, len);
        score += ScoreSize(rf.Ext, len);
        score += ScoreStructure(rf.Ext, d, len);

        return Math.Clamp(score, 0, 100);
    }

    // ── Footer check (+30) ────────────────────────────────────────────

    private static int ScoreFooter(string ext, byte[] d, int len)
    {
        if (len < 4) return 0;
        return ext switch
        {
            "jpg"  => EndsWith(d, len, 0xFF, 0xD9)          ? 30 : 0,
            "png"  => Contains(d, len, 0x49,0x45,0x4E,0x44) ? 30 : 0, // IEND
            "gif"  => EndsWith(d, len, 0x00, 0x3B)          ? 30 : 0,
            "pdf"  => ContainsAscii(d, len, "%%EOF")        ? 30 : 15,// may have trailing
            "zip"  => Contains(d, len, 0x50,0x4B,0x05,0x06) ? 30 : 0, // EOCD
            "mp3"  => len > 128 && d[len-128] == 'T'
                               && d[len-127] == 'A'
                               && d[len-126] == 'G'         ? 25 : 10,
            "mp4"  => Contains(d, len, 0x6D,0x6F,0x6F,0x76) ? 30 : 10,// moov atom
            _      => 15, // no footer spec for this type — partial credit
        };
    }

    // ── Size check (+20) ─────────────────────────────────────────────

    private static int ScoreSize(string ext, int len)
    {
        // Penalise files that hit their MaxSize exactly — likely truncated
        long maxSize = ext switch
        {
            "jpg"  => 30L  * 1024 * 1024,
            "png"  => 40L  * 1024 * 1024,
            "pdf"  => 100L * 1024 * 1024,
            "zip"  => 200L * 1024 * 1024,
            "mp4"  => 500L * 1024 * 1024,
            "rar"  => 200L * 1024 * 1024,
            _      => 50L  * 1024 * 1024,
        };

        if (len < 64)           return 0;   // suspiciously tiny
        if (len >= maxSize)     return 5;   // almost certainly truncated
        if (len < 1024)         return 10;  // very small — might be fragment
        return 20;
    }

    // ── Structure check (+20) ─────────────────────────────────────────

    private static int ScoreStructure(string ext, byte[] d, int len)
    {
        try
        {
            return ext switch
            {
                "jpg" => ScoreJpeg(d, len),
                "png" => ScorePng(d, len),
                "pdf" => ScorePdf(d, len),
                "zip" => ScoreZip(d, len),
                "bmp" => ScoreBmp(d, len),
                "mp3" => ScoreMp3(d, len),
                _     => 10, // unknown type — partial credit
            };
        }
        catch { return 0; }
    }

    private static int ScoreJpeg(byte[] d, int len)
    {
        // Walk JPEG markers to verify internal consistency
        if (len < 6) return 0;
        int markers = 0;
        int i = 2; // skip SOI
        while (i + 3 < len && markers < 30)
        {
            if (d[i] != 0xFF) break;
            byte marker = d[i + 1];
            if (marker == 0xD9) return 20; // found EOI cleanly
            if (marker == 0x00 || marker == 0xFF) { i++; continue; }
            if (marker >= 0xD0 && marker <= 0xD7) { i += 2; markers++; continue; }
            int segLen = (d[i+2] << 8) | d[i+3];
            if (segLen < 2) break;
            i += 2 + segLen;
            markers++;
        }
        return markers > 2 ? 15 : 5;
    }

    private static int ScorePng(byte[] d, int len)
    {
        if (len < 33) return 0;
        // First chunk must be IHDR (width + height must be non-zero)
        bool ihdr = d[12]=='I' && d[13]=='H' && d[14]=='D' && d[15]=='R';
        if (!ihdr) return 0;
        int w = (d[16]<<24)|(d[17]<<16)|(d[18]<<8)|d[19];
        int h = (d[20]<<24)|(d[21]<<16)|(d[22]<<8)|d[23];
        return (w > 0 && h > 0 && w < 65536 && h < 65536) ? 20 : 5;
    }

    private static int ScorePdf(byte[] d, int len)
    {
        // Check for xref table or cross-reference stream
        bool hasXref = ContainsAscii(d, len, "xref") ||
                       ContainsAscii(d, len, "/XRef");
        bool hasPages = ContainsAscii(d, len, "/Pages");
        return (hasXref ? 10 : 0) + (hasPages ? 10 : 0);
    }

    private static int ScoreZip(byte[] d, int len)
    {
        // Central directory must exist
        bool hasCd = Contains(d, len, 0x50,0x4B,0x01,0x02); // central dir sig
        return hasCd ? 20 : 5;
    }

    private static int ScoreBmp(byte[] d, int len)
    {
        if (len < 54) return 0;
        uint fileSizeField = BitConverter.ToUInt32(d, 2);
        uint pixelOffset   = BitConverter.ToUInt32(d, 10);
        uint dibSize       = BitConverter.ToUInt32(d, 14);
        // File size in header should be close to actual length
        bool sizeMatch = Math.Abs((long)fileSizeField - len) < 512;
        bool validDib  = dibSize is 12 or 40 or 52 or 56 or 64 or 108 or 124;
        bool validOff  = pixelOffset >= 54 && pixelOffset < len;
        return (sizeMatch ? 8 : 0) + (validDib ? 6 : 0) + (validOff ? 6 : 0);
    }

    private static int ScoreMp3(byte[] d, int len)
    {
        if (len < 10) return 0;
        // Check ID3v2 tag size is reasonable
        int tagSize = ((d[6] & 0x7F) << 21) | ((d[7] & 0x7F) << 14)
                    | ((d[8] & 0x7F) << 7)  |  (d[9] & 0x7F);
        return (tagSize > 0 && tagSize < len) ? 20 : 8;
    }

    // ── Byte search helpers ───────────────────────────────────────────

    private static bool EndsWith(byte[] d, int len, byte b0, byte b1)
        => len >= 2 && d[len-2] == b0 && d[len-1] == b1;

    private static bool Contains(byte[] d, int len,
                                  byte b0, byte b1, byte b2, byte b3)
    {
        for (int i = 0; i + 3 < len; i++)
            if (d[i]==b0 && d[i+1]==b1 && d[i+2]==b2 && d[i+3]==b3)
                return true;
        return false;
    }

    private static bool ContainsAscii(byte[] d, int len, string s)
    {
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(s);
        return new ReadOnlySpan<byte>(d, 0, len).IndexOf(needle) >= 0;
    }
}
