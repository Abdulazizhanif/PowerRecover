using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace PowerRecover.App;

/// <summary>
/// Smart file filter — sits between the raw _rows collection and the
/// DataGrid. Lets the user filter by:
///   • File type (extension)
///   • Status (all / deleted only / ok only)
///   • Size range (min / max bytes)
///   • Confidence score (min %)
///   • Free text search on filename
///
/// Uses WPF's CollectionViewSource so filtering/sorting stays in the
/// UI layer — the underlying _rows collection is never modified.
///
/// Usage in MainWindow:
///   var filter = new FileFilter(_rows);
///   ResultsGrid.ItemsSource = filter.View;
///   // then bind filter controls:
///   filter.SetStatusFilter(FilterStatus.DeletedOnly);
///   filter.SetMinConfidence(70);
///   filter.SetSearchText("vacation");
/// </summary>
public sealed class FileFilter
{
    private readonly ICollectionView _view;

    // Current filter state
    private FilterStatus _status     = FilterStatus.All;
    private string       _searchText = "";
    private long         _minSize    = 0;
    private long         _maxSize    = long.MaxValue;
    private int          _minConf    = 0;
    private string       _extFilter  = "";

    public ICollectionView View => _view;

    public FileFilter(ObservableCollection<FileRow> source)
    {
        _view = CollectionViewSource.GetDefaultView(source);
        _view.Filter = FilterItem;
    }

    // ── Filter setters (each one refreshes the view) ──────────────────

    public void SetStatusFilter(FilterStatus status)
    {
        _status = status;
        _view.Refresh();
    }

    public void SetSearchText(string text)
    {
        _searchText = text?.Trim() ?? "";
        _view.Refresh();
    }

    public void SetSizeRange(long minBytes, long maxBytes)
    {
        _minSize = minBytes;
        _maxSize = maxBytes;
        _view.Refresh();
    }

    public void SetMinConfidence(int minPercent)
    {
        _minConf = minPercent;
        _view.Refresh();
    }

    public void SetExtFilter(string ext)
    {
        _extFilter = ext?.Trim().TrimStart('.').ToLowerInvariant() ?? "";
        _view.Refresh();
    }

    public void ClearAll()
    {
        _status     = FilterStatus.All;
        _searchText = "";
        _minSize    = 0;
        _maxSize    = long.MaxValue;
        _minConf    = 0;
        _extFilter  = "";
        _view.Refresh();
    }

    // ── Sort helpers ──────────────────────────────────────────────────

    public void SortBy(string propertyName, bool descending = false)
    {
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(
            propertyName,
            descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    // ── Stats on visible items ────────────────────────────────────────

    public (int count, long totalBytes, int deleted) GetStats()
    {
        int  count      = 0;
        long totalBytes = 0;
        int  deleted    = 0;
        foreach (FileRow row in _view)
        {
            count++;
            totalBytes += row.SizeBytes;
            if (row.Status == "Deleted") deleted++;
        }
        return (count, totalBytes, deleted);
    }

    // ── Filter predicate ──────────────────────────────────────────────

    private bool FilterItem(object obj)
    {
        if (obj is not FileRow row) return false;

        // Status filter
        if (_status == FilterStatus.DeletedOnly && row.Status != "Deleted") return false;
        if (_status == FilterStatus.OkOnly      && row.Status != "Found")   return false;

        // Extension filter
        if (!string.IsNullOrEmpty(_extFilter) &&
            !row.Ext.Equals(_extFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        // Size filter
        if (row.SizeBytes < _minSize || row.SizeBytes > _maxSize) return false;

        // Confidence filter
        if (row.ConfidenceValue >= 0 && row.ConfidenceValue < _minConf) return false;

        // Text search (filename or path)
        if (!string.IsNullOrEmpty(_searchText))
        {
            bool nameMatch = row.Name.Contains(_searchText,
                                StringComparison.OrdinalIgnoreCase);
            bool pathMatch = row.FolderPath.Contains(_searchText,
                                StringComparison.OrdinalIgnoreCase);
            if (!nameMatch && !pathMatch) return false;
        }

        return true;
    }
}

public enum FilterStatus { All, DeletedOnly, OkOnly }
