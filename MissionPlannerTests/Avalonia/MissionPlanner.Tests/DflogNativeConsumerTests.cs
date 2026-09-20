using MissionPlanner.Services;
using MissionPlanner.Utilities;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

/// <summary>
/// The converted consumers (DataFlashLog field reads, the graph expression
/// evaluator) must produce the same series through the native columnar path
/// as through the managed enumeration path: identical row counts and
/// timestamps, values equal within display rounding (the managed path parses
/// display strings, which round floats to 7 significant digits; the native
/// path carries the raw decoded values).
/// </summary>
public class DflogNativeConsumerTests {
  private static string TestData(string name) {
    return Path.Combine(AppContext.BaseDirectory, "testdata", name + ".bin");
  }

  private static bool NativeMissing {
    get {
      if (DFLogNative.Available) {
        return false;
      }

      Assert.True(Environment.GetEnvironmentVariable("DFLOG_REQUIRE_NATIVE") != "1",
          "DFLOG_REQUIRE_NATIVE=1 but the dflog native library is unavailable");

      // The Rust toolchain is an optional build dependency; hosts without it
      // build no native library and keep full managed coverage.
      return true;
    }
  }

  private static void AssertSeriesEqual(
      IReadOnlyList<(double time, double value)> managed,
      IReadOnlyList<(double time, double value)> native) {
    Assert.Equal(managed.Count, native.Count);
    for (int i = 0; i < managed.Count; i++) {
      Assert.Equal(managed[i].time, native[i].time);
      AssertValueEqual(managed[i].value, native[i].value);
    }
  }

  private static void AssertValueEqual(double managed, double native) {
    // display strings round to 7 significant digits - allow exactly that
    double tolerance = Math.Max(1e-9, Math.Abs(managed) * 1e-6);
    Assert.True(Math.Abs(managed - native) <= tolerance,
        $"managed {managed} vs native {native}");
  }

  [Theory]
  [InlineData("copter", "ATT", "Roll")]
  [InlineData("copter", "GPS", "Lat")]
  [InlineData("copter", "IMU", "GyrX")]
  [InlineData("plane", "ATT", "Pitch")]
  public void Read_field_native_matches_the_enumeration_path(
      string log, string type, string field) {
    if (NativeMissing) {
      return;
    }

    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = false;
      var managed = DataFlashLog.ReadField(TestData(log), type, field);
      Assert.NotEmpty(managed);

      DFLogBuffer.UseNativeScan = true;
      long hitsBefore = DFLogBuffer.NativeColumnHits;
      var native = DataFlashLog.ReadField(TestData(log), type, field);
      Assert.True(DFLogBuffer.NativeColumnHits > hitsBefore,
          "the native column path did not engage");

      AssertSeriesEqual(managed, native);
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  /// <summary>
  /// The managed fallback of ReadFields (one shared buffer, one enumeration
  /// for every field) must match a direct walk of the decoder: the same
  /// records, the same display-string parse, per field. ReadField is that
  /// call with one field, so the oracle is the enumerator itself. No skip
  /// guard: this is the permanent path on hosts without the native library,
  /// including CI before the toolchain is wired up.
  /// </summary>
  [Fact]
  public void Read_fields_fallback_matches_the_enumeration_path() {
    string[] fields = { "Roll", "Pitch", "TimeUS" };
    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = false;
      var expected = fields.Select(_ => new List<(double time, double value)>()).ToList();
      using (var log = new DFLogBuffer(TestData("copter"))) {
        foreach (var item in log.GetEnumeratorType(new[] { "ATT" })) {
          for (int f = 0; f < fields.Length; f++) {
            if (double.TryParse(item[fields[f]], System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double value)) {
              expected[f].Add((item.timems / 1000.0, value));
            }
          }
        }
      }

      Assert.NotEmpty(expected[0]);
      var combined = DataFlashLog.ReadFields(TestData("copter"), "ATT", fields);
      Assert.Equal(expected.Count, combined.Count);
      for (int f = 0; f < expected.Count; f++) {
        Assert.Equal(expected[f], combined[f]);
      }

      Assert.Equal(expected[1], DataFlashLog.ReadField(TestData("copter"), "ATT", "Pitch"));
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  /// <summary>
  /// 'M' (flight mode) fields render as resolver-dependent text in the
  /// managed decoder but are plain numbers natively - they must stay on the
  /// managed path so a graph shows the same thing with and without the
  /// native library. The engagement counter staying put proves the gate, not
  /// just coincidental equality.
  /// </summary>
  [Fact]
  public void Mode_fields_stay_on_the_managed_path() {
    if (NativeMissing) {
      return;
    }

    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = false;
      var managed = DataFlashLog.ReadField(TestData("copter"), "MODE", "Mode");

      DFLogBuffer.UseNativeScan = true;
      long hitsBefore = DFLogBuffer.NativeColumnHits;
      var native = DataFlashLog.ReadField(TestData("copter"), "MODE", "Mode");
      Assert.Equal(hitsBefore, DFLogBuffer.NativeColumnHits);
      Assert.Equal(managed, native);

      // an expression touching the field keeps the whole evaluation on the
      // enumeration path, whatever outcome that path produces
      hitsBefore = DFLogBuffer.NativeColumnHits;
      Exception? nativeOutcome = Record.Exception(
          () => DataFlashExpressionEvaluator.Evaluate(TestData("copter"), "MODE.Mode + 0"));
      Assert.Equal(hitsBefore, DFLogBuffer.NativeColumnHits);

      DFLogBuffer.UseNativeScan = false;
      Exception? managedOutcome = Record.Exception(
          () => DataFlashExpressionEvaluator.Evaluate(TestData("copter"), "MODE.Mode + 0"));
      Assert.Equal(managedOutcome == null, nativeOutcome == null);
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  /// <summary>
  /// The rows view is a text table: one record per row, the decoder's own
  /// display strings, bounded by maxRows. A type with a text column
  /// (PARM.Name, MODE.Mode) has no numeric series for it and must still list
  /// its rows, and numeric cells must match the decoder verbatim - the same
  /// with and without the native library.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Rows_view_keeps_text_columns_and_decoder_strings(bool nativeScan) {
    if (nativeScan && NativeMissing) {
      return;
    }

    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = nativeScan;
      var vm = new LogBrowseViewModel();
      await vm.LoadFileAsync(TestData("copter"));

      foreach ((string type, string textColumn) in new[] { ("PARM", "Name"), ("MODE", "Mode") }) {
        var (columns, rows) = vm.ReadRows(type);
        int column = columns.ToList().IndexOf(textColumn);
        Assert.True(column > 0, $"{type} has no {textColumn} column");
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(row[column]),
            $"{type}.{textColumn} rendered empty"));
      }

      var (gpsColumns, gpsRows) = vm.ReadRows("GPS", 10);
      Assert.Equal(10, gpsRows.Count);
      using var log = new DFLogBuffer(TestData("copter"));
      var expected = log.GetEnumeratorType("GPS").Take(10).Select(item => item["Lat"]).ToList();
      int lat = gpsColumns.ToList().IndexOf("Lat");
      Assert.Equal(expected, gpsRows.Select(row => row[lat]).ToList());
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  [Fact]
  public void Read_fields_matches_per_field_reads() {
    if (NativeMissing) {
      return;
    }

    string[] fields = { "Roll", "Pitch", "Yaw", "TimeUS" };
    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = false;
      var managed = fields
          .Select(field => DataFlashLog.ReadField(TestData("copter"), "ATT", field)).ToList();

      DFLogBuffer.UseNativeScan = true;
      long hitsBefore = DFLogBuffer.NativeColumnHits;
      var native = DataFlashLog.ReadFields(TestData("copter"), "ATT", fields);
      Assert.True(DFLogBuffer.NativeColumnHits > hitsBefore,
          "the native column path did not engage");

      Assert.Equal(managed.Count, native.Count);
      for (int f = 0; f < managed.Count; f++) {
        AssertSeriesEqual(managed[f], native[f]);
      }
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  /// <summary>
  /// The ISBH/ISBD batch-FFT collection: the native header/data merge must
  /// reproduce the enumeration loop's state machine. copter-isbd carries real
  /// batch samples (INS_LOG_BAT_MASK=1).
  /// </summary>
  [Fact]
  public void Fft_isbh_native_matches_the_enumeration_path() {
    if (NativeMissing) {
      return;
    }

    AssertFftParity("copter-isbd");
  }

  /// <summary>The IMU fallback FFT collection over a log without batch samples.</summary>
  [Fact]
  public void Fft_imu_native_matches_the_enumeration_path() {
    if (NativeMissing) {
      return;
    }

    AssertFftParity("copter");
  }

  private static void AssertFftParity(string log) {
    bool old = DFLogBuffer.UseNativeScan;
    try {
      var viewModel = new MissionPlanner.ViewModels.GCSViews.ConfigurationView.ConfigFFTViewModel {
        // small FFT windows so the corpus logs' few hundred IMU rows still
        // produce series
        Bins = 5,
      };

      DFLogBuffer.UseNativeScan = false;
      var managed = viewModel.ComputeFft(TestData(log));
      Assert.NotEmpty(managed.Series);

      DFLogBuffer.UseNativeScan = true;
      long hitsBefore = DFLogBuffer.NativeColumnHits;
      var native = viewModel.ComputeFft(TestData(log));
      Assert.True(DFLogBuffer.NativeColumnHits > hitsBefore,
          "the native column path did not engage");

      Assert.Equal(managed.Series.Count, native.Series.Count);
      // smp_rate reaches the managed path through a display string - allow
      // rounding there and in the magnitudes
      AssertValueEqual(managed.SampleRate, native.SampleRate);
      for (int s = 0; s < managed.Series.Count; s++) {
        Assert.Equal(managed.Series[s].Label, native.Series[s].Label);
        Assert.Equal(managed.Series[s].Freq.Length, native.Series[s].Freq.Length);
        for (int b = 0; b < managed.Series[s].Freq.Length; b++) {
          AssertValueEqual(managed.Series[s].Freq[b], native.Series[s].Freq[b]);
          // the managed path's display-rounded samples (1e-7 relative) pass
          // through FFT bin cancellation and a log scale, which can amplify
          // the difference well past display rounding; 0.01 dB is far below
          // anything visible in the plot
          Assert.True(Math.Abs(managed.Series[s].Mag[b] - native.Series[s].Mag[b]) <= 0.01,
              $"bin {b}: managed {managed.Series[s].Mag[b]} vs native {native.Series[s].Mag[b]}");
        }
      }
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  [Fact]
  public void Spectrogram_native_matches_the_enumeration_path() {
    if (NativeMissing) {
      return;
    }

    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = false;
      List<(double timeus, double[] value)> managedData;
      double[] managedFreq;
      using (var buffer = new DFLogBuffer(TestData("copter-isbd"))) {
        using var image = Spectrogram.GenerateImage(buffer, out managedFreq, out managedData);
      }
      Assert.NotEmpty(managedData);

      DFLogBuffer.UseNativeScan = true;
      long hitsBefore = DFLogBuffer.NativeColumnHits;
      List<(double timeus, double[] value)> nativeData;
      double[] nativeFreq;
      using (var buffer = new DFLogBuffer(TestData("copter-isbd"))) {
        using var image = Spectrogram.GenerateImage(buffer, out nativeFreq, out nativeData);
      }
      Assert.True(DFLogBuffer.NativeColumnHits > hitsBefore,
          "the native column path did not engage");

      Assert.Equal(managedFreq.Length, nativeFreq.Length);
      for (int b = 0; b < managedFreq.Length; b++) {
        AssertValueEqual(managedFreq[b], nativeFreq[b]);
      }
      Assert.Equal(managedData.Count, nativeData.Count);
      for (int i = 0; i < managedData.Count; i++) {
        Assert.Equal(managedData[i].timeus, nativeData[i].timeus);
        Assert.Equal(managedData[i].value.Length, nativeData[i].value.Length);
        for (int b = 0; b < managedData[i].value.Length; b++) {
          AssertValueEqual(managedData[i].value[b], nativeData[i].value[b]);
        }
      }
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  /// <summary>
  /// Expressions cover the interesting merge semantics: a single type, two
  /// interleaved types (latest-value merge in record order), an instanced
  /// reference, and an order-sensitive stateful function.
  /// </summary>
  [Theory]
  [InlineData("ATT.Roll - ATT.Pitch")]
  [InlineData("ATT.Roll + IMU.GyrX")]
  [InlineData("degrees(IMU[0].GyrX)")]
  [InlineData("lowpass(ATT.Roll,1,0.9)")]
  [InlineData("delta(IMU.GyrX,1,IMU.TimeUS)")]
  public void Expression_native_matches_the_enumeration_path(string expression) {
    if (NativeMissing) {
      return;
    }

    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = false;
      var managed = DataFlashExpressionEvaluator.Evaluate(TestData("copter"), expression);
      Assert.NotEmpty(managed);

      DFLogBuffer.UseNativeScan = true;
      long hitsBefore = DFLogBuffer.NativeColumnHits;
      var native = DataFlashExpressionEvaluator.Evaluate(TestData("copter"), expression);
      Assert.True(DFLogBuffer.NativeColumnHits > hitsBefore,
          "the native column path did not engage");

      Assert.Equal(managed.Count, native.Count);
      for (int i = 0; i < managed.Count; i++) {
        Assert.Equal(managed[i].TimeSeconds, native[i].TimeSeconds);
        AssertValueEqual(managed[i].Value, native[i].Value);
      }
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }
}
