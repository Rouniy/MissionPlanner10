using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Services;
using MissionPlanner.Utilities;
using px4uploader;

namespace MissionPlanner.ViewModels.Setup;

public partial class FirmwareTile : ObservableObject {
  public FirmwareTile(string key, string displayName, string mavType, string glyph) {
    Key = key;
    DisplayName = displayName;
    MavType = mavType;
    Glyph = glyph;
  }

  public string Key { get; }
  public string DisplayName { get; }
  public string MavType { get; }
  public string Glyph { get; }

  [ObservableProperty]
  private string _versionLabel = "—";

  [ObservableProperty]
  private bool _available;
}

public partial class InstallFirmwareViewModel : ViewModelBase {
  private readonly MAVLinkInterface _comPort = AppState.comPort;

  private APFirmware.RELEASE_TYPES _releaseType = APFirmware.RELEASE_TYPES.OFFICIAL;

  public ObservableCollection<FirmwareTile> Tiles { get; } = new();

  [ObservableProperty]
  private string _status = "Connect your board over USB, then pick a vehicle to flash.";

  [ObservableProperty]
  private string _log = "";

  [ObservableProperty]
  private double _progress;

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(FlashTileCommand))]
  [NotifyCanExecuteChangedFor(nameof(RefreshTilesCommand))]
  [NotifyCanExecuteChangedFor(nameof(ForceBootloaderCommand))]
  [NotifyCanExecuteChangedFor(nameof(BootloaderUpdateCommand))]
  [NotifyCanExecuteChangedFor(nameof(ToggleBetaCommand))]
  private bool _busy;

  [ObservableProperty]
  private string _channelLabel = "Channel: OFFICIAL (stable)";

  public InstallFirmwareViewModel() {
    Tiles.Add(new FirmwareTile("rover", "Rover", "GROUND_ROVER", "fa-solid fa-car"));
    Tiles.Add(new FirmwareTile("plane", "Plane", "FIXED_WING", "fa-solid fa-plane"));
    Tiles.Add(new FirmwareTile("quad", "Copter Quad", "Copter", "fa-solid fa-fan"));
    Tiles.Add(new FirmwareTile("hexa", "Copter Hexa", "Copter", "fa-solid fa-fan"));
    Tiles.Add(new FirmwareTile("octo", "Copter Octo", "Copter", "fa-solid fa-fan"));
    Tiles.Add(new FirmwareTile("y6", "Copter Y6", "Copter", "fa-solid fa-fan"));
    Tiles.Add(new FirmwareTile("tri", "Copter Tri", "Copter", "fa-solid fa-fan"));
    Tiles.Add(new FirmwareTile("octaquad", "Copter OctaQuad", "Copter", "fa-solid fa-fan"));
    Tiles.Add(new FirmwareTile("heli", "Heli", "HELICOPTER", "fa-solid fa-helicopter"));
    Tiles.Add(new FirmwareTile("sub", "Sub", "SUBMARINE", "fa-solid fa-ship"));
    Tiles.Add(new FirmwareTile("tracker", "AntennaTracker", "ANTENNA_TRACKER", "fa-solid fa-satellite-dish"));

    _ = RefreshTiles();
  }

  private bool NotBusy => !Busy;

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task RefreshTiles() {
    Busy = true;
    Status = "Fetching firmware manifest…";
    try {
      var reltype = _releaseType;
      var newest = await Task.Run(() => {
        APFirmware.GetList();
        return APFirmware.Manifest?.Firmware == null
          ? new List<APFirmware.FirmwareInfo>()
          : APFirmware.GetReleaseNewest(reltype);
      });

      var byType = newest
        .Where(a => a != null)
        .GroupBy(a => a.MavType)
        .ToDictionary(g => g.Key, g => g.First());

      foreach (var tile in Tiles) {
        if (byType.TryGetValue(tile.MavType, out var info)) {
          var ver = string.IsNullOrEmpty(info.MavFirmwareVersionStr)
            ? info.MavFirmwareVersion?.ToString()
            : info.MavFirmwareVersionStr;
          tile.VersionLabel = $"{tile.DisplayName.Split(' ')[0]} {ver} {info.MavFirmwareVersionType}";
          tile.Available = true;
        } else {
          tile.VersionLabel = "(unavailable)";
          tile.Available = false;
        }
      }

      Status = byType.Count > 0
        ? "Select a vehicle to download and flash its firmware."
        : "No firmware found in the manifest.";
    } catch (Exception ex) {
      Status = "Failed to fetch manifest: " + ex.Message;
    } finally {
      Busy = false;
    }
  }

  /// <summary>Set by the view: shows the option list and returns the operator's choice, or null.</summary>
  internal Func<InstallFirmwareViewModel, Task<AutopilotFirmwareOption?>>? ChooseFirmwareAsync { get; set; }

  /// <summary>Builds found for the detected board and the chosen vehicle type.</summary>
  public ObservableCollection<AutopilotFirmwareOption> FirmwareOptions { get; } = new();

  [ObservableProperty]
  private AutopilotFirmwareOption? _selectedFirmwareOption;

  /// <summary>Header for the choice dialog: board, id and the firmware it runs now.</summary>
  [ObservableProperty]
  private string _boardLabel = "";

  internal sealed record DetectedBoard(int BoardId, string? Name, string? Running, uint Commit, bool Connected) {
    internal string Describe() {
      string board = Name != null ? $"{Name} (board id {BoardId})" : $"board id {BoardId}";
      return Connected
          ? $"{board} runs {(string.IsNullOrWhiteSpace(Running) ? "an unknown firmware" : Running.Trim())}."
          : $"{board} is waiting in its bootloader.";
    }
  }

  private static readonly HttpClient _firmwareHttp = CreateFirmwareHttp();

  private static HttpClient CreateFirmwareHttp() {
    var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    try {
      client.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlanner10");
    } catch {
    }
    return client;
  }

  [RelayCommand(CanExecute = nameof(CanFlash))]
  private async Task FlashTile(FirmwareTile? tile) {
    if (tile == null) {
      return;
    }

    Busy = true;
    Progress = 0;
    Log = "";
    FirmwareOptions.Clear();
    SelectedFirmwareOption = null;
    var reltype = _releaseType;
    try {
      DetectedBoard? board = await Task.Run(DetectBoard);
      if (board == null) {
        SetStatus("Could not identify the board. Connect to the autopilot with CONNECT, or plug the board in over USB and try again.");
        return;
      }
      AppendLog(board.Describe());

      SetStatus("Fetching firmware manifest…");
      var options = await Task.Run(() => {
        APFirmware.GetList();
        return AutopilotFirmwareCatalog.Match(APFirmware.Manifest?.Firmware, tile.MavType, board.BoardId, board.Name, board.Commit);
      });
      if (options.Count == 0) {
        SetStatus($"No {tile.DisplayName} firmware in the ArduPilot manifest for {board.Name ?? "this board"} (board id {board.BoardId}).");
        return;
      }
      await AutopilotFirmwareCatalog.FillPublishedDatesAsync(_firmwareHttp, options, CancellationToken.None);
      foreach (var option in options) {
        FirmwareOptions.Add(option);
      }
      SelectedFirmwareOption = reltype == APFirmware.RELEASE_TYPES.BETA
          ? options.FirstOrDefault(o => o.PreferredPlatform && o.Channel == "beta") ?? AutopilotFirmwareCatalog.Recommended(options)
          : AutopilotFirmwareCatalog.Recommended(options);
      BoardLabel = $"{board.Describe()} {options.Count} {tile.DisplayName} build(s) available"
          + (options.Any(o => o.Installed) ? "; one matches the running commit." : ".");
      Status = BoardLabel;

      AutopilotFirmwareOption? chosen = ChooseFirmwareAsync != null ? await ChooseFirmwareAsync(this) : SelectedFirmwareOption;
      if (chosen == null) {
        SetStatus("Firmware selection cancelled.");
        return;
      }
      await InstallFirmwareOptionCoreAsync(chosen, board);
    } catch (Exception ex) {
      SetStatus("Flash failed: " + ex.Message);
      AppendLog(ex.ToString());
    } finally {
      Busy = false;
    }
  }

  private bool CanFlash(FirmwareTile? tile) => !Busy;

  private async Task InstallFirmwareOptionCoreAsync(AutopilotFirmwareOption option, DetectedBoard board) {
    string? tempFile = null;
    try {
      SetStatus($"Downloading {option.VehicleType} {option.Version} ({option.Channel}) for {option.Platform}…");
      var progress = new Progress<double>(SetProgress);
      tempFile = await AutopilotFirmwareCatalog.DownloadAsync(_firmwareHttp, option,
          Path.Combine(Path.GetTempPath(), "missionplanner-autopilot-firmware"), progress, CancellationToken.None);
      AppendLog($"Downloaded {option.Url} to {tempFile}");
      await Task.Run(() => UploadToBoard(tempFile, board.BoardId));
    } finally {
      if (tempFile != null && File.Exists(tempFile)) {
        try {
          File.Delete(tempFile);
        } catch {
        }
      }
    }
  }

  internal async Task FlashCustomFirmwareAsync(
      string path, LegacyFirmwareTarget target, string? portName = null) {
    if (Busy) {
      return;
    }

    Busy = true;
    Progress = 0;
    Log = "";
    try {
      LegacyFirmwareUploader.ValidateFileTarget(path, target);
      AppendLog($"Custom firmware: {path}");
      AppendLog($"Selected target: {LegacyFirmwareUploader.DescribeTarget(target)}");
      await Task.Run(() => UploadCustomFirmware(path, target, portName));
    } catch (Exception ex) {
      SetStatus("Flash failed: " + ex.Message);
      AppendLog(ex.ToString());
    } finally {
      Busy = false;
    }
  }

  private void UploadCustomFirmware(
      string path, LegacyFirmwareTarget target, string? portName) {
    switch (target) {
      case LegacyFirmwareTarget.Px4Bootloader:
        UploadToBoard(path, null);
        break;
      case LegacyFirmwareTarget.Stm32Dfu:
      case LegacyFirmwareTarget.Stm32DfuBinary:
        LegacyFirmwareUploader.UploadDfu(path, target, OnLegacyProgress);
        break;
      case LegacyFirmwareTarget.Apm1280:
      case LegacyFirmwareTarget.Apm2560:
      case LegacyFirmwareTarget.Apm2560V2:
        LegacyFirmwareUploader.UploadAvr(
            path, portName ?? throw new InvalidOperationException("No serial port was selected."),
            target, OnLegacyProgress);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(target), target, null);
    }
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private void ToggleBeta() {
    _releaseType = _releaseType == APFirmware.RELEASE_TYPES.BETA
      ? APFirmware.RELEASE_TYPES.OFFICIAL
      : APFirmware.RELEASE_TYPES.BETA;
    ChannelLabel = _releaseType == APFirmware.RELEASE_TYPES.BETA
      ? "Channel: BETA"
      : "Channel: OFFICIAL (stable)";
    _ = RefreshTiles();
  }

  [RelayCommand]
  private void AllOptions() {
    OpenUrl("https://firmware.ardupilot.org/");
  }

  [RelayCommand(CanExecute = nameof(NotBusy))]
  private async Task ForceBootloader() {
    Busy = true;
    Log = "";
    try {
      await Task.Run(() => {
        if (!EnterBootloader()) {
          return;
        }
        var up = FindBootloader(null, TimeSpan.FromSeconds(20));
        if (up == null) {
          SetStatus("The board did not show up as a bootloader. Unplug it, plug it back in and try again.");
          return;
        }
        try {
          SetStatus($"Bootloader on {up.port.PortName}: board id {up.board_type}, bootloader rev {up.bl_rev}. Pick a firmware to flash.");
        } finally {
          up.close();
        }
      });
    } finally {
      Busy = false;
    }
  }

  [RelayCommand(CanExecute = nameof(CanBootloaderUpdate))]
  [Obsolete]
  private async Task BootloaderUpdate() {
    Busy = true;
    try {
      await Task.Run(() => {
        try {
          SetStatus("Sending bootloader update command…");

          var ok = _comPort.doCommand(MAVLink.MAV_CMD.FLASH_BOOTLOADER, 0, 0, 0, 0, 290876, 0, 0);
          SetStatus(ok ? "Bootloader upgraded." : "Bootloader upgrade failed.");
        } catch (Exception ex) {
          SetStatus("Bootloader upgrade error: " + ex.Message);
        }
      });
    } finally {
      Busy = false;
    }
  }

  private bool CanBootloaderUpdate() => !Busy && _comPort.BaseStream?.IsOpen == true;

  private bool LinkIsOpen => _comPort.BaseStream?.IsOpen == true;

  /// <summary>
  /// Identifies the board without rebooting it when a vehicle is connected: ArduPilot reports the
  /// APJ board id in AUTOPILOT_VERSION and its hwdef name in the startup banner. Without a link,
  /// a board already sitting in its bootloader is identified over the bootloader protocol.
  /// </summary>
  private DetectedBoard? DetectBoard() {
    if (LinkIsOpen) {
      SetStatus("Reading the board identity from the vehicle…");
      var mav = _comPort.MAV;
      int? boardId = AutopilotFirmwareCatalog.BoardIdFromBoardVersion(mav.cs.board_version);
      if (boardId == null) {
        try {
          _comPort.getVersion();
        } catch (Exception ex) {
          AppendLog("AUTOPILOT_VERSION request failed: " + ex.Message);
        }
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (boardId == null && DateTime.UtcNow < deadline) {
          Thread.Sleep(100);
          boardId = AutopilotFirmwareCatalog.BoardIdFromBoardVersion(mav.cs.board_version);
        }
      }
      if (boardId != null) {
        return new DetectedBoard(boardId.Value, AutopilotFirmwareCatalog.BoardNameFromSerialString(mav.SerialString),
            mav.VersionString, AutopilotFirmwareCatalog.RunningCommit(mav.VersionString), true);
      }
      AppendLog("The vehicle did not report its board id; looking for a bootloader instead.");
    }

    SetStatus("Looking for a board in bootloader mode…");
    var up = FindBootloader(null, TimeSpan.FromSeconds(LinkIsOpen ? 2 : 6));
    if (up == null) {
      return null;
    }
    try {
      return new DetectedBoard(up.board_type, null, null, 0, false);
    } finally {
      up.close();
    }
  }

  /// <summary>
  /// Asks the running firmware to restart into its bootloader: over the open MAVLink link when
  /// connected, else over the last used serial port. Returns false when neither was possible.
  /// </summary>
  private bool EnterBootloader() {
    if (LinkIsOpen) {
      SetStatus("Rebooting the vehicle into its bootloader…");
      bool accepted;
      try {
        accepted = _comPort.doReboot(true, true);
      } catch (Exception ex) {
        AppendLog("Reboot command failed: " + ex.Message);
        accepted = false;
      }
      AppendLog(accepted ? "Reboot to bootloader acknowledged." : "The vehicle did not acknowledge the reboot command; trying anyway.");
      // The USB device re-enumerates as "<board>-BL"; the link cannot survive that.
      try {
        AppState.Connections.Primary.MarkClosed();
        _comPort.Close();
      } catch (Exception ex) {
        AppendLog("Closing the MAVLink link: " + ex.Message);
      }
      AppState.RaiseConnectionChanged();
      return true;
    }

    string port = Settings.Instance.ComPort ?? "";
    if (port.Length == 0 || port == "AUTO" || port == "TCP" || port == "UDP" || port.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)
        || port.StartsWith("TCP", StringComparison.OrdinalIgnoreCase) || port.StartsWith("WS", StringComparison.OrdinalIgnoreCase)) {
      SetStatus("Not connected. Connect to the autopilot with CONNECT first, or plug the board in while it is in bootloader mode.");
      return false;
    }
    SetStatus($"Looking for a heartbeat on {port}…");
    try {
      int baud = int.TryParse(Settings.Instance.BaudRate, out int b) && b > 0 ? b : 115200;
      using var link = new MAVLinkInterface { BaseStream = new MissionPlanner.Comms.SerialPort(port, baud) };
      link.BaseStream.Open();
      link.giveComport = true;
      bool accepted = link.doReboot(true, false);
      AppendLog(accepted ? $"{port}: reboot to bootloader sent." : $"{port}: no vehicle answered.");
      try {
        link.BaseStream.Close();
      } catch {
      }
      if (!accepted) {
        SetStatus($"No vehicle answered on {port}. Connect with CONNECT first, or plug the board in while it is in bootloader mode.");
      }
      return accepted;
    } catch (Exception ex) {
      AppendLog($"{port}: {ex.Message}");
      SetStatus($"Could not use {port}: {ex.Message}");
      return false;
    }
  }

  /// <summary>
  /// Polls the serial ports until a bootloader answers; with an expected board id, bootloaders
  /// of other boards are closed again. The returned uploader stays open for the caller.
  /// </summary>
  private Uploader? FindBootloader(int? expectedBoardId, TimeSpan timeout) {
    var deadline = DateTime.UtcNow + timeout;
    var reported = new HashSet<string>(StringComparer.Ordinal);
    while (DateTime.UtcNow < deadline) {
      foreach (string port in AutopilotFirmwareCatalog.OrderPortsForBootloaderScan(MissionPlanner.Comms.SerialPort.GetPortNames())) {
        if (LinkIsOpen && _comPort.BaseStream is MissionPlanner.Comms.SerialPort live
            && AutopilotFirmwareCatalog.ResolveDevice(live.PortName) == AutopilotFirmwareCatalog.ResolveDevice(port)) {
          continue;
        }
        Uploader up;
        try {
          up = new Uploader(port, 115200);
        } catch (Exception ex) {
          if (reported.Add(port + "|open")) {
            AppendLog($"{port}: {ex.Message}");
          }
          continue;
        }
        try {
          up.identify();
          AppendLog($"{port}: bootloader rev {up.bl_rev}, board id {up.board_type}, flash {up.fw_maxsize} bytes");
          if (expectedBoardId != null && !AutopilotFirmwareCatalog.BoardMatches(up.board_type, expectedBoardId.Value)) {
            AppendLog($"{port}: board id {up.board_type} does not match the firmware's {expectedBoardId}; skipped.");
            up.close();
            continue;
          }
          return up;
        } catch (Exception ex) {
          if (reported.Add(port + "|identify")) {
            AppendLog($"{port}: not a bootloader ({ex.Message})");
          }
          try {
            up.close();
          } catch {
          }
        }
      }
      Thread.Sleep(250);
    }
    return null;
  }

  /// <summary>
  /// Flashes an .apj: reboots a connected vehicle into its bootloader, waits for the bootloader
  /// of the right board, skips the write when the board already holds the same image, else
  /// erases, programs, verifies and restarts the board.
  /// </summary>
  private void UploadToBoard(string filename, int? detectedBoardId) {
    SetStatus("Reading firmware file…");
    px4uploader.Firmware fw;
    try {
      fw = px4uploader.Firmware.ProcessFirmware(filename);
    } catch (Exception ex) {
      SetStatus("Invalid firmware file: " + ex.Message);
      return;
    }
    AppendLog($"Firmware image: board id {fw.board_id}, {fw.image_size} bytes"
        + (string.IsNullOrEmpty(fw.ardupilot_git_hash) ? "" : $", git {fw.ardupilot_git_hash}"));
    if (detectedBoardId != null && !AutopilotFirmwareCatalog.BoardMatches(fw.board_id, detectedBoardId.Value)) {
      SetStatus($"This image is for board id {fw.board_id}, but the connected board is {detectedBoardId}. Nothing was written.");
      return;
    }

    bool wasConnected = LinkIsOpen;
    if (wasConnected && !EnterBootloader()) {
      return;
    }

    SetStatus("Waiting for the bootloader…");
    Uploader? found = FindBootloader(fw.board_id, TimeSpan.FromSeconds(wasConnected ? 40 : 20));
    if (found == null) {
      SetStatus(wasConnected
          ? "The board did not come back as a bootloader. Unplug it, plug it back in and press the vehicle again within a few seconds."
          : "No bootloader found. Connect to the autopilot with CONNECT first, or plug the board in and try again.");
      return;
    }

    found.ProgressEvent += OnUploaderProgress;
    found.LogEvent += OnUploaderLog;
    // Same image on the board: report it instead of re-flashing.
    found.ConfirmEvent += _ => false;
    try {
      SetStatus("Comparing with the firmware on the board…");
      try {
        found.currentChecksum(fw);
      } catch (Exception ex) when (ex.Message.Contains("Same Firmware", StringComparison.OrdinalIgnoreCase)) {
        AppendLog("The board already holds exactly this image.");
        SetStatus("The board already runs this exact firmware. Nothing was written; the board is restarting.");
        found.__reboot();
        return;
      }

      SetStatus("Uploading firmware…");
      SetProgress(0);
      found.upload(fw);
      SetProgress(100);
      SetStatus("Upload complete. The board is restarting; press CONNECT to reconnect.");
    } catch (Exception ex) {
      SetStatus("Upload failed: " + ex.Message);
      AppendLog(ex.ToString());
    } finally {
      try {
        found.close();
      } catch {
      }
    }
  }

  private void OnUploaderProgress(double completed) => SetProgress(completed);

  private void OnUploaderLog(string message, int level) => AppendLog(message);

  private void OnLegacyProgress(int percent, string status) {
    if (percent >= 0) {
      SetProgress(percent);
    }
    SetStatus(status);
    AppendLog(status);
  }

  private static void OpenUrl(string url) {
    try {
      using Process? process = Process.Start(
          new ProcessStartInfo(url) { UseShellExecute = true });
    } catch {
    }
  }

  private void SetStatus(string status) => Dispatcher.UIThread.Post(() => Status = status);

  private void SetProgress(double value) => Dispatcher.UIThread.Post(() => Progress = value);

  private void AppendLog(string line) =>
    Dispatcher.UIThread.Post(() => Log += line + Environment.NewLine);
}
