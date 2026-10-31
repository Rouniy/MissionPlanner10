using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Utilities;
using MissionPlanner.Services;
using MissionPlanner.Services.Mcp;

namespace MissionPlanner.ViewModels;

public partial class LogTypeNode : ObservableObject {
  public string Type { get; }
  public ObservableCollection<LogFieldNode> Fields { get; } = new();
  public LogTypeNode(string type) => Type = type;
}

public partial class LogFieldNode : ObservableObject {
  public string Type { get; }
  public string Field { get; }
  public string Display => Field;
  public LogFieldNode(string type, string field) {
    Type = type;
    Field = field;
  }
}

public partial class LogBrowseViewModel : ViewModelBase {
  private readonly Dictionary<string, string[]> _formats = new(StringComparer.OrdinalIgnoreCase);

  [ObservableProperty]
  private string _info = "Open a .tlog or .bin dataflash log.";

  [ObservableProperty]
  private string _status = string.Empty;

  [ObservableProperty]
  private string? _currentPath;

  [ObservableProperty]
  private string? _selectedType;

  [ObservableProperty]
  private string? _selectedField;

  [ObservableProperty]
  private string _fieldExpression = string.Empty;

  [ObservableProperty]
  private GraphPreset? _selectedPreset;

  [ObservableProperty]
  private bool _busy;

  public ObservableCollection<string> MessageTypes { get; } = new();
  public ObservableCollection<string> Fields { get; } = new();
  public ObservableCollection<LogTypeNode> Tree { get; } = new();
  public ObservableCollection<GraphPreset> Presets { get; } = new();

  public IReadOnlyList<(double lat, double lng)> Track { get; private set; } =
      Array.Empty<(double, double)>();

  public IReadOnlyList<(double time, double lat, double lng)> TimedTrack { get; private set; } =
      Array.Empty<(double, double, double)>();

  public event Action? TrackChanged;

  public (double lat, double lng)? NearestTrackSample(double timeSec) {
    var tt = TimedTrack;
    if (tt.Count == 0) {
      return null;
    }
    double best = double.MaxValue;
    (double lat, double lng) found = default;
    foreach (var p in tt) {
      var d = Math.Abs(p.time - timeSec);
      if (d < best) {
        best = d;
        found = (p.lat, p.lng);
      }
    }
    return found;
  }

  public LogBrowseViewModel() {
    FlightModeNames.Initialize();
    foreach (var p in LoadPresets()) {
      Presets.Add(p);
    }
  }

  partial void OnSelectedTypeChanged(string? value) {
    Fields.Clear();
    if (value != null && _formats.TryGetValue(value, out var fields)) {
      foreach (var f in fields) {
        Fields.Add(f);
      }
    }
    SelectedField = Fields.FirstOrDefault();
  }

  internal long LoadRevision { get; private set; }

  public Task LoadFileAsync(string path) => LoadFileAsync(path, CancellationToken.None);

  public async Task LoadFileAsync(string path, CancellationToken ct) {
    if (Busy) { throw new InvalidOperationException("A log is already loading."); }
    ct.ThrowIfCancellationRequested();
    LoadRevision++;
    CurrentPath = path;
    Busy = true;
    Status = "Parsing log…";
    try {
      var (summary, formats, types, track, timedTrack) = await Task.Run(() => Parse(path, ct), ct);
      ct.ThrowIfCancellationRequested();
      _formats.Clear();
      foreach (var kv in formats) {
        _formats[kv.Key] = kv.Value;
      }
      MessageTypes.Clear();
      Tree.Clear();
      foreach (var t in types) {
        MessageTypes.Add(t);
        var node = new LogTypeNode(t);
        foreach (var f in formats[t]) {
          node.Fields.Add(new LogFieldNode(t, f));
        }
        Tree.Add(node);
      }
      Track = track;
      TimedTrack = timedTrack;
      Info = summary;
      SelectedType = MessageTypes.FirstOrDefault();
      Status = $"Loaded {types.Count} message types.";
      TrackChanged?.Invoke();
    } catch (OperationCanceledException) {
      CurrentPath = null; throw;
    } catch (Exception ex) {
      CurrentPath = null; _formats.Clear(); MessageTypes.Clear(); Tree.Clear(); Fields.Clear();
      Track = []; TimedTrack = []; TrackChanged?.Invoke();
      Info = $"Failed to read log:\n{ex.Message}";
      Status = "Parse failed.";
    } finally {
      Busy = false;
    }
  }

  public (IReadOnlyList<double> xs, IReadOnlyList<double> ys)? ReadCurve(string type, string field) {
    if (CurrentPath == null) {
      return null;
    }
    var series = McpTelemetryLog.IsTlog(CurrentPath) ? McpTelemetryLog.Series(CurrentPath, type, field)
        : DataFlashLog.ReadField(CurrentPath, type, field);
    if (series.Count == 0) {
      return null;
    }
    return (series.Select(s => s.time).ToList(), series.Select(s => s.value).ToList());
  }

  public static bool IsExpression(string s) => s.IndexOfAny(new[] { '(', ')', '+', '-', '*', '/' }) >= 0;

  public (IReadOnlyList<double> xs, IReadOnlyList<double> ys)? ReadExpressionCurve(string expr) {
    if (CurrentPath == null) {
      return null;
    }
    if (McpTelemetryLog.IsTlog(CurrentPath)) { throw new InvalidOperationException("For telemetry logs select a message/source and numeric field directly. DataFlash expressions require BIN/LOG files."); }
    var points = DataFlashExpressionEvaluator.Evaluate(CurrentPath, expr);
    return (points.Select(point => point.TimeSeconds).ToArray(),
        points.Select(point => point.Value).ToArray());
  }

  public IReadOnlyList<GraphCurve> ResolvePresetCurves(GraphPreset preset) {
    return ResolvePresetAlternatives(preset).FirstOrDefault() ?? Array.Empty<GraphCurve>();
  }

  public IReadOnlyList<IReadOnlyList<GraphCurve>> ResolvePresetAlternatives(GraphPreset preset) {
    if (CurrentPath != null && McpTelemetryLog.IsTlog(CurrentPath)) { return []; }
    return preset.Alternatives
        .Select(alternative => (IReadOnlyList<GraphCurve>)alternative.Curves
            .Where(curve => DataFlashExpressionEvaluator.CanEvaluate(curve.Expression, _formats))
            .ToArray())
        .Where(curves => curves.Count > 0)
        .ToArray();
  }

  public static double? EvalExpression(string expr, IReadOnlyList<string> refs,
      IReadOnlyDictionary<string, double> values) {
    string e = expr;
    // Substitute longer references first so e.g. RCOU.C10 is not clobbered by RCOU.C1.
    foreach (var r in refs.OrderByDescending(x => x.Length)) {
      e = e.Replace(r,
          "(" + values[r].ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }
    try {
      using var dt = new System.Data.DataTable();
      var v = Convert.ToDouble(dt.Compute(e, null), System.Globalization.CultureInfo.InvariantCulture);
      return double.IsFinite(v) ? v : null;
    } catch {
      return null;
    }
  }

  public (string type, string field)? ResolveField() {
    var expr = FieldExpression?.Trim();
    if (!string.IsNullOrEmpty(expr) && expr.Contains('.')) {
      var parts = expr.Split('.', 2);
      var t = parts[0].Trim();
      var f = parts[1].Trim();
      if (t.Length > 0 && f.Length > 0) {
        return (t, f);
      }
    }
    if (!string.IsNullOrEmpty(SelectedType) && !string.IsNullOrEmpty(SelectedField)) {
      return (SelectedType!, SelectedField!);
    }
    return null;
  }

  public IReadOnlyList<(double x, string label)> ReadOverlay(string type, string labelField) {
    if (CurrentPath == null || !_formats.ContainsKey(type)) {
      return Array.Empty<(double, string)>();
    }
    var answer = new List<(double, string)>();
    if (McpTelemetryLog.IsTlog(CurrentPath)) {
      foreach (var row in McpTelemetryLog.Select(CurrentPath, type, 0, 1e12, null, default)) {
        string value = McpTelemetryLog.Text(McpTelemetryLog.Fields(row.Packet).FirstOrDefault(f => f.Name == labelField)?.GetValue(row.Packet.data));
        if (value.Length > 0) { answer.Add((row.TimeSeconds, $"{row.Key} {value}")); }
      }
      return answer;
    }
    using var log = new DFLogBuffer(CurrentPath);
    foreach (var item in log.GetEnumeratorType(type)) {
      string? value = item[labelField];
      if (string.IsNullOrWhiteSpace(value)
          && string.Equals(type, "MODE", StringComparison.OrdinalIgnoreCase)) {
        value = item["ModeNum"];
      }
      if (!string.IsNullOrWhiteSpace(value)) {
        answer.Add((item.timems / 1000.0, $"{type} {value.Trim()}"));
      }
    }
    return answer;
  }

  public IReadOnlyList<DataFlashParameter> ReadParameters() => ReadParameterHistory().FinalValues;

  public DataFlashParameterHistory ReadParameterHistory() {
    if (CurrentPath == null) { return new([], []); }
    if (!McpTelemetryLog.IsTlog(CurrentPath)) { return DataFlashLog.ReadParameterHistory(CurrentPath); }
    string? instance = SelectedType?.Split('[').LastOrDefault()?.TrimEnd(']');
    var changes = McpTelemetryLog.ParameterHistory(CurrentPath).Where(p => p.Instance == instance && p.Value.HasValue)
        .Select(p => new DataFlashParameterChange(p.TimeSeconds, p.Name, p.Value!.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "")).ToArray();
    return new(changes, changes.GroupBy(p => p.Name).Select(g => new DataFlashParameter(g.Key, g.Last().Value, "")).ToArray());
  }

  public IReadOnlyList<DataFlashMessage> ReadMessages() => CurrentPath == null ? [] : McpTelemetryLog.IsTlog(CurrentPath)
      ? McpTelemetryLog.Select(CurrentPath, "STATUSTEXT", 0, 1e12, null, default).Select(row =>
          new DataFlashMessage(row.TimeSeconds, $"[{row.Instance}] " + McpTelemetryLog.Text(row.Packet.ToStructure<MAVLink.mavlink_statustext_t>().text))).ToArray()
      : DataFlashLog.ReadMessages(CurrentPath);

  public (IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows) ReadRows(
      string type, int maxRows = 5000) {
    if (CurrentPath == null || !_formats.TryGetValue(type, out var fields) || fields.Length == 0) {
      return (Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
    }
    var columns = new[] { "time" }.Concat(fields).ToList();
    var rows = new List<IReadOnlyList<string>>();
    maxRows = Math.Clamp(maxRows, 0, 5000);
    if (McpTelemetryLog.IsTlog(CurrentPath)) {
      foreach (var item in McpTelemetryLog.Select(CurrentPath, type, 0, 1e12, null, default).Take(maxRows)) {
        var values = McpTelemetryLog.Fields(item.Packet).ToDictionary(f => f.Name, f => McpTelemetryLog.Text(f.GetValue(item.Packet.data)));
        rows.Add(new[] { item.TimeSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(fields.Select(f => values.GetValueOrDefault(f, ""))).ToArray());
      }
    } else {
      // one record per row, the decoder's own display strings: a text column
      // (PARM.Name, MSG.Message, MODE.Mode) has no numeric series, and the
      // preview stops after maxRows records instead of decoding the log
      using var log = new DFLogBuffer(CurrentPath);
      foreach (var item in log.GetEnumeratorType(type).Take(maxRows)) {
        rows.Add(new[] { (item.timems / 1000).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(fields.Select(f => item[f] ?? "")).ToArray());
      }
    }
    return (columns, rows);
  }

  private static (string summary, Dictionary<string, string[]> formats, List<string> types,
      IReadOnlyList<(double lat, double lng)> track,
      IReadOnlyList<(double time, double lat, double lng)> timedTrack) Parse(string path, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();
    if (McpTelemetryLog.IsTlog(path)) { return ParseTelemetry(path, ct); }
    var formats = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    List<string> types;

    using (var log = new DFLogBuffer(path)) {
      types = log.SeenMessageTypes
          .Where(t => !string.IsNullOrEmpty(t))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
          .ToList();

      foreach (var t in types) {
        formats[t] = log.dflog.logformat.TryGetValue(t, out var lbl) && lbl.FieldNames != null
            ? lbl.FieldNames.ToArray()
            : Array.Empty<string>();
      }
    }

    ct.ThrowIfCancellationRequested();
    var fullTrack = DataFlashLog.ReadTrack(path);
    ct.ThrowIfCancellationRequested();
    var track = fullTrack.Select(p => (p.lat, p.lng)).ToList();
    var timedTrack = ReadTimedTrack(path, types);
    ct.ThrowIfCancellationRequested();
    var summary = BuildSummary(path, types.Count, fullTrack);
    return (summary, formats, types, track, timedTrack);
  }

  private static (string summary, Dictionary<string, string[]> formats, List<string> types,
      IReadOnlyList<(double lat, double lng)> track, IReadOnlyList<(double time, double lat, double lng)> timedTrack) ParseTelemetry(string path, CancellationToken ct) {
    var formats = new Dictionary<string, string[]>();
    var timed = new List<(double time, double lat, double lng)>();
    string? trackSource = null; int count = 0;
    foreach (var row in McpTelemetryLog.Read(path, ct)) {
      count++;
      if (!formats.ContainsKey(row.Key)) {
        if (formats.Count >= 4096) { throw new InvalidDataException("Too many telemetry message/source combinations."); }
        formats[row.Key] = McpTelemetryLog.Fields(row.Packet).Select(f => f.Name).ToArray();
      }
      if (row.Packet.data is MAVLink.mavlink_global_position_int_t p) {
        double lat = p.lat / 1e7, lng = p.lon / 1e7;
        if (lat is < -90 or > 90 || lng is < -180 or > 180 || (lat == 0 && lng == 0)) { continue; }
        trackSource ??= row.Instance;
        if (trackSource == row.Instance) {
          if (timed.Count >= 2_000_000) { throw new InvalidDataException("Map track exceeds two million points."); }
          timed.Add((row.TimeSeconds, lat, lng));
        }
      }
    }
    if (count == 0) { throw new InvalidDataException("No MAVLink packets in this telemetry log."); }
    return ($"{Path.GetFileName(path)}\n{count} telemetry packets; {formats.Count} message/source combinations. Map source: {trackSource ?? "none"}.\n"
        + "Time: seconds from first receipt. Graphs show MAVLink wire values; select TYPE[system:component] to keep vehicles separate.",
        formats, formats.Keys.Order(StringComparer.Ordinal).ToList(), timed.Select(p => (p.lat, p.lng)).ToArray(), timed);
  }

  private static IReadOnlyList<(double time, double lat, double lng)> ReadTimedTrack(
      string path, IReadOnlyCollection<string> types) {
    if (!types.Contains("GPS", StringComparer.OrdinalIgnoreCase)) {
      return Array.Empty<(double, double, double)>();
    }
    var series = DataFlashLog.ReadFields(path, "GPS", new[] { "Lat", "Lng" });
    var lats = series[0];
    var lngs = series[1];
    int n = Math.Min(lats.Count, lngs.Count);
    var timed = new List<(double, double, double)>(n);
    for (int i = 0; i < n; i++) {
      var lat = lats[i].value;
      var lng = lngs[i].value;
      if ((lat == 0 && lng == 0) || lat is < -90 or > 90 || lng is < -180 or > 180) {
        continue;
      }
      timed.Add((lats[i].time, lat, lng));
    }
    return timed;
  }

  private static string BuildSummary(
      string path, int typeCount,
      IReadOnlyList<(double lat, double lng, double alt, DateTime time)> track) {
    var fi = new FileInfo(path);
    string trackInfo;
    if (track.Count > 0) {
      var duration = track[^1].time - track[0].time;
      var maxAlt = track.Max(p => p.alt);
      trackInfo = $"GPS: {track.Count} pts, {duration:hh\\:mm\\:ss}, max alt {maxAlt:0.#} m";
    } else {
      trackInfo = "GPS: no 3D-fix track";
    }
    return $"{fi.Name}\n{fi.Length / 1024.0 / 1024.0:0.0} MB\n{typeCount} msg types\n{trackInfo}";
  }

  private static List<GraphPreset> LoadPresets() {
    foreach (var dir in PresetDirs()) {
      var list = GraphPresets.LoadDirectory(dir);
      if (list.Count > 0) {
        return list;
      }
    }
    return new List<GraphPreset>();
  }

  private static IEnumerable<string> PresetDirs() {
    var baseDir = AppContext.BaseDirectory;
    yield return Path.Combine(baseDir, "graphs");
    var dir = new DirectoryInfo(baseDir);
    for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent) {
      yield return Path.Combine(dir.FullName, "external", "MissionPlanner", "graphs");
    }
  }
}
