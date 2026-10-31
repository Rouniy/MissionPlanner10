using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MissionPlanner.Log;
using MissionPlanner.Utilities;
using org.mariuszgromada.math.mxparser;

namespace MissionPlanner.Services;

internal readonly record struct DataFlashFieldReference(
    string Text, string Type, string BaseType, string? Instance, string Field, string Argument);

/// <summary>
/// Evaluates the same graph-expression language as Mission Planner's DFLogScript while keeping
/// the latest value from each requested message type. The latter makes mixed-type expressions
/// deterministic; upstream's old evaluator indexes every field against the current record.
/// </summary>
internal static class DataFlashExpressionEvaluator {
  private static readonly Regex _fieldReference = new(
      @"(?<type>[A-Za-z_][A-Za-z0-9_]*(?:\[(?<instance>\d+)\])?)\.(?<field>[A-Za-z_][A-Za-z0-9_]*)",
      RegexOptions.Compiled);
  private static readonly Regex _quotedKey = new(
      "[\\\"'](?<key>[^\\\"']+)[\\\"']", RegexOptions.Compiled);

  internal static IReadOnlyList<(double TimeSeconds, double Value)> Evaluate(
      string path, string expression) {
    if (ContainsUpstreamSpecialFunction(expression)) {
      return EvaluateUpstreamFallback(path, expression);
    }
    var references = References(expression);
    if (references.Count == 0) {
      return EvaluateUpstreamFallback(path, expression);
    }

    var compiled = Compile(expression, references);
    using var log = new DFLogBuffer(path);

    IReadOnlyList<(double, double)> answer =
        TryEvaluateColumnwise(log, references, compiled)
        ?? EvaluateByEnumeration(log, references, compiled);

    if (answer.Count == 0) {
      throw new InvalidOperationException($"'{expression}' produced no finite values.");
    }
    return answer;
  }

  /// <summary>
  /// Native fast path: fetches every referenced type's columns in one typed
  /// decode each, then replays the exact latest-value-per-reference merge the
  /// enumeration path performs, in global record order (native linenos are
  /// record indexes, so interleaving across types is preserved - the
  /// order-sensitive lowpass/delta functions see the same sequence). Returns
  /// null when any column cannot be fetched natively; values are the raw
  /// decoded values, where the enumeration path parses display strings that
  /// round floats to 7 significant digits.
  /// </summary>
  private static List<(double, double)>? TryEvaluateColumnwise(
      DFLogBuffer log, IReadOnlyList<DataFlashFieldReference> references, Expression compiled) {
    var streams = new List<TypeColumns>();
    foreach (var group in references.GroupBy(
                 reference => reference.BaseType, StringComparer.OrdinalIgnoreCase)) {
      var groupReferences = group.ToList();
      // mixed-case references to one type are pathological; let the
      // enumeration path define the behavior
      if (groupReferences.Select(reference => reference.BaseType).Distinct().Count() > 1) {
        return null;
      }

      string type = groupReferences[0].BaseType;
      if (DataFlashLog.TimeField(log, type) is not { } time) {
        return null;
      }

      // 'M' (flight mode) fields render as resolver-dependent text in the
      // enumeration path but decode as plain numbers natively - keep any
      // expression touching one on the enumeration path
      if (groupReferences.Any(
              reference => log.GetFieldFormatChar(type, reference.Field) == 'M')) {
        return null;
      }

      string? instanceField = null;
      if (groupReferences.Any(reference => reference.Instance != null)) {
        instanceField = log.GetInstanceFieldName(type);
        if (instanceField == null) {
          return null;
        }
      }

      IEnumerable<string> queried = groupReferences.Select(reference => reference.Field)
          .Append(time.field);
      if (instanceField != null) {
        queried = queried.Append(instanceField);
      }
      string[] query = queried.Distinct().ToArray();
      if (!log.TryGetColumnsNative(type, query, out long[] linenos, out double[][] columns)) {
        return null;
      }

      streams.Add(new TypeColumns(
          Linenos: linenos,
          Seconds: columns[Array.IndexOf(query, time.field)]
              .Select(v => v / time.divisorToMs / 1000.0).ToArray(),
          InstanceValues: instanceField == null
              ? null
              : columns[Array.IndexOf(query, instanceField)],
          References: groupReferences.Select(reference => (
              reference,
              columns[Array.IndexOf(query, reference.Field)],
              reference.Instance == null
                  ? (double?)null
                  : double.Parse(reference.Instance, CultureInfo.InvariantCulture))).ToList()));
    }

    var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    var answer = new List<(double, double)>();
    var positions = new int[streams.Count];
    while (true) {
      int best = -1;
      long bestLineno = long.MaxValue;
      for (int s = 0; s < streams.Count; s++) {
        if (positions[s] < streams[s].Linenos.Length && streams[s].Linenos[positions[s]] < bestLineno) {
          best = s;
          bestLineno = streams[s].Linenos[positions[s]];
        }
      }
      if (best == -1) {
        break;
      }

      TypeColumns stream = streams[best];
      int row = positions[best]++;
      bool updated = false;
      foreach ((DataFlashFieldReference reference, double[] column, double? instance) in
               stream.References) {
        if (instance.HasValue && stream.InstanceValues![row] != instance.Value) {
          continue;
        }
        values[reference.Text] = column[row];
        updated = true;
      }
      if (!updated || values.Count != references.Count) {
        continue;
      }
      foreach (var reference in references) {
        compiled.setArgumentValue(reference.Argument, values[reference.Text]);
      }
      double result = compiled.calculate();
      if (double.IsFinite(result)) {
        answer.Add((stream.Seconds[row], result));
      }
    }

    return answer;
  }

  private sealed record TypeColumns(
      long[] Linenos, double[] Seconds, double[]? InstanceValues,
      List<(DataFlashFieldReference Reference, double[] Column, double? Instance)> References);

  private static List<(double, double)> EvaluateByEnumeration(
      DFLogBuffer log, IReadOnlyList<DataFlashFieldReference> references, Expression compiled) {
    var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    var answer = new List<(double, double)>();
    string[] selectors = references.Select(reference => reference.Type)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    foreach (var item in log.GetEnumeratorType(selectors)) {
      bool updated = false;
      foreach (var reference in references) {
        if (!string.Equals(reference.BaseType, item.msgtype, StringComparison.OrdinalIgnoreCase)
            || !MatchesInstance(item, reference.Instance)) {
          continue;
        }
        if (double.TryParse(item[reference.Field], NumberStyles.Any,
                CultureInfo.InvariantCulture, out double value)) {
          values[reference.Text] = value;
          updated = true;
        }
      }
      if (!updated || values.Count != references.Count) {
        continue;
      }
      foreach (var reference in references) {
        compiled.setArgumentValue(reference.Argument, values[reference.Text]);
      }
      double result = compiled.calculate();
      if (double.IsFinite(result)) {
        answer.Add((item.timems / 1000.0, result));
      }
    }

    return answer;
  }

  internal static IReadOnlyList<DataFlashFieldReference> References(string expression) {
    var references = new List<DataFlashFieldReference>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (Match match in _fieldReference.Matches(expression)) {
      string text = match.Value;
      if (!seen.Add(text)) {
        continue;
      }
      string type = match.Groups["type"].Value;
      string? instance = match.Groups["instance"].Success
          ? match.Groups["instance"].Value
          : null;
      string baseType = instance == null ? type : type[..type.IndexOf('[', StringComparison.Ordinal)];
      references.Add(new DataFlashFieldReference(
          text, type, baseType, instance, match.Groups["field"].Value,
          "__field" + references.Count.ToString(CultureInfo.InvariantCulture)));
    }
    return references;
  }

  internal static bool CanEvaluate(
      string expression, IReadOnlyDictionary<string, string[]> formats) {
    var references = References(expression);
    bool fieldsAvailable = references.All(reference =>
        formats.TryGetValue(reference.BaseType, out var fields)
        && fields.Contains(reference.Field, StringComparer.OrdinalIgnoreCase));
    if (ContainsUpstreamSpecialFunction(expression)) {
      string[] types = KnownSpecialTypes(expression).Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
      return types.Length > 0 && types.All(type => formats.ContainsKey(type)) && fieldsAvailable;
    }
    return references.Count > 0 && fieldsAvailable;
  }

  private static Expression Compile(
      string expression, IReadOnlyList<DataFlashFieldReference> references) {
    var argumentNames = references.ToDictionary(
        reference => reference.Text, reference => reference.Argument,
        StringComparer.OrdinalIgnoreCase);
    string normalized = _fieldReference.Replace(expression,
        match => argumentNames[match.Value]);
    normalized = normalized.Replace("**", "^", StringComparison.Ordinal)
        .Replace("%", "#", StringComparison.Ordinal)
        .Replace(":2", string.Empty, StringComparison.Ordinal);
    normalized = _quotedKey.Replace(normalized,
        match => PackKey(match.Groups["key"].Value).ToString(CultureInfo.InvariantCulture));

    var evaluator = new Expression(normalized);
    evaluator.addFunctions(new Function("wrap_360(x) = (x+360) # 360"));
    evaluator.addFunctions(new Function("degrees(x) = x*57.295779513"));
    evaluator.addFunctions(new Function("atan2", new Atan2Function()));
    evaluator.addFunctions(new Function("lowpass", new LowPassFunction()));
    evaluator.addFunctions(new Function("delta", new DeltaFunction()));
    foreach (var reference in references) {
      evaluator.addArguments(new Argument(reference.Argument));
    }
    if (!evaluator.checkSyntax()) {
      throw new InvalidOperationException(
          $"Invalid Mission Planner graph expression '{expression}': {evaluator.getErrorMessage()}");
    }
    return evaluator;
  }

  private static bool MatchesInstance(DFLog.DFItem item, string? requested) {
    if (requested == null) {
      return true;
    }
    try {
      return string.Equals(item.instance, requested, StringComparison.Ordinal);
    } catch {
      return false;
    }
  }

  private static IReadOnlyList<(double TimeSeconds, double Value)> EvaluateUpstreamFallback(
      string path, string expression) {
    using var log = new DFLogBuffer(path);
    var values = DFLogScript.ProcessExpression(log.dflog, log, expression);
    var answer = values.Where(value => double.IsFinite(value.Item2))
        .Select(value => (value.Item1.timems / 1000.0, value.Item2)).ToArray();
    if (answer.Length == 0) {
      throw new InvalidOperationException(
          $"'{expression}' produced no finite values; required messages may be missing from the log.");
    }
    return answer;
  }

  private static IEnumerable<string> KnownSpecialTypes(string expression) {
    var matches = Regex.Matches(expression,
        @"(?:earth_accel_df|mag_heading_df|gps_velocity_df)\((?<args>[^)]*)\)");
    foreach (Match match in matches) {
      foreach (string token in match.Groups["args"].Value.Split(',')) {
        string type = token.Trim();
        if (Regex.IsMatch(type, @"^[A-Za-z_][A-Za-z0-9_]*$")) {
          yield return type;
        }
      }
    }
  }

  private static bool ContainsUpstreamSpecialFunction(string expression) =>
      expression.Contains("earth_accel_df", StringComparison.Ordinal)
      || expression.Contains("gps_velocity_df", StringComparison.Ordinal)
      || expression.Contains("mag_heading_df", StringComparison.Ordinal);

  private static ulong PackKey(string key) {
    Span<byte> bytes = stackalloc byte[8];
    Encoding.ASCII.GetBytes(key.AsSpan(0, Math.Min(key.Length, bytes.Length)), bytes);
    return BitConverter.ToUInt64(bytes);
  }

  private sealed class Atan2Function : FunctionExtension {
    private double _y;
    private double _x;
    public double calculate() => Math.Atan2(_y, _x);
    public FunctionExtension clone() => new Atan2Function();
    public string getParameterName(int argumentIndex) => argumentIndex == 0 ? "y" : "x";
    public int getParametersNumber() => 2;
    public void setParameterValue(int argumentIndex, double argumentValue) {
      if (argumentIndex == 0) {
        _y = argumentValue;
      } else {
        _x = argumentValue;
      }
    }
  }

  private sealed class LowPassFunction : FunctionExtension {
    private readonly Dictionary<double, double> _last = new();
    private double _value;
    private double _key;
    private double _factor;
    public double calculate() {
      if (!_last.TryGetValue(_key, out double previous)) {
        previous = _value;
      }
      double filtered = _factor * previous + (1.0 - _factor) * _value;
      _last[_key] = filtered;
      return filtered;
    }
    public FunctionExtension clone() => new LowPassFunction();
    public string getParameterName(int argumentIndex) =>
        argumentIndex switch { 0 => "value", 1 => "key", _ => "factor" };
    public int getParametersNumber() => 3;
    public void setParameterValue(int argumentIndex, double argumentValue) {
      if (argumentIndex == 0) {
        _value = argumentValue;
      } else if (argumentIndex == 1) {
        _key = argumentValue;
      } else {
        _factor = argumentValue;
      }
    }
  }

  private sealed class DeltaFunction : FunctionExtension {
    private readonly Dictionary<double, (double Value, double Time)> _last = new();
    private double _value;
    private double _key;
    private double _timeUsec;
    public double calculate() {
      double time = _timeUsec == 0 ? 0 : _timeUsec * 1.0e-6;
      _last.TryGetValue(_key, out var previous);
      double delta = time.Equals(previous.Time)
          ? 0
          : (_value - previous.Value) / (time - previous.Time);
      _last[_key] = (_value, time);
      return delta;
    }
    public FunctionExtension clone() => new DeltaFunction();
    public string getParameterName(int argumentIndex) =>
        argumentIndex switch { 0 => "value", 1 => "key", _ => "timeUsec" };
    public int getParametersNumber() => 3;
    public void setParameterValue(int argumentIndex, double argumentValue) {
      if (argumentIndex == 0) {
        _value = argumentValue;
      } else if (argumentIndex == 1) {
        _key = argumentValue;
      } else {
        _timeUsec = argumentValue;
      }
    }
  }
}
