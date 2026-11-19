# Portable plugins

MissionPlanner-Avalonia ports the official Mission Planner plugin lifecycle to a native Avalonia
host. A plugin DLL derives from `MissionPlanner.Plugin.Plugin` in
`MissionPlannerAvalonia.PluginApi.dll` and implements the familiar sequence:

1. `Init()` runs once for validation and lightweight setup.
2. `Loaded()` runs once after the Avalonia application shell exists.
3. `Loop()` runs outside the UI thread at `loopratehz` (up to 100 Hz). `NextRun` can postpone the
   next callback, matching official Mission Planner behavior.
4. `Exit()` runs during bounded application shutdown.

Open **Tools > Plugin Manager** or press **Ctrl+P** to inspect plugins, see loader/loop errors and
enable or disable a DLL. Enable/disable changes use the upstream `DisabledPlugins` setting. Loaded
plugins remain active until restart; newly copied enabled DLLs can be found with **Refresh / Load
New**.

## Locations

The writable plugin directory is shown in Plugin Manager. Its default location is:

| Platform | Directory |
| --- | --- |
| Linux | `$XDG_DATA_HOME/MissionPlannerAvalonia/plugins` (normally `~/.local/share/MissionPlannerAvalonia/plugins`) |
| Windows | `%LOCALAPPDATA%\MissionPlannerAvalonia\plugins` |
| macOS | `~/Library/Application Support/MissionPlannerAvalonia/plugins` |

Package authors may also place system plugins in a `plugins` directory beside the application.
When both locations contain the same DLL file name, the writable user copy takes precedence.
Plugin-owned writable files belong in `Host.DataDirectory`, not beside the DLL.

Copy the main plugin DLL, its `.deps.json` when one is produced, and any private managed/native
dependencies it needs into the same plugin directory. Each main DLL receives a collectible
dependency context with a same-directory fallback; already loaded application,
Mission Planner and Avalonia contract assemblies stay shared so `MAVLinkInterface`, `CurrentState`
and drawing types keep identity across the boundary.

## Minimal plugin

Reference `MissionPlannerAvalonia.PluginApi.dll` from the exact application release used to run the
plugin, target .NET 10, and compile a DLL such as:

```csharp
using Avalonia.Media;
using MissionPlanner.Plugin;

public sealed class ExamplePlugin : Plugin {
  private IDisposable? _action;
  private IDisposable? _overlay;

  public override string Name => "Example";
  public override string Version => "1.0";
  public override string Author => "Operator";

  public override bool Init() {
    loopratehz = 2;
    return true;
  }

  public override bool Loaded() {
    _action = Host.RegisterFlightAction("Example_Action", _ => Host.Log("Action selected"));
    _overlay = Host.RegisterHudOverlay(context =>
        context.DrawingContext.DrawEllipse(Brushes.Lime, null,
            context.Bounds.Center, 4, 4));
    return true;
  }

  public override bool Loop() {
    Host.Log($"Connected: {Host.IsConnected}; mode: {Host.cs.mode}");
    return true;
  }

  public override bool Exit() {
    _overlay?.Dispose();
    _action?.Dispose();
    return true;
  }
}
```

`Host.comPort`, `Host.cs` and `Host.config` retain the official names. Portable additions provide
connection-change notifications, a plugin data directory, Flight Data actions, HUD overlays,
main-screen navigation, diagnostics and UI dispatch. Registrations are also removed automatically
after `Exit`, including when plugin cleanup throws.

`Init` and `Loop` do not run on the Avalonia UI thread. Use `Host.PostToUi` for windows or controls.
HUD drawing is already on the UI thread and its drawing context is valid only for that callback.

## Compatibility and trust

The application ships a compatibility assembly named `MissionPlanner.dll`. Existing official
Mission Planner plugin DLLs can therefore run without recompilation when they use the non-visual
contract: `Plugin` lifecycle and metadata, `Host.comPort`, `Host.cs`, `Host.config`,
`Host.DeviceChanged`, `Host.MainForm` identity/device events, `MainV2.comPort`, the current
`MainV2.Comports` connection snapshot, and `AddWPtoList`/`InsertWP`/`GetWPs`. The loader deliberately
binds a plugin's `MissionPlanner` assembly reference to this shim even when the original executable
had a different 1.3-era assembly version. This path is regression-tested with an untouched .NET
Framework 4.7.2 plugin binary compiled against a separate old-shape reference assembly.

Old plugins that reference WinForms/GMap Windows controls or UI members such as `FDMenuMap`,
`FDMenuHud`, `FPMenuMap`, `FPGMapControl`, `FDGMapControl`, `FPDrawnPolygon`, or the original
WinForms members of `MainV2` still need source adaptation to the portable Avalonia actions, HUD and
UI-dispatch API. Their private dependencies must also be loadable on the current platform and .NET
runtime. Runtime compilation of loose `.cs` plugin files is not provided; compile them into DLLs
first. New cross-platform plugins should reference `MissionPlannerAvalonia.PluginApi.dll` directly.

Plugins are trusted in-process code, not scripts in a security sandbox. They have the same file,
network, serial-device and vehicle access as the application. Install only code you trust. A plugin
exception is recorded without stopping other plugins, repeated `Loop` failures disable that loop,
and shutdown has time bounds, but those fault boundaries cannot make hostile code safe.
