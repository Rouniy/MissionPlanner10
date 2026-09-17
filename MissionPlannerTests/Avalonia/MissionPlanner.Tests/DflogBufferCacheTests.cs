using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

/// <summary>
/// The DFLogBuffer index cache for large logs. Upstream serialized it with
/// BinaryFormatter, which throws unconditionally on modern .NET - saving
/// crashed the open of any path-backed log over the threshold, and loading
/// silently never worked. These tests pin the replacement format: round-trip
/// fidelity, stale- and corrupt-cache rejection, and that an over-threshold
/// open never throws (the first open here fails outright on the old code).
/// </summary>
public class DflogBufferCacheTests {
  private static byte[] BuildLog(int rows, byte flip = 0) {
    // FMT: type 0xA0 "TST", format "Hf", labels "N,V", len 3+2+4
    var data = new List<byte> { 0xA3, 0x95, 0x80 };
    var fmt = new byte[86];
    fmt[0] = 0xA0;
    fmt[1] = 3 + 2 + 4;
    System.Text.Encoding.ASCII.GetBytes("TST").CopyTo(fmt, 2);
    System.Text.Encoding.ASCII.GetBytes("Hf").CopyTo(fmt, 6);
    System.Text.Encoding.ASCII.GetBytes("N,V").CopyTo(fmt, 22);
    data.AddRange(fmt);
    for (int i = 0; i < rows; i++) {
      data.AddRange(new byte[] { 0xA3, 0x95, 0xA0 });
      data.AddRange(BitConverter.GetBytes((ushort)(i ^ flip)));
      data.AddRange(BitConverter.GetBytes(1.5f * i));
    }
    return data.ToArray();
  }

  private static List<string> Lines(DFLogBuffer buffer) {
    var lines = new List<string>();
    for (int i = 0; i < buffer.Count; i++) {
      lines.Add(buffer[i]);
    }
    return lines;
  }

  /// <summary>
  /// Lowers the cache threshold so kilobyte fixtures exercise the cache, pins
  /// the managed scan path (native-capable opens deliberately skip the index
  /// cache in both directions), and removes the per-user cache file on the
  /// way out.
  /// </summary>
  private sealed class CacheScope : IDisposable {
    private readonly long _oldThreshold;
    private readonly bool _oldUseNativeScan;

    public DirectoryInfo Dir { get; }
    public string LogPath { get; }
    public string? CachePath { get; private set; }

    public CacheScope() {
      _oldThreshold = DFLogBuffer.CacheThresholdBytes;
      _oldUseNativeScan = DFLogBuffer.UseNativeScan;
      DFLogBuffer.CacheThresholdBytes = 1;
      DFLogBuffer.UseNativeScan = false;
      Dir = Directory.CreateTempSubdirectory("DflogBufferCacheTests");
      LogPath = Path.Combine(Dir.FullName, "test.bin");
    }

    public void TrackCache(DFLogBuffer buffer) {
      CachePath = buffer.CacheFilePath;
    }

    public string RequireCache() {
      Assert.False(string.IsNullOrEmpty(CachePath));
      Assert.True(File.Exists(CachePath), "the expected cache was not written");
      return CachePath!;
    }

    public void Dispose() {
      DFLogBuffer.CacheThresholdBytes = _oldThreshold;
      DFLogBuffer.UseNativeScan = _oldUseNativeScan;
      try {
        Dir.Delete(true);
      } catch (IOException) {
      }
      if (!string.IsNullOrEmpty(CachePath)) {
        try {
          File.Delete(CachePath);
          foreach (string stale in Directory.GetFiles(
              Path.GetDirectoryName(CachePath)!, Path.GetFileName(CachePath) + ".*.tmp")) {
            File.Delete(stale);
          }
        } catch (IOException) {
        }
      }
    }
  }

  [Fact]
  public void Cache_round_trip_restores_the_identical_index() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    List<string> scanned;
    long count;
    // the first open scans and saves the cache; on the BinaryFormatter code
    // this line throws for any over-threshold log
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scope.TrackCache(buffer);
      Assert.False(buffer.LastLoadFromCache);
      scanned = Lines(buffer);
      count = buffer.Count;
      Assert.Equal(51, count);
    }

    scope.RequireCache();

    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.True(buffer.LastLoadFromCache,
          "second open scanned instead of loading the cache");
      Assert.Equal(count, buffer.Count);
      Assert.Equal(scanned, Lines(buffer));
      Assert.Equal(50, buffer.GetEnumeratorType("TST").Count());
    }
  }

  [Fact]
  public void Text_log_cache_round_trip_preserves_line_offsets() {
    using var scope = new CacheScope();
    File.WriteAllText(scope.LogPath, "alpha\nbeta\ngamma\n");

    List<string> scanned;
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scope.TrackCache(buffer);
      Assert.False(buffer.LastLoadFromCache);
      scanned = Lines(buffer);
      Assert.Equal(3, buffer.Count);
    }

    scope.RequireCache();
    using var cached = new DFLogBuffer(scope.LogPath);
    Assert.True(cached.LastLoadFromCache);
    Assert.Equal(scanned, Lines(cached));
  }

  /// <summary>
  /// The contract between the cache and the native scanner: a native-capable
  /// open neither loads nor saves the index cache - a fresh native index is
  /// cheap, and the cache stays a managed-fallback optimization.
  /// </summary>
  [Fact]
  public void Native_capable_open_skips_the_cache_in_both_directions() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    // seed a cache from the pinned managed path
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scope.TrackCache(buffer);
    }
    string cache = scope.RequireCache();

    DFLogBuffer.UseNativeScan = true;
    if (!DFLogNative.Available) {
      // no native library on this host - the skip path is unreachable
      return;
    }

    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.True(DFLogBuffer.LastScanNative, "native scan did not engage");
      Assert.False(buffer.LastLoadFromCache,
          "a native-capable open loaded the index cache");
    }

    File.Delete(cache);
    using (new DFLogBuffer(scope.LogPath)) {
    }
    Assert.False(File.Exists(cache),
        "a native-capable open saved an index cache");
  }

  [Fact]
  public void Same_length_edit_rejects_the_stale_cache() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));
    using (var first = new DFLogBuffer(scope.LogPath)) {
      scope.TrackCache(first);
    }
    scope.RequireCache();

    // Same byte count and source path, different content: the recorded write
    // time must prevent the old index from being reused.
    File.WriteAllBytes(scope.LogPath, BuildLog(50, flip: 1));
    File.SetLastWriteTimeUtc(scope.LogPath, DateTime.UtcNow.AddSeconds(3));

    using var buffer = new DFLogBuffer(scope.LogPath);
    Assert.False(buffer.LastLoadFromCache,
        "a cache for an older copy of the log was loaded");
    Assert.Equal(51, buffer.Count);
  }

  /// <summary>
  /// A crafted file with a valid header but an absurd list length must be
  /// rejected by the plausibility bound instead of turning into a giant
  /// pre-allocation.
  /// </summary>
  [Fact]
  public void Implausible_list_length_in_the_cache_is_rejected() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    List<string> scanned;
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scope.TrackCache(buffer);
      scanned = Lines(buffer);
    }

    string cache = scope.RequireCache();
    var source = new FileInfo(scope.LogPath);
    using (var file = File.Create(cache))
    using (var gzip = new System.IO.Compression.GZipStream(
        file, System.IO.Compression.CompressionMode.Compress))
    using (var writer = new BinaryWriter(gzip)) {
      writer.Write(0x4C444D31u);                       // valid magic
      writer.Write(1);                                 // valid version
      writer.Write(source.Length);                     // matching identity
      writer.Write(source.LastWriteTimeUtc.Ticks);
      writer.Write(51L);                               // line count
      writer.Write(int.MaxValue);                      // absurd list length
    }

    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.False(buffer.LastLoadFromCache);
      Assert.Equal(scanned, Lines(buffer));
    }
  }

  [Fact]
  public void Structurally_valid_but_out_of_range_offset_is_rejected() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    List<string> scanned;
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scope.TrackCache(buffer);
      scanned = Lines(buffer);
    }

    string cache = scope.RequireCache();
    byte[] payload;
    using (var source = File.OpenRead(cache))
    using (var gzip = new System.IO.Compression.GZipStream(
        source, System.IO.Compression.CompressionMode.Decompress))
    using (var output = new MemoryStream()) {
      gzip.CopyTo(output);
      payload = output.ToArray();
    }

    // Header is 32 bytes, followed by the offset-list count and first offset.
    BitConverter.GetBytes(new FileInfo(scope.LogPath).Length + 1).CopyTo(payload, 36);
    using (var output = File.Create(cache))
    using (var gzip = new System.IO.Compression.GZipStream(
        output, System.IO.Compression.CompressionMode.Compress)) {
      gzip.Write(payload);
    }

    using var reopened = new DFLogBuffer(scope.LogPath);
    Assert.False(reopened.LastLoadFromCache);
    Assert.Equal(scanned, Lines(reopened));
  }

  [Fact]
  public void Cache_observability_is_per_buffer_instance() {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    using var scanned = new DFLogBuffer(scope.LogPath);
    scope.TrackCache(scanned);
    Assert.False(scanned.LastLoadFromCache);
    scope.RequireCache();

    using var cached = new DFLogBuffer(scope.LogPath);
    Assert.True(cached.LastLoadFromCache);
    Assert.False(scanned.LastLoadFromCache,
        "opening another log buffer changed this instance's cache state");
  }

  [Theory]
  [InlineData("garbage")]
  [InlineData("truncated")]
  public void Corrupt_cache_falls_back_to_a_clean_rescan(string mode) {
    using var scope = new CacheScope();
    File.WriteAllBytes(scope.LogPath, BuildLog(50));

    List<string> scanned;
    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      scope.TrackCache(buffer);
      scanned = Lines(buffer);
    }

    string cache = scope.RequireCache();
    if (mode == "garbage") {
      File.WriteAllBytes(cache, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    } else {
      // a torn write: valid prefix, missing tail - the loader must reject it
      // without leaving a half-committed index behind for the rescan
      byte[] full = File.ReadAllBytes(cache);
      File.WriteAllBytes(cache, full.Take(full.Length / 2).ToArray());
    }

    using (var buffer = new DFLogBuffer(scope.LogPath)) {
      Assert.False(buffer.LastLoadFromCache);
      Assert.Equal(scanned, Lines(buffer));
      Assert.Equal(50, buffer.GetEnumeratorType("TST").Count());
    }
  }
}
