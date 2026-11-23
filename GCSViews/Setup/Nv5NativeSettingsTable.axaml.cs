using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Views.Setup;

/// <summary>The settings table of one NV5 modem: filter, Read settings, status and the typed
/// values. Saving belongs to whoever shows it.</summary>
public partial class Nv5NativeSettingsTable : UserControl {
  /// <summary>Shows the values for review only, where the window saves values of its
  /// own.</summary>
  public static readonly StyledProperty<bool> IsReadOnlyProperty =
      AvaloniaProperty.Register<Nv5NativeSettingsTable, bool>(nameof(IsReadOnly));

  public Nv5NativeSettingsTable() {
    AvaloniaXamlLoader.Load(this);
    this.FindControl<DataGrid>("SettingsGrid")!.BeginningEdit += (_, args) => {
      if (IsReadOnly || args.Row.DataContext is Nv5NativeSettingRow { IsEditable: false }) {
        args.Cancel = true;
      }
    };
  }

  public bool IsReadOnly {
    get => GetValue(IsReadOnlyProperty);
    set => SetValue(IsReadOnlyProperty, value);
  }
}
