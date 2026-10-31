using Avalonia.Headless.XUnit;
using MissionPlanner.Controls;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

/// <summary>
/// The horizon must move continuously between telemetry samples: a physically rotated autopilot
/// streaming ATTITUDE at 4–10 Hz showed one jerk per packet with the old exponential approach.
/// </summary>
public sealed class HudAttitudeSmoothingTests {
  [Fact]
  public void Regular_samples_move_at_constant_speed_and_land_when_the_next_sample_is_due() {
    var tween = new HudControl.SampleTween();
    tween.Set(0, 0);
    Assert.Equal(0, tween.ValueAt(50));
    tween.Set(10, 100);
    Assert.Equal(100, tween.DurationMs);
    Assert.Equal(2.5, tween.ValueAt(125), 6);
    Assert.Equal(5, tween.ValueAt(150), 6);
    Assert.Equal(10, tween.ValueAt(200), 6);
    Assert.Equal(10, tween.ValueAt(260), 6);
    Assert.False(tween.IsMoving(200));
    tween.Set(20, 200);
    Assert.True(tween.IsMoving(250));
    Assert.Equal(15, tween.ValueAt(250), 6);
    // A late sample starts from where the display actually is, so speed stays continuous.
    tween.Set(30, 330);
    Assert.Equal(20, tween.ValueAt(330), 6);
    Assert.InRange(tween.DurationMs, 100, 130);
  }

  [Fact]
  public void Sample_interval_is_clamped_and_long_idle_gaps_do_not_slow_later_motion() {
    var fast = new HudControl.SampleTween();
    fast.Set(0, 0);
    fast.Set(1, 5);
    Assert.Equal(HudControl.SampleTween.MinDurationMs, fast.DurationMs);

    var slow = new HudControl.SampleTween();
    slow.Set(0, 0);
    slow.Set(1, 5000);
    Assert.Equal(HudControl.SampleTween.MaxDurationMs, slow.DurationMs);

    var idle = new HudControl.SampleTween();
    idle.Set(0, 0);
    idle.Set(1, 100);
    idle.Set(2, 200);
    idle.Set(2, 300); // unchanged value: not a sample
    idle.Set(3, 5000); // vehicle sat still, then moved again
    Assert.Equal(100, idle.DurationMs);
    Assert.Equal(2.5, idle.ValueAt(5050), 6);
  }

  [Fact]
  public void Yaw_takes_the_short_way_round_and_snap_stops_motion() {
    var yaw = new HudControl.SampleTween(angle: true);
    yaw.Set(350, 0);
    yaw.Set(10, 100);
    Assert.Equal(355, yaw.ValueAt(125), 6);
    Assert.Equal(0, yaw.ValueAt(150), 6);
    Assert.Equal(5, yaw.ValueAt(175), 6);
    yaw.Set(300, 200);
    Assert.Equal(335, yaw.ValueAt(250), 6);

    yaw.Snap(90);
    Assert.False(yaw.IsMoving(251));
    Assert.Equal(90, yaw.ValueAt(251));
    Assert.Equal(90, yaw.Target);
  }

  [AvaloniaFact]
  public void Hud_draws_the_first_sample_immediately_and_tweens_the_next() {
    var hud = new HudControl { Roll = 12, Pitch = -4, Yaw = 227 };
    Assert.Equal(12, hud.EasedRoll);
    Assert.Equal(-4, hud.EasedPitch);
    Assert.Equal(227, hud.EasedYaw);
    hud.Roll = 40;
    Assert.InRange(hud.EasedRoll, 12, 39.99);
    hud.SnapToValues();
    Assert.Equal(40, hud.EasedRoll);
  }

  [Fact]
  public void Attitude_is_copied_to_the_hud_much_faster_than_the_rest_of_the_panel() {
    Assert.Equal(TimeSpan.FromMilliseconds(20), FlightDataViewModel.AttitudeInterval(false));
    Assert.Equal(TimeSpan.FromMilliseconds(50), FlightDataViewModel.AttitudeInterval(true));
    Assert.Equal(10, CurrentState.DefaultAttitudeRate);
    Assert.Equal(CurrentState.DefaultAttitudeRate, CurrentState.rateattitudebackup);
  }
}
