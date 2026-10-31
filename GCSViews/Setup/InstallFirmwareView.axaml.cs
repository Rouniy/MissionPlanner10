using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Views.Setup;

public partial class InstallFirmwareView : UserControl {
  public InstallFirmwareView() {
    AvaloniaXamlLoader.Load(this);
    this.FindControl<Button>("CustomFwBtn")!.Click += OnLoadCustomFirmware;
    DataContextChanged += (_, _) => {
      if (DataContext is InstallFirmwareViewModel vm) {
        vm.ChooseFirmwareAsync = ChooseFirmwareAsync;
      }
    };
  }

  private Task<MissionPlanner.Services.AutopilotFirmwareOption?> ChooseFirmwareAsync(InstallFirmwareViewModel vm) =>
      AutopilotFirmwareDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, vm.BoardLabel, vm.FirmwareOptions.ToList(), vm.SelectedFirmwareOption);

  private async void OnLoadCustomFirmware(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top is null || DataContext is not InstallFirmwareViewModel vm) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = "Load custom firmware",
          AllowMultiple = false,
          FileTypeFilter = new[]
            {
                    new FilePickerFileType("ArduPilot firmware") {
                      Patterns = new[] { "*.apj", "*.px4", "*.vrx", "*.hex", "*.dfu", "*.bin" },
                    },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        }
    );

    var path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }

    var target = await SelectTarget(path);
    if (target == null) {
      return;
    }

    string? portName = null;
    if (LegacyFirmwareUploader.RequiresSerialPort(target.Value)) {
      var ports = MissionPlanner.Comms.SerialPort.GetPortNames()
          .OrderBy(value => value, System.StringComparer.OrdinalIgnoreCase)
          .ToArray();
      var selected = await Dialogs.Select(
          "Select bootloader port",
          "Select the physical serial port connected to the retired APM board.",
          ports);
      if (selected == null) {
        return;
      }
      portName = ports[selected.Value];
    }

    var warning = target == LegacyFirmwareTarget.Stm32DfuBinary
      ? "The raw binary will be written at STM32 address 0x08000000. An image for another board " +
        "can make it unbootable."
      : "An image for another board can make it unbootable.";
    if (!await Dialogs.ConfirmDangerous(
            "Program flight-controller firmware",
            $"Target: {LegacyFirmwareUploader.DescribeTarget(target.Value)}. {warning} " +
            "Programming interrupts the connection; do not remove power until verification finishes.",
            "PROGRAM FIRMWARE")) {
      return;
    }

    await vm.FlashCustomFirmwareAsync(path, target.Value, portName);
  }

  private static async System.Threading.Tasks.Task<LegacyFirmwareTarget?> SelectTarget(string path) {
    var extension = System.IO.Path.GetExtension(path);
    if (extension.Equals(".apj", System.StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".px4", System.StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".vrx", System.StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Px4Bootloader;
    }
    if (extension.Equals(".dfu", System.StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Stm32Dfu;
    }
    if (extension.Equals(".bin", System.StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Stm32DfuBinary;
    }
    if (!extension.Equals(".hex", System.StringComparison.OrdinalIgnoreCase)) {
      await Dialogs.Alert("Unsupported firmware", "Select an APJ, PX4, VRX, HEX, DFU or BIN image.");
      return null;
    }

    var choice = await Dialogs.Choice(
        "Select Intel HEX target",
        "HEX files do not identify the programming transport. Select the exact board/bootloader.",
        "STM32 DFU", "APM1 1280", "APM1 2560", "APM2 2560");
    return choice switch {
      "STM32 DFU" => LegacyFirmwareTarget.Stm32Dfu,
      "APM1 1280" => LegacyFirmwareTarget.Apm1280,
      "APM1 2560" => LegacyFirmwareTarget.Apm2560,
      "APM2 2560" => LegacyFirmwareTarget.Apm2560V2,
      _ => null,
    };
  }
}
