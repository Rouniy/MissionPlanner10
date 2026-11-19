using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Views.Setup;

/// <summary>
/// Configures ranging between two NV5 modems. Saving restarts both modems, so no button is the
/// default one: Enter in a field only ends the edit. The window stays open until both save
/// outcomes are known.
/// </summary>
public partial class NvModemDistancePairWindow : Window {
  public NvModemDistancePairWindow() {
    AvaloniaXamlLoader.Load(this);
    this.FindControl<Button>("PairApply")!.Click += (_, _) => ViewModel?.Apply();
    this.FindControl<Button>("PairClose")!.Click += (_, _) => Close();
    // Rows are reused for other settings, so each one is marked anew as it is shown.
    this.FindControl<DataGrid>("PairComparison")!.LoadingRow += (_, args) => {
      var row = args.Row.DataContext as NvPairComparisonRow;
      args.Row.Classes.Set("marked", row?.Marked == true);
      args.Row.Classes.Set("problem", row?.Kind == NvPairComparisonKind.Problem);
    };
    Closing += (_, args) => {
      if (ViewModel is { } pair && !pair.TryClose()) {
        args.Cancel = true;
      }
    };
  }

  private NvModemDistancePairViewModel? ViewModel => DataContext as NvModemDistancePairViewModel;
}
