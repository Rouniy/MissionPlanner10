using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlanner;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services;

public class LoadingBox : Window {
  private readonly TextBlock _label;

  public LoadingBox(string title, string prompt) {
    Title = title;
    Width = 300;
    SizeToContent = SizeToContent.Height;
    CanResize = false;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Background = new SolidColorBrush(Color.Parse("#262728"));
    _label = new TextBlock {
      Text = prompt,
      Foreground = Brushes.WhiteSmoke,
      Margin = new Thickness(20),
      HorizontalAlignment = HorizontalAlignment.Center,
    };
    Content = _label;
  }

  public void SetText(string text) => Dispatcher.UIThread.Post(() => _label.Text = text);

  public void Show2() {
    var owner = Dialogs.Owner;
    if (owner != null) {
      Show(owner);
    } else {
      Show();
    }
  }
}

public class ProgressReporter : Window {
  private readonly ProgressBar _bar = new() { Maximum = 100, Height = 18, Margin = new Thickness(0, 8, 0, 8) };
  private readonly TextBlock _status = new() { Foreground = Brushes.WhiteSmoke };
  private readonly Button _cancel = new() {
    Content = "Cancel",
    MinWidth = 80,
    HorizontalAlignment = HorizontalAlignment.Right,
  };
  private readonly CancellationTokenSource _cts = new();
  private readonly CancellationToken _token;
  private int _cancellationDisposed;
  private int _completed;

  public CancellationToken Token => _token;
  public bool CancelRequested => _cts.IsCancellationRequested;
  internal bool KeepConnectionOpenOnCancel { get; set; }

  /// <summary>
  /// When set, closing the window (title-bar close) only hides the dialog: the work keeps running
  /// and reports through the owner's own progress display. The cancel button still cancels.
  /// </summary>
  internal bool ContinueInBackgroundOnClose { get; set; }

  /// <summary>True once the window was closed without cancelling the work.</summary>
  internal bool Detached { get; private set; }

  internal bool IsClosed { get; private set; }

  public ProgressReporter(string title) {
    _token = _cts.Token;
    Title = title;
    Width = 360;
    SizeToContent = SizeToContent.Height;
    CanResize = false;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Background = new SolidColorBrush(Color.Parse("#262728"));
    Closed += (_, _) => { IsClosed = true; DisposeCancellation(); };
    _cancel.Click += (_, _) => RequestCancellation();
    Content = new StackPanel {
      Margin = new Thickness(16),
      Children = { _status, _bar, _cancel },
    };
  }

  public void Set(double percent, string status) => Dispatcher.UIThread.Post(() => {
    _bar.Value = Math.Clamp(percent, 0, 100);
    _status.Text = status;
  });

  public void SetCancellationText(string text) {
    if (Dispatcher.UIThread.CheckAccess()) {
      _cancel.Content = text;
    } else {
      Dispatcher.UIThread.Post(() => _cancel.Content = text);
    }
  }

  internal string CancellationText => _cancel.Content?.ToString() ?? "";

  public void Show2() {
    var owner = Dialogs.Owner;
    if (owner != null) {
      Show(owner);
    } else {
      Show();
    }

    Activate();
  }

  /// <summary>
  /// Closes a successfully completed operation without turning normal dialog teardown into a
  /// cancellation request. User/window Close still cancels outstanding work.
  /// </summary>
  public void Complete() {
    Interlocked.Exchange(ref _completed, 1);
    if (!IsClosed) {
      Close();
    }
  }

  internal void RequestCancellation() {
    try {
      _cts.Cancel();
    } catch (ObjectDisposedException) {
    }
  }

  private void DisposeCancellation() {
    if (Volatile.Read(ref _completed) == 0) {
      if (ContinueInBackgroundOnClose && !_cts.IsCancellationRequested) {
        // The operation still owns the token; keep it alive and let the caller finish normally.
        Detached = true;
        return;
      }
      RequestCancellation();
    }
    if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0) {
      _cts.Dispose();
    }
  }
}

public class ForwardingProgressReporter : IProgressReporterDialogue {
  private readonly ProgressReporter? _target;
  private readonly CancellationTokenRegistration _targetCancellation;
  private readonly CancellationTokenRegistration _operationCancellation;

  public ProgressWorkerEventArgs doWorkArgs { get; set; } = new();
  public event DoWorkEventHandler? DoWork;

  public ForwardingProgressReporter(
      ProgressReporter? target,
      CancellationToken operationCancellation = default) {
    _target = target;
    doWorkArgs.KeepConnectionOpenOnCancel = target?.KeepConnectionOpenOnCancel ?? false;
    try {
      _targetCancellation = _target?.Token.Register(RequestCancellation) ?? default;
    } catch (ObjectDisposedException) {
    }
    _operationCancellation = operationCancellation.CanBeCanceled
        ? operationCancellation.Register(RequestCancellation)
        : default;
  }

  private void RequestCancellation() => doWorkArgs.CancelRequested = true;

  // The upstream contract is synchronous: callers expect DoWork to be finished on return, so
  // the delegate runs inline on the calling thread. Call sites must not be on the UI thread.
  public void RunBackgroundOperationAsync() {
    try {
      DoWork?.Invoke(this);
    } catch (Exception e) {
      doWorkArgs.ErrorMessage = e.Message;
    }
  }

  public void UpdateProgressAndStatus(int progress, string status) =>
      _target?.Set(progress < 0 ? 0 : progress, status);

  public void BeginInvoke(Delegate method) =>
      Dispatcher.UIThread.Post(() => method?.DynamicInvoke());

  public void Dispose() {
    _targetCancellation.Dispose();
    _operationCancellation.Dispose();
  }
}

/// <summary>
/// Carries a per-operation cancellation token through the upstream progress factory. AsyncLocal
/// keeps parallel Connection List opens isolated while preserving the normal primary-link dialog.
/// </summary>
internal static class MavLinkProgressContext {
  private static readonly AsyncLocal<CancellationToken?> _current = new();

  static MavLinkProgressContext() {
    MAVLinkInterface.CreateIProgressReporterDialogue += _ =>
        new ForwardingProgressReporter(AppState.ActiveConnectReporter, _current.Value ?? default);
  }

  internal static void EnsureRegistered() {
  }

  internal static IDisposable Use(CancellationToken cancellationToken) {
    CancellationToken? previous = _current.Value;
    _current.Value = cancellationToken;
    return new Scope(previous);
  }

  private sealed class Scope(CancellationToken? previous) : IDisposable {
    private CancellationToken? _previous = previous;

    public void Dispose() {
      _current.Value = _previous;
      _previous = null;
    }
  }
}

/// <summary>
/// Gives synchronous upstream operations a cancellation source without showing another dialog.
/// Mission Planner's parameter protocol observes doWorkArgs.CancelRequested at every retry.
/// </summary>
internal sealed class CancellationProgressReporter : IProgressReporterDialogue {
  private readonly CancellationTokenRegistration _registration;
  private readonly Action<int, string>? _progress;

  public ProgressWorkerEventArgs doWorkArgs { get; set; } = new();
  public event DoWorkEventHandler? DoWork;

  internal CancellationProgressReporter(
      CancellationToken cancellationToken,
      Action<int, string>? progress = null) {
    _progress = progress;
    _registration = cancellationToken.Register(() => doWorkArgs.CancelRequested = true);
  }

  public void RunBackgroundOperationAsync() => DoWork?.Invoke(this);

  public void UpdateProgressAndStatus(int progress, string status) =>
      _progress?.Invoke(progress, status);

  public void BeginInvoke(Delegate method) => method?.DynamicInvoke();

  public void Dispose() => _registration.Dispose();
}
