using Avalonia.Headless.XUnit;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class ProgressReporterTests {
  [AvaloniaFact]
  public void Closing_progress_window_cancels_work_and_keeps_captured_token_readable() {
    var reporter = new ProgressReporter("Test operation");
    CancellationToken token = reporter.Token;
    reporter.Show();

    reporter.Close();

    Assert.True(token.IsCancellationRequested);
    Assert.True(reporter.CancelRequested);
    Assert.Equal(token, reporter.Token);
  }

  [AvaloniaFact]
  public void Completing_progress_window_does_not_cancel_successful_work() {
    var reporter = new ProgressReporter("Completed operation");
    CancellationToken token = reporter.Token;
    bool cancellationCallbackRan = false;
    using CancellationTokenRegistration registration = token.Register(
        () => cancellationCallbackRan = true);
    reporter.Show();

    reporter.Complete();

    Assert.False(token.IsCancellationRequested);
    Assert.False(reporter.CancelRequested);
    Assert.False(cancellationCallbackRan);
    Assert.Equal(token, reporter.Token);
  }

  [AvaloniaFact]
  public void Closing_the_window_during_a_background_capable_stage_keeps_the_work_running() {
    var reporter = new ProgressReporter("Connecting Mavlink") { ContinueInBackgroundOnClose = true };
    CancellationToken token = reporter.Token;
    reporter.Show();

    reporter.Close();

    Assert.False(token.IsCancellationRequested);
    Assert.False(reporter.CancelRequested);
    Assert.True(reporter.Detached);
    Assert.True(reporter.IsClosed);
    // The owner still drives the dialog object after it is gone; that must stay harmless.
    reporter.Set(100, "Connected. 900 params.");
    reporter.SetCancellationText("Close");
    reporter.Complete();
    Assert.False(token.IsCancellationRequested);
  }

  [AvaloniaFact]
  public void Skip_button_still_cancels_when_closing_would_continue_in_background() {
    var reporter = new ProgressReporter("Connecting Mavlink") { ContinueInBackgroundOnClose = true };
    CancellationToken token = reporter.Token;
    reporter.Show();

    reporter.RequestCancellation();
    reporter.Close();

    Assert.True(token.IsCancellationRequested);
    Assert.True(reporter.CancelRequested);
    Assert.False(reporter.Detached);
  }

  [AvaloniaFact]
  public void Cancellation_action_can_describe_a_non_destructive_parameter_skip() {
    var reporter = new ProgressReporter("Parameter loading");

    reporter.SetCancellationText("Skip Parameters");

    Assert.Equal("Skip Parameters", reporter.CancellationText);
  }

  [AvaloniaFact]
  public void Persistent_transport_policy_is_forwarded_to_upstream_open() {
    var reporter = new ProgressReporter("UDP initialization") {
      KeepConnectionOpenOnCancel = true,
    };
    var forwarding = new ForwardingProgressReporter(reporter);

    try {
      Assert.True(forwarding.doWorkArgs.KeepConnectionOpenOnCancel);
    } finally {
      forwarding.Dispose();
    }
  }
}
