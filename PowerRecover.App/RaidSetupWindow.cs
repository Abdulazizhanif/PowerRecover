using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PowerRecover.Engine;

namespace PowerRecover.App;

/// <summary>
/// RAID Setup dialog — lets the user configure a RAID array before scanning.
/// Shows member slots, RAID level picker, stripe size, health summary.
/// Returns a configured RaidReconstructor ready to build a stream.
/// </summary>
public sealed class RaidSetupWindow : Window
{
    public RaidReconstructor? Result { get; private set; }

    private readonly List<TextBox>  _memberBoxes = new();
    private readonly ComboBox       _levelCombo;
    private readonly ComboBox       _stripeSizeCombo;
    private readonly TextBlock      _healthLabel;
    private readonly TextBox        _missingBox;
    private int                     _memberCount = 3;

    public RaidSetupWindow()
    {
        Title  = "RAID Array Setup";
        Width  = 520;
        Height = 580;
        ResizeMode           = ResizeMode.NoResize;
        WindowStartupLocation= WindowStartupLocation.CenterOwner;
        Background           = new SolidColorBrush(Color.FromRgb(0x08, 0x0B, 0x0F));

        var root = new StackPanel { Margin = new Thickness(20) };

        // Title
        root.Children.Add(MakeLabel("RAID RECONSTRUCTION SETUP",
                                    16, isBold: true,
                                    color: Color.FromRgb(0x00, 0xD4, 0xB8)));
        root.Children.Add(MakeLabel("Select member disks and RAID configuration.",
                                    11, color: Color.FromRgb(0x7A, 0x88, 0x99)));

        // RAID Level
        root.Children.Add(MakeLabel("RAID Level", 11));
        _levelCombo = new ComboBox
        {
            Margin     = new Thickness(0, 4, 0, 12),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
        };
        _levelCombo.Items.Add("RAID 0 — Striped (fastest, no redundancy)");
        _levelCombo.Items.Add("RAID 1 — Mirrored (2 identical copies)");
        _levelCombo.Items.Add("RAID 5 — Parity (N-1 data + 1 parity, 1 disk fault tolerance)");
        _levelCombo.Items.Add("RAID 6 — Dual parity (N-2 data + 2 parity, 2 disk fault tolerance)");
        _levelCombo.SelectedIndex = 2;
        root.Children.Add(_levelCombo);

        // Stripe size
        root.Children.Add(MakeLabel("Stripe Size", 11));
        _stripeSizeCombo = new ComboBox
        {
            Margin     = new Thickness(0, 4, 0, 12),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
        };
        foreach (int kb in new[] { 16, 32, 64, 128, 256, 512, 1024 })
            _stripeSizeCombo.Items.Add($"{kb} KB");
        _stripeSizeCombo.SelectedIndex = 2; // 64 KB default
        root.Children.Add(_stripeSizeCombo);

        // Member count
        var countPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 8),
        };
        countPanel.Children.Add(MakeLabel("Number of members:", 11));
        var countBox = new TextBox
        {
            Text       = "3",
            Width      = 40,
            Margin     = new Thickness(8, 0, 8, 0),
            FontFamily = new FontFamily("Consolas"),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xB8)),
        };
        countPanel.Children.Add(countBox);
        countPanel.Children.Add(MakeLabel("(2–8)", 10,
                                           color: Color.FromRgb(0x4A, 0x56, 0x68)));
        root.Children.Add(countPanel);

        // Missing member
        var missingPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 12),
        };
        missingPanel.Children.Add(MakeLabel("Missing member index (blank if all present):", 11));
        _missingBox = new TextBox
        {
            Width      = 40,
            Margin     = new Thickness(8, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35)),
        };
        missingPanel.Children.Add(_missingBox);
        root.Children.Add(missingPanel);

        // Member disk slots (dynamic)
        root.Children.Add(MakeLabel("Member Disks", 11));
        var membersPanel = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 12),
        };
        root.Children.Add(membersPanel);

        // Wire SelectionChanged now that membersPanel exists
        _levelCombo.SelectionChanged += (s, e) => UpdateMemberSlots();
        countBox.TextChanged += (s, e) =>
        {
            if (int.TryParse(countBox.Text, out int n) && n >= 2 && n <= 8)
            {
                _memberCount = n;
                UpdateMemberSlots();
            }
        };

        // Health summary
        _healthLabel = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
            Margin     = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(_healthLabel);

        // Buttons
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var okBtn = MakeButton("Reconstruct & Scan");
        okBtn.Background = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xB8));
        okBtn.Foreground = new SolidColorBrush(Colors.Black);
        okBtn.Click += OnReconstruct;
        var cancelBtn = MakeButton("Cancel");
        cancelBtn.Click += (s, e) => Close();
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        root.Children.Add(btnPanel);

        Content = new ScrollViewer { Content = root };

        // Initial slots
        void UpdateMemberSlots()
        {
            membersPanel.Children.Clear();
            _memberBoxes.Clear();
            for (int i = 0; i < _memberCount; i++)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 3, 0, 3),
                };
                row.Children.Add(new TextBlock
                {
                    Text       = $"Disk {i}:",
                    Width      = 50,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x88, 0x99)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var box = new TextBox
                {
                    Width      = 280,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 11,
                    Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
                    BorderBrush= new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30)),
                    Margin     = new Thickness(4, 0, 4, 0),
                };
                var browse = MakeButton("…");
                int captured = i;
                browse.Click += (s, e) =>
                {
                    var dlg = new OpenFileDialog
                    {
                        Title  = $"Select member disk {captured}",
                        Filter = "Disk images|*.img;*.dd;*.raw;*.bin|All files|*.*",
                    };
                    if (dlg.ShowDialog() == true) box.Text = dlg.FileName;
                };
                row.Children.Add(box);
                row.Children.Add(browse);
                _memberBoxes.Add(box);
                membersPanel.Children.Add(row);
            }
        }

        UpdateMemberSlots();
    }

    private void OnReconstruct(object s, RoutedEventArgs e)
    {
        var paths = _memberBoxes.Select(b =>
            string.IsNullOrWhiteSpace(b.Text) ? null : b.Text.Trim())
            .ToArray();

        int? missing = null;
        if (!string.IsNullOrWhiteSpace(_missingBox.Text) &&
            int.TryParse(_missingBox.Text, out int m))
            missing = m;

        int stripeSizeKb = (_stripeSizeCombo.SelectedIndex + 1);
        int stripeBytes  = new[] { 16, 32, 64, 128, 256, 512, 1024 }
            [_stripeSizeCombo.SelectedIndex] * 1024;

        RaidLevel level = _levelCombo.SelectedIndex switch
        {
            0 => RaidLevel.Raid0,
            1 => RaidLevel.Raid1,
            2 => RaidLevel.Raid5,
            3 => RaidLevel.Raid6,
            _ => RaidLevel.Raid5,
        };

        try
        {
            Result = new RaidReconstructor(level, paths!, stripeBytes, missing);
            _healthLabel.Text = Result.GetHealthSummary();
            _healthLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _healthLabel.Text       = $"Error: {ex.Message}";
            _healthLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
    }

    // ── UI helpers ────────────────────────────────────────────────────

    private static TextBlock MakeLabel(string text, double size,
                                        bool isBold = false,
                                        Color? color = null) => new()
    {
        Text       = text,
        FontSize   = size,
        FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
        FontFamily = new FontFamily("Consolas"),
        Foreground = new SolidColorBrush(
            color ?? Color.FromRgb(0xC8, 0xD0, 0xDB)),
        Margin     = new Thickness(0, 4, 0, 2),
    };

    private static Button MakeButton(string text) => new()
    {
        Content    = text,
        Padding    = new Thickness(12, 6, 12, 6),
        Margin     = new Thickness(0, 0, 8, 0),
        FontFamily = new FontFamily("Consolas"),
        FontSize   = 12,
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x18)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDB)),
        BorderBrush= new SolidColorBrush(Color.FromRgb(0x1C, 0x23, 0x30)),
    };
}
