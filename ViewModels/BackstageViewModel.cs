using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Services;

namespace MissionPlanner.ViewModels;

public partial class BackstagePage : ObservableObject {
  private readonly Func<ViewModelBase> _factory;
  private ViewModelBase? _content;

  public BackstagePage(
      string header,
      Func<ViewModelBase> factory,
      bool advanced = false,
      bool sub = false,
      bool requiresConnection = false,
      Func<bool>? visibleWhen = null,
      bool isHeader = false,
      string? badge = null,
      bool allowsPartialParameters = false
  ) {
    Header = header;
    _factory = factory;
    IsAdvanced = advanced;
    IsSub = sub;
    RequiresConnection = requiresConnection;
    AllowsPartialParameters = allowsPartialParameters;
    VisibleWhen = visibleWhen;
    IsHeader = isHeader;
    Badge = badge;
  }

  public string Header { get; }
  public bool IsAdvanced { get; }
  public bool IsSub { get; }
  public bool RequiresConnection { get; }
  public bool AllowsPartialParameters { get; }

  public string? Badge { get; }
  public bool HasBadge => !string.IsNullOrEmpty(Badge);

  public bool IsHeader { get; }
  public BackstagePage? Group { get; set; }

  [ObservableProperty]
  private bool _isExpanded = true;

  public Func<bool>? VisibleWhen { get; }

  [ObservableProperty]
  private bool _isSelected;

  [ObservableProperty]
  private bool _visible = true;

  public ViewModelBase Content => _content ??= _factory();
  public ViewModelBase? CreatedContent => _content;

  internal ViewModelBase? ResetContent() {
    var previous = _content;
    _content = null;
    if (previous is IDeactivationAware lifecycle) {
      lifecycle.Deactivate();
    }
    if (previous is IDisposable disposable) {
      disposable.Dispose();
    }
    return previous;
  }
}

public partial class BackstageViewModel : ViewModelBase, IDeactivationAware, IDisposable {
  public ObservableCollection<BackstagePage> Pages { get; } = new();

  [ObservableProperty]
  private BackstagePage? _selectedPage;

  [ObservableProperty]
  private ViewModelBase? _currentContent;

  private readonly string? _persistKey;

  public GCSViews.ConfigurationView.ConfigParamLoadingViewModel ParamLoading { get; } = new();

  [ObservableProperty]
  private bool _showParamLoading;

  private readonly Avalonia.Threading.DispatcherTimer _paramLoadTimer;
  private bool _disposed;

  protected BackstageViewModel(string? persistKey = null) {
    _persistKey = persistKey;
    AppState.ConnectionChanged += OnConnectionChanged;
    DisplayViewService.Changed += OnDisplayViewChanged;
    _paramLoadTimer = new Avalonia.Threading.DispatcherTimer {
      Interval = TimeSpan.FromMilliseconds(300),
    };
    _paramLoadTimer.Tick += (_, _) => {
      bool wasLoading = ShowParamLoading;
      bool connected = AppState.IsConnected;
      ShowParamLoading = ShouldShowParameterLoading(
          connected, ParamLoading.ParametersReady,
          PageBypassesParameterOverlay(SelectedPage));
      if (connected && wasLoading && !ShowParamLoading
          && CurrentContent is IActivationAware activation) {
        activation.Activate();
      }
    };
    _paramLoadTimer.Start();
  }

  private void OnConnectionChanged() {
    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) {
      RefreshForConnectionChange();
    } else {
      Avalonia.Threading.Dispatcher.UIThread.Post(RefreshForConnectionChange);
    }
  }

  private void RefreshForConnectionChange() {
    var selected = SelectedPage;
    bool resetSelected = selected?.RequiresConnection == true;
    foreach (var page in Pages.Where(page => page.RequiresConnection)) {
      page.ResetContent();
    }
    RefreshVisibility();
    if (resetSelected && ReferenceEquals(SelectedPage, selected)) {
      CurrentContent = selected!.Content;
      if (ParamLoading.ParametersReady && CurrentContent is IActivationAware activation) {
        activation.Activate();
      }
    }
  }

  private void OnDisplayViewChanged(object? sender, EventArgs e) {
    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) {
      RefreshVisibility();
    } else {
      Avalonia.Threading.Dispatcher.UIThread.Post(RefreshVisibility);
    }
  }

  protected void RefreshVisibility() {
    bool connected = AppState.IsConnected;
    foreach (var p in Pages) {
      bool vis = (!p.RequiresConnection || connected)
          && (!p.IsAdvanced || DisplayViewService.Current.isAdvancedMode)
          && (p.VisibleWhen?.Invoke() ?? true);

      p.Visible = vis && (p.Group?.IsExpanded ?? true);
    }
    if (SelectedPage is { Visible: false }) {
      SelectedPage = null;
      SelectFirst();
    }
  }

  partial void OnSelectedPageChanged(BackstagePage? oldValue, BackstagePage? newValue) {
    if (oldValue?.CreatedContent is IDeactivationAware lifecycle) {
      lifecycle.Deactivate();
    }
    if (oldValue != null) {
      oldValue.IsSelected = false;
    }
    if (newValue != null) {
      newValue.IsSelected = true;
      CurrentContent = newValue.Content;
      if ((!newValue.RequiresConnection || ParamLoading.ParametersReady)
          && CurrentContent is IActivationAware activation) {
        activation.Activate();
      }
      if (_persistKey != null && !newValue.IsSub) {
        MissionPlanner.Utilities.Settings.Instance[_persistKey] = newValue.Header;
      }
    }
    ShowParamLoading = ShouldShowParameterLoading(
        AppState.IsConnected, ParamLoading.ParametersReady,
        PageBypassesParameterOverlay(newValue));
  }

  /// <summary>
  /// Pages that work without a vehicle (firmware install, radios, modems) must stay visible while
  /// a link is open but has no parameters: during a firmware upload the primary link closes and
  /// an idle secondary listener becomes the active connection, which would otherwise replace the
  /// upload progress with the parameter-loading overlay.
  /// </summary>
  internal static bool PageBypassesParameterOverlay(BackstagePage? page) =>
      page != null && (page.AllowsPartialParameters || !page.RequiresConnection);

  internal static bool ShouldShowParameterLoading(
      bool connected, bool parametersReady, bool allowsPartialParameters) =>
      connected && !parametersReady && !allowsPartialParameters;

  public void Deactivate() {
    if (CurrentContent is IDeactivationAware lifecycle) {
      lifecycle.Deactivate();
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _paramLoadTimer.Stop();
    AppState.ConnectionChanged -= OnConnectionChanged;
    DisplayViewService.Changed -= OnDisplayViewChanged;
    foreach (var content in Pages.Select(page => page.CreatedContent).OfType<ViewModelBase>().Distinct()) {
      if (content is IDeactivationAware lifecycle) {
        lifecycle.Deactivate();
      }
      if (content is IDisposable disposable) {
        disposable.Dispose();
      }
    }
    ParamLoading.Dispose();
  }

  [RelayCommand]
  private void Select(BackstagePage? page) {
    if (page == null) {
      return;
    }

    if (page.IsHeader) {
      page.IsExpanded = !page.IsExpanded;
      RefreshVisibility();
      return;
    }
    SelectedPage = page;
  }

  public bool SelectPage(string header) {
    var page = Pages.FirstOrDefault(candidate =>
        string.Equals(candidate.Header, header, StringComparison.Ordinal));
    if (page == null || page.IsHeader) {
      return false;
    }

    if (page.Group is { IsExpanded: false } group) {
      group.IsExpanded = true;
    }
    RefreshVisibility();
    if (!page.Visible) {
      return false;
    }

    SelectedPage = page;
    return true;
  }

  private BackstagePage? _currentGroup;

  protected BackstagePage Add(
      string header,
      Func<ViewModelBase> factory,
      bool advanced = false,
      bool sub = false,
      bool requiresConnection = false,
      Func<bool>? visibleWhen = null,
      string? badge = null,
      bool allowsPartialParameters = false
  ) {
    bool isHeader = header.StartsWith(">>", StringComparison.Ordinal);
    var p = new BackstagePage(
        header, factory, advanced, sub, requiresConnection, visibleWhen, isHeader, badge,
        allowsPartialParameters);
    if (isHeader) {
      _currentGroup = p;
    } else if (sub) {
      p.Group = _currentGroup;
    }
    Pages.Add(p);
    return p;
  }

  protected void SelectFirst() {
    RefreshVisibility();
    if (SelectedPage != null) {
      return;
    }

    if (_persistKey != null
        && MissionPlanner.Utilities.Settings.Instance[_persistKey] is { Length: > 0 } last) {
      foreach (var p in Pages) {
        if (p.Visible && !p.IsSub && !p.IsHeader && p.Header == last) {
          SelectedPage = p;
          return;
        }
      }
    }
    foreach (var p in Pages) {
      if (p.Visible && !p.IsHeader) {
        SelectedPage = p;
        break;
      }
    }
  }
}
