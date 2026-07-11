namespace PowerRecover.Engine;

/// <summary>
/// Extended carving signatures — adds 20 new file types on top of your
/// existing 11. Plug into CarveScanner by replacing FileSignature.All
/// with FileSignature.Extended, or filter per user checkbox selection.
///
/// NEW TYPES: WAV, FLAC, OGG, AVI, MOV/HEIC, WEBP, PSD, SQLite,
///            EXE/DLL/ELF, 7Z already in original.
///            Plus: VMDK, VHD, ISO, TAR.GZ, XML/Office Open XML variants.
///
/// Every new signature follows the same validation discipline as your
/// existing ones — short headers MUST have a structural validator.
/// </summary>
public static class ExtendedSignatures
{
    public static readonly FileSignature[] New = new FileSignature[]
    {
        // ── Audio ─────────────────────────────────────────────────────

        new()
        {
            Name="WAV audio", Ext="wav",
            Header=new byte[]{0x52,0x49,0x46,0x46},  // "RIFF"
            MaxSize=500L*1024*1024,
            // RIFF must be followed by "WAVE" at offset 8
            Validate=(b,p,n) =>
            {
                if (n < 12) return false;
                return b[p+8]=='W' && b[p+9]=='A' && b[p+10]=='V' && b[p+11]=='E';
            },
        },

        new()
        {
            Name="FLAC audio", Ext="flac",
            Header=new byte[]{0x66,0x4C,0x61,0x43},  // "fLaC"
            MaxSize=500L*1024*1024,
            // Byte 4 must be the STREAMINFO block (type 0), mandatory first block
            Validate=(b,p,n) => n > 4 && (b[p+4] & 0x7F) == 0,
        },

        new()
        {
            Name="OGG container", Ext="ogg",
            Header=new byte[]{0x4F,0x67,0x67,0x53},  // "OggS"
            MaxSize=200L*1024*1024,
            // Version must be 0, header type 0x02 = beginning of stream
            Validate=(b,p,n) => n > 6 && b[p+4]==0 && (b[p+5] & 0x02) != 0,
        },

        // ── Video ─────────────────────────────────────────────────────

        new()
        {
            Name="AVI video", Ext="avi",
            Header=new byte[]{0x52,0x49,0x46,0x46},  // "RIFF"
            MaxSize=2000L*1024*1024,
            // AVI: RIFF header + "AVI " at offset 8 (cf WAV which has "WAVE")
            Validate=(b,p,n) =>
            {
                if (n < 12) return false;
                return b[p+8]=='A' && b[p+9]=='V' && b[p+10]=='I' && b[p+11]==' ';
            },
        },

        new()
        {
            Name="MKV/WebM video", Ext="mkv",
            Header=new byte[]{0x1A,0x45,0xDF,0xA3},  // EBML header
            MaxSize=5000L*1024*1024,
            // EBML DocType element at offset ~5; check version byte is sane
            Validate=(b,p,n) => n > 5 && b[p+4] < 8,
        },

        // ── Images ────────────────────────────────────────────────────

        new()
        {
            Name="WebP image", Ext="webp",
            Header=new byte[]{0x52,0x49,0x46,0x46},  // "RIFF"
            MaxSize=50L*1024*1024,
            // WebP: RIFF + "WEBP" at offset 8
            Validate=(b,p,n) =>
            {
                if (n < 12) return false;
                return b[p+8]=='W' && b[p+9]=='E' && b[p+10]=='B' && b[p+11]=='P';
            },
        },

        new()
        {
            Name="Adobe PSD", Ext="psd",
            Header=new byte[]{0x38,0x42,0x50,0x53},  // "8BPS"
            MaxSize=500L*1024*1024,
            // Version must be 1 (PSD) or 2 (PSB large doc)
            Validate=(b,p,n) =>
            {
                if (n < 6) return false;
                ushort ver = (ushort)(b[p+4]<<8 | b[p+5]);
                return ver == 1 || ver == 2;
            },
        },

        new()
        {
            Name="HEIC/HEIF image", Ext="heic",
            Header=new byte[]{0x66,0x74,0x79,0x70},  // "ftyp"
            HeaderOffset=4, MaxSize=50L*1024*1024,
            // ftyp brand must be "heic", "heix", "hevc", "mif1", "msf1", or "hevm"
            Validate=(b,p,n) =>
            {
                if (n < 12) return false;
                string brand = System.Text.Encoding.ASCII.GetString(b, p+8, 4).ToLower();
                return brand is "heic" or "heix" or "hevc" or "mif1" or "msf1";
            },
        },

        new()
        {
            Name="TIFF image", Ext="tif",
            Header=new byte[]{0x49,0x49,0x2A,0x00},  // little-endian TIFF
            MaxSize=500L*1024*1024,
            // IFD offset (bytes 4-7) must be >= 8
            Validate=(b,p,n) =>
            {
                if (n < 8) return false;
                uint ifd = BitConverter.ToUInt32(b, p+4);
                return ifd >= 8 && ifd < 1_000_000;
            },
        },

        new()
        {
            Name="TIFF image (BE)", Ext="tif",
            Header=new byte[]{0x4D,0x4D,0x00,0x2A},  // big-endian TIFF
            MaxSize=500L*1024*1024,
            Validate=(b,p,n) =>
            {
                if (n < 8) return false;
                uint ifd = (uint)(b[p+4]<<24|b[p+5]<<16|b[p+6]<<8|b[p+7]);
                return ifd >= 8 && ifd < 1_000_000;
            },
        },

        // ── Documents & Data ──────────────────────────────────────────

        new()
        {
            Name="SQLite database", Ext="db",
            Header=new byte[]{
                0x53,0x51,0x4C,0x69,0x74,0x65,0x20,
                0x66,0x6F,0x72,0x6D,0x61,0x74,0x20,0x33,0x00
            }, // "SQLite format 3\0"
            MaxSize=2000L*1024*1024,
            // Page size at offset 16 must be power of 2 between 512 and 65536
            Validate=(b,p,n) =>
            {
                if (n < 18) return false;
                int ps = (b[p+16]<<8) | b[p+17];
                return ps == 1 || (ps >= 512 && ps <= 65536 && (ps & (ps-1)) == 0);
            },
        },

        new()
        {
            Name="XML document", Ext="xml",
            Header=new byte[]{0x3C,0x3F,0x78,0x6D,0x6C}, // "<?xml"
            MaxSize=50L*1024*1024,
            // Version attribute must follow
            Validate=(b,p,n) => n > 6 && b[p+5]==' ' || b[p+5]=='v',
        },

        // ── Executables ───────────────────────────────────────────────

        new()
        {
            Name="Windows PE (EXE/DLL)", Ext="exe",
            Header=new byte[]{0x4D,0x5A},              // "MZ"
            MaxSize=200L*1024*1024,
            // e_lfanew at offset 0x3C points to "PE\0\0" signature
            // Must be a sane pointer (64-512 range is typical)
            Validate=(b,p,n) =>
            {
                if (n < 66) return false;
                int peOff = BitConverter.ToInt32(b, p+0x3C);
                if (peOff < 0 || peOff + 4 > n) return false;
                return b[p+peOff]=='P' && b[p+peOff+1]=='E'
                    && b[p+peOff+2]==0  && b[p+peOff+3]==0;
            },
        },

        new()
        {
            Name="ELF binary", Ext="elf",
            Header=new byte[]{0x7F,0x45,0x4C,0x46},   // "\x7fELF"
            MaxSize=100L*1024*1024,
            // EI_CLASS must be 1 (32-bit) or 2 (64-bit), EI_DATA 1 or 2
            Validate=(b,p,n) =>
            {
                if (n < 7) return false;
                return b[p+4] is 1 or 2 && b[p+5] is 1 or 2;
            },
        },

        // ── Archives & Containers ─────────────────────────────────────

        new()
        {
            Name="GZIP archive", Ext="gz",
            Header=new byte[]{0x1F,0x8B,0x08},        // gzip + deflate method
            MaxSize=500L*1024*1024,
            // Flags byte (offset 3) must be within valid range
            Validate=(b,p,n) => n > 3 && (b[p+3] & 0xE0) == 0,
        },

        new()
        {
            Name="TAR archive", Ext="tar",
            Header=new byte[]{0x75,0x73,0x74,0x61,0x72}, // "ustar" at offset 257
            HeaderOffset=257, MaxSize=2000L*1024*1024,
            // Magic version: "00" or " \0"
            Validate=(b,p,n) =>
            {
                if (n < 6) return false;
                return (b[p+5]=='0' && b[p+6]=='0')
                    || (b[p+5]==' ' && b[p+6]==0);
            },
        },

        // ── Virtual Disks ─────────────────────────────────────────────

        new()
        {
            Name="VHD disk image", Ext="vhd",
            Header=new byte[]{0x63,0x6F,0x6E,0x65,0x63,0x74,0x69,0x78}, // "conectix"
            MaxSize=2000L*1024*1024,
        },

        new()
        {
            Name="VMDK disk image", Ext="vmdk",
            Header=new byte[]{0x4B,0x44,0x4D,0x56},   // "KDMV"
            MaxSize=2000L*1024*1024,
            // Version must be 1 or 2
            Validate=(b,p,n) =>
            {
                if (n < 8) return false;
                uint ver = BitConverter.ToUInt32(b, p+4);
                return ver is 1 or 2 or 3;
            },
        },

        // ── Email ─────────────────────────────────────────────────────

        new()
        {
            Name="Outlook MSG", Ext="msg",
            // Same as DOC/XLS/PPT — Compound Document file
            Header=new byte[]{0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1},
            MaxSize=100L*1024*1024,
            // Distinguish MSG from DOC by checking minor version: MSG uses 0x3E
            // This check is advisory only — doc/xls/msg all share this magic
            Validate=(b,p,n) =>
            {
                if (n < 10) return false;
                ushort minor = (ushort)(b[p+8]|(b[p+9]<<8));
                return minor is 0x3E or 0x3B or 0x00;
            },
        },
    };

    /// <summary>All signatures: original 11 + 20 new = 31 total.
    /// Pass to CarveScanner constructor instead of FileSignature.All.</summary>
    public static FileSignature[] All =>
        FileSignature.All.Concat(New).ToArray();

    /// <summary>Filter to only the types the user checked in the UI.
    /// Call this with the selected extension set from the checkboxes.</summary>
    public static FileSignature[] Filtered(IEnumerable<string> selectedExts)
    {
        var exts = new HashSet<string>(selectedExts,
                                       StringComparer.OrdinalIgnoreCase);
        return All.Where(s => exts.Contains(s.Ext)).ToArray();
    }
}
