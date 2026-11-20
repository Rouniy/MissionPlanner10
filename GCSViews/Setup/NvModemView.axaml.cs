using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MissionPlanner.Services;
using MissionPlanner.ViewModels;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Views.Setup;

public partial class NvModemView : UserControl {
  private static readonly FilePickerFileType ParamFiles = new("Mission Planner parameters") {
    Patterns = ["*.param", "*.parm", "*.txt"],
  };

  private static readonly FilePickerFileType NativeSnapshotFiles = new("NV5 configuration") {
    Patterns = ["*.json"],
  };

  public NvModemView() {
    InitializeComponent();
    this.FindControl<Button>("LoadParametersButton")!.Click += LoadParameters;
    this.FindControl<Button>("SaveParametersButton")!.Click += SaveParameters;
    this.FindControl<Button>("CopyRadioSettingsButton")!.Click += CopyRadioSettings;
    this.FindControl<DataGrid>("ParametersGrid")!.BeginningEdit += (_, args) => {
      if (args.Row.DataContext is NvModemParameterRow { IsReadOnly: true }) {
        args.Cancel = true;
      }
    };
    this.FindControl<Button>("ConfigureDistancePairButton")!.Click += ConfigureDistancePair;
    this.FindControl<Button>("LoadNativeSnapshotButton")!.Click += LoadNativeSnapshot;
    this.FindControl<Button>("ExportNativeSnapshotButton")!.Click += ExportNativeSnapshot;
    this.FindControl<DataGrid>("NativeSettingsGrid")!.BeginningEdit += (_, args) => {
      if (args.Row.DataContext is Nv5NativeSettingRow { IsEditable: false }) {
        args.Cancel = true;
      }
    };
  }

  private NvModemViewModel? ViewModel => DataContext as NvModemViewModel;

  private async void ConfigureDistancePair(object? sender, RoutedEventArgs e) {
    if (TopLevel.GetTopLevel(this) is not Window owner || ViewModel is not { } page
        || page.OpenDistancePair() is not { } pair) {
      return;
    }
    try {
      await new NvModemDistancePairWindow { DataContext = pair }.ShowDialog(owner);
    } finally {
      page.CloseDistancePair();
    }
  }

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private async void LoadParameters(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || ViewModel == null) {
      return;
    }

    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load NV modem parameters",
      AllowMultiple = false,
      FileTypeFilter = [ParamFiles, new FilePickerFileType("All files") { Patterns = ["*"] }],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }

    try {
      NvModemParameterComparison? comparison = ViewModel.BuildParameterFileComparison(
          await File.ReadAllTextAsync(path), Path.GetFileName(path));
      if (comparison != null) {
        await ReviewAndApply(
            top,
            comparison,
            "Load NV modem parameters",
            "File",
            $"Choose which of {comparison.Rows.Count} differing file value(s) to stage. "
                + "Nothing is sent to the modem until Save to selected modem is pressed.");
      }
    } catch (Exception ex) {
      await Dialogs.Alert("Load NV modem parameters", ex.Message);
    }
  }

  private async void CopyRadioSettings(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    NvModemParameterComparison? comparison = ViewModel?.BuildCopyParameterComparison();
    if (top == null || comparison == null) {
      return;
    }
    await ReviewAndApply(
        top,
        comparison,
        "Copy NV modem parameters",
        "Source modem",
        $"Choose which of {comparison.Rows.Count} channel-local difference(s) to copy from "
            + $"{comparison.SourceLabel}. Network, transport and system IDs are not copied. "
            + "Nothing is sent until Save to selected modem is pressed.");
  }

  private async System.Threading.Tasks.Task ReviewAndApply(
      TopLevel top,
      NvModemParameterComparison comparison,
      string title,
      string proposedHeader,
      string instructions) {
    if (comparison.Rows.Count == 0) {
      await Dialogs.Alert(title,
          $"No differing supported settings were found. Unknown: {comparison.Unknown}; "
              + $"invalid: {comparison.Invalid}; read-only: {comparison.ReadOnly}.");
      return;
    }
    if (top is not Window owner) {
      await Dialogs.Alert(title, "The comparison window cannot be opened without an owner window.");
      return;
    }
    IReadOnlyList<IParameterComparisonRow> rows = comparison.Rows;
    if (await ParamCompareWindow.ShowAsync(
            owner, rows, title, proposedHeader, instructions)) {
      ViewModel?.ApplyParameterComparison(comparison);
    }
  }

  private async void LoadNativeSnapshot(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || ViewModel == null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Load native configuration snapshot",
      AllowMultiple = false,
      FileTypeFilter = [NativeSnapshotFiles],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }
    try {
      ViewModel.NativeSettings.LoadSnapshot(path);
    } catch (Exception ex) {
      await Dialogs.Alert("Load native configuration snapshot", ex.Message);
    }
  }

  private async void ExportNativeSnapshot(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || ViewModel == null) {
      return;
    }
    var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Export committed configuration",
      SuggestedFileName = ViewModel.NativeSettings.SuggestedSnapshotName(),
      DefaultExtension = "json",
      FileTypeChoices = [NativeSnapshotFiles],
    });
    string? path = file?.TryGetLocalPath();
    if (path == null || !await Dialogs.ConfirmDangerous(
            "Export NV modem configuration",
            "The export includes readable encryption key words and network settings. "
            + "Save it only to a trusted location and review it before sharing.",
            "EXPORT CONFIGURATION")) {
      return;
    }
    try {
      ViewModel.NativeSettings.ExportSnapshot(path);
    } catch (Exception ex) {
      await Dialogs.Alert("Export NV modem configuration", ex.Message);
    }
  }

  private async void SaveParameters(object? sender, RoutedEventArgs e) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || ViewModel == null) {
      return;
    }

    var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save NV modem parameters",
      SuggestedFileName = ViewModel.SuggestedParameterFileName(),
      DefaultExtension = "param",
      FileTypeChoices = [ParamFiles],
    });
    string? path = file?.TryGetLocalPath();
    if (path == null || !await Dialogs.ConfirmDangerous(
            "Export NV modem parameters",
            "The export includes readable encryption key words and network settings. "
            + "Save it only to a trusted location and review it before sharing.",
            "EXPORT PARAMETERS")) {
      return;
    }

    try {
      await File.WriteAllTextAsync(path, ViewModel.ExportParameterFile());
    } catch (Exception ex) {
      await Dialogs.Alert("Save NV modem parameters", ex.Message);
    }
  }
}
