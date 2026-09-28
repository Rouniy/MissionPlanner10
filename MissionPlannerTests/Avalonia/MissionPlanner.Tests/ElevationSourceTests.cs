using Avalonia.Headless.XUnit;
using BitMiracle.LibTiff.Classic;
using MissionPlanner.Utilities;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;
using MissionPlanner.Views.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public sealed class ElevationSourceTests {
  [AvaloniaFact]
  public void Elevation_sources_page_constructs_and_binds_to_its_view_model() {
    using var viewModel = new ConfigElevationSourcesViewModel();
    var view = new ConfigElevationSourcesView { DataContext = viewModel };

    Assert.NotNull(view.Content);
    Assert.Same(viewModel, view.DataContext);
  }

  [Fact]
  public void Discovery_is_recursive_case_insensitive_and_uses_dem_priority_order() {
    WithDirectory(directory => {
      string nested = Directory.CreateDirectory(Path.Combine(directory, "nested")).FullName;
      File.WriteAllText(Path.Combine(directory, "level0.DT0"), "x");
      File.WriteAllText(Path.Combine(nested, "level2.dt2"), "x");
      File.WriteAllText(Path.Combine(directory, "level1.Dt1"), "x");
      File.WriteAllText(Path.Combine(nested, "surface.TIFF"), "x");
      File.WriteAllText(Path.Combine(directory, "ignore.hgt"), "x");

      IReadOnlyList<string> files = ElevationSourceService.FindSupportedFiles(directory);

      Assert.Equal(4, files.Count);
      Assert.Equal(".TIFF", Path.GetExtension(files[0]));
      Assert.Equal(".dt2", Path.GetExtension(files[1]));
      Assert.Equal(".Dt1", Path.GetExtension(files[2]));
      Assert.Equal(".DT0", Path.GetExtension(files[3]));
    });
  }

  [Fact]
  public async Task Switching_an_active_directory_requires_restart_to_avoid_hidden_stale_dem() {
    await WithDirectoryAsync(async first => {
      await WithDirectoryAsync(async second => {
        string path = Path.Combine(first, "active.tif");
        WriteGeoTiff(path);
        try {
          ElevationScanResult result = await ElevationSourceService.ScanAsync(
              first, progress: null, CancellationToken.None);
          Assert.Equal(1, result.IndexedCount);

          Assert.False(ElevationSourceService.RequiresRestartToSwitch(first));
          Assert.True(ElevationSourceService.RequiresRestartToSwitch(second));
        } finally {
          lock (GeoTiff.index) {
            GeoTiff.index.RemoveAll(item =>
                string.Equals(item.FileName, path, StringComparison.Ordinal));
          }
        }
      });
    });
  }

  [Fact]
  public async Task Corrupt_dem_is_reported_without_aborting_other_files() {
    await WithDirectoryAsync(async directory => {
      string badTiff = Path.Combine(directory, "broken.tif");
      File.WriteAllText(badTiff, "not a TIFF");
      File.WriteAllText(Path.Combine(directory, "ignored.txt"), "not terrain");

      ElevationScanResult result = await ElevationSourceService.ScanAsync(
          directory, progress: null, CancellationToken.None);

      ElevationSourceFile file = Assert.Single(result.Files);
      Assert.Equal(badTiff, file.FullPath);
      Assert.Equal("GeoTIFF", file.Format);
      Assert.False(file.Indexed);
      Assert.NotEmpty(file.Error ?? "");
      Assert.Equal(1, result.ErrorCount);
    });
  }

  [Fact]
  public async Task Indexed_geotiff_is_used_before_srtm_and_returns_real_height() {
    await WithDirectoryAsync(async directory => {
      string path = Path.Combine(directory, "synthetic.tif");
      WriteGeoTiff(path);
      try {
        ElevationScanResult result = await ElevationSourceService.ScanAsync(
            directory, progress: null, CancellationToken.None);

        ElevationSourceFile file = Assert.Single(result.Files);
        Assert.True(file.Indexed, file.Error);
        Assert.Contains("3×3", file.Coverage);

        srtm.altresponce altitude = srtm.getAltitude(73.5, 142.5);
        Assert.Equal(srtm.tiletype.valid, altitude.currenttype);
        Assert.Equal("GeoTiff", altitude.altsource);
        Assert.Equal(210, altitude.alt, 6);
      } finally {
        lock (GeoTiff.index) {
          foreach (GeoTiff.geotiffdata item in GeoTiff.index.Where(item =>
                       string.Equals(item.FileName, path, StringComparison.Ordinal))) {
            // The first altitude query leaves the file open for later scanline reads; Windows
            // refuses to delete the directory until that handle is closed.
            lock (item) {
              item.Tiff?.Dispose();
              item.Tiff = null;
            }
          }
          GeoTiff.index.RemoveAll(item =>
              string.Equals(item.FileName, path, StringComparison.Ordinal));
        }
      }
    });
  }

  [Fact]
  public async Task Indexed_dted_is_used_before_downloaded_srtm() {
    await WithDirectoryAsync(async directory => {
      string path = Path.Combine(directory, "synthetic.dt2");
      WriteDted(path);

      ElevationScanResult result = await ElevationSourceService.ScanAsync(
          directory, progress: null, CancellationToken.None);

      ElevationSourceFile file = Assert.Single(result.Files);
      Assert.True(file.Indexed, file.Error);
      Assert.Equal("DTED", file.Format);
      Assert.Contains("3×3", file.Coverage);

      srtm.altresponce altitude = srtm.getAltitude(12.1, -44.9);
      Assert.Equal(srtm.tiletype.valid, altitude.currenttype);
      Assert.Equal("DTED", altitude.altsource);
      Assert.Equal(210, altitude.alt, 6);
    });
  }

  private static void WriteGeoTiff(string path) {
    ElevationSourceService.EnsureGeoTiffTagsRegistered();
    using Tiff tiff = Tiff.Open(path, "w")
        ?? throw new InvalidOperationException("Could not create synthetic GeoTIFF.");
    tiff.SetField(TiffTag.IMAGEWIDTH, 3);
    tiff.SetField(TiffTag.IMAGELENGTH, 3);
    tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
    tiff.SetField(TiffTag.BITSPERSAMPLE, 16);
    tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.INT);
    tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
    tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
    tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
    tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
    tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
    tiff.SetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG, 3, new[] { 1d, 1d, 0d });
    tiff.SetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG, 6,
        new[] { 0d, 0d, 0d, 141d, 75d, 0d });
    tiff.SetField((TiffTag)34735, 8,
        new ushort[] { 1, 1, 0, 1, 1025, 0, 1, 2 });

    short[][] rows = [
      [100, 110, 120],
      [200, 210, 220],
      [300, 310, 320],
    ];
    for (int row = 0; row < rows.Length; row++) {
      var bytes = new byte[rows[row].Length * sizeof(short)];
      Buffer.BlockCopy(rows[row], 0, bytes, 0, bytes.Length);
      Assert.True(tiff.WriteScanline(bytes, row));
    }
    tiff.WriteDirectory();
  }

  private static void WriteDted(string path) {
    string uhl = "UHL" + "1" + "0450000W" + "0120000N" + "3600" + "3600" +
                 "0000" + "000" + new string(' ', 12) + "0003" + "0003" +
                 "0" + new string(' ', 24);
    Assert.Equal(80, uhl.Length);
    string dsi = "DSI" + new string(' ', 645);
    string accuracy = "ACC" + new string(' ', 2697);
    using var stream = File.Create(path);
    stream.Write(System.Text.Encoding.ASCII.GetBytes(uhl));
    stream.Write(System.Text.Encoding.ASCII.GetBytes(dsi));
    stream.Write(System.Text.Encoding.ASCII.GetBytes(accuracy));

    short[][] rows = [
      [100, 110, 120],
      [200, 210, 220],
      [300, 310, 320],
    ];
    for (int block = 0; block < rows.Length; block++) {
      var record = new byte[18];
      record[0] = 0xaa;
      record[4] = (byte)(block >> 8);
      record[5] = (byte)block;
      for (int sample = 0; sample < rows[block].Length; sample++) {
        short value = rows[block][sample];
        record[8 + sample * 2] = (byte)(value >> 8);
        record[9 + sample * 2] = (byte)value;
      }
      stream.Write(record);
    }
  }

  private static void WithDirectory(Action<string> action) {
    string directory = Path.Combine(Path.GetTempPath(), "mp-elevation-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    try {
      action(directory);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static async Task WithDirectoryAsync(Func<string, Task> action) {
    string directory = Path.Combine(Path.GetTempPath(), "mp-elevation-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    try {
      await action(directory);
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }
}
