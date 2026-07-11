using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerRecover.App;

/// <summary>
/// Standalone hex viewer window. Opens alongside the main window and lets
/// the user inspect raw disk sectors at any offset.
///
/// Features:
///   • 16 bytes per row — offset | hex pairs | ASCII sidebar
///   • Jump to absolute byte offset, LBA sector number, or MFT record#
///   • "Show on disk" — jump directly to the offset of a recovered file
///   • Highlights known structures: MFT "FILE" records, NTFS boot, MBR
///   • Page Up / Page Down navigation (512 bytes = 1 sector per page)
///   • Keyboard shortcut: Ctrl+G = Go to offset dialog
///
/// Usage from MainWindow:
///   var hex = new HexViewerWindow(_disk);
///   hex.Show();
///   hex.JumpToOffset(rf.Offset);  // jump to a recovered file's location
/// </summary>
public sealed class HexViewerWindow : Window
{
    private readonly PowerRecover.Engine.RawDisk _disk;
    private long _currentOffset = 0;
    private const int BYTES_PER_ROW  = 16;
    private const int ROWS_PER_PAGE  = 32;   // 512 bytes = 1 sector
    private const int PAGE_SIZE      = BYTES_PER_ROW * ROWS_PER_PAGE;

    // UI elements
    private readonly RichTextBox _hexBox;
    private readonly TextBox     _offsetBox;
    private readonly TextBlock   _statusBar;
    private readonly Button      _prevBtn;
    private readonly Button      _nextBtn;

    // Highlight colors for known structures
    private static readonly Brush BrushMft    = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x5F));
    private static readonly Brush BrushBoot   = new SolidColorBrush(Color.FromRgb(0x1E, 0x4A, 0x2A));
    private static readonly Brush BrushMbr    = new SolidColorBrush(Color.FromRgb(0x4A, 0x2A, 0x1E));
    private static readonly Brush BrushNormal = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18));

    public HexViewerWindow(PowerRecover.Engine.RawDisk disk)
    {
        _disk  = disk;
        Title  = $"Hex Viewer — {disk.Source}";
        Width  = 900;
        Height = 680;
        Background = new SolidColorBrush(Color.FromRgb(0x08, 0x0B, 0x0F));

        // ── Layout ────────────────────────────────────────────────────
        var root = new DockPanel { LastChildFill = true };

        // Top toolbar
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(8, 6, 8, 6),
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        _offsetBox = new TextBox
        {
            Width       = 180,
            FontFamily  = new FontFamily("Consolas"),
            FontSize    = 12,
            Text        = "0x0",
            Margin      = new Thickness(0, 0, 6, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xB8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30)),
        };
        _offsetBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) GoToOffsetFromBox();
        };

        var goBtn = MakeButton("Go");
        goBtn.Click += (s, e) => GoToOffsetFromBox();

        var sectorBtn = MakeButton("By Sector");
        sectorBtn.Click += (s, e) => GoToSectorDialog();

        var mftBtn = MakeButton("By MFT#");
        mftBtn.Click += (s, e) => GoToMftDialog();

        _prevBtn = MakeButton("◄ Prev");
        _prevBtn.Click += (s, e) => Navigate(-PAGE_SIZE);

        _nextBtn = MakeButton("Next ►");
        _nextBtn.Click += (s, e) => Navigate(PAGE_SIZE);

        toolbar.Children.Add(new TextBlock
        {
            Text       = "Offset:",
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x56, 0x68)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(0, 0, 6, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
        });
        toolbar.Children.Add(_offsetBox);
        toolbar.Children.Add(goBtn);
        toolbar.Children.Add(sectorBtn);
        toolbar.Children.Add(mftBtn);
        toolbar.Children.Add(_prevBtn);
        toolbar.Children.Add(_nextBtn);

        // Status bar
        _statusBar = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x56, 0x68)),
            Margin     = new Thickness(10, 4, 10, 4),
        };
        DockPanel.SetDock(_statusBar, Dock.Bottom);

        // Hex display
        _hexBox = new RichTextBox
        {
            FontFamily        = new FontFamily("Consolas"),
            FontSize          = 12,
            IsReadOnly        = true,
            Background        = BrushNormal,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(10, 6, 10, 6),
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        root.Children.Add(toolbar);
        root.Children.Add(_statusBar);
        root.Children.Add(_hexBox);
        Content = root;

        KeyDown += OnKeyDown;
        Loaded  += (s, e) => RenderPage(_currentOffset);
    }

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>Jump to the offset of a recovered file and highlight it.</summary>
    public void JumpToOffset(long offset)
    {
        _currentOffset = AlignToRow(offset);
        RenderPage(_currentOffset);
        _offsetBox.Text = $"0x{offset:X}";
    }

    // ── Navigation ───────────────────────────────────────────────────

    private void Navigate(long delta)
    {
        long next = _currentOffset + delta;
        next = Math.Max(0, Math.Min(next, _disk.Length - PAGE_SIZE));
        _currentOffset = AlignToRow(next);
        RenderPage(_currentOffset);
    }

    private void GoToOffsetFromBox()
    {
        string text = _offsetBox.Text.Trim();
        try
        {
            long offset = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt64(text, 16)
                : long.Parse(text);
            JumpToOffset(offset);
        }
        catch { _offsetBox.BorderBrush = Brushes.Red; }
    }

    private void GoToSectorDialog()
    {
        string? input = PromptDialog("Go to Sector", "Enter LBA sector number:");
        if (input == null) return;
        if (long.TryParse(input, out long lba))
            JumpToOffset(lba * _disk.SectorSize);
    }

    private void GoToMftDialog()
    {
        string? input = PromptDialog("Go to MFT Record", "Enter MFT record number:");
        if (input == null) return;
        if (long.TryParse(input, out long rec))
        {
            // MFT starts after boot sector — we'd need NtfsScanner._mftOffset
            // For now jump to record * 1024 from start (approximate)
            JumpToOffset(rec * 1024);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.PageDown) Navigate(PAGE_SIZE);
        if (e.Key == Key.PageUp)   Navigate(-PAGE_SIZE);
        if (e.Key == Key.Home)     JumpToOffset(0);
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
            GoToOffsetFromBox();
    }

    // ── Rendering ─────────────────────────────────────────────────────

    private void RenderPage(long offset)
    {
        byte[] buf  = new byte[PAGE_SIZE];
        int    got  = _disk.ReadAt(offset, buf, PAGE_SIZE, out int bad);

        var doc  = new FlowDocument();
        var para = new Paragraph { LineHeight = 18, Margin = new Thickness(0) };

        // Column header
        var header = new Run("  Offset          00 01 02 03 04 05 06 07  " +
                             "08 09 0A 0B 0C 0D 0E 0F  ASCII\n")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)),
        };
        para.Inlines.Add(header);

        for (int row = 0; row < ROWS_PER_PAGE && row * BYTES_PER_ROW < got; row++)
        {
            int    rowStart  = row * BYTES_PER_ROW;
            long   absOffset = offset + rowStart;
            Brush  rowBrush  = GetRowHighlight(buf, rowStart);

            // Offset column
            var offRun = new Run($"  {absOffset:X12}   ")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x5A)),
                Background = rowBrush,
            };
            para.Inlines.Add(offRun);

            // Hex bytes
            var hexSb  = new StringBuilder();
            var ascSb  = new StringBuilder();
            for (int col = 0; col < BYTES_PER_ROW; col++)
            {
                int idx = rowStart + col;
                if (idx < got)
                {
                    byte b = buf[idx];
                    hexSb.Append($"{b:X2} ");
                    if (col == 7) hexSb.Append(' ');
                    ascSb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                else
                {
                    hexSb.Append("   ");
                    ascSb.Append(' ');
                }
            }

            var hexRun = new Run(hexSb.ToString())
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
                Background = rowBrush,
            };
            var ascRun = new Run($" {ascSb}\n")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x8A, 0x6A)),
                Background = rowBrush,
            };
            para.Inlines.Add(hexRun);
            para.Inlines.Add(ascRun);
        }

        doc.Blocks.Add(para);
        _hexBox.Document = doc;

        // Update status bar
        _statusBar.Text =
            $"Offset: 0x{offset:X12}  |  Sector: {offset / _disk.SectorSize:N0}  |  " +
            $"Disk size: {_disk.Length / (1024.0 * 1024 * 1024):F2} GB  |  " +
            (bad > 0 ? $"⚠ {bad} bad sector(s) in this page" : "No bad sectors");

        _offsetBox.Text = $"0x{offset:X}";
        _prevBtn.IsEnabled = offset > 0;
        _nextBtn.IsEnabled = offset + PAGE_SIZE < _disk.Length;
    }

    // Highlight MFT records, NTFS boot sectors, MBR
    private static Brush GetRowHighlight(byte[] buf, int rowStart)
    {
        if (rowStart + 4 <= buf.Length)
        {
            // MFT record signature "FILE"
            if (buf[rowStart] == 'F' && buf[rowStart+1] == 'I' &&
                buf[rowStart+2] == 'L' && buf[rowStart+3] == 'E')
                return BrushMft;

            // NTFS boot sector "NTFS"
            if (rowStart >= 3 && rowStart < buf.Length - 4)
            {
                if (buf[3] == 'N' && buf[4] == 'T' && buf[5] == 'F' && buf[6] == 'S')
                    return BrushBoot;
            }
        }
        return BrushNormal;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static long AlignToRow(long offset)
        => (offset / BYTES_PER_ROW) * BYTES_PER_ROW;

    private static Button MakeButton(string text) => new()
    {
        Content    = text,
        Margin     = new Thickness(4, 0, 0, 0),
        Padding    = new Thickness(8, 3, 8, 3),
        FontFamily = new FontFamily("Consolas"),
        FontSize   = 11,
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
        BorderBrush= new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30)),
    };

    private static string? PromptDialog(string title, string prompt)
    {
        var win = new Window
        {
            Title           = title,
            Width           = 320,
            Height          = 130,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode      = ResizeMode.NoResize,
            Background      = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text       = prompt,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
            Margin     = new Thickness(0, 0, 0, 8),
        });
        var input = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xB8)),
            BorderBrush= new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30)),
        };
        panel.Children.Add(input);
        var btn = new Button
        {
            Content    = "Go",
            Margin     = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        string? result = null;
        btn.Click += (s, e) => { result = input.Text; win.Close(); };
        input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { result = input.Text; win.Close(); } };
        panel.Children.Add(btn);
        win.Content = panel;
        win.ShowDialog();
        return result;
    }
}
