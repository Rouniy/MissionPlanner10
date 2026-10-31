using System.Text.Json;
using MissionPlanner.Services.Mcp;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

/// <summary>
/// The MCP DataFlash tools are consumers of DFLogBuffer that were written
/// against the managed scanner: they read rows through GetEnumeratorType and
/// the DFItem indexer, never the typed column path. Opening the same log
/// through the catalog with the native scanner must produce byte-identical
/// tool output - the native scan replaces only the index (offsets and types),
/// row rendering stays managed, so exact equality is the bar, not a
/// tolerance. Every comparison runs through McpLogCatalog.Read so the
/// per-server cached reader takes the same path the MCP server does.
/// </summary>
public sealed class DflogNativeMcpParityTests {
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

  private const string RowTypes = "ATT,GPS,IMU,MODE,PARM,VIBE";

  /// <summary>
  /// One tool's outcome, serialized: its JSON, or the exception it threw. A
  /// tool that rejects the log (no PARM history, an incomplete batch) must
  /// reject it the same way on both scanners.
  /// </summary>
  private static string Outcome(Func<object> tool) {
    try {
      return JsonSerializer.Serialize(tool());
    } catch (ArgumentException e) {
      return "rejected: " + e.Message;
    }
  }

  private static async Task<Dictionary<string, string>> RunTools(string path, bool nativeScan) {
    bool old = DFLogBuffer.UseNativeScan;
    try {
      DFLogBuffer.UseNativeScan = nativeScan;
      using var catalog = new McpLogCatalog();
      string id = catalog.Attach(path).Id;
      var outcomes = await catalog.Read(id, (log, ct) => {
        var results = new Dictionary<string, string> {
          ["schema"] = Outcome(() => McpLogCatalog.Schema(log)),
          ["overview"] = Outcome(() => McpFlightAnalysis.Overview(log, ct)),
          ["rows"] = Outcome(() => McpLogCatalog.Rows(log, RowTypes, 0, 500, 0, 1e12, null, ct)),
          ["series"] = Outcome(() => McpLogCatalog.Series(log, "ATT", ["Roll", "Pitch"], 0, 1e12, null, ct)),
          ["seriesInstanced"] = Outcome(() => McpLogCatalog.Series(log, "IMU", ["GyrX", "AccZ"], 0, 1e12, "0", ct)),
          ["parameters"] = Outcome(() => McpFlightAnalysis.Parameters(log, 1e9, "", 0, 200, ct)),
          ["vibration"] = Outcome(() => McpFlightAnalysis.Vibration(log, 0, 1e12, ct)),
        };

        // the second page starts where the first one stopped - pagination
        // depends on line numbers, which the native index assigns
        var firstPage = JsonSerializer.SerializeToElement(
            McpLogCatalog.Rows(log, RowTypes, 0, 500, 0, 1e12, null, ct));
        int nextLine = firstPage.GetProperty("nextLine").GetInt32();
        results["rowsPage2"] = Outcome(() => McpLogCatalog.Rows(log, RowTypes, nextLine, 500, 0, 1e12, null, ct));

        // the raw batch path is the one MCP tool that reads a field's decoded
        // object (GetRaw) instead of its display string
        var headers = log.GetEnumeratorType("ISBH").Take(1).ToList();
        results["spectrum"] = headers.Count == 0 ? "no ISBH"
            : Outcome(() => McpBatchImu.Spectrum(log, headers[0].lineno, "x", 256, ct));
        return results;
      }, CancellationToken.None);

      Assert.Equal(nativeScan, DFLogBuffer.LastScanNative);
      return outcomes;
    } finally {
      DFLogBuffer.UseNativeScan = old;
    }
  }

  /// <summary>
  /// <paramref name="hasBatches"/>: the log carries ISBH/ISBD batch samples
  /// (INS_LOG_BAT_MASK set in SITL), so the spectrum tool must have run.
  /// </summary>
  [Theory]
  [InlineData("copter", false)]
  [InlineData("plane", false)]
  [InlineData("rover", true)]
  [InlineData("copter-isbd", true)]
  public async Task Mcp_tools_produce_identical_output_on_the_native_index(string name, bool hasBatches) {
    if (NativeMissing) {
      return;
    }

    var managed = await RunTools(TestData(name), nativeScan: false);
    // the comparison is only meaningful when the managed scanner produced
    // real tool output, not a rejection that the native scanner merely repeats
    foreach (string tool in managed.Keys.Where(t => hasBatches || t != "spectrum")) {
      Assert.False(managed[tool].StartsWith("rejected", StringComparison.Ordinal)
          || managed[tool] == "no ISBH", $"{tool} on the managed index: {managed[tool]}");
    }
    Assert.NotEqual("[[],[]]", managed["series"]);
    Assert.NotEqual("[[],[]]", managed["seriesInstanced"]);

    var native = await RunTools(TestData(name), nativeScan: true);

    Assert.Equal(managed.Keys, native.Keys);
    foreach (string tool in managed.Keys) {
      Assert.True(managed[tool] == native[tool], $"{tool} differs between the managed and native index");
    }
  }
}
