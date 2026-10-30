using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Views;

/// <summary>
/// Lets the operator pick which AP_Periph build to send to the selected DroneCAN node. The list
/// comes from <see cref="ConfigDroneCanViewModel.FirmwareOptions"/>; nothing is downloaded or
/// flashed until the operator confirms here.
/// </summary>
internal sealed class DroneCanFirmwareDialog : Window {
  private readonly ListBox _list;
  private DroneCanFirmwareOption? _result;

  internal DroneCanFirmwareDialog(ConfigDroneCanViewModel viewModel) {
    Title = "DroneCAN node firmware";
    Width = 720; Height = 420; MinWidth = 520; MinHeight = 300;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var header = new TextBlock { Text = viewModel.FirmwareCheckStatus, TextWrapping = TextWrapping.Wrap };
    _list = new ListBox {
      Name = "FirmwareOptions",
      ItemsSource = viewModel.FirmwareOptions,
      SelectedItem = viewModel.SelectedFirmwareOption,
      ItemTemplate = new FuncDataTemplate<DroneCanFirmwareOption>((option, _) => new StackPanel {
        Spacing = 2, Margin = new Thickness(2),
        Children = {
          new TextBlock { Text = $"{option.Channel} {option.Version}" + (option.Installed ? "  (installed)" : ""), FontWeight = FontWeight.Bold },
          new TextBlock { Text = option.Label, Opacity = 0.8, TextWrapping = TextWrapping.Wrap },
          new TextBlock { Text = option.Url.ToString(), Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap },
        },
      }),
    };
    var install = new Button { Content = "Download and flash", Name = "InstallFirmware", IsEnabled = _list.SelectedItem != null };
    _list.SelectionChanged += (_, _) => {
      viewModel.SelectedFirmwareOption = _list.SelectedItem as DroneCanFirmwareOption;
      install.IsEnabled = _list.SelectedItem != null;
    };
    var note = new TextBlock {
      TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
      Text = "The node reboots into its bootloader, receives the image over the CAN bus and restarts. "
          + "Keep the vehicle disarmed and powered; stable is the safest choice.",
    };
    install.Click += (_, _) => { _result = _list.SelectedItem as DroneCanFirmwareOption; Close(); };
    var cancel = new Button { Content = "Cancel" };
    cancel.Click += (_, _) => Close();
    Content = new Avalonia.Controls.Grid {
      Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8,
      Children = {
        Row(header, 0), Row(_list, 1), Row(note, 2),
        Row(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
          Children = { install, cancel } }, 3),
      },
    };
  }

  private static Control Row(Control control, int row) { Avalonia.Controls.Grid.SetRow(control, row); return control; }

  internal static async Task<DroneCanFirmwareOption?> ShowAsync(Window? owner, ConfigDroneCanViewModel viewModel) {
    var dialog = new DroneCanFirmwareDialog(viewModel);
    if (owner != null) {
      await dialog.ShowDialog(owner);
    } else {
      var closed = new TaskCompletionSource();
      dialog.Closed += (_, _) => closed.TrySetResult();
      dialog.Show();
      await closed.Task;
    }
    return dialog._result;
  }
}
