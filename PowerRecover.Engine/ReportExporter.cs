namespace PowerRecover.Engine;

/// <summary>
/// Generates a self-contained HTML scan report — no external dependencies,
/// one file, opens in any browser. Saved to the output folder as
/// PowerRecover_Report_YYYYMMDD_HHmmss.html
///
/// Includes:
///   • Summary stats (files found, total size, scan duration, bad sectors)
///   • Full file table (name, path, size, offset, method, status, confidence)
///   • S.M.A.R.T. health section (if available)
///   • Colour-coded rows (deleted = amber, ok = normal)
///   • Sortable columns (pure JS, no jQuery)
/// </summary>
public static class ReportExporter
{
    public static string Export(
        ScanSession session,
        string outputDir,
        SmartResult? smart = null)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = Path.Combine(outputDir,
                                   $"PowerRecover_Report_{timestamp}.html");

        string html = BuildHtml(session, smart);
        File.WriteAllText(path, html, System.Text.Encoding.UTF8);
        return path;
    }

    private static string BuildHtml(ScanSession session, SmartResult? smart)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append(@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>PowerRecover Report</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0}
  body{font-family:'Segoe UI',system-ui,sans-serif;background:#0A0D11;color:#C8D0DB;font-size:13px;line-height:1.5}
  a{color:#00D4B8;text-decoration:none}
  .header{background:#0F1318;border-bottom:1px solid #1C2330;padding:18px 32px;display:flex;align-items:center;gap:12px}
  .logo{font-size:20px;font-weight:700;letter-spacing:.02em}
  .logo span{color:#00D4B8}
  .meta{font-size:11px;color:#4A5568;margin-left:auto;text-align:right}
  .container{padding:24px 32px}
  .stats-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px;margin-bottom:24px}
  .stat-card{background:#0F1318;border:1px solid #1C2330;border-radius:8px;padding:14px 16px}
  .stat-val{font-size:24px;font-weight:600;color:#E2E8F0;font-family:'Consolas',monospace;margin-bottom:4px}
  .stat-val.accent{color:#00D4B8}
  .stat-val.warn{color:#FF6B35}
  .stat-val.crit{color:#EF4444}
  .stat-label{font-size:10px;color:#4A5568;text-transform:uppercase;letter-spacing:.06em}
  .smart-box{background:#0F1318;border:1px solid;border-radius:8px;padding:14px 16px;margin-bottom:24px}
  .smart-box.good{border-color:#166534}.smart-box.warn{border-color:#854D0E}.smart-box.crit{border-color:#7F1D1D}.smart-box.unknown{border-color:#1C2330}
  .smart-title{font-size:11px;letter-spacing:.08em;text-transform:uppercase;margin-bottom:6px}
  .smart-title.good{color:#4ADE80}.smart-title.warn{color:#FBB040}.smart-title.crit{color:#EF4444}.smart-title.unknown{color:#4A5568}
  .section-head{font-size:10px;letter-spacing:.1em;text-transform:uppercase;color:#4A5568;margin-bottom:10px;padding-bottom:6px;border-bottom:1px solid #1C2330}
  table{width:100%;border-collapse:collapse;font-family:'Consolas',monospace;font-size:11.5px}
  thead th{background:#0F1318;color:#4A5568;font-size:9px;letter-spacing:.08em;text-transform:uppercase;padding:8px 12px;text-align:left;border-bottom:1px solid #1C2330;cursor:pointer;user-select:none;white-space:nowrap}
  thead th:hover{color:#C8D0DB}
  tbody tr{border-bottom:1px solid #0F1318;transition:background .1s}
  tbody tr:hover{background:#141920}
  tbody tr.deleted{background:#1A110A}
  tbody tr.deleted:hover{background:#211508}
  td{padding:7px 12px;color:#7A8899}
  td.name{color:#E2E8F0;font-size:12px}
  td.offset{color:#3A4A5A}
  .badge{display:inline-block;padding:1px 6px;border-radius:3px;font-size:9px;font-weight:500}
  .badge-ntfs{background:#1E1B4B;color:#7B61FF;border:1px solid #312E81}
  .badge-carve{background:#042F2E;color:#00D4B8;border:1px solid #0F6E56}
  .badge-fat{background:#1C1A0A;color:#EF9F27;border:1px solid #633806}
  .badge-deleted{background:#1A0E00;color:#FF6B35}
  .badge-ok{background:#052E16;color:#4ADE80}
  .conf-bar{display:flex;align-items:center;gap:6px}
  .conf-track{width:48px;height:3px;background:#1C2330;border-radius:2px;overflow:hidden;flex-shrink:0}
  .conf-fill{height:100%;border-radius:2px}
  .footer{padding:18px 32px;border-top:1px solid #1C2330;font-size:10px;color:#3A4A5A;text-align:center}
</style>
</head>
<body>
");

        // Header
        sb.Append($@"<div class=""header"">
  <div class=""logo"">POWER<span>RECOVER</span></div>
  <div class=""meta"">
    Source: {Esc(session.Source)}<br>
    Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}<br>
    Session started: {session.StartedAt:yyyy-MM-dd HH:mm:ss}
  </div>
</div>
<div class=""container"">
");

        // Stats cards
        long totalBytes = session.TotalRecoveredBytes;
        sb.Append(@"<div class=""stats-grid"">");
        StatCard(sb, session.FilesFound.ToString("N0"),        "Files found",        "accent");
        StatCard(sb, session.DeletedCount.ToString("N0"),      "Deleted recovered",  "warn");
        StatCard(sb, FormatSize(totalBytes),                   "Total size",         "");
        StatCard(sb, session.BadSectors.ToString("N0"),        "Bad sectors",
                     session.BadSectors > 0 ? "crit" : "");
        StatCard(sb, session.ElapsedSinceStart.ToString(@"hh\:mm\:ss"), "Scan duration", "");
        StatCard(sb, session.CarveOffset > 0
                     ? FormatSize(session.CarveOffset) : "—", "Carve progress",    "");
        sb.Append("</div>\n");

        // SMART section
        if (smart != null && smart.Available)
        {
            string cls = smart.HealthStatus switch
            {
                HealthStatus.Good     => "good",
                HealthStatus.Warning  => "warn",
                HealthStatus.Critical => "crit",
                _                     => "unknown",
            };
            sb.Append($@"<div class=""smart-box {cls}"">
  <div class=""smart-title {cls}"">S.M.A.R.T. Drive Health</div>
  <div>{Esc(smart.HealthSummary)}</div>
  <div style=""margin-top:6px;color:#4A5568;font-size:10px"">
    Reallocated: {smart.ReallocatedSectors} &nbsp;|&nbsp;
    Pending: {smart.PendingSectors} &nbsp;|&nbsp;
    Uncorrectable: {smart.UncorrectableSectors} &nbsp;|&nbsp;
    Temp: {smart.TemperatureCelsius}°C &nbsp;|&nbsp;
    Power-on hours: {smart.PowerOnHours:N0}
  </div>
</div>
");
        }

        // File table
        sb.Append("<div class=\"section-head\">Recovered files</div>\n");
        sb.Append(@"<table id=""ftable"">
<thead>
<tr>
  <th onclick=""sort(0)"">Filename ↕</th>
  <th onclick=""sort(1)"">Path ↕</th>
  <th onclick=""sort(2)"">Type</th>
  <th onclick=""sort(3)"">Size ↕</th>
  <th onclick=""sort(4)"">Method</th>
  <th onclick=""sort(5)"">Status</th>
  <th onclick=""sort(6)"">Confidence ↕</th>
  <th onclick=""sort(7)"">Disk offset ↕</th>
</tr>
</thead>
<tbody>
");

        foreach (var f in session.Files)
        {
            string rowCls = f.Deleted ? " class=\"deleted\"" : "";
            string methodBadge = f.Method switch
            {
                "NTFS-MFT" => "badge-ntfs",
                "FAT32"    => "badge-fat",
                "exFAT"    => "badge-fat",
                _          => "badge-carve",
            };
            string statusBadge = f.Deleted ? "badge-deleted" : "badge-ok";
            string statusText  = f.Deleted ? "deleted" : "ok";

            // Build a fake RecoveredFile just for confidence scoring
            int conf = f.Method == "NTFS-MFT" ? (f.Deleted ? 80 : 100) : 70;
            string confColor = conf >= 85 ? "#4ADE80"
                             : conf >= 60 ? "#FBB040" : "#EF4444";

            string folder = f.SavedPath.Length > 0
                ? Path.GetDirectoryName(f.SavedPath) ?? ""
                : "";

            sb.Append($@"<tr{rowCls}>
  <td class=""name"">{Esc(f.Name)}</td>
  <td>{Esc(folder)}</td>
  <td>{Esc(f.Ext.ToUpperInvariant())}</td>
  <td data-val=""{f.Size}"">{FormatSize(f.Size)}</td>
  <td><span class=""badge {methodBadge}"">{Esc(f.Method)}</span></td>
  <td><span class=""badge {statusBadge}"">{statusText}</span></td>
  <td data-val=""{conf}"">
    <div class=""conf-bar"">
      <div class=""conf-track""><div class=""conf-fill"" style=""width:{conf}%;background:{confColor}""></div></div>
      <span style=""font-size:10px;color:{confColor}"">{conf}%</span>
    </div>
  </td>
  <td class=""offset"">0x{f.Offset:X12}</td>
</tr>
");
        }

        sb.Append("</tbody></table>\n");
        sb.Append("</div>\n"); // container

        // Footer
        sb.Append($@"<div class=""footer"">
  PowerRecover — Read-Only Forensic Recovery &nbsp;|&nbsp;
  Report generated {DateTime.Now:R}
</div>
");

        // Sort script
        sb.Append(@"<script>
let sortDir = 1;
let lastCol = -1;
function sort(col) {
  const tbody = document.querySelector('#ftable tbody');
  const rows = Array.from(tbody.rows);
  sortDir = col === lastCol ? -sortDir : 1;
  lastCol = col;
  rows.sort((a, b) => {
    const av = a.cells[col]?.dataset?.val ?? a.cells[col]?.innerText ?? '';
    const bv = b.cells[col]?.dataset?.val ?? b.cells[col]?.innerText ?? '';
    const an = parseFloat(av), bn = parseFloat(bv);
    if (!isNaN(an) && !isNaN(bn)) return (an - bn) * sortDir;
    return av.localeCompare(bv) * sortDir;
  });
  rows.forEach(r => tbody.appendChild(r));
}
</script>
</body></html>
");

        return sb.ToString();
    }

    private static void StatCard(System.Text.StringBuilder sb,
                                 string val, string label, string cls)
    {
        sb.Append($@"<div class=""stat-card"">
  <div class=""stat-val {cls}"">{val}</div>
  <div class=""stat-label"">{label}</div>
</div>
");
    }

    private static string FormatSize(long b)
    {
        if (b >= 1L << 40) return $"{b / (double)(1L << 40):F2} TB";
        if (b >= 1L << 30) return $"{b / (double)(1L << 30):F2} GB";
        if (b >= 1L << 20) return $"{b / (double)(1L << 20):F1} MB";
        if (b >= 1L << 10) return $"{b / (double)(1L << 10):F0} KB";
        return $"{b} B";
    }

    private static string Esc(string s)
        => System.Web.HttpUtility.HtmlEncode(s ?? "");
}
