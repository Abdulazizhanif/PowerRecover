// TimelineWindow.xaml.cs  —  PowerRecover.App
// Standalone window that shows the forensic event timeline.
// Open from MainWindow via: new TimelineWindow(timeline).Show();

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PowerRecover.Engine;

namespace PowerRecover.App
{
    public partial class TimelineWindow : Window
    {
        private readonly ForensicTimeline          _timeline;
        private readonly ObservableCollection<TimelineRow> _rows = new();
        private          ICollectionView           _view = null!;

        public TimelineWindow(ForensicTimeline timeline)
        {
            _timeline = timeline;
            InitializeComponent();
            BuildRows();
            Title = $"Forensic Timeline — {_rows.Count:N0} events";
        }

        // ────────────────────────────────────────────────────────────────
        // Build flat row list from timeline events
        // ────────────────────────────────────────────────────────────────

        private void BuildRows()
        {
            foreach (var ev in _timeline.Events)
                _rows.Add(new TimelineRow(ev));

            _view = CollectionViewSource.GetDefaultView(_rows);
            EventGrid.ItemsSource = _view;

            // Populate file name suggestions in the search box
            UpdateStats();
        }

        private void UpdateStats()
        {
            StatsText.Text =
                $"{_rows.Count:N0} events  |  " +
                $"{_timeline.CountByKind(TimelineEventKind.Created):N0} created  |  " +
                $"{_timeline.CountByKind(TimelineEventKind.Modified):N0} modified  |  " +
                $"{_timeline.CountByKind(TimelineEventKind.Deleted):N0} deleted";
        }

        // ────────────────────────────────────────────────────────────────
        // Filter controls
        // ────────────────────────────────────────────────────────────────

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
            ApplyFilter();

        private void OnKindFilterChanged(object sender, SelectionChangedEventArgs e) =>
            ApplyFilter();

        private void OnClearFilter(object sender, RoutedEventArgs e)
        {
            SearchBox.Text   = string.Empty;
            KindFilter.SelectedIndex = 0;
        }

        private void ApplyFilter()
        {
            var search = SearchBox.Text.Trim();
            var kind   = (KindFilter.SelectedItem as ComboBoxItem)?.Tag as string;

            _view.Filter = obj =>
            {
                if (obj is not TimelineRow row) return false;

                if (!string.IsNullOrEmpty(search) &&
                    !row.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !row.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!string.IsNullOrEmpty(kind) && kind != "All" &&
                    !row.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            };
        }

        // ────────────────────────────────────────────────────────────────
        // Export CSV
        // ────────────────────────────────────────────────────────────────

        private void OnExportCsv(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Export Timeline as CSV",
                Filter   = "CSV|*.csv",
                FileName = $"PowerRecover_Timeline_{DateTime.Now:yyyyMMdd}.csv"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                using var w = new System.IO.StreamWriter(dlg.FileName, false,
                                  System.Text.Encoding.UTF8);
                w.WriteLine("Timestamp,Kind,Source,FileName,Path,USN,Description");

                foreach (var row in _rows)
                    w.WriteLine(
                        $"\"{row.Timestamp:u}\"," +
                        $"\"{row.Kind}\"," +
                        $"\"{row.Source}\"," +
                        $"\"{EscCsv(row.FileName)}\"," +
                        $"\"{EscCsv(row.FullPath)}\"," +
                        $"{row.Usn}," +
                        $"\"{EscCsv(row.Description)}\"");

                MessageBox.Show($"Exported {_rows.Count:N0} events to:\n{dlg.FileName}",
                                "Export Complete", MessageBoxButton.OK,
                                MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscCsv(string s) => s.Replace("\"", "\"\"");
    }

    // ────────────────────────────────────────────────────────────────────────
    // View model row (DataGrid binding)
    // ────────────────────────────────────────────────────────────────────────

    public sealed class TimelineRow
    {
        public string Timestamp   { get; }
        public string Kind        { get; }
        public string Source      { get; }
        public string FileName    { get; }
        public string FullPath    { get; }
        public long   Usn         { get; }
        public string Description { get; }
        public string KindColor   { get; }   // for DataGrid row or cell coloring

        public TimelineRow(TimelineEvent ev)
        {
            Timestamp   = ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            Kind        = ev.KindText;
            Source      = ev.Source;
            FileName    = ev.FileName;
            FullPath    = ev.FullPath;
            Usn         = ev.Usn;
            Description = ev.Description;
            KindColor   = ev.Kind switch
            {
                TimelineEventKind.Created         => "#00E5CC",   // teal
                TimelineEventKind.Modified        => "#7B61FF",   // violet
                TimelineEventKind.Accessed        => "#4A9EFF",   // blue
                TimelineEventKind.MetadataChanged => "#9E9E9E",   // grey
                TimelineEventKind.Deleted         => "#FF6B35",   // amber-coral
                TimelineEventKind.Renamed         => "#FFD166",   // yellow
                TimelineEventKind.Overwritten     => "#FF4F4F",   // red
                TimelineEventKind.Truncated       => "#FF8800",   // orange
                _                                 => "#CCCCCC"
            };
        }
    }
}
