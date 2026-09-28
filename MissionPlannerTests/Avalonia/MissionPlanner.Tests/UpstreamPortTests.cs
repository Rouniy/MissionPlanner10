using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using MissionPlanner.Controls;
using MissionPlanner.Services;
using MissionPlanner.ViewModels;
using MissionPlanner.Views;
using Settings = MissionPlanner.Utilities.Settings;

namespace MissionPlanner.Tests;

public class UpstreamPortTests {
  [Fact]
  public void App_paths_follow_each_platform_convention() {
    static string? EmptyEnvironment(string _) => null;

    var linux = AppPaths.Resolve(
        AppPlatform.Linux, "/home/test", "", "", EmptyEnvironment);
    Assert.Equal("/home/test/.config/MissionPlanner10", Posix(linux.ConfigRoot));
    Assert.Equal("/home/test/.local/share/MissionPlanner10", Posix(linux.DataRoot));
    Assert.Equal("/home/test/.cache/MissionPlanner10", Posix(linux.CacheRoot));
    Assert.Equal("/home/test/.local/state/MissionPlanner10", Posix(linux.StateRoot));
    Assert.Equal("/home/test/.cache/MissionPlanner/map-tiles", Posix(linux.MapTileCacheRoot));

    var windows = AppPaths.Resolve(
        AppPlatform.Windows,
        @"C:\Users\test",
        @"C:\Users\test\AppData\Roaming",
        @"C:\Users\test\AppData\Local",
        EmptyEnvironment);
    Assert.EndsWith(Path.Combine("Roaming", "MissionPlanner10"), windows.ConfigRoot);
    Assert.EndsWith(Path.Combine("Local", "MissionPlanner10"), windows.DataRoot);
    Assert.EndsWith(Path.Combine("Local", "MissionPlanner", "cache", "map-tiles"),
        windows.MapTileCacheRoot);

    var mac = AppPaths.Resolve(
        AppPlatform.MacOS, "/Users/test", "", "", EmptyEnvironment);
    Assert.Equal(
        "/Users/test/Library/Application Support/MissionPlanner10",
        Posix(mac.ConfigRoot));
    Assert.Equal("/Users/test/Library/Caches/MissionPlanner10", Posix(mac.CacheRoot));
    Assert.Equal(
        "/Users/test/Library/Caches/MissionPlanner/map-tiles",
        Posix(mac.MapTileCacheRoot));
  }

  [Fact]
  public void Linux_paths_honor_only_absolute_xdg_overrides() {
    var values = new Dictionary<string, string?> {
      ["XDG_CONFIG_HOME"] = "/xdg/config",
      ["XDG_DATA_HOME"] = "relative-data-is-invalid",
      ["XDG_CACHE_HOME"] = "/xdg/cache",
      ["XDG_STATE_HOME"] = "/xdg/state",
    };
    var layout = AppPaths.Resolve(
        AppPlatform.Linux,
        "/home/test",
        "",
        "",
        name => values.GetValueOrDefault(name));

    Assert.Equal("/xdg/config/MissionPlanner10", Posix(layout.ConfigRoot));
    Assert.Equal("/home/test/.local/share/MissionPlanner10", Posix(layout.DataRoot));
    Assert.Equal("/xdg/cache/MissionPlanner10", Posix(layout.CacheRoot));
    Assert.Equal("/xdg/state/MissionPlanner10", Posix(layout.StateRoot));
    Assert.Equal("/xdg/cache/MissionPlanner/map-tiles", Posix(layout.MapTileCacheRoot));
  }

  [Fact]
  public void Upstream_data_directory_is_redirected_to_the_cache_directory() {
    AppPaths.Initialize();

    Assert.Equal(
        Path.GetFullPath(AppPaths.CacheRoot).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(Settings.GetDataDirectory()).TrimEnd(Path.DirectorySeparatorChar));
    Assert.StartsWith(
        Path.GetFullPath(AppPaths.ConfigRoot),
        Path.GetFullPath(Settings.GetUserDataDirectory()));
  }

  [Fact]
  public void Legacy_nested_cache_files_are_copied_without_overwriting_newer_files() {
    string root = Path.Combine(Path.GetTempPath(), "mp-cache-migration-" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "old", "sitl");
    string destination = Path.Combine(root, "new", "sitl");
    try {
      Directory.CreateDirectory(Path.Combine(source, "xplane"));
      Directory.CreateDirectory(destination);
      string binary = Path.Combine(source, "ArduPlane");
      File.WriteAllText(binary, "legacy binary");
      File.WriteAllText(Path.Combine(source, "xplane", "dependency.so"), "dependency");
      File.WriteAllText(Path.Combine(destination, "ArduPlane"), "newer binary");
      if (!OperatingSystem.IsWindows()) {
        File.SetUnixFileMode(binary,
            File.GetUnixFileMode(binary) | UnixFileMode.UserExecute);
      }

      AppPaths.CopyDirectoryFilesIfMissing(source, destination);

      Assert.Equal("newer binary", File.ReadAllText(Path.Combine(destination, "ArduPlane")));
      Assert.Equal("dependency", File.ReadAllText(
          Path.Combine(destination, "xplane", "dependency.so")));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void LogOrganizerFindsAllSupportedExtensionsCaseInsensitively() {
    var root = Path.Combine(Path.GetTempPath(), "mp-log-organizer-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "nested"));
    try {
      File.WriteAllText(Path.Combine(root, "one.tlog"), "data");
      File.WriteAllText(Path.Combine(root, "two.RLOG"), "data");
      File.WriteAllText(Path.Combine(root, "nested", "three.BIN"), "data");
      File.WriteAllText(Path.Combine(root, "nested", "four.log"), "data");
      File.WriteAllText(Path.Combine(root, "ignore.txt"), "data");

      Assert.Equal(4, LogOrganizer.FindCandidates(root).Length);
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [AvaloniaFact]
  public async Task Custom_flight_action_can_be_registered_invoked_and_removed() {
    var vm = new FlightDataViewModel();
    string? invoked = null;

    vm.RegisterCustomAction("Extension_Action", value => invoked = value,
        after: "Return_To_Launch");

    Assert.Equal("Extension_Action",
        vm.Actions[vm.Actions.IndexOf("Return_To_Launch") + 1]);

    vm.SelectedAction = "Extension_Action";
    await vm.DoActionCommand.ExecuteAsync(null);

    Assert.Equal("Extension_Action", invoked);
    Assert.True(vm.UnregisterCustomAction("Extension_Action"));
    Assert.DoesNotContain("Extension_Action", vm.Actions);
  }

  [AvaloniaFact]
  public void Hud_custom_paint_runs_after_builtin_rendering() {
    var hud = new HudControl();
    int calls = 0;
    hud.CustomPaint += (_, _) => calls++;
    hud.Measure(new Size(640, 480));
    hud.Arrange(new Rect(0, 0, 640, 480));

    using var target = new RenderTargetBitmap(new PixelSize(640, 480));
    target.Render(hud);

    Assert.Equal(1, calls);
  }

  [AvaloniaFact]
  public void Restored_advanced_windows_and_typed_palettes_construct() {
    foreach (var theme in ThemeService.Names) {
      ThemeService.Apply(theme);
      Assert.Equal(theme, ThemeService.Current);
    }

    var windows = new Window[] {
      new ConfigFFTWindow(),
      new SpectrogramWindow(),
      new ProximityWindow(),
      new WarningManagerWindow(),
      new OfflineMagFitWindow(),
    };
    try {
      Assert.All(windows, window => Assert.NotNull(window.Content));
    } finally {
      foreach (var window in windows) {
        window.Close();
      }
    }
  }

  // Resolve joins with the host's separator, so on Windows the Linux and macOS layouts come back
  // with backslashes; compare them as POSIX paths there. Elsewhere a backslash is a real defect.
  private static string Posix(string path) =>
      OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
}
