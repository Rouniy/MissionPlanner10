using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using MissionPlanner.Controls;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

/// <summary>
/// The HUD shows the newest attitude as soon as it is received: no easing, which looked like
/// "turn, pause, turn" at the 4 Hz stream rate when the autopilot was rotated by hand.
/// </summary>
public sealed class HudAttitudeSmoothingTests {
  [AvaloniaFact]
  public void Hud_renders_the_bound_attitude_without_easing() {
    var hud = new HudControl { Roll = 12, Pitch = -4, Yaw = 227 };
    hud.Measure(new Size(320, 240));
    hud.Arrange(new Rect(0, 0, 320, 240));
    using var target = new RenderTargetBitmap(new PixelSize(320, 240));
    target.Render(hud);
    hud.Roll = 40;
    target.Render(hud);
    Assert.Equal(40, hud.Roll);
    Assert.DoesNotContain(typeof(HudControl).GetFields(
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
        field => field.FieldType == typeof(Avalonia.Threading.DispatcherTimer));
  }

  [Fact]
  public void Attitude_is_copied_to_the_hud_faster_than_the_rest_of_the_panel_and_the_stream_default_is_upstream() {
    Assert.Equal(TimeSpan.FromMilliseconds(20), FlightDataViewModel.AttitudeInterval(false));
    Assert.Equal(TimeSpan.FromMilliseconds(50), FlightDataViewModel.AttitudeInterval(true));
    Assert.Equal(4, CurrentState.rateattitudebackup);
  }
}
