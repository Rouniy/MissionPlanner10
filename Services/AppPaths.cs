using System;
using System.Collections.Generic;
using System.IO;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services;

internal enum AppPlatform { Windows, MacOS, Linux }

internal sealed record AppPathLayout(
    string ConfigRoot,
    string DataRoot,
    string CacheRoot,
    string StateRoot,
    string MapTileCacheRoot);

public static class AppPaths {
  public const string AppDirectoryName = "MissionPlanner10";
  public const string LegacyAppDirectoryName = "MissionPlanner";
  public const string PackageManagedMarkerName = ".package-managed";

  private static readonly object _gate = new();
  private static readonly AppPathLayout _layout = ResolveCurrent();
  private static bool _initialized;

  public static string InstallRoot => Path.GetFullPath(AppContext.BaseDirectory);
  public static string ConfigRoot => _layout.ConfigRoot;
  public static string DataRoot => _layout.DataRoot;
  public static string CacheRoot => _layout.CacheRoot;
  public static string StateRoot => _layout.StateRoot;
  public static string LogsRoot => Path.Combine(DataRoot, "logs");
  public static string CrashLogPath => Path.Combine(StateRoot, "logs", "crash.log");
  public static string PoiFilePath => Path.Combine(DataRoot, "poi.txt");
  public static string SitlCacheRoot => Path.Combine(CacheRoot, "sitl");
  public static string SrtmCacheRoot => Path.Combine(CacheRoot, "srtm");
  // Map tiles intentionally retain the official Mission Planner location. The filesystem cache
  // format is shared, so Mission Planner 10, official Mission Planner and other ground stations
  // that use the same layout can reuse downloads.
  public static string MapTileCacheRoot => _layout.MapTileCacheRoot;
  public static string UpdateCacheRoot => Path.Combine(CacheRoot, "update");
  public static string PluginRoot => Path.Combine(DataRoot, "plugins");
  public static string PluginDataRoot => Path.Combine(DataRoot, "plugin-data");
  public static string PluginLogPath => Path.Combine(StateRoot, "logs", "plugins.log");

  public static bool IsPackageManaged {
    get {
      string? forced = Environment.GetEnvironmentVariable("MISSIONPLANNER_PACKAGE_MANAGED");
      return string.Equals(forced, "1", StringComparison.OrdinalIgnoreCase)
          || string.Equals(forced, "true", StringComparison.OrdinalIgnoreCase)
          || File.Exists(Path.Combine(InstallRoot, PackageManagedMarkerName));
    }
  }

  public static void Initialize() {
    lock (_gate) {
      if (_initialized) {
        return;
      }

      CreateRequiredDirectories();
      MigrateLegacyFiles();

      Settings.AppConfigName = AppDirectoryName;
      Settings.CustomUserDataDirectory = Path.GetDirectoryName(ConfigRoot) ?? ConfigRoot;
      Settings.CustomDataDirectory = CacheRoot;

      var settings = Settings.Instance;
      if (string.IsNullOrWhiteSpace(settings["logdirectory"])) {
        settings["logdirectory"] = FindLegacyLogDirectory() ?? LogsRoot;
      }

      Directory.CreateDirectory(settings.LogDir);
      srtm.datadirectory = SrtmCacheRoot;
      DisplayViewExtensions.custompath = Path.Combine(ConfigRoot, "custom.displayview");
      _initialized = true;
    }
  }

  internal static AppPathLayout Resolve(
      AppPlatform platform,
      string userProfile,
      string applicationData,
      string localApplicationData,
      Func<string, string?> getEnvironment) {
    string profile = NonEmpty(userProfile, Path.GetTempPath());
    string configBase;
    string dataBase;
    string cacheBase;
    string stateBase;

    switch (platform) {
      case AppPlatform.Windows:
        configBase = NonEmpty(applicationData, Path.Combine(profile, "AppData", "Roaming"));
        dataBase = NonEmpty(localApplicationData, Path.Combine(profile, "AppData", "Local"));
        cacheBase = Path.Combine(dataBase, AppDirectoryName, "cache");
        stateBase = Path.Combine(dataBase, AppDirectoryName, "state");
        return new AppPathLayout(
            Path.Combine(configBase, AppDirectoryName),
            Path.Combine(dataBase, AppDirectoryName),
            cacheBase,
            stateBase,
            Path.Combine(dataBase, LegacyAppDirectoryName, "cache", "map-tiles"));

      case AppPlatform.MacOS:
        configBase = Path.Combine(profile, "Library", "Application Support");
        cacheBase = Path.Combine(profile, "Library", "Caches");
        stateBase = Path.Combine(profile, "Library", "Logs");
        return new AppPathLayout(
            Path.Combine(configBase, AppDirectoryName),
            Path.Combine(configBase, AppDirectoryName),
            Path.Combine(cacheBase, AppDirectoryName),
            Path.Combine(stateBase, AppDirectoryName),
            Path.Combine(cacheBase, LegacyAppDirectoryName, "map-tiles"));

      default:
        configBase = Xdg(getEnvironment, "XDG_CONFIG_HOME", Path.Combine(profile, ".config"));
        dataBase = Xdg(getEnvironment, "XDG_DATA_HOME", Path.Combine(profile, ".local", "share"));
        cacheBase = Xdg(getEnvironment, "XDG_CACHE_HOME", Path.Combine(profile, ".cache"));
        stateBase = Xdg(getEnvironment, "XDG_STATE_HOME", Path.Combine(profile, ".local", "state"));
        return new AppPathLayout(
            Path.Combine(configBase, AppDirectoryName),
            Path.Combine(dataBase, AppDirectoryName),
            Path.Combine(cacheBase, AppDirectoryName),
            Path.Combine(stateBase, AppDirectoryName),
            Path.Combine(cacheBase, LegacyAppDirectoryName, "map-tiles"));
    }
  }

  private static AppPathLayout ResolveCurrent() {
    var platform = OperatingSystem.IsWindows()
        ? AppPlatform.Windows
        : OperatingSystem.IsMacOS() ? AppPlatform.MacOS : AppPlatform.Linux;
    return Resolve(
        platform,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable);
  }

  private static void CreateRequiredDirectories() {
    foreach (var path in new[] {
        ConfigRoot,
        DataRoot,
        CacheRoot,
        StateRoot,
        LogsRoot,
        Path.GetDirectoryName(CrashLogPath)!,
        SitlCacheRoot,
        SrtmCacheRoot,
        MapTileCacheRoot,
        UpdateCacheRoot,
        PluginRoot,
        PluginDataRoot,
      }) {
      Directory.CreateDirectory(path);
    }
  }

  private static void MigrateLegacyFiles() {
    foreach (var legacy in LegacyRoots()) {
      CopyTopLevelFiles(legacy, ConfigRoot, fileName =>
          !string.Equals(fileName, "poi.txt", StringComparison.OrdinalIgnoreCase)
          && !string.Equals(fileName, "last_params.param", StringComparison.OrdinalIgnoreCase)
          && !string.Equals(fileName, "crash.log", StringComparison.OrdinalIgnoreCase)
          && !IsLegacyCacheFile(fileName));
      CopyTopLevelFiles(legacy, CacheRoot, IsLegacyCacheFile);
      CopyIfMissing(Path.Combine(legacy, "poi.txt"), PoiFilePath);
      CopyIfMissing(Path.Combine(legacy, "crash.log"), CrashLogPath);
    }

    // Mission Planner 10 has its own cache except for the deliberately shared map tiles. Preserve
    // already downloaded SITL binaries and terrain data from the pre-rename cache so an offline
    // operator (or Skip Download) is not broken by the product-name migration.
    string legacyCacheRoot = Path.GetDirectoryName(MapTileCacheRoot)!;
    if (!SamePath(legacyCacheRoot, CacheRoot)) {
      CopyTopLevelFiles(legacyCacheRoot, CacheRoot, IsLegacyCacheFile);
      CopyDirectoryFilesIfMissing(
          Path.Combine(legacyCacheRoot, "sitl"), SitlCacheRoot);
      CopyDirectoryFilesIfMissing(
          Path.Combine(legacyCacheRoot, "srtm"), SrtmCacheRoot);
    }

    string oldSrtm = Path.Combine(InstallRoot, "srtm");
    if (!SamePath(oldSrtm, SrtmCacheRoot)) {
      CopyTopLevelFiles(oldSrtm, SrtmCacheRoot);
    }
  }

  private static IEnumerable<string> LegacyRoots() {
    var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void Add(string? path) {
      if (!string.IsNullOrWhiteSpace(path)) {
        roots.Add(Path.GetFullPath(path));
      }
    }
    void AddUnder(string? parent, string child) {
      if (!string.IsNullOrWhiteSpace(parent)) {
        Add(Path.Combine(parent, child));
      }
    }

    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    AddUnder(local, "Mission Planner");
    AddUnder(local, LegacyAppDirectoryName);
    AddUnder(local, AppDirectoryName);
    AddUnder(local, "MissionPlannerAvalonia");
    AddUnder(roaming, "Mission Planner");
    AddUnder(roaming, LegacyAppDirectoryName);
    AddUnder(roaming, AppDirectoryName);
    AddUnder(roaming, "MissionPlannerAvalonia");
    AddUnder(documents, "Mission Planner");
    return roots;
  }

  private static string? FindLegacyLogDirectory() {
    foreach (var root in LegacyRoots()) {
      string logs = Path.Combine(root, "logs");
      if (Directory.Exists(logs)) {
        return logs;
      }
    }
    return null;
  }

  private static void CopyTopLevelFiles(
      string sourceDirectory,
      string destinationDirectory,
      Func<string, bool>? include = null) {
    try {
      if (!Directory.Exists(sourceDirectory) || SamePath(sourceDirectory, destinationDirectory)) {
        return;
      }
      Directory.CreateDirectory(destinationDirectory);
      foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)) {
        if (include != null && !include(Path.GetFileName(source))) {
          continue;
        }
        CopyIfMissing(source, Path.Combine(destinationDirectory, Path.GetFileName(source)));
      }
    } catch {
      // Migration is best-effort. A failed legacy copy must never prevent startup.
    }
  }

  internal static void CopyDirectoryFilesIfMissing(
      string sourceDirectory, string destinationDirectory) {
    try {
      if (!Directory.Exists(sourceDirectory) || SamePath(sourceDirectory, destinationDirectory)) {
        return;
      }

      var options = new EnumerationOptions {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
      };
      foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*", options)) {
        string relative = Path.GetRelativePath(sourceDirectory, source);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)) {
          continue;
        }
        CopyIfMissing(source, Path.Combine(destinationDirectory, relative));
      }
    } catch {
      // Migration is best-effort. Keep using the new cache even if an old file is unreadable.
    }
  }

  private static void CopyIfMissing(string source, string destination) {
    try {
      if (!File.Exists(source) || File.Exists(destination) || SamePath(source, destination)) {
        return;
      }
      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      File.Copy(source, destination, overwrite: false);
      if (!OperatingSystem.IsWindows()) {
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
      }
    } catch {
      // See CopyTopLevelFiles: retain the legacy copy and continue startup.
    }
  }

  private static bool IsLegacyCacheFile(string fileName) {
    return fileName.EndsWith(".apm.pdef.xml", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".apm.pdef.xml.gz", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("LogMessages", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "UserAlerts.json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "eunfz.json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "hknfz.json", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("gstreamer-", StringComparison.OrdinalIgnoreCase);
  }

  private static string Xdg(Func<string, string?> getEnvironment, string name, string fallback) {
    string? value = getEnvironment(name);
    return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : fallback;
  }

  private static string NonEmpty(string value, string fallback) =>
      string.IsNullOrWhiteSpace(value) ? fallback : value;

  private static bool SamePath(string left, string right) =>
      string.Equals(
          Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
          Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
