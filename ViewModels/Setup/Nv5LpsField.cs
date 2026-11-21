using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MissionPlanner.Services;
using Value = MissionPlanner.Services.Nv5ConfigurationClient.Value;

namespace MissionPlanner.ViewModels.Setup;

/// <summary>
/// A surveyed LPS field as exact native text, keyed by setting name: the survey every modem of the
/// field holds, the ranging radio settings they share, and the field document that carries both.
/// Values are kept as the settings table writes them, so that a value shown is the value saved.
/// </summary>
internal static class Nv5LpsField {
  internal const string DocumentFormat = "nv5-lps-field";
  internal const int MaximumBeacons = 4;

  /// <summary>Settings of one survey row, LPS_Bn_ followed by these.</summary>
  internal static readonly IReadOnlyList<string> RowSuffixes =
      ["ID", "ADDR", "E_MM", "N_MM", "U_MM", "CEE", "CEN", "CEU", "CNN", "CNU", "CUU", "BVAR"];

  /// <summary>Survey covariance of a row in the order [EE, EN, EU, NN, NU, UU].</summary>
  private static readonly string[] CovarianceSuffixes = ["CEE", "CEN", "CEU", "CNN", "CNU", "CUU"];

  /// <summary>Every setting of a field, in document order.</summary>
  internal static IReadOnlyList<string> Keys { get; } = BuildKeys();

  /// <summary>Whether a setting belongs to the survey, which decides the field generation, rather
  /// than to the ranging radio settings.</summary>
  internal static bool SurveyKey(string key) =>
      key != "LPS_FIELD_GEN" && !key.StartsWith("LPS_RNG_", StringComparison.Ordinal)
      && !key.StartsWith("LPS_RV_", StringComparison.Ordinal) && key != "LPS_RSP_SYM";

  /// <summary>The native type of a field setting.</summary>
  internal static byte TypeOf(string key) =>
      IsFloat(key) ? Nv5ConfigValueTypes.Float32
      : key.EndsWith("_MM", StringComparison.Ordinal) || key.EndsWith("_E7", StringComparison.Ordinal)
          ? Nv5ConfigValueTypes.Int32
          : Nv5ConfigValueTypes.UInt32;

  /// <summary>The field settings a modem holds, as its settings table shows them.</summary>
  internal static SortedDictionary<string, string> FromValues(
      IReadOnlyDictionary<string, Value> values) {
    var text = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (string key in Keys) {
      if (values.TryGetValue(key, out Value? value)) {
        text[key] = Nv5NativeSettingsEditor.Display(value);
      }
    }
    return text;
  }

  /// <summary>The same values in the text a modem reads back: decimal integers, and Float32 with
  /// nine significant digits. A value that does not parse is kept as it is, for
  /// <see cref="Validate"/> to name.</summary>
  internal static SortedDictionary<string, string> Canonical(
      IReadOnlyDictionary<string, string> values) {
    var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach ((string key, string text) in values) {
      result[key] = TryNative(key, text, out byte[] data)
          ? Nv5NativeSettingsEditor.Display(ValueOf(key, data))
          : text;
    }
    return result;
  }

  /// <summary>Rows past the beacon count as a modem stores them: zero.</summary>
  internal static SortedDictionary<string, string> Normalized(
      IReadOnlyDictionary<string, string> values) {
    var result = new SortedDictionary<string, string>(
        values.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
    long count = Number(values, "LPS_BCOUNT");
    for (long row = Math.Max(0, count) + 1; row <= MaximumBeacons; row++) {
      foreach (string suffix in RowSuffixes) {
        result[RowKey((int)row, suffix)] = "0";
      }
    }
    return result;
  }

  /// <summary>The field document: the survey and the ranging radio settings, without keys or
  /// network settings.</summary>
  internal static byte[] ToJson(IReadOnlyDictionary<string, string> values) {
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) {
      writer.WriteStartObject();
      writer.WriteString("format", DocumentFormat);
      writer.WriteNumber("version", 1);
      writer.WriteStartObject("values");
      foreach (string key in Keys) {
        writer.WriteString(key, values.TryGetValue(key, out string? text) ? text : "");
      }
      writer.WriteEndObject();
      writer.WriteEndObject();
    }
    stream.WriteByte((byte)'\n');
    return stream.ToArray();
  }

  /// <summary>Reads a whole field document strictly: every value present, none unknown, each the
  /// exact native text. Nothing is returned for a document that does not pass
  /// <see cref="Validate"/>.</summary>
  internal static bool FromJson(byte[] bytes, out SortedDictionary<string, string> values,
      out string error) {
    values = new SortedDictionary<string, string>(StringComparer.Ordinal);
    const string NotAField = "Not an NV5 LPS field document (version 1).";
    JsonDocument document;
    try {
      document = JsonDocument.Parse(bytes);
    } catch (JsonException) {
      error = NotAField;
      return false;
    }
    using (document) {
      JsonElement root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
          || !root.TryGetProperty("format", out JsonElement format)
          || format.ValueKind != JsonValueKind.String || format.GetString() != DocumentFormat
          || !root.TryGetProperty("version", out JsonElement version)
          || version.ValueKind != JsonValueKind.Number || version.GetDouble() != 1
          || !root.TryGetProperty("values", out JsonElement fields)
          || fields.ValueKind != JsonValueKind.Object) {
        error = NotAField;
        return false;
      }
      var candidate = new SortedDictionary<string, string>(StringComparer.Ordinal);
      foreach (JsonProperty field in fields.EnumerateObject()) {
        if (!Keys.Contains(field.Name) || candidate.ContainsKey(field.Name)) {
          error = "Field document has missing or unknown values.";
          return false;
        }
        if (field.Value.ValueKind != JsonValueKind.String) {
          error = "Field values must be exact native text.";
          return false;
        }
        candidate[field.Name] = field.Value.GetString()!;
      }
      if (candidate.Count != Keys.Count) {
        error = "Field document has missing or unknown values.";
        return false;
      }
      error = Validate(candidate);
      if (error.Length != 0) {
        return false;
      }
      values = candidate;
      return true;
    }
  }

  /// <summary>Whether an ENU covariance [EE, EN, EU, NN, NU, UU] is positive semidefinite.</summary>
  internal static bool CovarianceValid(ReadOnlySpan<double> covariance) =>
      Nv5LpsTelemetry.CovarianceValid(covariance);

  /// <summary>
  /// Local checks of a field before it is saved; empty when it passes. The modem still validates
  /// the complete candidate itself.
  /// </summary>
  internal static string Validate(IReadOnlyDictionary<string, string> values) {
    foreach (string key in Keys) {
      if (!values.TryGetValue(key, out string? text)) {
        return $"Missing {key}.";
      }
      if (!InRange(key, text.Trim())) {
        return $"Invalid {key}.";
      }
    }
    long count = Number(values, "LPS_BCOUNT");
    if (count is < 3 or > MaximumBeacons) {
      return "A field needs three or four surveyed beacons.";
    }
    if (Math.Abs(Number(values, "LPS_LAT_E7")) > 850000000L
        || Math.Abs(Number(values, "LPS_LON_E7")) > 1800000000L
        || Math.Abs(Number(values, "LPS_HAE_MM")) > 100000000L
        || Math.Abs(Number(values, "LPS_MSL_MM")) > 100000000L) {
      return "Survey origin is outside the supported bounds.";
    }
    var ids = new HashSet<long>();
    var addresses = new HashSet<long>();
    for (int row = 1; row <= count; row++) {
      long id = Number(values, RowKey(row, "ID"));
      long address = Number(values, RowKey(row, "ADDR"));
      if (address == 0 || !ids.Add(id) || !addresses.Add(address)) {
        return "Beacon IDs and nonzero addresses must be unique.";
      }
      double[] covariance = CovarianceSuffixes
          .Select(suffix => (double)Float(values, RowKey(row, suffix))).ToArray();
      if (!CovarianceValid(covariance)) {
        return $"Beacon {row} survey covariance is not positive semidefinite.";
      }
    }
    return "";
  }

  /// <summary>
  /// The generation the field is saved under: one a listed modem already holds with exactly this
  /// survey, so that the modems holding it need no save, or otherwise one no listed modem uses.
  /// Zero when every generation is taken.
  /// </summary>
  internal static uint Generation(IReadOnlyDictionary<string, string> proposed,
      IReadOnlyList<IReadOnlyDictionary<string, string>> bases) {
    // Rows past the count are compared as the modem stores them, so a dropped fourth row settles.
    SortedDictionary<string, string> candidate = Normalized(Canonical(proposed));
    bool SameSurvey(IReadOnlyDictionary<string, string> held) {
      SortedDictionary<string, string> basis = Normalized(Canonical(held));
      return Keys.Where(SurveyKey).All(key =>
          candidate.GetValueOrDefault(key) == basis.GetValueOrDefault(key));
    }
    uint GenerationOf(IReadOnlyDictionary<string, string> held) =>
        uint.TryParse(held.GetValueOrDefault("LPS_FIELD_GEN"), NumberStyles.None,
            CultureInfo.InvariantCulture, out uint generation) ? generation : 0;
    // A generation is not kept while any modem holds it with another survey, since a changed
    // survey needs a new generation there.
    foreach (IReadOnlyDictionary<string, string> basis in bases) {
      uint generation = GenerationOf(basis);
      if (generation != 0 && SameSurvey(basis)
          && !bases.Any(other => GenerationOf(other) == generation && !SameSurvey(other))) {
        return generation;
      }
    }
    var used = bases.Select(GenerationOf).ToHashSet();
    uint next = 1;
    while (used.Contains(next) && next != uint.MaxValue) {
      next++;
    }
    return used.Contains(next) ? 0 : next;
  }

  internal static string RowKey(int row, string suffix) =>
      "LPS_B" + row.ToString(CultureInfo.InvariantCulture) + "_" + suffix;

  /// <summary>An integer field value, or zero when it does not parse.</summary>
  internal static long Number(IReadOnlyDictionary<string, string> values, string key) =>
      values.TryGetValue(key, out string? text)
      && Nv5NativeSettingsEditor.TryParseInteger(text.Trim(), out long number) ? number : 0;

  private static float Float(IReadOnlyDictionary<string, string> values, string key) =>
      values.TryGetValue(key, out string? text)
      && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
          out float number) ? number : 0;

  private static bool IsFloat(string key) =>
      key.StartsWith("LPS_B", StringComparison.Ordinal)
      && (key.EndsWith("_CEE", StringComparison.Ordinal) || key.EndsWith("_CEN", StringComparison.Ordinal)
          || key.EndsWith("_CEU", StringComparison.Ordinal) || key.EndsWith("_CNN", StringComparison.Ordinal)
          || key.EndsWith("_CNU", StringComparison.Ordinal) || key.EndsWith("_CUU", StringComparison.Ordinal)
          || key.EndsWith("_BVAR", StringComparison.Ordinal));

  /// <summary>Cross terms may be negative; variances may not.</summary>
  private static bool IsCross(string key) =>
      key.EndsWith("_CEN", StringComparison.Ordinal) || key.EndsWith("_CEU", StringComparison.Ordinal)
      || key.EndsWith("_CNU", StringComparison.Ordinal);

  private static bool InRange(string key, string text) {
    if (IsFloat(key)) {
      return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
              out float number) && float.IsFinite(number) && Math.Abs(number) <= 100000000f
          && (IsCross(key) || number >= 0);
    }
    if (!Nv5NativeSettingsEditor.TryParseInteger(text, out long value)) {
      return false;
    }
    return TypeOf(key) == Nv5ConfigValueTypes.Int32
        ? value is >= int.MinValue and <= int.MaxValue
        : !text.StartsWith('-') && value is >= 0 and <= uint.MaxValue;
  }

  /// <summary>Encodes a value as the settings table does, so that its text round-trips.</summary>
  private static bool TryNative(string key, string text, out byte[] data) =>
      Nv5NativeSettingsEditor.Encode(ValueOf(key, new byte[4]), text, out data);

  private static Value ValueOf(string key, byte[] data) =>
      new(key, data, 0, 0, TypeOf(key), Nv5ConfigValueFlags.Supported);

  private static List<string> BuildKeys() {
    List<string> keys = ["LPS_FIELD_GEN", "LPS_DATUM_LO", "LPS_DATUM_HI", "LPS_LAT_E7",
        "LPS_LON_E7", "LPS_HAE_MM", "LPS_MSL_MM", "LPS_BCOUNT", "LPS_RNG_BW_HZ", "LPS_RNG_SF",
        "LPS_RNG_EXT", "LPS_RSP_SYM", "LPS_RV_HZ", "LPS_RV_BW_HZ", "LPS_RV_SF"];
    for (int row = 1; row <= MaximumBeacons; row++) {
      keys.AddRange(RowSuffixes.Select(suffix => RowKey(row, suffix)));
    }
    return keys;
  }
}
