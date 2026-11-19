using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Views.Setup;

/// <summary>Ranging rows, one per radio or per modem, as a Distance group shows them.</summary>
public partial class NvDistanceRowsView : UserControl {
  public static readonly StyledProperty<IEnumerable<NvDistanceRow>?> RowsProperty =
      AvaloniaProperty.Register<NvDistanceRowsView, IEnumerable<NvDistanceRow>?>(nameof(Rows));

  private readonly DataGrid _grid;

  public NvDistanceRowsView() {
    AvaloniaXamlLoader.Load(this);
    _grid = this.FindControl<DataGrid>("DistanceGrid")!;
  }

  public IEnumerable<NvDistanceRow>? Rows {
    get => GetValue(RowsProperty);
    set => SetValue(RowsProperty, value);
  }

  protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
    base.OnPropertyChanged(change);
    if (change.Property == RowsProperty) {
      _grid.ItemsSource = Rows;
    }
  }
}
