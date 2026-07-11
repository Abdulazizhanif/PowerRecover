using System.Management;
using System.Windows;
using System.Windows.Controls;

namespace PowerRecover.App;

public partial class DriveDialog : Window
{
    public string SelectedPath { get; private set; } = "";

    public DriveDialog()
    {
        InitializeComponent();
        ManualBox.Text = @"\\.\PhysicalDrive1";
        EnumerateDrives();
    }

    private void EnumerateDrives()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Model, Size, MediaType FROM Win32_DiskDrive");
            foreach (ManagementObject d in searcher.Get())
            {
                string id = d["DeviceID"]?.ToString() ?? "";
                string model = d["Model"]?.ToString() ?? "Unknown";
                double gb = 0;
                if (d["Size"] != null &&
                    double.TryParse(d["Size"].ToString(), out double sz))
                    gb = sz / (1024.0 * 1024 * 1024);

                DriveList.Items.Add(new ListBoxItem
                {
                    Content = $"{id}    {model}    {gb:F1} GB",
                    Tag = id,
                });
            }
            if (DriveList.Items.Count == 0)
                DriveList.Items.Add(new ListBoxItem
                {
                    Content = "No drives found. Try running PowerRecover as Administrator.",
                    IsEnabled = false,
                });
        }
        catch (Exception ex)
        {
            DriveList.Items.Add(new ListBoxItem
            {
                Content = $"Could not list drives: {ex.Message}",
                IsEnabled = false,
            });
        }
    }

    private void OnSelect(object s, RoutedEventArgs e)
    {
        if (DriveList.SelectedItem is ListBoxItem item &&
            item.Tag is string id && !string.IsNullOrEmpty(id))
        {
            SelectedPath = id;
            DialogResult = true;
        }
        else MessageBox.Show("Choose a drive from the list, or type a drive path.");
    }

    private void OnManual(object s, RoutedEventArgs e)
    {
        string p = ManualBox.Text.Trim();
        if (!string.IsNullOrEmpty(p))
        {
            SelectedPath = p;
            DialogResult = true;
        }
    }
}
