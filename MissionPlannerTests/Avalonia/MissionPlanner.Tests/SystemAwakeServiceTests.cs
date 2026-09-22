using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class SystemAwakeServiceTests {
  [Fact]
  public void LinuxCommandUsesArgumentListWithoutShellText() {
    // BuildLinuxCommand only checks that both files exist, so stand-ins work on every platform.
    string inhibit = Path.GetTempFileName();
    string idle = Path.GetTempFileName();
    try {
      AwakeCommand? command = SystemAwakeService.BuildLinuxCommand(inhibit, idle);

      Assert.NotNull(command);
      Assert.Equal(inhibit, command!.FileName);
      Assert.Contains("--what=sleep", command.Arguments);
      Assert.Contains("--", command.Arguments);
      Assert.Contains(idle, command.Arguments);
      Assert.DoesNotContain(command.Arguments, argument => argument.Contains("sh -c"));
    } finally {
      File.Delete(inhibit);
      File.Delete(idle);
    }
  }

  [Fact]
  public void LinuxCommandRequiresBothExecutables() {
    Assert.Null(SystemAwakeService.BuildLinuxCommand("/missing/inhibit", "/bin/true"));
    Assert.Null(SystemAwakeService.BuildLinuxCommand("/bin/true", "/missing/tail"));
  }

  [Fact]
  public void MacCommandTracksOnlyCurrentApplicationProcess() {
    AwakeCommand command = SystemAwakeService.BuildMacCommand("/usr/bin/caffeinate", 12345);

    Assert.Equal(new[] { "-i", "-w", "12345" }, command.Arguments);
  }
}
