using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PowerRecover.Engine;

namespace PowerRecover.App;

public partial class MainWindow : Window
{
    private static readonly string[] FolderFileTypes =
    {
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf",
        "txt", "rtf", "csv", "xml",
        "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic",
        "zip", "rar", "7z",
        "mp3", "wav", "flac", "ogg", "mp4", "mov", "avi", "mkv",
        "db", "sqlite", "msg", "pst", "ost"
    };

    private FileDeduplicator _dedup = new();
    private readonly RecoveryPolicy _policy = new();
    private VssShadowCopy?         _vss;
    private EncryptedVolumeHandler? _encHandler;
    private string?                _encMountedPath;
    private string?                _encType;
    private readonly ObservableCollection<FileRow> _rows = new();
    private readonly Dictionary<string, CheckBox>  _typeChecks = new();
    private CancellationTokenSource? _cts;
    private FileFilter?      _filter;
    private HexViewerWindow? _hexViewer;
    private SmartResult?     _lastSmart;
    private ScanSession?     _session;
    private string           _previewDir = "";
    private int              _recoveredCount;

    public MainWindow()
    {
        InitializeComponent();

        _filter = new FileFilter(_rows);
        ResultsGrid.ItemsSource = _filter.View;

        OutputBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "PowerRecover_Output");

        var common = new HashSet<string>
        {
            "doc", "docx", "xls", "xlsx", "ppt", "pptx",
            "pdf", "jpg", "jpeg", "png", "txt", "csv", "zip"
        };
        var selectableTypes = ExtendedSignatures.All
            .Select(sig => sig.Ext)
            .Concat(FolderFileTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(FileTypeSortKey)
            .ThenBy(FriendlyFileType)
            .ThenBy(ext => ext, StringComparer.OrdinalIgnoreCase);

        foreach (var ext in selectableTypes)
        {
            var cb = new CheckBox
            {
                Content   = FriendlyFileTypeWithExtension(ext),
                IsChecked = common.Contains(ext),
                Tag       = ext,
            };
            _typeChecks[ext] = cb;
            TypePanel.Children.Add(cb);
        }

        StartPulse();
        Log("PowerRecover is ready.");
        Log("Safe scan mode is on. The source drive will not be changed.");
    }

    private async void OnUnlockEncrypted(object s, RoutedEventArgs e)
{
    string source = SourceBox.Text.Trim();
    if (string.IsNullOrEmpty(source))
    { MessageBox.Show("Choose a drive or disk image first."); return; }
 
    _encHandler = new EncryptedVolumeHandler();
    _encHandler.Log += msg => Ui(() => Log(msg));
 
    string encType = _encHandler.DetectEncryption(source);
    if (string.IsNullOrEmpty(encType))
    {
        Log("This source does not look password protected.");
        return;
    }
 
    string? password = PromptForPassword(
        $"{encType} Encryption Detected",
        $"Enter password or recovery key for {encType}:");
    if (password == null) return;
 
    Log($"Trying to unlock {encType}...");
    bool ok = false;
    string mounted = "";
 
    await Task.Run(() =>
    {
        if (encType == "BitLocker")
            ok = _encHandler.UnlockBitLocker(source, password, out mounted);
        else if (encType == "VeraCrypt")
            ok = _encHandler.MountVeraCrypt(source, password, out mounted);
    });
 
    if (ok)
    {
        _encMountedPath = mounted;
        _encType        = encType;
        SourceBox.Text  = mounted;
        Log($"Unlocked. Search location set to: {mounted}");
        Log("Remember to lock it again after recovery.");
    }
}
 
private void OnSelectAllTypes(object s, RoutedEventArgs e)
{
    foreach (var cb in _typeChecks.Values)
        cb.IsChecked = true;
}

private void OnSelectNoneTypes(object s, RoutedEventArgs e)
{
    foreach (var cb in _typeChecks.Values)
        cb.IsChecked = false;
}
private void OnLockEncrypted(object s, RoutedEventArgs e)
{
    if (_encHandler == null || _encMountedPath == null) return;
    if (_encType == "BitLocker")
        _encHandler.LockBitLocker(_encMountedPath.TrimEnd('\\'));
    else if (_encType == "VeraCrypt")
        _encHandler.DismountVeraCrypt(_encMountedPath.TrimEnd('\\')[^1..]);
    _encMountedPath = null;
    Log("Protected drive locked.");
}
 
// ── VSS Shadow Copies ─────────────────────────────────────────────
private void OnShowShadowCopies(object s, RoutedEventArgs e)
{
    _vss = new VssShadowCopy();
    _vss.Log += _ => { };
 
    var copies = _vss.GetShadowCopies();
    if (copies.Count == 0)
    {
        MessageBox.Show("No Windows backups were found.\n\n" +
                        "If you expected backups to appear, try running PowerRecover as Administrator.",
                        "No Backups Found");
        return;
    }
 
    // Show picker dialog
    var win = new Window
    {
        Title  = "Choose a Windows Backup",
        Width  = 600,
        Height = 400,
        Owner  = this,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x08, 0x0B, 0x0F)),
    };
 
    var list = new ListBox
    {
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x0F, 0x13, 0x18)),
        Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xC8, 0xD0, 0xDB)),
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        FontSize   = 12,
        Margin     = new Thickness(10),
    };
    foreach (var copy in copies)
        list.Items.Add(copy.Description);
 
    var selectBtn = new Button
    {
        Content  = "Search this backup",
        Margin   = new Thickness(10, 0, 10, 10),
        Padding  = new Thickness(12, 6, 12, 6),
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x00, 0xD4, 0xB8)),
        Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Colors.Black),
    };
    selectBtn.Click += (ss, ee) =>
    {
        int idx = list.SelectedIndex;
        if (idx < 0) return;
        var selected = copies[idx];
        SourceBox.Text = selected.ScanPath;
        Log($"Search location set to Windows backup: {selected.Description}");
        Log($"Backup path: {selected.ScanPath}");
        win.Close();
    };
 
    var panel = new DockPanel();
    DockPanel.SetDock(selectBtn, Dock.Bottom);
    panel.Children.Add(selectBtn);
    panel.Children.Add(list);
    win.Content = panel;
    win.ShowDialog();
}
 
// ── Journal scan ──────────────────────────────────────────────────
private async void OnScanJournal(object s, RoutedEventArgs e)
{
    string source = SourceBox.Text.Trim();
    if (string.IsNullOrEmpty(source))
    { MessageBox.Show("Choose a drive or disk image first."); return; }
 
    Log("Searching for recently deleted file names…");
    ScanButton.IsEnabled = false;
 
    int found = 0;
    await Task.Run(() =>
    {
        using var disk = new RawDisk(source);
        var parts = PartitionTable.Read(disk);
        var offsets = parts.Count > 0
            ? parts.Select(p => p.OffsetBytes).ToList()
            : new List<long> { 0 };
 
        foreach (long pOff in offsets)
        {
            var journal = new NtfsJournalScanner(disk, pOff);
            journal.Log += _ => { };
            journal.Progress += (d, t) => Ui(() => SetProgress(d, t));
 
            var ct = _cts?.Token ?? CancellationToken.None;
            foreach (var entry in journal.Scan(ct))
            {
                if (!entry.IsDirectory)
                {
                    var rf = entry.ToRecoveredFile();
                    rf.Method = "Journal";
                    Ui(() =>
                    {
                        _rows.Add(new FileRow(rf));
                        CountLabel.Text = _rows.Count.ToString();
                    });
                    found++;
                }
            }
        }
    });
 
    ScanButton.IsEnabled = true;
        Log($"Recent-name search complete: {found} deleted file name(s) found.");
    UpdateFilterStats();
}
 
// ── Password prompt helper ────────────────────────────────────────
private string? PromptForPassword(string title, string prompt)
{
    var win = new Window
    {
        Title  = title,
        Width  = 400,
        Height = 160,
        Owner  = this,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x0F, 0x13, 0x18)),
    };
 
    var panel = new StackPanel { Margin = new Thickness(16) };
    panel.Children.Add(new TextBlock
    {
        Text       = prompt,
        Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xC8, 0xD0, 0xDB)),
        Margin     = new Thickness(0, 0, 0, 8),
    });
 
    var pwd = new PasswordBox
    {
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x08, 0x0B, 0x0F)),
        Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x00, 0xD4, 0xB8)),
        Margin     = new Thickness(0, 0, 0, 10),
    };
    panel.Children.Add(pwd);
 
    string? result = null;
    var btn = new Button { Content = "Unlock" };
    btn.Click += (s, e) => { result = pwd.Password; win.Close(); };
    pwd.KeyDown += (s, e) =>
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        { result = pwd.Password; win.Close(); }
    };
    panel.Children.Add(btn);
    win.Content = panel;
    win.ShowDialog();
    return result;
}

    private void OnRaidSetup(object s, RoutedEventArgs e)
{
    var win = new RaidSetupWindow { Owner = this };
    if (win.ShowDialog() != true || win.Result == null) return;
 
    try
    {
        var stream = win.Result.BuildStream();
        // Save stream to temp .img file so RawDisk can open it
        string tmp = Path.Combine(Path.GetTempPath(),
            $"pr_raid_{DateTime.Now:yyyyMMddHHmmss}.img");
 
        Log($"Preparing combined drive copy: {win.Result.GetHealthSummary()}");
        Log($"Saving temporary copy to {tmp}...");
 
        ScanButton.IsEnabled = false;
        Task.Run(() =>
        {
            byte[] buf = new byte[8 * 1024 * 1024];
            long remaining = win.Result.VirtualSize;
            using var outFile = File.Create(tmp);
            while (remaining > 0)
            {
                int read = stream.Read(buf, 0,
                    (int)Math.Min(buf.Length, remaining));
                if (read == 0) break;
                outFile.Write(buf, 0, read);
                remaining -= read;
            }
            stream.Dispose();
        }).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            ScanButton.IsEnabled = true;
            SourceBox.Text = tmp;
            Log($"Combined drive copy is ready: {tmp}");
        }));
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Could not prepare the combined drive: {ex.Message}");
    }
}
    private void StartPulse()
    {
        var anim = new DoubleAnimation
        {
            From           = 1.0,
            To             = 0.25,
            Duration       = TimeSpan.FromSeconds(1.1),
            AutoReverse    = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase(),
        };
        PulseDot.BeginAnimation(OpacityProperty, anim);
    }

    private async void OnPickImage(object s, RoutedEventArgs e)
{
    var dlg = new OpenFileDialog
    {
        Title  = "Choose a disk image",
        Filter = "All supported|*.img;*.dd;*.raw;*.iso;*.bin;*.vhd;*.vhdx;*.vmdk;*.vdi" +
                 "|Disk images|*.img;*.dd;*.raw;*.iso;*.bin" +
                 "|Virtual disks|*.vhd;*.vhdx;*.vmdk;*.vdi" +
                 "|All files|*.*",
    };
    if (dlg.ShowDialog() != true) return;

    string path = dlg.FileName;
    string ext  = Path.GetExtension(path).ToLowerInvariant();

    if (ext is ".vhd" or ".vhdx" or ".vmdk" or ".vdi")
    {
        if (!VirtualDiskReader.TryOpen(path, out var vStream,
                                        out long vSize, out string fmt))
        { MessageBox.Show($"Could not open this disk image: {path}"); return; }

        Log($"Preparing disk image: {fmt} ({vSize / (1024.0*1024*1024):F2} GB)...");
        string tmp = Path.Combine(Path.GetTempPath(),
                       $"pr_vdisk_{DateTime.Now:yyyyMMddHHmmss}.img");
        ScanButton.IsEnabled = false;
        await Task.Run(() =>
        {
            using var outFile = File.Create(tmp);
            byte[] buf = new byte[8 * 1024 * 1024];
            long remaining = vSize;
            while (remaining > 0)
            {
                int read = vStream!.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (read == 0) break;
                outFile.Write(buf, 0, read);
                remaining -= read;
            }
            vStream!.Dispose();
        });
        ScanButton.IsEnabled = true;
        SourceBox.Text = tmp;
        Log($"Ready to search: {tmp}");
    }
    else
    {
        SourceBox.Text = path;
    }
}

    private void OnPickDrive(object s, RoutedEventArgs e)
    {
        var win = new DriveDialog { Owner = this };
        if (win.ShowDialog() == true)
            SourceBox.Text = win.SelectedPath;
    }

    private void OnPickFolder(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to search" };
        if (dlg.ShowDialog() == true)
        {
            SourceBox.Text = dlg.FolderName;
            Log($"Search folder selected: {dlg.FolderName}");
        }
    }

    private void OnWizardDeleted(object s, RoutedEventArgs e)
    {
        ModeBox.SelectedIndex = 0;
        Log("Recovery Assistant: deleted file recovery selected.");
        OnPickDrive(s, e);
        HeaderStatsLabel.Text = "Deleted recovery - scan a drive";
    }

    private void OnWizardFolder(object s, RoutedEventArgs e)
    {
        ModeBox.SelectedIndex = 0;
        Log("Recovery Assistant: folder check selected.");
        OnPickFolder(s, e);
        HeaderStatsLabel.Text = "Folder check - preview existing files";
    }

    private void OnWizardImage(object s, RoutedEventArgs e)
    {
        ModeBox.SelectedIndex = 0;
        Log("Recovery Assistant: disk image recovery selected.");
        OnPickImage(s, e);
        HeaderStatsLabel.Text = "Disk image recovery";
    }

    private void OnPickOutput(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select output folder" };
        if (dlg.ShowDialog() == true) OutputBox.Text = dlg.FolderName;
    }

    private async void OnImageDrive(object s, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim();
        if (string.IsNullOrEmpty(source))
        { MessageBox.Show("Choose a drive first."); return; }

        var dlg = new SaveFileDialog
        {
            Title    = "Save drive copy as…",
            Filter   = "Disk image (*.img)|*.img|Raw (*.dd)|*.dd",
            FileName = $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.img",
        };
        if (dlg.ShowDialog() != true) return;

        var imagingCts       = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        await Task.Run(() =>
        {
            using var disk  = new RawDisk(source);
            var imager      = new DiskImager(disk);
            imager.Log     += _       => { };
            imager.Progress += (d, t) => Ui(() => SetProgress(d, t));
            imager.Clone(dlg.FileName, imagingCts.Token);
        });

        ScanButton.IsEnabled = true;
        StopButton.IsEnabled = false;

        if (MessageBox.Show("Drive copy is ready. Search the copy now?",
            "Search copy?", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            SourceBox.Text = dlg.FileName;
            OnScan(s, e);
        }
    }
    private void UpdateDetailPanelForScan(string source, string output, int modeIndex)
{
    // Drive source
    if (DetailDriveSource != null)
        DetailDriveSource.Text = System.IO.Path.GetFileName(source) is { Length: > 0 } n
            ? n : source;

    // Output path
    if (DetailOutputLabel != null)
        DetailOutputLabel.Text = System.IO.Path.GetFileName(output.TrimEnd('\\', '/'))
                                 is { Length: > 0 } o ? o : output;

    // Mode
    if (DetailModeLabel != null)
        DetailModeLabel.Text = modeIndex switch
        {
            _ when Directory.Exists(source) => "Folder search",
            0 => "Recommended search",
            1 => "Raw search",
            2 => "Full search",
            3 => "Quick search",
            _ => "—"
        };

    // SMART
    if (DetailSmartLabel != null && _lastSmart != null)
    {
        if (_lastSmart.IsCritical)
        {
            DetailSmartLabel.Text       = "Critical";
            DetailSmartLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC4, 0x2B, 0x1C));
        }
        else if (_lastSmart.Available)
        {
            DetailSmartLabel.Text       = "Healthy";
            DetailSmartLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
        }
        else
        {
            DetailSmartLabel.Text = "N/A";
        }
    }
}
private void OnResultsSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
{
    if (ResultsGrid.SelectedItem is not FileRow row)
    {
        // Nothing selected — reset
        DetailFileName.Text  = "No file selected";
        DetailFileSub.Text   = "Choose a file from the list";
        DetailStatus.Text    = "—";
        DetailSize.Text      = "—";
        DetailMethod.Text    = "—";
        DetailConf.Text      = "—";
        DetailOffset.Text    = "—";
        DetailPath.Text      = "";
        if (RecoverFileButton != null) RecoverFileButton.IsEnabled = false;
        if (InspectFileButton != null) InspectFileButton.IsEnabled = false;
        ClearPreview("Select a file to preview it");
        return;
    }

    // Populate selected file details
    DetailFileName.Text = row.Name;
    DetailFileSub.Text  = $"{row.Ext.ToUpperInvariant()} file  ·  {row.SizeText}";

    DetailStatus.Text = row.Status;
    DetailStatus.Foreground = row.Status == "Deleted"
        ? new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE6, 0x51, 0x00))
        : new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));

    DetailSize.Text   = row.SizeText;
    DetailMethod.Text = row.Method;
    DetailConf.Text   = row.ConfidenceText;
    DetailOffset.Text = row.OffsetHex;
    DetailPath.Text   = string.IsNullOrEmpty(row.FolderPath)
        ? "" : $"Folder: {row.FolderPath}";

    if (RecoverFileButton != null) RecoverFileButton.IsEnabled = true;
    if (InspectFileButton != null) InspectFileButton.IsEnabled = row.Offset > 0;
    UpdatePreview(row);
}

private void ClearPreview(string message)
{
    PreviewImage.Source = null;
    PreviewImage.Visibility = Visibility.Collapsed;
    PreviewText.Text = "";
    PreviewText.Visibility = Visibility.Collapsed;
    PreviewStatus.Text = message;
    PreviewStatus.Visibility = Visibility.Visible;
}

private void UpdatePreview(FileRow row)
{
    if (string.IsNullOrWhiteSpace(row.PreviewPath) || !File.Exists(row.PreviewPath))
    {
        ClearPreview("Preview is not available for this file.");
        return;
    }

    string ext = row.Ext.ToLowerInvariant();
    try
    {
        if (ext is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff")
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(row.PreviewPath);
            bmp.EndInit();
            bmp.Freeze();

            PreviewImage.Source = bmp;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewText.Visibility = Visibility.Collapsed;
            PreviewStatus.Visibility = Visibility.Collapsed;
            return;
        }

        if (ext is "txt" or "csv" or "xml")
        {
            PreviewText.Text = ReadPreviewText(row.PreviewPath);
            PreviewText.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewStatus.Visibility = Visibility.Collapsed;
            return;
        }

        if (ext == "pdf")
        {
            PreviewText.Text = LooksLikePdf(row.PreviewPath)
                ? "PDF looks valid. Recover it, then open it with your PDF reader."
                : "This PDF may be damaged.";
            PreviewText.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewStatus.Visibility = Visibility.Collapsed;
            return;
        }

        if (ext is "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx")
        {
            PreviewText.Text = "Office file looks recoverable. Recover it, then open it in Microsoft Office.";
            PreviewText.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewStatus.Visibility = Visibility.Collapsed;
            return;
        }

        ClearPreview("Preview is limited for this file type. You can still recover it and open it normally.");
    }
    catch
    {
        ClearPreview("Preview failed. The file may still recover, but it may be damaged.");
    }
}

private static string ReadPreviewText(string path)
{
    using var fs = File.OpenRead(path);
    int len = (int)Math.Min(fs.Length, 4096);
    byte[] buffer = new byte[len];
    fs.Read(buffer, 0, len);
    return System.Text.Encoding.UTF8.GetString(buffer).Replace("\0", "");
}

private static bool LooksLikePdf(string path)
{
    byte[] data = File.ReadAllBytes(path);
    return data.Length > 8 &&
           System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(5, data.Length)) == "%PDF-" &&
           System.Text.Encoding.ASCII.GetString(data).Contains("%%EOF", StringComparison.Ordinal);
}

    private void OnOpenHexViewer(object s, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim();
        if (string.IsNullOrEmpty(source))
        { MessageBox.Show("Choose a drive or disk image first."); return; }
        if (Directory.Exists(source))
        {
            MessageBox.Show("Disk inspector is for drives and disk images. Folder scan results do not have a raw disk view.");
            return;
        }

        try
        {
            var disk = new RawDisk(source);
            _hexViewer = new HexViewerWindow(disk);
            _hexViewer.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the disk inspector: {ex.Message}");
        }
    }

    private void OnShowOnDisk(object s, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not FileRow row) return;
        if (row.Offset <= 0)
        {
            MessageBox.Show("This file came from a folder scan, so there is no raw disk location to inspect.");
            return;
        }
        if (_hexViewer == null || !_hexViewer.IsVisible) OnOpenHexViewer(s, e);
        _hexViewer?.JumpToOffset(row.Offset);
    }

    private void OnOpenTimeline(object s, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Timeline view is available for NTFS drive investigations after timeline data is collected.\n\n" +
            "For normal folder checks, use the file list and preview panel.",
            "Timeline",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnFilterStatusChanged(object s, SelectionChangedEventArgs e)
    {
        if (_filter == null) return;
        var status = (FilterStatusCombo?.SelectedIndex ?? 0) switch
        {
            1 => FilterStatus.DeletedOnly,
            2 => FilterStatus.OkOnly,
            _ => FilterStatus.All,
        };
        _filter.SetStatusFilter(status);
        UpdateFilterStats();
    }

    private void OnFilterTextChanged(object s, TextChangedEventArgs e)
    {
        _filter?.SetSearchText(FilterSearchBox?.Text ?? "");
        UpdateFilterStats();
    }

    private void OnMinConfidenceChanged(object s, TextChangedEventArgs e)
    {
        if (_filter == null) return;
        if (int.TryParse(MinConfBox?.Text, out int v))
            _filter.SetMinConfidence(Math.Clamp(v, 0, 100));
        UpdateFilterStats();
    }

    private void OnClearFilters(object s, RoutedEventArgs e)
    {
        _filter?.ClearAll();
        if (FilterStatusCombo != null) FilterStatusCombo.SelectedIndex = 0;
        if (FilterSearchBox   != null) FilterSearchBox.Text   = "";
        if (MinConfBox        != null) MinConfBox.Text        = "0";
        UpdateFilterStats();
    }

    private void UpdateFilterStats()
{
    if (_filter == null || FilterStatsLabel == null) return;
    var (count, bytes, deleted) = _filter.GetStats();
    FilterStatsLabel.Text =
        $"{count:N0} visible  -  {deleted:N0} deleted  -  {Human(bytes)}";

    // Also update right panel session counters
    if (DetailCountLabel  != null) DetailCountLabel.Text  = _rows.Count.ToString("N0");
    if (DetailDeletedLabel != null)
        DetailDeletedLabel.Text = _rows.Count(r => r.Status == "Deleted").ToString("N0");
}

    private async void OnExportReport(object s, RoutedEventArgs e)
{
    if (_session == null || _session.Files.Count == 0)
    {
        MessageBox.Show("No files found yet. Run a search first.",
                        "No Files Yet", MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }

    var dlg = new SaveFileDialog
    {
        Title      = "Save Recovery Report",
        Filter     = "HTML Report|*.html",
        FileName   = $"PowerRecover_Report_{DateTime.Now:yyyyMMdd_HHmmss}.html",
        InitialDirectory = _session.OutputDir
    };
    if (dlg.ShowDialog() != true) return;

    try
    {
        ReportButton.IsEnabled = false;
        _session.MarkComplete();
        await Task.Run(() =>
            ReportExporter.Export(_session, dlg.FileName, _lastSmart));

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = dlg.FileName,
            UseShellExecute = true
        });
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Could not save the report: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
    }
    finally
    {
        ReportButton.IsEnabled = true;
    }
}

    private void OnStop(object s, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Stopping...");
    }

    private async void OnScan(object s, RoutedEventArgs e)
    {
        string source = SourceBox.Text.Trim();
        string output = OutputBox.Text.Trim();

        if (string.IsNullOrEmpty(source))
        { MessageBox.Show("Choose a folder, drive, or disk image first."); return; }

        if (string.IsNullOrWhiteSpace(output))
        { MessageBox.Show("Choose where to save recovered files first."); return; }

        if (!ConfirmSafeOutputLocation(source, output))
            return;

        if (source.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            _lastSmart = await Task.Run(() => new SmartMonitor(source).Read());
            if (_lastSmart.IsCritical)
            {
                var ans = MessageBox.Show(
                    $"DRIVE MAY BE FAILING\n\n{_lastSmart.HealthSummary}\n\n" +
                    "Make a copy first if possible. Continue anyway?",
                    "Drive Health Warning", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (ans == MessageBoxResult.No) return;
            }
            else if (_lastSmart.Available)
                Log($"Drive health: {_lastSmart.HealthSummary}");
        }

        var exts  = _typeChecks.Where(k => k.Value.IsChecked == true)
                               .Select(k => k.Key).ToHashSet();
        int mode  = ModeBox.SelectedIndex;

        _rows.Clear();
        ClearPreview("Scan first, then select a file to preview it.");
        _recoveredCount = 0;
        _previewDir = Path.Combine(Path.GetTempPath(),
            $"PowerRecoverPreview_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(_previewDir);
        _cts               = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        var token          = _cts.Token;

        (_session, _) = ScanSession.LoadOrCreate(source, output);
        UpdateDetailPanelForScan(source, output, mode);

        try
        {
            if (Directory.Exists(source))
                await Task.Run(() => RunFolderScan(source, output, exts, token), token);
            else
                await Task.Run(() => RunScan(source, output, exts, mode, token), token);
            Log("Search complete.");
        }
        catch (OperationCanceledException) { Log("Search stopped."); }
        catch (Exception ex)              { Log($"Problem: {ex.Message}"); }
        finally
        {
            ScanButton.IsEnabled   = true;
            StopButton.IsEnabled   = false;
            ReportButton.IsEnabled = _rows.Count > 0;
            SetStatus("Ready.");
            UpdateFilterStats();
        }
    }

    private bool ConfirmSafeOutputLocation(string source, string output)
    {
        if (!Directory.Exists(source) || string.IsNullOrWhiteSpace(output))
            return true;

        string fullSource = Path.GetFullPath(source).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string fullOutput = Path.GetFullPath(output).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!fullOutput.StartsWith(fullSource, StringComparison.OrdinalIgnoreCase))
            return true;

        var answer = MessageBox.Show(
            "The recovery output is inside the folder you are scanning.\n\n" +
            "For the safest recovery test, choose a different output folder. Continue anyway?",
            "Choose a safer output folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return answer == MessageBoxResult.Yes;
    }

    private void RunFolderScan(string source, string output,
                               HashSet<string> exts,
                               CancellationToken token)
    {
        int saved = 0;
        int skippedQuality = 0;
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(_previewDir);

        Ui(() =>
        {
            Log($"Searching folder: {source}");
            if (DetailDriveCapacity != null) DetailDriveCapacity.Text = "Folder";
            if (DetailDriveFs != null) DetailDriveFs.Text = "Windows folder";
            if (DetailSmartLabel != null) DetailSmartLabel.Text = "Not needed";
        });

        foreach (string file in SafeEnumerateFiles(source, token))
        {
            if (token.IsCancellationRequested) break;

            try
            {
                var info = new FileInfo(file);
                string ext = info.Extension.TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(ext) || !exts.Contains(ext))
                    continue;

                string relative = Path.GetRelativePath(source, info.DirectoryName ?? source);
                if (relative == ".") relative = "";

                var rf = new RecoveredFile
                {
                    Name = info.Name,
                    Ext = ext,
                    Size = info.Length,
                    Offset = 0,
                    Method = "Folder",
                    Deleted = false,
                    FolderPath = relative,
                    Confidence = 100,
                };

                var decision = _policy.Evaluate(rf);
                if (!decision.Accepted)
                {
                    skippedQuality++;
                    continue;
                }

                string previewPath = StageExistingFilePreview(file, ++saved);
                AddRow(rf, previewPath);
                if (saved % 100 == 0)
                {
                    FlushPendingRows();
                    Ui(() => SetStatus($"{saved:N0} good file(s) found"));
                }
            }
            catch
            {
                skippedQuality++;
            }
        }

        FlushPendingRows();
        Ui(() => Log($"{saved} good file(s) from this folder are ready to preview. Skipped {skippedQuality:N0} junk or unreadable file(s)."));
    }

    private void RunScan(string source, string output,
                     HashSet<string> exts, int mode,
                     CancellationToken token)
{
    using var disk    = new RawDisk(source);
    var extractor     = new FileExtractor(disk);
    int saved         = 0;
    int skippedQuality = 0;
    Directory.CreateDirectory(output);

    _dedup.Reset();  // reset deduplication for new scan

    Ui(() => Log($"Searching {source} ({disk.Length / (1024.0 * 1024 * 1024):F1} GB)"));
    Ui(() => {
    if (DetailDriveCapacity != null)
        DetailDriveCapacity.Text = $"{disk.Length / (1024.0 * 1024 * 1024):F1} GB";
});

    var parts   = PartitionTable.Read(disk);
    var offsets = parts.Count > 0
        ? parts.Select(p => p.OffsetBytes).ToList()
        : new List<long> { 0 };

    // ── TRIAGE MODE (index 3) ─────────────────────────────────────
    if (mode == 3)
    {
        Ui(() => Log("Quick search: checking named files first, then high-quality raw matches."));
        foreach (long pOff in offsets)
        {
            if (token.IsCancellationRequested) break;
            var sigs   = ExtendedSignatures.All.Where(x => exts.Contains(x.Ext));
            var triage = new TriageScanner(disk, pOff,
                minConfidence: 75,
                maxCarveBytes: 10L * 1024 * 1024 * 1024,
                sigs: sigs);
            triage.Log      += _      => { };
            triage.Progress += (d, t) => Ui(() => SetProgress(d, t));

            foreach (var rf in triage.Scan(token))
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (!ShouldKeepRecoveredFile(rf, ref skippedQuality)) continue;
                    string savedPath = StagePreviewFile(extractor, rf, ++saved);
                    AddRow(rf, savedPath);
                    if (saved % 500 == 0) FlushPendingRows();
                }
                catch { saved--; }
            }
        }
        FlushPendingRows();
        Ui(() => Log($"Quick search complete. {saved} good file(s) are ready to preview. Skipped {skippedQuality:N0} low-quality match(es)."));
        return;
    }

    // ── NTFS deep scan ────────────────────────────────────────────
    if (mode == 0 || mode == 2)
    {
        Ui(() => Log("Checking the drive layout…"));
        foreach (long pOff in offsets)
        {
            if (token.IsCancellationRequested) break;
            var ntfs    = new NtfsScanner(disk, pOff);
            ntfs.Log     += _      => { };
            ntfs.Progress += (d,t) => Ui(() => SetProgress(d, t));
            if (!ntfs.ReadBootSector()) continue;
            var ntfsFiles = ntfs.Scan(token).ToList();
            ntfs.ResolvePaths(ntfsFiles);
            foreach (var rf in ntfsFiles)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    rf.Confidence = ConfidenceScorer.Score(rf);
                    if (!ShouldKeepRecoveredFile(rf, ref skippedQuality)) continue;
                    string savedPath = StagePreviewFile(extractor, rf, ++saved);
                    AddRow(rf, savedPath);
                    if (saved % 500 == 0) FlushPendingRows();
                }
                catch { saved--; }
            }
        }
    }

    // ── FAT32 / exFAT scan ────────────────────────────────────────
    if (mode == 0 || mode == 2)
    {
        foreach (long pOff in offsets)
        {
            if (token.IsCancellationRequested) break;
            var fat    = new FatScanner(disk, pOff);
            fat.Log     += _      => { };
            fat.Progress += (d,t) => Ui(() => SetProgress(d, t));
            if (!fat.ReadBootSector()) continue;
            foreach (var rf in fat.Scan(token))
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    rf.Confidence = ConfidenceScorer.Score(rf);
                    if (!ShouldKeepRecoveredFile(rf, ref skippedQuality)) continue;
                    string savedPath = StagePreviewFile(extractor, rf, ++saved);
                    AddRow(rf, savedPath);
                    if (saved % 500 == 0) FlushPendingRows();
                }
                catch { saved--; }
            }
        }
    }

    // ── ext4 / Linux scan ─────────────────────────────────────────
    if (mode == 0 || mode == 2)
    {
        foreach (long pOff in offsets)
        {
            if (token.IsCancellationRequested) break;
            var ext4 = new Ext4Scanner(disk, pOff);
            ext4.Log     += _      => { };
            ext4.Progress += (d,t) => Ui(() => SetProgress(d, t));
            if (!ext4.ReadSuperblock()) continue;
            foreach (var rf in ext4.Scan(token))
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    rf.Confidence = ConfidenceScorer.Score(rf);
                    if (!ShouldKeepRecoveredFile(rf, ref skippedQuality)) continue;
                    string savedPath = StagePreviewFile(extractor, rf, ++saved);
                    AddRow(rf, savedPath);
                    if (saved % 500 == 0) FlushPendingRows();
                }
                catch { saved--; }
            }
        }
    }

    // ── Signature carve ───────────────────────────────────────────
    if (mode == 1 || mode == 2)
    {
        var sigs    = ExtendedSignatures.All.Where(x => exts.Contains(x.Ext));
        var scanner = new MultiThreadedScanner(disk, sigs);
        scanner.Log     += _      => { };
        scanner.Progress += (d,t) => Ui(() => SetProgress(d, t));
        foreach (var rf in scanner.Scan(token))
        {
            rf.Confidence = ConfidenceScorer.Score(rf);
            if (!ShouldKeepRecoveredFile(rf, ref skippedQuality)) continue;
            string path = StagePreviewFile(extractor, rf, ++saved);
            AddRow(rf, path);
            if (saved % 500 == 0) FlushPendingRows();
        }
    }

    FlushPendingRows();
    Ui(() => Log($"{saved} good file(s) are ready to preview. Skipped {skippedQuality:N0} low-quality match(es). Select files and click Recover selected file(s)."));
}

private bool ShouldKeepRecoveredFile(RecoveredFile rf, ref int skippedQuality)
{
    var decision = _policy.Evaluate(rf);
    if (!decision.Accepted)
    {
        skippedQuality++;
        return false;
    }

    if (_dedup.IsDuplicate(rf))
    {
        skippedQuality++;
        return false;
    }

    return true;
}

private string StagePreviewFile(FileExtractor extractor, RecoveredFile rf, int index)
{
    Directory.CreateDirectory(_previewDir);
    string name = MakeSafeFileName($"{index:D5}_{rf.Name}");
    string path = Path.Combine(_previewDir, name);
    byte[] data = extractor.Materialize(rf);
    File.WriteAllBytes(path, data);
    return path;
}

private string StageExistingFilePreview(string filePath, int index)
{
    Directory.CreateDirectory(_previewDir);
    string name = MakeSafeFileName($"{index:D5}_{Path.GetFileName(filePath)}");
    string path = EnsureUniquePath(Path.Combine(_previewDir, name));
    File.Copy(filePath, path, overwrite: false);
    return path;
}

private void OnRecoverSelected(object sender, RoutedEventArgs e)
{
    if (ResultsGrid.SelectedItems.Count == 0)
    {
        MessageBox.Show("Select one or more files first.");
        return;
    }

    string output = OutputBox.Text.Trim();
    if (string.IsNullOrWhiteSpace(output))
    {
        MessageBox.Show("Choose where to save recovered files first.");
        return;
    }

    int saved = 0;
    int failed = 0;

    foreach (var item in ResultsGrid.SelectedItems)
    {
        if (item is not FileRow row) continue;
        try
        {
            if (string.IsNullOrWhiteSpace(row.PreviewPath) || !File.Exists(row.PreviewPath))
            {
                failed++;
                continue;
            }

            string finalPath = EnsureUniquePath(BuildRecoveredOutputPath(output, row, ++_recoveredCount));
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Copy(row.PreviewPath, finalPath, overwrite: false);
            row.RecoveredPath = finalPath;
            saved++;
        }
        catch
        {
            failed++;
        }
    }

    Log($"Recovered {saved} selected file(s) to {output}." +
        (failed > 0 ? $" {failed} file(s) could not be recovered." : ""));
    MessageBox.Show($"Recovered {saved} file(s)." +
                    (failed > 0 ? $"\n{failed} file(s) could not be recovered." : ""),
                    "Recovery Complete",
                    MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
}

private static string BuildRecoveredOutputPath(string output, FileRow row, int index)
{
    string targetDir = output;
    if (!string.IsNullOrWhiteSpace(row.FolderPath))
    {
        string folder = MakeSafeRelativePath(row.FolderPath);
        if (!string.IsNullOrWhiteSpace(folder))
            targetDir = Path.Combine(output, "RecoveredFolders", folder);
    }
    else
    {
        targetDir = Path.Combine(output, "RecoveredByType", row.Ext.ToLowerInvariant());
    }

    return Path.Combine(targetDir, $"{index:D5}_{MakeSafeFileName(row.Name)}");
}

private static string EnsureUniquePath(string path)
{
    if (!File.Exists(path)) return path;

    string dir = Path.GetDirectoryName(path) ?? "";
    string name = Path.GetFileNameWithoutExtension(path);
    string ext = Path.GetExtension(path);

    for (int i = 2; i < 10_000; i++)
    {
        string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
        if (!File.Exists(candidate)) return candidate;
    }

    return Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}");
}

private static string MakeSafeRelativePath(string folderPath)
{
    string cleaned = folderPath.Trim().TrimStart('\\', '/');
    if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "?") return "";

    string[] parts = cleaned
        .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(MakeSafeFileName)
        .Where(p => p != "." && p != "..")
        .ToArray();

    return parts.Length == 0 ? "" : Path.Combine(parts);
}

private static string MakeSafeFileName(string name)
{
    foreach (char c in Path.GetInvalidFileNameChars())
        name = name.Replace(c, '_');
    return string.IsNullOrWhiteSpace(name) ? "unnamed" : name;
}

private static IEnumerable<string> SafeEnumerateFiles(string root, CancellationToken token)
{
    var pending = new Stack<string>();
    pending.Push(root);

    while (pending.Count > 0)
    {
        token.ThrowIfCancellationRequested();
        string current = pending.Pop();

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(current).ToList(); }
        catch { continue; }

        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            yield return file;
        }

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(current).ToList(); }
        catch { continue; }

        foreach (string dir in dirs)
            pending.Push(dir);
    }
}

    private void Ui(Action a) => Dispatcher.Invoke(a);

    private readonly List<FileRow> _pendingRows = new();
private readonly object _pendingLock = new();

private void AddRow(RecoveredFile rf, string savedPath = "")
{
    _session?.AddFile(rf, savedPath);
    lock (_pendingLock)
        _pendingRows.Add(new FileRow(rf, savedPath));
}

private void FlushPendingRows()
{
    List<FileRow> toAdd;
    lock (_pendingLock)
    {
        if (_pendingRows.Count == 0) return;
        toAdd = new List<FileRow>(_pendingRows);
        _pendingRows.Clear();
    }
    Dispatcher.Invoke(() =>
    {
        foreach (var row in toAdd)
            _rows.Add(row);
        CountLabel.Text = _rows.Count.ToString();
        UpdateFilterStats();
    });
}

    private void Log(string msg)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");
        LogBox.ScrollToEnd();
    }

    private void SetStatus(string s) => StatusLabel.Text = s;

    private void SetProgress(long done, long total)
    {
        double frac = total > 0 ? (double)done / total : 0;
        ProgressFill.Width =
            ((Border)ProgressFill.Parent).ActualWidth * frac;
        SetStatus($"{done / (1024.0 * 1024):N0} / {total / (1024.0 * 1024):N0} MB" +
                  $"  ({frac * 100:F1}%)");
    }

    private static string Human(long n) => n switch
    {
        >= 1L << 30 => $"{n / (double)(1 << 30):F1} GB",
        >= 1L << 20 => $"{n / (double)(1 << 20):F1} MB",
        >= 1L << 10 => $"{n / (double)(1 << 10):F1} KB",
        _           => $"{n} B",
    };

    private static string FriendlyFileType(string ext)
        => ext.ToLowerInvariant() switch
        {
            "doc" or "docx" => "Word documents",
            "xls" or "xlsx" => "Excel spreadsheets",
            "ppt" or "pptx" => "PowerPoint files",
            "pdf" => "PDF documents",
            "jpg" or "jpeg" => "Photos (JPG)",
            "png" => "Images (PNG)",
            "gif" => "Images (GIF)",
            "bmp" => "Images (BMP)",
            "tif" or "tiff" => "Scanned images (TIFF)",
            "webp" => "Web images",
            "heic" => "iPhone photos",
            "mp3" => "Music (MP3)",
            "wav" or "flac" or "ogg" => "Audio files",
            "mp4" or "mov" or "avi" or "mkv" => "Videos",
            "zip" or "rar" or "7z" => "Compressed folders",
            "txt" or "rtf" or "csv" or "xml" => "Text and data files",
            "db" or "sqlite" => "Database files",
            "msg" or "pst" or "ost" => "Email files",
            _ => $"{ext.ToUpperInvariant()} files",
        };

    private static string FriendlyFileTypeWithExtension(string ext)
        => $"{FriendlyFileType(ext)} (.{ext.ToLowerInvariant()})";

    private static int FileTypeSortKey(string ext)
        => ext.ToLowerInvariant() switch
        {
            "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "pdf" => 0,
            "txt" or "rtf" or "csv" or "xml" => 1,
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff" or "webp" or "heic" => 2,
            "zip" or "rar" or "7z" => 3,
            "mp4" or "mov" or "avi" or "mkv" => 4,
            "mp3" or "wav" or "flac" or "ogg" => 5,
            "db" or "sqlite" or "msg" or "pst" or "ost" => 6,
            _ => 20,
        };

    internal static string FriendlyMethod(string method)
    {
        if (method.Contains("MFT", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("FAT", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("exFAT", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("ext", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("HFS", StringComparison.OrdinalIgnoreCase))
            return "Folder scan";

        if (method.Contains("Journal", StringComparison.OrdinalIgnoreCase))
            return "Deleted name search";

        if (method.Contains("Office", StringComparison.OrdinalIgnoreCase))
            return "Office file search";

        if (method.Contains("Carve", StringComparison.OrdinalIgnoreCase))
            return "Raw file search";

        return method;
    }
}

public sealed class FileRow
{
    public string Name            { get; }
    public string Ext             { get; }
    public string SizeText        { get; }
    public long   SizeBytes       { get; }
    public string Method          { get; }
    public string Status          { get; }
    public string ConfidenceText  { get; }
    public int    ConfidenceValue { get; }
    public string FolderPath      { get; }
    public long   Offset          { get; }
    public string OffsetHex       => Offset > 0 ? $"0x{Offset:X}" : "—";
    public string PreviewPath     { get; }
    public string RecoveredPath   { get; set; } = "";

    public FileRow(RecoveredFile rf, string previewPath = "")
    {
        Name            = rf.Name;
        Ext             = rf.Ext;
        SizeBytes       = rf.Size;
        SizeText        = Human(rf.Size);
        Method          = MainWindow.FriendlyMethod(rf.Method);
        Status          = rf.Deleted ? "Deleted" : "Found";
        ConfidenceValue = rf.Confidence;
        ConfidenceText  = rf.Confidence >= 0 ? $"{rf.Confidence}%" : "—";
        FolderPath      = rf.FolderPath;
        Offset          = rf.Offset;
        PreviewPath     = previewPath;
    }

    private static string Human(long n) => n switch
    {
        >= 1L << 30 => $"{n / (double)(1 << 30):F1} GB",
        >= 1L << 20 => $"{n / (double)(1 << 20):F1} MB",
        >= 1L << 10 => $"{n / (double)(1 << 10):F1} KB",
        _           => $"{n} B",
    };
}
