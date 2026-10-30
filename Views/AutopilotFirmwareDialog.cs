using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MissionPlanner.Services;

namespace MissionPlanner.Views;

/// <summary>
/// Lets the operator pick which ArduPilot build to flash to the detected flight controller.
/// Nothing is downloaded or flashed until the operator confirms here.
/// </summary>
internal sealed class AutopilotFirmwareDialog : Window {
  private readonly ListBox _list;
  private AutopilotFirmwareOption? _result;

  internal AutopilotFirmwareDialog(string header, IReadOnlyList<AutopilotFirmwareOption> options, AutopilotFirmwareOption? selected) {
    Title = "Flight controller firmware";
    Width = 760; Height = 480; MinWidth = 520; MinHeight = 320;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    var headerBlock = new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap };
    _list = new ListBox {
      Name = "FirmwareOptions",
      ItemsSource = options,
      SelectedItem = selected,
      // Virtualised lists recycle containers by rebuilding the template with null; never dereference it.
      ItemTemplate = new FuncDataTemplate<AutopilotFirmwareOption>((option, _) => option == null ? new Panel() : new StackPanel {
        Spacing = 2, Margin = new Thickness(2),
        Children = {
          new TextBlock { Text = option.Title, FontWeight = FontWeight.Bold },
          new TextBlock { Text = option.Label, Opacity = 0.8, TextWrapping = TextWrapping.Wrap },
          new TextBlock { Text = option.Url.ToString(), Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap },
        },
      }),
    };
    var install = new Button { Content = "Download and flash", Name = "InstallFirmware", IsEnabled = _list.SelectedItem != null };
    _list.SelectionChanged += (_, _) => install.IsEnabled = _list.SelectedItem != null;
    var note = new TextBlock {
      TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
      Text = "The board reboots into its bootloader, receives the image over USB and restarts; the "
          + "MAVLink connection closes for the upload. Keep the vehicle disarmed and powered. "
          + "Stable for the board's own hwdef variant is the safest choice.",
    };
    install.Click += (_, _) => { _result = _list.SelectedItem as AutopilotFirmwareOption; Close(); };
    var cancel = new Button { Content = "Cancel" };
    cancel.Click += (_, _) => Close();
    Content = new Avalonia.Controls.Grid {
      Margin = new Thickness(12), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8,
      Children = {
        Row(headerBlock, 0), Row(_list, 1), Row(note, 2),
        Row(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
          Children = { install, cancel } }, 3),
      },
    };
  }

  internal ListBox OptionList => _list;

  private static Control Row(Control control, int row) { Avalonia.Controls.Grid.SetRow(control, row); return control; }

  internal static async Task<AutopilotFirmwareOption?> ShowAsync(Window? owner, string header,
      IReadOnlyList<AutopilotFirmwareOption> options, AutopilotFirmwareOption? selected) {
    var dialog = new AutopilotFirmwareDialog(header, options, selected ?? options.FirstOrDefault());
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
