using System.Globalization;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

/// <summary>
/// Parity tests for the native dflog log core (rust/): the native index scan
/// and typed column paths must reproduce exactly what the managed
/// DFLogBuffer/BinaryLog path produces, row for row, over the vendored SITL
/// corpus. The native library is an optional build product (it exists when
/// cargo was on the PATH at build time), so every test that needs it returns
/// early when it is absent - the managed path stays covered either way.
/// </summary>
public class DflogNativeTests {
  private static string TestData(string name) {
    return Path.Combine(AppContext.BaseDirectory, "testdata", name + ".bin");
  }

  private static bool NativeMissing {
    get {
      if (DFLogNative.Available) {
        return false;
      }

      // Hosts that build the native library set DFLOG_REQUIRE_NATIVE=1 so a
      // broken native build fails loudly instead of quietly skipping every
      // parity test.
      Assert.True(Environment.GetEnvironmentVariable("DFLOG_REQUIRE_NATIVE") != "1",
          "DFLOG_REQUIRE_NATIVE=1 but the dflog native library is unavailable");

      // The Rust toolchain is an optional build dependency; hosts without it
      // build no native library and keep full managed coverage.
      return true;
    }
  }

  /// <summary>
  /// An ABI bump on the Rust side without a matching DFLogNative.AbiVersion
  /// update would silently disable the native path everywhere (Available goes
  /// false, every parity test skips). When the library was actually built,
  /// it must be loadable and ABI-compatible.
  /// </summary>
  [Fact]
  public void Built_native_library_reports_the_expected_abi() {
    string library = Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "dflog_ffi.dll"
        : OperatingSystem.IsMacOS() ? "libdflog_ffi.dylib"
        : "libdflog_ffi.so");
    if (!File.Exists(library)) {
      // no Rust toolchain on this host - nothing was built, nothing to check
      return;
    }

    Assert.True(DFLogNative.Available,
        "the native library exists next to the test assembly but is unusable - "
        + "ABI mismatch between DFLogNative.AbiVersion and rust/crates/dflog-ffi?");
  }

  private static IDisposable ForceNativeScan(bool enabled) {
    bool old = DFLogBuffer.UseNativeScan;
    DFLogBuffer.UseNativeScan = enabled;
    return new RestoreScan(old);
  }

  private sealed class RestoreScan : IDisposable {
    private readonly bool _old;

    public RestoreScan(bool old) {
      _old = old;
    }

    public void Dispose() {
      DFLogBuffer.UseNativeScan = _old;
    }
  }

  [Theory]
  [InlineData("copter")]
  [InlineData("plane")]
  [InlineData("rover")]
  [InlineData("copter-isbd")]
  public void Native_scan_produces_the_managed_index(string name) {
    if (NativeMissing) {
      return;
    }

    AssertIndexParity(TestData(name), expectNativeScan: true);
  }

  /// <summary>
  /// Resync behavior on damaged input is where the two scanners would drift
  /// apart first: truncation (clean and mid-record), a corrupt FMT length
  /// byte that breaks framing, and a garbage prefix (which flips the binary
  /// sniff to the text path, so the native scanner must stand aside).
  /// Whatever each variant parses to, both scanners must agree line for line.
  /// </summary>
  [Fact]
  public void Native_scan_matches_managed_on_malformed_logs() {
    if (NativeMissing) {
      return;
    }

    byte[] source = File.ReadAllBytes(TestData("copter"));

    var corruptFmt = (byte[])source.Clone();
    // byte 4 is the first FMT record's length field - the nastiest framing case
    corruptFmt[4] ^= 0xFF;

    var variants = new (string name, byte[] bytes, bool expectNativeScan)[] {
      ("truncated60", source.Take(source.Length * 6 / 10).ToArray(), true),
      ("truncated-midrecord", source.Take(source.Length * 6 / 10 + 13).ToArray(), true),
      ("corrupt-fmt", corruptFmt, true),
      ("garbage-prefix", new byte[] { 0x00, 0x11, 0x22, 0x33 }.Concat(source).ToArray(), false),
    };

    DirectoryInfo dir = Directory.CreateTempSubdirectory("DflogNativeTests");
    try {
      foreach ((string name, byte[] bytes, bool expectNativeScan) in variants) {
        string file = Path.Combine(dir.FullName, name + ".bin");
        File.WriteAllBytes(file, bytes);
        AssertIndexParity(file, expectNativeScan);
      }
    } finally {
      try {
        dir.Delete(true);
      } catch (IOException) {
      }
    }
  }

  /// <summary>
  /// Opens the log twice - managed scanner, then native scanner - and
  /// requires an identical index: line count, seen types, and every line's
  /// rendered content.
  /// </summary>
  private static void AssertIndexParity(string path, bool expectNativeScan) {
    long managedCount;
    var managedTypes = new List<string>();
    var managedLines = new List<string>();
    using (ForceNativeScan(false))
    using (var buffer = new DFLogBuffer(path)) {
      Assert.False(DFLogBuffer.LastScanNative);
      managedCount = buffer.Count;
      managedTypes.AddRange(buffer.SeenMessageTypes);
      for (int i = 0; i < buffer.Count; i++) {
        managedLines.Add(buffer[i]);
      }
    }

    using (ForceNativeScan(true))
    using (var buffer = new DFLogBuffer(path)) {
      Assert.True(expectNativeScan == DFLogBuffer.LastScanNative,
          expectNativeScan
              ? "native scanner did not run despite the library being available"
              : "native scanner ran on input the managed path treats as text");
      Assert.Equal(managedCount, buffer.Count);
      Assert.Equal(managedTypes, buffer.SeenMessageTypes);
      for (int i = 0; i < buffer.Count; i++) {
        Assert.Equal(managedLines[i], buffer[i]);
      }
    }
  }

  public static IEnumerable<object[]> ColumnCases =>
      new[] {
        new object[] { "copter", "ATT", new[] { "Roll", "Pitch", "Yaw" } },
        new object[] { "copter", "IMU", new[] { "I", "GyrX", "AccZ" } },
        new object[] { "copter", "GPS", new[] { "Lat", "Lng", "Alt" } },
        new object[] { "copter", "ATT", new[] { "TimeUS" } },
        new object[] { "plane", "ATT", new[] { "Roll", "Pitch" } },
        new object[] { "rover", "GPS", new[] { "Lat", "Lng" } },
      };

  [Theory]
  [MemberData(nameof(ColumnCases))]
  public void Native_columns_match_the_managed_decode(string logname, string type, string[] fields) {
    if (NativeMissing) {
      return;
    }

    using (ForceNativeScan(true))
    using (var buffer = new DFLogBuffer(TestData(logname))) {
      Assert.True(buffer.TryGetColumnsNative(type, fields, out long[] linenos, out double[][] columns));

      (long[] expectedLinenos, double[][] expectedCols) = ManagedReference(buffer, type, fields);

      Assert.Equal(expectedLinenos, linenos);
      for (int c = 0; c < fields.Length; c++) {
        Assert.Equal(expectedCols[c], columns[c]);
      }
    }
  }

  /// <summary>
  /// The native instance filter must select exactly the rows the managed
  /// filter-after-fetch approach selects: per-instance results partition the
  /// unfiltered rows, lineno for lineno, value for value.
  /// </summary>
  [Fact]
  public void Native_instance_filter_matches_managed_filtering() {
    if (NativeMissing) {
      return;
    }

    using (ForceNativeScan(true))
    using (var buffer = new DFLogBuffer(TestData("copter"))) {
      string instanceField = buffer.GetInstanceFieldName("IMU");
      Assert.Equal("I", instanceField);

      string[] fields = { "GyrX", instanceField };
      Assert.True(buffer.TryGetColumnsNative("IMU", fields, out long[] allLinenos, out double[][] allCols));

      double[] instances = allCols[1].Distinct().OrderBy(v => v).ToArray();
      Assert.True(instances.Length > 1, "corpus IMU should have multiple instances");

      var seenLinenos = new List<long>();
      foreach (double instance in instances) {
        Assert.True(buffer.TryGetColumnsNative("IMU", fields, (long)instance,
            out long[] linenos, out double[][] cols));

        int[] expected = Enumerable.Range(0, allLinenos.Length)
            .Where(i => allCols[1][i] == instance).ToArray();

        Assert.Equal(expected.Select(i => allLinenos[i]), linenos);
        Assert.Equal(expected.Select(i => allCols[0][i]), cols[0]);
        Assert.True(cols[1].All(v => v == instance));
        seenLinenos.AddRange(linenos);
      }

      seenLinenos.Sort();
      Assert.Equal(allLinenos, seenLinenos);

      // a type without an instance field fails instead of guessing
      Assert.False(buffer.TryGetColumnsNative("ATT", new[] { "Roll" }, 0, out _, out _));
    }
  }

  /// <summary>
  /// The pattern instanced-type consumers use: fetch the value column plus
  /// the instance column and filter client-side. Must select exactly the rows
  /// GetEnumeratorType("TYPE[n]") yields.
  /// </summary>
  [Fact]
  public void Instance_filtered_columns_match_the_enumerator() {
    if (NativeMissing) {
      return;
    }

    using (ForceNativeScan(true))
    using (var buffer = new DFLogBuffer(TestData("copter"))) {
      string instanceField = buffer.GetInstanceFieldName("IMU");
      Assert.NotNull(instanceField);

      Assert.True(buffer.TryGetColumnsNative("IMU", new[] { "GyrX", instanceField },
          out long[] linenos, out double[][] columns));

      var nativeRows = new List<(long lineno, double value)>();
      for (int i = 0; i < linenos.Length; i++) {
        if (columns[1][i] == 0) {
          nativeRows.Add((linenos[i], columns[0][i]));
        }
      }

      int col = buffer.dflog.FindMessageOffset("IMU", "GyrX");
      var managedRows = new List<(long lineno, double value)>();
      foreach (var item in buffer.GetEnumeratorType("IMU[0]")) {
        managedRows.Add((item.lineno, Convert.ToDouble(item.raw[col], CultureInfo.InvariantCulture)));
      }

      Assert.Equal(managedRows, nativeRows);
    }
  }

  [Fact]
  public void Truncated_tail_decodes_like_managed() {
    if (NativeMissing) {
      return;
    }

    byte[] source = File.ReadAllBytes(TestData("copter"));
    DirectoryInfo dir = Directory.CreateTempSubdirectory("DflogNativeTests");
    using (ForceNativeScan(true)) {
      try {
        string file = Path.Combine(dir.FullName, "trunc.bin");
        File.WriteAllBytes(file, source.Take(source.Length - 37).ToArray());

        using var buffer = new DFLogBuffer(file);
        Assert.True(buffer.TryGetColumnsNative("ATT", new[] { "Roll" },
            out long[] linenos, out double[][] columns));

        (long[] expectedLinenos, double[][] expectedCols) = ManagedReference(buffer, "ATT", new[] { "Roll" });
        Assert.Equal(expectedLinenos, linenos);
        Assert.Equal(expectedCols[0], columns[0]);
      } finally {
        try {
          dir.Delete(true);
        } catch (IOException) {
        }
      }
    }
  }

  /// <summary>
  /// Array-column parity on a synthetic ISBD-like log: the native short[32]
  /// rows must equal the managed BinaryLog.UnionArray shorts.
  /// </summary>
  [Fact]
  public void Array_column_matches_union_array() {
    if (NativeMissing) {
      return;
    }

    // FMT: type 0xAB "ISB", format "Ha", labels "N,x", len 3+2+64
    var data = new List<byte> { 0xA3, 0x95, 0x80 };
    var fmt = new byte[86];
    fmt[0] = 0xAB;
    fmt[1] = 3 + 2 + 64;
    System.Text.Encoding.ASCII.GetBytes("ISB").CopyTo(fmt, 2);
    System.Text.Encoding.ASCII.GetBytes("Ha").CopyTo(fmt, 6);
    System.Text.Encoding.ASCII.GetBytes("N,x").CopyTo(fmt, 22);
    data.AddRange(fmt);

    var rnd = new Random(7);
    for (int rec = 0; rec < 5; rec++) {
      data.AddRange(new byte[] { 0xA3, 0x95, 0xAB });
      data.AddRange(BitConverter.GetBytes((ushort)rec));
      for (int e = 0; e < 32; e++) {
        data.AddRange(BitConverter.GetBytes((short)rnd.Next(short.MinValue, short.MaxValue)));
      }
    }

    DirectoryInfo dir = Directory.CreateTempSubdirectory("DflogNativeTests");
    using (ForceNativeScan(true)) {
      try {
        string file = Path.Combine(dir.FullName, "isb.bin");
        File.WriteAllBytes(file, data.ToArray());

        using var buffer = new DFLogBuffer(file);
        Assert.True(buffer.TryGetArrayColumnNative("ISB", "x", out long[] linenos, out short[][] rows));
        Assert.Equal(5, rows.Length);

        int idx = buffer.dflog.FindMessageOffset("ISB", "x");
        var managedLinenos = new List<long>();
        var managedRows = new List<short[]>();
        foreach (var item in buffer.GetEnumeratorType("ISB")) {
          managedLinenos.Add(item.lineno);
          var ua = (BinaryLog.UnionArray)item.raw[idx];
          managedRows.Add(ua.Shorts.ToArray());
        }

        Assert.Equal(managedLinenos, linenos);
        for (int r = 0; r < rows.Length; r++) {
          Assert.Equal(managedRows[r], rows[r]);
        }

        // non-array field fails cleanly
        Assert.False(buffer.TryGetArrayColumnNative("ISB", "N", out _, out _));
      } finally {
        try {
          dir.Delete(true);
        } catch (IOException) {
        }
      }
    }
  }

  /// <summary>
  /// Same parity check over real vehicle data: ISBD batch samples from a SITL
  /// log recorded with INS_LOG_BAT_MASK=1.
  /// </summary>
  [Fact]
  public void Array_column_matches_union_array_on_the_real_isbd_log() {
    if (NativeMissing) {
      return;
    }

    using (ForceNativeScan(true))
    using (var buffer = new DFLogBuffer(TestData("copter-isbd"))) {
      Assert.Contains("ISBD", buffer.SeenMessageTypes);

      Assert.True(buffer.TryGetArrayColumnNative("ISBD", "x", out long[] linenos, out short[][] rows));
      Assert.True(rows.Length > 0, "no ISBD rows decoded");

      int idx = buffer.dflog.FindMessageOffset("ISBD", "x");
      int r = 0;
      foreach (var item in buffer.GetEnumeratorType("ISBD")) {
        Assert.Equal(item.lineno, linenos[r]);
        var ua = (BinaryLog.UnionArray)item.raw[idx];
        Assert.Equal(ua.Shorts.ToArray(), rows[r]);
        r++;
      }

      Assert.Equal(r, rows.Length);
    }
  }

  /// <summary>
  /// The time-axis computation columnar consumers use: TimeUS column / 1000
  /// through DFLog.GetTimeFromMs must equal DFItem.time tick for tick.
  /// </summary>
  [Fact]
  public void Columnwise_time_matches_dfitem_time() {
    if (NativeMissing) {
      return;
    }

    using (ForceNativeScan(true))
    using (var buffer = new DFLogBuffer(TestData("copter"))) {
      Assert.True(buffer.TryGetColumnsNative("ATT", new[] { "TimeUS" }, out _, out double[][] cols));

      int r = 0;
      foreach (var item in buffer.GetEnumeratorType("ATT")) {
        DateTime columnwise = buffer.dflog.GetTimeFromMs(cols[0][r] / 1000.0);
        Assert.Equal(item.time.Ticks, columnwise.Ticks);
        r++;
      }

      Assert.Equal(r, cols[0].Length);
    }
  }

  /// <summary>
  /// The native GPS time correlation must match the managed DFLog conversion
  /// within 1 ms (tick truncation in the managed conversion).
  /// </summary>
  [Theory]
  [InlineData("copter")]
  [InlineData("plane")]
  [InlineData("rover")]
  [InlineData("copter-isbd")]
  public void Native_time_base_matches_the_managed_conversion(string name) {
    if (NativeMissing) {
      return;
    }

    string logfile = TestData(name);
    using (ForceNativeScan(false))
    using (var buffer = new DFLogBuffer(logfile))
    using (var reader = DFLogNative.ColumnReader.Open(logfile)) {
      Assert.True(buffer.dflog.gpsstarttime != DateTime.MinValue,
          "managed path found no gps time - corpus log unusable for this test");
      Assert.NotNull(reader);
      Assert.True(reader.TryGetTimeBase(out long gpsStartUnixMs, out long msOffset));

      foreach (double boardMs in new[] { 0.0, msOffset, msOffset + 123456.789, 1e9 }) {
        double managedUnixMs =
            (buffer.dflog.GetTimeFromMs(boardMs).ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        double native = gpsStartUnixMs + (boardMs - msOffset);

        Assert.True(Math.Abs(managedUnixMs - native) <= 1.0,
            $"boardMs={boardMs}: managed {managedUnixMs} vs native {native}");
      }
    }
  }

  [Fact]
  public void Unknown_field_fails_cleanly() {
    if (NativeMissing) {
      return;
    }

    using (ForceNativeScan(true))
    using (var buffer = new DFLogBuffer(TestData("copter"))) {
      Assert.False(buffer.TryGetColumnsNative("ATT", new[] { "NoSuchField" }, out _, out _));
      Assert.False(buffer.TryGetColumnsNative("NOTYPE", new[] { "Roll" }, out _, out _));
    }
  }

  /// <summary>
  /// The managed truth: enumerate DFItems and convert the raw decoded objects
  /// (not the display strings) to double, as a precision-exact consumer would.
  /// </summary>
  private static (long[] linenos, double[][] cols) ManagedReference(
      DFLogBuffer buffer, string type, string[] fields) {
    int[] indices = fields.Select(f => buffer.dflog.FindMessageOffset(type, f)).ToArray();
    Assert.All(indices, i => Assert.True(i > 0, "field not found in managed logformat"));

    var linenos = new List<long>();
    List<double>[] cols = fields.Select(_ => new List<double>()).ToArray();
    foreach (var item in buffer.GetEnumeratorType(type)) {
      linenos.Add(item.lineno);
      for (int c = 0; c < indices.Length; c++) {
        object raw = item.raw[indices[c]];
        cols[c].Add(raw is string s
            ? double.Parse(s, CultureInfo.InvariantCulture)
            : Convert.ToDouble(raw, CultureInfo.InvariantCulture));
      }
    }

    return (linenos.ToArray(), cols.Select(c => c.ToArray()).ToArray());
  }
}
