using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using Core.Geometry;
using KMLib;
using KMLib.Feature;
using MissionPlanner.Log;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services;

public sealed record DataFlashParameter(string Name, string Value, string DefaultValue);

public sealed record DataFlashParameterChange(
    double TimeSeconds, string Name, string Value, string DefaultValue) {
  public string TimeText => TimeSeconds.ToString("0.000", CultureInfo.InvariantCulture);
}

public sealed record DataFlashParameterHistory(
    IReadOnlyList<DataFlashParameterChange> Changes,
    IReadOnlyList<DataFlashParameter> FinalValues);

public sealed record DataFlashMessage(double TimeSeconds, string Message) {
  public string TimeText => TimeSeconds.ToString("0.000", CultureInfo.InvariantCulture);
}

public class DataFlashLog {
  public static IReadOnlyList<(double lat, double lng, double alt, DateTime time)> ReadTrack(string binPath) {
    var track = new List<(double lat, double lng, double alt, DateTime time)>();

    if (Mcp.McpTelemetryLog.IsTlog(binPath)) {
      string? source = null;
      foreach (var row in Mcp.McpTelemetryLog.Read(binPath)) {
        if (row.Packet.data is not MAVLink.mavlink_global_position_int_t p) { continue; }
        double lat = p.lat / 1e7, lng = p.lon / 1e7;
        if (lat is < -90 or > 90 || lng is < -180 or > 180 || (lat == 0 && lng == 0)) { continue; }
        source ??= row.Instance;
        if (row.Instance == source) { track.Add((lat, lng, p.alt / 1000.0, row.Packet.rxtime)); }
      }
      return track;
    }

    using var log = new DFLogBuffer(binPath);

    foreach (var item in log.GetEnumeratorType(new[] { "GPS" })) {
      if (!int.TryParse(item["Status"], out var status) || status < 3) {
        continue;
      }

      if (!double.TryParse(item["Lat"], NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) ||
          !double.TryParse(item["Lng"], NumberStyles.Any, CultureInfo.InvariantCulture, out var lng) ||
          !double.TryParse(item["Alt"], NumberStyles.Any, CultureInfo.InvariantCulture, out var alt)) {
        continue;
      }

      if (lat is < -90 or > 90 || lng is < -180 or > 180) {
        continue;
      }

      if (lat == 0 && lng == 0) {
        continue;
      }

      track.Add((lat, lng, alt, item.time));
    }

    return track;
  }

  public static IReadOnlyList<(double time, double value)> ReadField(string binPath, string msgType, string field) =>
      ReadFields(binPath, msgType, new[] { field })[0];

  /// <summary>
  /// Reads several fields of one message type with a single pass over the
  /// log, natively or managed. Native values are the raw decoded values; the
  /// managed path parses the decoder's display strings, which round floats
  /// to 7 significant digits. 'M' (flight mode) fields keep every requested
  /// field on the managed path: the display string is resolver-dependent
  /// text there, a plain number natively, and a graph must show the same
  /// thing either way.
  /// </summary>
  public static IReadOnlyList<IReadOnlyList<(double time, double value)>> ReadFields(
      string binPath, string msgType, IReadOnlyList<string> fields) {
    if (Mcp.McpTelemetryLog.IsTlog(binPath)) {
      return fields.Select(field => Mcp.McpTelemetryLog.Series(binPath, msgType, field)).ToList();
    }
    using var log = new DFLogBuffer(binPath);

    if (TimeField(log, msgType) is { } time
        && fields.All(f => log.GetFieldFormatChar(msgType, f) != 'M')) {
      // the time field may itself be one of the requested fields - never
      // query a duplicate column name
      string[] query = fields.Contains(time.field)
          ? fields.ToArray()
          : fields.Append(time.field).ToArray();
      if (log.TryGetColumnsNative(msgType, query, out _, out double[][] columns)) {
        double[] raw = columns[Array.IndexOf(query, time.field)];
        double[] seconds = raw.Select(v => v / time.divisorToMs / 1000.0).ToArray();
        return fields.Select((_, index) => (IReadOnlyList<(double, double)>)seconds
            .Select((s, row) => (s, columns[index][row])).ToList()).ToList();
      }
    }

    // managed fallback: one enumeration shared by every field, each value
    // parsed from its display string exactly as before
    var series = fields.Select(_ => new List<(double time, double value)>()).ToList();
    foreach (var item in log.GetEnumeratorType(new[] { msgType })) {
      double seconds = item.timems / 1000.0;
      for (int f = 0; f < fields.Count; f++) {
        var raw = item[fields[f]];
        if (raw != null
            && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) {
          series[f].Add((seconds, value));
        }
      }
    }

    return series;
  }

  /// <summary>
  /// The time field DFLog.DFItem.timems reads for <paramref name="msgType"/>
  /// (TimeMS, then TimeUS, then T) and the divisor that turns it into
  /// milliseconds, or null when the type has none - callers should then keep
  /// the managed path and its timems-is-zero behavior. Seconds must be
  /// computed as (value / divisorToMs) / 1000.0 - the same two-step division
  /// the enumeration path performs - because a single multiplication by the
  /// combined reciprocal differs by 1 ULP.
  /// </summary>
  internal static (string field, double divisorToMs)? TimeField(DFLogBuffer log, string msgType) {
    if (log.dflog.FindMessageOffset(msgType, "TimeMS") >= 0) {
      return ("TimeMS", 1.0);
    }
    if (log.dflog.FindMessageOffset(msgType, "TimeUS") >= 0) {
      return ("TimeUS", 1000.0);
    }
    if (log.dflog.FindMessageOffset(msgType, "T") >= 0) {
      return ("T", 1.0);
    }
    return null;
  }

  public static void ExportKml(string binPath, string outKmlPath) {
    WriteKmlTrack(ReadTrack(binPath), outKmlPath);
  }

  public static void ExportGpx(string binPath, string outGpxPath) {
    WriteGpxTrack(ReadTrack(binPath), outGpxPath);
  }

  public static void ExportMatlab(string binPath, Action<string>? progress = null) {

    if (binPath.EndsWith(".tlog", StringComparison.OrdinalIgnoreCase)) {
      MatLab.tlog(binPath);
    } else {
      MatLab.ProcessLog(binPath, progress);
    }
  }

  public static void ConvertBinToLog(string binPath, string outTextLogPath) {
    if (Mcp.McpTelemetryLog.IsTlog(binPath)) { throw new ArgumentException("BIN-to-LOG conversion requires a DataFlash file. Use the telemetry CSV/text export for TLOG."); }
    BinaryLog.ConvertBin(binPath, outTextLogPath);
  }

  public static IReadOnlyList<DataFlashParameter> ReadParameters(string path) {
    return ReadParameterHistory(path).FinalValues;
  }

  public static DataFlashParameterHistory ReadParameterHistory(string path) {
    var changes = new List<DataFlashParameterChange>();
    var parameters = new Dictionary<string, DataFlashParameter>(StringComparer.OrdinalIgnoreCase);
    using var log = new DFLogBuffer(path);
    foreach (var item in log.GetEnumeratorType("PARM")) {
      string name = item["Name"]?.Trim() ?? "";
      string value = item["Value"]?.Trim() ?? "";
      if (name.Length == 0 || value.Length == 0) {
        continue;
      }
      string defaultValue = item["Default"]?.Trim() ?? "";
      changes.Add(new DataFlashParameterChange(
          item.timems / 1000.0, name, value, defaultValue));
      parameters[name] = new DataFlashParameter(name, value, defaultValue);
    }
    return new DataFlashParameterHistory(
        changes,
        parameters.Values.OrderBy(
            value => value.Name, StringComparer.OrdinalIgnoreCase).ToList());
  }

  public static IReadOnlyList<DataFlashMessage> ReadMessages(string path) {
    var messages = new List<DataFlashMessage>();
    using var log = new DFLogBuffer(path);
    foreach (var item in log.GetEnumeratorType("MSG")) {
      string text = item["Message"] ?? item["Msg"] ?? item["Text"] ?? "";
      text = text.Trim();
      if (text.Length > 0) {
        messages.Add(new DataFlashMessage(item.timems / 1000.0, text));
      }
    }
    return messages;
  }

  public static void ExportParameters(
      IEnumerable<DataFlashParameter> parameters, string outputPath) {
    using var writer = new System.IO.StreamWriter(outputPath);
    writer.WriteLine("# Parameters extracted from a DataFlash log");
    foreach (var parameter in parameters.OrderBy(
               value => value.Name, StringComparer.OrdinalIgnoreCase)) {
      writer.WriteLine($"{parameter.Name},{parameter.Value}");
    }
  }

  internal static void WriteKmlTrack(IReadOnlyList<(double lat, double lng, double alt, DateTime time)> track,
    string outKmlPath) {
    var kml = new KMLRoot();
    var folder = new Folder("Track");

    var line = new LineString { AltitudeMode = AltitudeMode.absolute, Extrude = true };
    var coords = new Coordinates();
    foreach (var (lat, lng, alt, _) in track) {
      coords.Add(new Point3D(lng, lat, alt));
    }

    line.coordinates = coords;

    var placemark = new Placemark { name = "Flight Path", LineString = line };
    folder.Add(placemark);

    kml.Document.Add(folder);
    kml.Save(outKmlPath);
  }

  internal static void WriteGpxTrack(IReadOnlyList<(double lat, double lng, double alt, DateTime time)> track,
    string outGpxPath) {
    var settings = new XmlWriterSettings { Indent = true };
    using var writer = XmlWriter.Create(outGpxPath, settings);

    writer.WriteStartDocument();
    writer.WriteStartElement("gpx");
    writer.WriteAttributeString("version", "1.1");
    writer.WriteAttributeString("creator", "Mission Planner");
    writer.WriteAttributeString("xmlns", "http://www.topografix.com/GPX/1/1");

    writer.WriteStartElement("trk");
    writer.WriteStartElement("trkseg");

    foreach (var (lat, lng, alt, time) in track) {
      writer.WriteStartElement("trkpt");
      writer.WriteAttributeString("lat", lat.ToString(CultureInfo.InvariantCulture));
      writer.WriteAttributeString("lon", lng.ToString(CultureInfo.InvariantCulture));
      writer.WriteElementString("ele", alt.ToString(CultureInfo.InvariantCulture));
      writer.WriteElementString("time",
        time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
      writer.WriteEndElement();
    }

    writer.WriteEndElement();
    writer.WriteEndElement();
    writer.WriteEndElement();
    writer.WriteEndDocument();
  }
}
