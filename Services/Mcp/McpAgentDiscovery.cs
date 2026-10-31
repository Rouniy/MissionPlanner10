using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.Services.Mcp;

internal enum McpAgentKind { CodexCli, ClaudeCode, OpenAiDesktop, ClaudeDesktop, LmStudio }
internal sealed record McpAgent(McpAgentKind Kind, string Name, string Executable, string[] Arguments) {
  internal bool IsDesktop => Kind is McpAgentKind.OpenAiDesktop or McpAgentKind.ClaudeDesktop or McpAgentKind.LmStudio;
  public override string ToString() => Name;
}

/// <summary>Discovery never starts an agent or infers desktop support from a CLI URL handler.</summary>
internal static class McpAgentDiscovery {
  /// <summary>The terminal agent kind an executable name implies, so Codex is never started with Claude flags or vice versa.</summary>
  internal static McpAgentKind? TerminalKind(string executable) {
    string name = Path.GetFileNameWithoutExtension(executable);
    if (name.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) { return McpAgentKind.ClaudeCode; }
    if (name.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) { return McpAgentKind.CodexCli; }
    return null;
  }
  internal static string? FindExecutable(string name, IEnumerable<string> directories) {
    foreach (string directory in directories.Where(d => !string.IsNullOrWhiteSpace(d) && Path.IsPathRooted(d)).Distinct()) {
      string path = Path.Combine(directory, name);
      try {
        if (File.Exists(path) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path)
            & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)) { return path; }
      } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    return null;
  }

  internal static async Task<McpAgent[]> DiscoverAsync(CancellationToken ct = default) {
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Concat(new[] { Path.Combine(home, ".local", "bin"), Path.Combine(home, ".cargo", "bin"),
          Path.Combine(local, "Programs", "Claude"), "/opt/homebrew/bin", "/usr/local/bin" }).ToArray();
    var result = new List<McpAgent>();
    foreach (var (name, kind, label) in new[] { ("codex", McpAgentKind.CodexCli, "Codex CLI"), ("claude", McpAgentKind.ClaudeCode, "Claude Code") }) {
      string? executable = FindExecutable(name + (OperatingSystem.IsWindows() ? ".exe" : ""), paths);
      if (executable != null) { result.Add(new(kind, label, executable, [])); }
    }
    if (OperatingSystem.IsLinux()) {
      string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
      string[] dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share").Split(':');
      string? gio = FindExecutable("gio", paths);
      if (gio != null) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in new[] { dataHome }.Concat(dataDirs).Where(Path.IsPathRooted)) {
          foreach (string file in DesktopEntryFiles) {
            ct.ThrowIfCancellationRequested();
            // Vendors spell these files inconsistently (Claude Desktop ships com.anthropic.Claude.desktop), and
            // Linux file systems are case sensitive, so match the directory listing instead of guessing the case.
            string? path = FindDesktopFile(Path.Combine(dir, "applications"), file);
            try {
              if (path == null || !seen.Add(file)) { continue; }
              string entry = File.ReadAllText(path);
              string? label = DesktopEntryName(entry);
              string? program = DesktopProgram(entry);
              if (program == null || (Path.IsPathRooted(program) ? !File.Exists(program) : FindExecutable(program, paths) == null)) { continue; }
              if (label != null && !result.Any(a => a.IsDesktop && a.Name == label)) {
                var kind = label == "Claude Desktop" ? McpAgentKind.ClaudeDesktop : label == "LM Studio" ? McpAgentKind.LmStudio : McpAgentKind.OpenAiDesktop;
                result.Add(new(kind, label, gio, ["launch", path]));
              }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
          }
        }
      }
    } else if (OperatingSystem.IsMacOS()) {
      foreach (string name in new[] { "Codex", "ChatGPT", "Claude", "LM Studio" }) {
        string? app = new[] { Path.Combine(home, "Applications", name + ".app"), "/Applications/" + name + ".app" }
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "Contents", "Info.plist")) && Directory.Exists(Path.Combine(p, "Contents", "MacOS")));
        if (app != null) { result.Add(new(name == "Claude" ? McpAgentKind.ClaudeDesktop : name == "LM Studio" ? McpAgentKind.LmStudio : McpAgentKind.OpenAiDesktop,
            name == "LM Studio" ? name : name + " Desktop", "/usr/bin/open", ["-a", app])); }
      }
    } else if (OperatingSystem.IsWindows()) {
      // Get-StartApps covers MSIX/Store installs without touching protected WindowsApps directories.
      var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
      };
      foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
          "Get-StartApps | Where-Object { $_.Name -in @('Codex','ChatGPT','Claude','LM Studio') } | ConvertTo-Json -Compress" }) { start.ArgumentList.Add(arg); }
      try {
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try {
          var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
          var errors = process.StandardError.ReadToEndAsync(timeout.Token);
          await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
          string json = await output.ConfigureAwait(false); _ = await errors.ConfigureAwait(false);
          if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(json)) {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
            foreach (var item in items) {
              string? id = item.GetProperty("AppID").GetString(), name = item.GetProperty("Name").GetString();
              if (id != null && id.Length < 250 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '!')) {
                result.Add(new(name == "Claude" ? McpAgentKind.ClaudeDesktop : name == "LM Studio" ? McpAgentKind.LmStudio : McpAgentKind.OpenAiDesktop,
                    name == "LM Studio" ? name : name + " Desktop", "shell:AppsFolder\\" + id, []));
              }
            }
          }
        } finally { if (!process.HasExited) { process.Kill(true); } }
      } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException) {
        ct.ThrowIfCancellationRequested();
      }
    }
    return result.ToArray();
  }

  internal static readonly string[] DesktopEntryFiles = [
    "codex.desktop", "chatgpt.desktop", "com.openai.codex.desktop", "com.openai.chatgpt.desktop",
    "claude.desktop", "com.anthropic.claude.desktop", "lm-studio.desktop", "lmstudio.desktop",
  ];

  /// <summary>The existing file whose name equals <paramref name="file"/> ignoring case, or null.</summary>
  internal static string? FindDesktopFile(string directory, string file) {
    try {
      if (!Directory.Exists(directory)) { return null; }
      string exact = Path.Combine(directory, file);
      if (File.Exists(exact)) { return exact; }
      return Directory.EnumerateFiles(directory).FirstOrDefault(p => string.Equals(Path.GetFileName(p), file, StringComparison.OrdinalIgnoreCase));
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
  }

  private static Dictionary<string, string> DesktopValues(string text) {
    var values = new Dictionary<string, string>(); bool main = false;
    foreach (string raw in text.Split('\n')) {
      string line = raw.Trim();
      if (line.StartsWith('[')) { main = line == "[Desktop Entry]"; continue; }
      int equals = line.IndexOf('=');
      if (main && equals > 0) { values[line[..equals]] = line[(equals + 1)..]; }
    }
    return values;
  }

  internal static string? DesktopProgram(string text) {
    var values = DesktopValues(text);
    string command = values.GetValueOrDefault("TryExec") ?? values.GetValueOrDefault("Exec") ?? "";
    if (command.StartsWith('"')) {
      int end = command.IndexOf('"', 1);
      return end > 1 ? command[1..end] : null;
    }
    return command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
  }

  internal static string? DesktopEntryName(string text) {
    var values = DesktopValues(text);
    if (values.GetValueOrDefault("Type") != "Application" || values.GetValueOrDefault("Hidden") == "true"
        || values.GetValueOrDefault("NoDisplay") == "true" || !values.ContainsKey("Exec")) { return null; }
    return values.GetValueOrDefault("Name") switch { "Codex" => "Codex Desktop", "ChatGPT" => "ChatGPT Desktop",
      "Claude" when !string.Equals(Path.GetFileName(DesktopProgram(text)), "claude", StringComparison.Ordinal) => "Claude Desktop",
      "LM Studio" => "LM Studio", _ => null };
  }

  internal static async Task LaunchDesktopAsync(McpAgent agent, CancellationToken ct) {
    if (!agent.IsDesktop) { throw new ArgumentException("Select a desktop application."); }
    var start = new ProcessStartInfo(agent.Executable) { UseShellExecute = OperatingSystem.IsWindows() };
    foreach (string arg in agent.Arguments) { start.ArgumentList.Add(arg); }
    // Shell activation may return no process for an already-running Windows app.
    using var process = Process.Start(start);
    if (!OperatingSystem.IsWindows() && process != null) {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
      try {
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        if (process.ExitCode != 0) { throw new InvalidOperationException($"Desktop launcher exited with code {process.ExitCode}."); }
      } catch (OperationCanceledException) {
        if (!process.HasExited) { process.Kill(); }
        throw;
      }
    }
  }
}
