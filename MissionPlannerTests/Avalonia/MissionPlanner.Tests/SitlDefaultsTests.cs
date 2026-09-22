using MissionPlanner;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class SitlDefaultsTests {
  [Fact]
  public void Parses_single_frame_defaults_from_vehicleinfo() {
    const string source = """
        class VehicleInfo(object):
            options = {
              "ArduCopter": {
                "frames": {
                  "+": {
                    "default_params_filename": "default_params/copter.parm",
                    "external": False,
                  },
                },
              },
            }
        """;

    Assert.Equal(new[] { "default_params/copter.parm" },
        SitlLauncher.ParseDefaultParameterPaths(source, "+"));
  }

  [Fact]
  public void Python_comments_do_not_strip_hash_characters_inside_strings() {
    const string source = """
        class VehicleInfo(object):
            options = {
              "ArduCopter": {
                "frames": {
                  "frame#1": { # a real Python comment
                    "default_params_filename": "default_params/copter#1.parm",
                  },
                },
              },
            }
        """;

    Assert.Equal(new[] { "default_params/copter#1.parm" },
        SitlLauncher.ParseDefaultParameterPaths(source, "frame#1"));
  }

  [Fact]
  public void Parses_ordered_multiple_frame_defaults_case_insensitively() {
    const string source = """
        options = {
          "ArduPlane": {
            "frames": {
              "QuadPlane": {
                "default_params_filename": [
                  "default_params/plane.parm",
                  "default_params/quadplane.parm",
                ],
              },
            },
          },
        }
        """;

    Assert.Equal(new[] {
      "default_params/plane.parm", "default_params/quadplane.parm",
    }, SitlLauncher.ParseDefaultParameterPaths(source, "quadplane"));
  }

  [Fact]
  public void Missing_frame_has_no_automatic_defaults() {
    Assert.Empty(SitlLauncher.ParseDefaultParameterPaths(
        "options = {\"Plane\": {\"frames\": {}}}", "unknown"));
  }

  [Fact]
  public void Multilink_swarm_plan_matches_official_instance_layout() {
    IReadOnlyList<SitlSwarmInstancePlan> plans = SitlLauncher.BuildSwarmPlan(
        -35.3633515, 149.1652412, 584, 90, 3, chained: false);

    Assert.Equal(new[] { 2, 1, 0 }, plans.Select(plan => plan.Instance));
    Assert.Equal(new[] { 3, 2, 1 }, plans.Select(plan => plan.SystemId));
    Assert.All(plans, plan => Assert.Null(plan.SecondarySerialClientPort));
    Assert.Equal(new[] { 5780, 5770, 5760 },
        plans.Select(plan => SitlLauncher.TcpPortForInstance(plan.Instance)));

    var origin = new PointLatLngAlt(-35.3633515, 149.1652412, 584);
    double[] distances = plans.Select(plan => {
      string[] fields = plan.Home.Split(',');
      var position = new PointLatLngAlt(
          double.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture),
          double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
          double.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture));
      Assert.Equal("90", fields[3]);
      return origin.GetDistance(position);
    }).ToArray();
    Assert.InRange(distances[0], 7.99, 8.01);
    Assert.InRange(distances[1], 3.99, 4.01);
    Assert.InRange(distances[2], 0, 0.001);
  }

  [Fact]
  public void Single_link_swarm_chains_each_lower_instance_to_the_next() {
    IReadOnlyList<SitlSwarmInstancePlan> plans = SitlLauncher.BuildSwarmPlan(
        1, 2, 3, 45, 4, chained: true);

    Assert.Equal(new int?[] { null, 5792, 5782, 5772 },
        plans.Select(plan => plan.SecondarySerialClientPort));
  }

  [Fact]
  public void Swarm_identity_parameters_match_official_mission_planner() {
    string identity = SitlLauncher.BuildIdentityParameters(17);

    // The launcher writes LF on every platform; the raw literal takes this file's line endings,
    // which are CRLF in a Windows checkout.
    Assert.Equal("""
        SERIAL0_PROTOCOL=2
        SERIAL1_PROTOCOL=2
        SYSID_THISMAV=17
        MAV_SYSID=17
        SIM_TERRAIN=0
        TERRAIN_ENABLE=0
        SCHED_LOOP_RATE=50
        SIM_RATE_HZ=400
        SIM_DRIFT_SPEED=0
        SIM_DRIFT_TIME=0

        """.ReplaceLineEndings("\n"), identity);
  }

  [Fact]
  public void Swarm_launch_arguments_keep_instance_defaults_and_chain_endpoint() {
    string arguments = SitlLauncher.BuildLaunchArguments(
        "+", "-35.3,149.1,584,90", 2, 3,
        "/tmp/frame.parm,/tmp/identity.parm", "--uartA udpclient:127.0.0.1:9000",
        wipeEeprom: true, secondarySerialClientPort: 5802);

    Assert.Contains("--model \"+\"", arguments);
    Assert.Contains("--instance 3 --serial0 tcp:0", arguments);
    Assert.Contains("--serial2 tcpclient:127.0.0.1:5802", arguments);
    Assert.Contains("--defaults \"/tmp/frame.parm,/tmp/identity.parm\"", arguments);
    Assert.Contains("--wipe --uartA udpclient:127.0.0.1:9000", arguments);
  }

  [Fact]
  public void Xplane_launch_uses_the_selected_external_model() {
    string arguments = SitlLauncher.BuildLaunchArguments(
        "xplane", "-35.3,149.1,584,90", 1, 0,
        defaults: null, extraCmdline: null, wipeEeprom: false,
        secondarySerialClientPort: null);

    Assert.Contains("--model \"xplane\"", arguments);
    Assert.Contains("--instance 0 --serial0 tcp:0", arguments);
  }

  [Fact]
  public void Sitl_child_path_finds_runtime_dependencies_and_working_files_first() {
    string childPath = SitlLauncher.BuildChildPath(
        Path.Combine("cache", "sitl"), Path.Combine("cache", "sitl", "xplane"),
        Path.Combine("system", "bin"));

    Assert.Equal(string.Join(Path.PathSeparator,
        Path.Combine("cache", "sitl"),
        Path.Combine("cache", "sitl", "xplane"),
        Path.Combine("system", "bin")), childPath);
  }

  [Theory]
  [InlineData(1)]
  [InlineData(51)]
  public void Swarm_plan_rejects_unsafe_instance_counts(int count) {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        SitlLauncher.BuildSwarmPlan(0, 0, 0, 0, count, chained: false));
  }

  [Fact]
  [Trait("Category", "Integration")]
  public async Task Linux_xplane_sitl_reuses_its_first_telemetry_connection() {
    if (!OperatingSystem.IsLinux() ||
        Environment.GetEnvironmentVariable("MP_RUN_SITL_INTEGRATION") != "1") {
      return;
    }

    AppPaths.Initialize();
    string binary = Path.Combine(AppPaths.SitlCacheRoot, "ArduPlane");
    Assert.True(File.Exists(binary), $"Cached SITL binary not found: {binary}");
    var launcher = new SitlLauncher();
    TcpSerial? stream = null;
    try {
      Assert.True(await launcher.StartAsync(new SitlStartOptions {
        Vehicle = SitlVehicle.Plane,
        Channel = SitlChannel.Skip,
        Model = "xplane",
        Home = "-35.3633515,149.1652412,584,0",
        ReserveTelemetryConnection = true,
      }));

      stream = launcher.TakePreparedTelemetryStream();
      Assert.NotNull(stream);
      Assert.True(stream.IsOpen);
      Assert.Null(launcher.TakePreparedTelemetryStream());
    } finally {
      stream?.Dispose();
      launcher.Stop();
    }
  }

  [Fact]
  [Trait("Category", "Integration")]
  public async Task Linux_cached_binary_starts_two_independent_swarm_instances() {
    if (!OperatingSystem.IsLinux() ||
        Environment.GetEnvironmentVariable("MP_RUN_SITL_INTEGRATION") != "1") {
      return;
    }

    string binary = Path.Combine(AppPaths.SitlCacheRoot, "ArduCopter");
    Assert.True(File.Exists(binary), $"Cached SITL binary not found: {binary}");
    IReadOnlyList<SitlSwarmInstancePlan> plans = SitlLauncher.BuildSwarmPlan(
        -35.3633515, 149.1652412, 584, 90, 2, chained: false);
    var launchers = new List<SitlLauncher>();
    try {
      foreach (SitlSwarmInstancePlan plan in plans) {
        var launcher = new SitlLauncher();
        launchers.Add(launcher);
        Assert.True(await launcher.StartAsync(new SitlStartOptions {
          Vehicle = SitlVehicle.Copter,
          Channel = SitlChannel.Skip,
          Home = plan.Home,
          Instance = plan.Instance,
          SystemId = plan.SystemId,
          UseIdentityParameters = true,
        }));
      }

      Assert.All(launchers, launcher => Assert.True(launcher.IsRunning));
      Assert.Equal(new[] { 5770, 5760 }, launchers.Select(launcher => launcher.TcpPort));

      using var manager = new MavLinkConnectionManager(new MAVLinkInterface());
      ConnectionListEndpoint[] endpoints = [.. plans
          .OrderBy(plan => plan.Instance)
          .Select(plan => new ConnectionListEndpoint(
              ConnectionListTransport.TcpClient, "127.0.0.1",
              SitlLauncher.TcpPortForInstance(plan.Instance), "", 0,
              plan.SystemId))];
      ConnectionListOpenResult opened = await ConnectionListService.OpenEndpointsAsync(
          endpoints, manager, openTelemetryLogs: false);
      Assert.Empty(opened.Failures);
      Assert.Equal(2, opened.Opened.Count);
      Assert.Equal(new byte[] { 1, 2 }, opened.Opened
          .SelectMany(connection => connection.Link.MAVlist.ToArray())
          .Where(mav => mav.sysid is 1 or 2)
          .Select(mav => mav.sysid)
          .Distinct()
          .OrderBy(sysid => sysid));
      foreach (MavLinkConnection connection in opened.Opened) {
        Assert.True(await manager.RemoveAsync(connection));
      }
    } finally {
      foreach (SitlLauncher launcher in launchers) {
        launcher.Stop();
      }
    }
  }
}
