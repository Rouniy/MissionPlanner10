using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Views.Setup;

/// <summary>
/// Configures one surveyed LPS field and each modem's role in it. Saving restarts modems, so no
/// button is the default one: Enter in a field only ends the edit. The window stays open while a
/// modem is being saved, and Close goes ahead once a save stopped before its commit is cancelled.
/// </summary>
public partial class NvModemLpsFieldWindow : Window {
  private static readonly FilePickerFileType FieldDocument = new("LPS field") {
    Patterns = ["*.json"],
  };

  private readonly TabControl _tabs;
  private readonly DataGrid _beacons;
  private readonly Dictionary<NvLpsModem, TabItem> _modemTabs = [];
  private NvModemLpsFieldViewModel? _field;

  public NvModemLpsFieldWindow() {
    AvaloniaXamlLoader.Load(this);
    _tabs = this.FindControl<TabControl>("LpsTabs")!;
    _beacons = this.FindControl<DataGrid>("LpsBeacons")!;
    this.FindControl<Button>("LpsFieldApply")!.Click += (_, _) => ViewModel?.Apply();
    this.FindControl<Button>("LpsFieldClose")!.Click += (_, _) => Close();
    this.FindControl<Button>("LpsFieldRead")!.Click += (_, _) => ViewModel?.ShowSourceField();
    this.FindControl<Button>("LpsFieldImport")!.Click += Import;
    this.FindControl<Button>("LpsFieldExport")!.Click += Export;
    // A typed coordinate is taken when the field is left, and Enter leaves it.
    foreach (string name in (string[])["LpsLatitude", "LpsLongitude", "LpsDatum"]) {
      this.FindControl<TextBox>(name)!.KeyDown += (_, args) => {
        if (args.Key == Key.Enter) {
          FocusManager?.ClearFocus();
          args.Handled = true;
        }
      };
    }
    Closing += (_, args) => {
      if (ViewModel is { } field && !field.TryClose()) {
        args.Cancel = true;
      }
    };
    DataContextChanged += (_, _) => Follow(ViewModel);
    Closed += (_, _) => Follow(null);
  }

  private NvModemLpsFieldViewModel? ViewModel => DataContext as NvModemLpsFieldViewModel;

  private void Follow(NvModemLpsFieldViewModel? field) {
    if (_field != null) {
      _field.CloseRequested -= Close;
      _field.Modems.CollectionChanged -= OnModemsChanged;
      _field.PropertyChanged -= OnFieldChanged;
    }
    foreach (TabItem tab in _modemTabs.Values) {
      _tabs.Items.Remove(tab);
    }
    _modemTabs.Clear();
    _field = field;
    if (_field != null) {
      _field.CloseRequested += Close;
      _field.Modems.CollectionChanged += OnModemsChanged;
      _field.PropertyChanged += OnFieldChanged;
      AddTabs(_field.Modems);
      ShowCovariance(_field.ShowCovariance);
    }
  }

  private void OnModemsChanged(object? sender, NotifyCollectionChangedEventArgs args) {
    if (args.NewItems != null) {
      AddTabs(args.NewItems.OfType<NvLpsModem>());
    }
  }

  /// <summary>Each modem's settings, for review: the window saves only what its form holds.</summary>
  private void AddTabs(IEnumerable<NvLpsModem> modems) {
    foreach (NvLpsModem modem in modems) {
      if (_modemTabs.ContainsKey(modem)) {
        continue;
      }
      var tab = new TabItem {
        Header = modem.Name,
        Content = new Nv5NativeSettingsTable { DataContext = modem.Editor, IsReadOnly = true },
      };
      _modemTabs[modem] = tab;
      _tabs.Items.Add(tab);
    }
  }

  private void OnFieldChanged(object? sender, PropertyChangedEventArgs args) {
    if (args.PropertyName == nameof(NvModemLpsFieldViewModel.ShowCovariance) && _field != null) {
      ShowCovariance(_field.ShowCovariance);
    }
  }

  private void ShowCovariance(bool shown) {
    for (int column = NvModemLpsFieldViewModel.FirstCovarianceColumn;
         column < NvModemLpsFieldViewModel.Columns.Count; column++) {
      // The first column of the table names the row.
      _beacons.Columns[column + 1].IsVisible = shown;
    }
  }

  private async void Import(object? sender, RoutedEventArgs e) {
    if (ViewModel is not { } field) {
      return;
    }
    IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
        new FilePickerOpenOptions {
          Title = "Import LPS field",
          AllowMultiple = false,
          FileTypeFilter = [FieldDocument],
        });
    if (files.Count != 0 && files[0].TryGetLocalPath() is { } path) {
      field.ImportField(path);
    }
  }

  private async void Export(object? sender, RoutedEventArgs e) {
    if (ViewModel is not { } field) {
      return;
    }
    IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Export LPS field",
      DefaultExtension = "json",
      SuggestedFileName = "lps-field.json",
      FileTypeChoices = [FieldDocument],
    });
    if (file?.TryGetLocalPath() is { } path) {
      field.ExportField(path);
    }
  }
}
