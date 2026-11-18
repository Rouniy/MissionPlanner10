using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using MissionPlanner;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;
using Target = MissionPlanner.Services.Nv5ConfigurationClient.Target;

namespace MissionPlanner.Tests;

public class Nv5NativeSettingsEditorTests {
  // A modem with two settings, no configuration digest and a validation that refuses the
  // candidate, as the transaction protocol's own editor tests use.
  [Fact]
  public void Reads_native_hertz_and_never_commits_a_rejected_complete_candidate() {
    var bench = new Bench(TwoSettingModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    Assert.Equal(2, editor.Rows.Count);
    Assert.Equal("CH1_FREQ_HZ", editor.Rows[0].Name);
    Assert.Equal("868000123", editor.Rows[0].ValueText);

    Assert.True(editor.SetValue("CH1_FREQ_HZ", "868000124"));
    Assert.True(editor.SetValue("CH1_RNG_EN", "1"));
    Assert.True(editor.CanSave);
    editor.Save();
    Assert.True(bench.Pump(() => !editor.Busy));

    Assert.True(editor.HasPendingChanges());
    Assert.Contains("12345678", editor.StatusText, StringComparison.Ordinal);
    List<Nv5ConfigItemMessage> items = [.. bench.Modem.Seen.Select(frame => frame.Payload)
        .OfType<Nv5ConfigItemMessage>()];
    Assert.Equal(2, items.Count);
    Assert.Equal(868000124u, BinaryPrimitives.ReadUInt32LittleEndian(
        items.Single(item => Name(item.Name) == "CH1_FREQ_HZ").Data));
    Assert.Equal(0, bench.Modem.Commits);
    Assert.All(bench.Modem.Seen, frame => Assert.True(
        frame.MessageId is NvModemMessageIds.Nv5ConfigControl or NvModemMessageIds.Nv5ConfigItem));
  }

  [Fact]
  public void Busy_never_falls_back_to_ordinary_parameters() {
    Nv5FakeModem modem = TwoSettingModem();
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach();
    modem.BusyBegin = true;
    Assert.True(editor.SetValue("CH1_RNG_EN", "1"));
    editor.Save();
    Assert.True(bench.Pump(() => !editor.Busy));

    Assert.Contains("busy", editor.StatusText, StringComparison.OrdinalIgnoreCase);
    Assert.True(editor.HasPendingChanges());
    Assert.DoesNotContain(modem.Seen, frame => frame.MessageId == NvModemMessageIds.Nv5ConfigItem);
  }

  [Fact]
  public void Native_snapshot_round_trip_preserves_integers_and_malformed_import_stages_nothing() {
    var bench = new Bench(TwoSettingModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    string directory = Directory.CreateTempSubdirectory("nv5-native-").FullName;
    try {
      string path = Path.Combine(directory, "settings.json");
      Assert.True(editor.SaveSnapshot(path));
      if (!OperatingSystem.IsWindows()) {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
      }
      Assert.True(editor.SetValue("CH1_FREQ_HZ", "868000124"));
      Assert.True(editor.LoadSnapshot(path));
      Assert.False(editor.HasPendingChanges());

      JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
      Assert.Equal("1234", (string?)document["catalog_id"]);
      JsonArray values = document["values"]!.AsArray();
      values[0]!["value"] = "123456789";
      values[1]!["value"] = "invalid";
      File.WriteAllText(path, document.ToJsonString());
      Assert.False(editor.LoadSnapshot(path));
      Assert.False(editor.HasPendingChanges());
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void Status_line_counts_a_read() {
    var bench = new Bench(TwoSettingModem());
    Nv5NativeSettingsEditor editor = bench.Attach();
    var seen = new List<string>();
    bench.Sent += (_, _) => seen.Add(editor.StatusText);
    editor.Reload();
    Assert.True(bench.Pump(() => editor.Ready));
    Assert.Contains("Reading settings… 1 / 2", seen);
    Assert.StartsWith("Read 2 settings", editor.StatusText, StringComparison.Ordinal);
  }

  [Fact]
  public void Modem_that_reports_no_change_is_not_read_again() {
    var bench = new Bench(CatalogueModem());
    var store = new Nv5SettingsSnapshots();
    bench.Attach(store);
    Assert.Equal(bench.Full, bench.Modem.Reads);
    Nv5NativeSettingsEditor second = bench.Attach(store);
    Assert.Equal(bench.Full, bench.Modem.Reads);
    Assert.Equal(bench.Full, second.CandidateValues().Count);
    Assert.Equal(868000000u, Number(second, "CH1_FREQ_HZ"));
    Assert.True(second.ValuesReadBack);
    Assert.False(second.HasPendingChanges());
    Assert.Contains("unchanged", second.StatusText, StringComparison.Ordinal);
  }

  [Fact]
  public void New_revision_or_explicit_reread_reads_everything() {
    var bench = new Bench(CatalogueModem());
    var store = new Nv5SettingsSnapshots();
    bench.Attach(store);
    bench.Modem.Commit("CH1_FREQ_HZ", 915000000);
    Nv5NativeSettingsEditor second = bench.Attach(store);
    Assert.Equal(2 * bench.Full, bench.Modem.Reads);
    Assert.Equal(915000000u, Number(second, "CH1_FREQ_HZ"));

    second.Reload();
    Assert.True(bench.Pump(() => second.Ready));
    Assert.Equal(2 * bench.Full, bench.Modem.Reads);
    second.RereadDevice();
    Assert.True(bench.Pump(() => second.Ready));
    Assert.Equal(3 * bench.Full, bench.Modem.Reads);
  }

  [Fact]
  public void Restarted_modem_with_the_same_settings_is_read_only_for_its_derived_values() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach(new Nv5SettingsSnapshots());
    Assert.Equal(bench.Full, bench.Modem.Reads);
    Assert.True(bench.Derived() > 0);

    // Same catalogue, revision and digest after a restart: only what the firmware derives, such
    // as the link hash, is read again.
    Target restarted = bench.Modem.Peer with { TransportEpoch = bench.Modem.Peer.TransportEpoch + 1 };
    bench.Modem.BootNonce++;
    editor.ObserveTarget(restarted, 50);
    Assert.True(bench.Pump(() => editor.Ready));
    Assert.Equal(bench.Full + bench.Derived(), bench.Modem.Reads);
    Assert.Equal(bench.Full, editor.CandidateValues().Count);
    Assert.Contains("kept from an earlier read", editor.StatusText, StringComparison.Ordinal);

    // A new revision is read in full, restart or not.
    bench.Modem.Commit("CH1_FREQ_HZ", 915000000);
    bench.Modem.BootNonce++;
    restarted = restarted with { TransportEpoch = restarted.TransportEpoch + 1 };
    editor.ObserveTarget(restarted, 40);
    Assert.True(bench.Pump(() => editor.Ready && Number(editor, "CH1_FREQ_HZ") == 915000000));
    Assert.Equal(2 * bench.Full + bench.Derived(), bench.Modem.Reads);
  }

  [Fact]
  public void Read_interrupted_by_a_restart_goes_on_where_it_stopped() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Add(new Nv5SettingsSnapshots());
    Target peer = bench.Modem.Peer;
    bool restarted = false;
    bench.Drop = (target, frame) => {
      // The modem restarts while the eleventh setting is asked for.
      if (restarted || !IsControl(frame, Nv5ConfigOperations.Read) || bench.Modem.Reads != 10) {
        return false;
      }
      restarted = true;
      bench.Modem.BootNonce++;
      peer = peer with { TransportEpoch = peer.TransportEpoch + 1 };
      target.ObserveTarget(peer, 30);
      return true;
    };
    editor.ObserveTarget(peer, 10000);
    Assert.True(bench.Pump(() => editor.Ready));

    Assert.True(restarted);
    Assert.False(editor.Failed);
    Assert.Equal(2, bench.Modem.ReadBegins);
    // The ten settings read before the restart are kept, except a derived one among them.
    Assert.Equal(bench.Full + bench.Derived(0, 10), bench.Modem.Reads);
    Assert.Equal(bench.Full, editor.CandidateValues().Count);
    Assert.Equal(868000000u, Number(editor, "CH1_FREQ_HZ"));
    Assert.Equal(5u, Number(editor, "CH2_RNG_N"));
    Assert.Contains("kept from an earlier read", editor.StatusText, StringComparison.Ordinal);
  }

  [Fact]
  public void Refused_read_is_tried_again_on_its_own() {
    // Another session holds the modem's single read session for a while.
    var bench = new Bench(CatalogueModem());
    bench.Modem.RefuseReadBegins = 2;
    var seen = new List<string>();
    Nv5NativeSettingsEditor editor = bench.Add(new Nv5SettingsSnapshots());
    bench.Sent += (_, _) => seen.Add(editor.StatusText);
    editor.ObserveTarget(bench.Modem.Peer, 10000);
    Assert.True(bench.Pump(() => editor.Ready));

    Assert.False(editor.Failed);
    Assert.Equal(3, bench.Modem.ReadBegins);
    Assert.Equal(bench.Full, bench.Modem.Reads);
    Assert.True(seen.Count(status =>
        status.Contains("Another configuration session", StringComparison.Ordinal)) >= 2,
        string.Join(" | ", seen));
  }

  [Fact]
  public void Editors_sharing_a_store_never_read_one_modem_at_once() {
    // The firmware refuses a second read session while one is open.
    var bench = new Bench(CatalogueModem());
    bench.Modem.SingleReader = true;
    var store = new Nv5SettingsSnapshots();
    Nv5NativeSettingsEditor first = bench.Add(store);
    Nv5NativeSettingsEditor second = bench.Add(store);
    first.ObserveTarget(bench.Modem.Peer, 10000);
    second.ObserveTarget(bench.Modem.Peer, 10000);
    Assert.True(bench.Pump(() => first.Ready && second.Ready));

    // The second waited for the first and took its result instead of being refused.
    Assert.Equal(1, bench.Modem.ReadBegins);
    Assert.Equal(bench.Full, bench.Modem.Reads);
    Assert.Contains("unchanged", second.StatusText, StringComparison.Ordinal);
  }

  [Fact]
  public void Without_a_store_or_a_digest_every_read_is_full() {
    var bench = new Bench(CatalogueModem());
    var store = new Nv5SettingsSnapshots();
    Nv5NativeSettingsEditor first = bench.Attach(store);
    bench.Attach(null);
    Assert.Equal(2 * bench.Full, bench.Modem.Reads);

    // Nothing identifies the settings of a modem that reports no digest.
    bench.Modem.Commit("CH1_FREQ_HZ", 915000000);
    bench.Modem.Digest = 0;
    first.Reload();
    Assert.True(bench.Pump(() => first.Ready));
    bench.Attach(store);
    Assert.Equal(4 * bench.Full, bench.Modem.Reads);
  }

  [Fact]
  public void Explicit_reread_remains_full_after_busy() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach(new Nv5SettingsSnapshots());
    Assert.Equal(bench.Full, bench.Modem.Reads);
    bench.Modem.RefuseReadBegins = 1;
    editor.RereadDevice();
    Assert.True(bench.Pump(() => editor.Ready));
    Assert.Equal(2 * bench.Full, bench.Modem.Reads);
  }

  [Fact]
  public void Explicit_reread_resumes_only_its_own_values_after_restart() {
    var bench = new Bench(CatalogueModem());
    Nv5NativeSettingsEditor editor = bench.Attach(new Nv5SettingsSnapshots());
    Target peer = bench.Modem.Peer;
    bool restarted = false;
    bench.Drop = (target, frame) => {
      if (restarted || !IsControl(frame, Nv5ConfigOperations.Read)
          || bench.Modem.Reads != bench.Full + 10) {
        return false;
      }
      restarted = true;
      bench.Modem.BootNonce++;
      peer = peer with { TransportEpoch = peer.TransportEpoch + 1 };
      target.ObserveTarget(peer, 30);
      return true;
    };
    editor.RereadDevice();
    Assert.True(bench.Pump(() => editor.Ready));
    Assert.True(restarted);
    Assert.Equal(2 * bench.Full + bench.Derived(0, 10), bench.Modem.Reads);
    Assert.Equal(bench.Full, editor.CandidateValues().Count);
  }

  [Fact]
  public void Full_width_native_names_are_read_and_staged_without_a_terminator() {
    Nv5FakeModem modem = CatalogueModem();
    modem.Settings.Add(new("CH1_RSV_GUARD_US", 5000));
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach(new Nv5SettingsSnapshots());
    Assert.Equal("5000", Nv5NativeSettingsEditor.Display(editor.ReadValues["CH1_RSV_GUARD_US"]));
    Assert.True(editor.SetValue("CH1_RSV_GUARD_US", "6000"));
    bool? prepared = null;
    editor.Prepared += (valid, _, _) => prepared = valid;
    editor.Prepare();
    Assert.True(bench.Pump(() => prepared != null || editor.Failed));
    Assert.True(prepared, editor.StatusText);
    Assert.Equal(6000u, modem.Staged["CH1_RSV_GUARD_US"]);
    editor.Discard();
  }

  [Fact]
  public void Changing_one_key_word_stages_the_whole_key_of_that_radio() {
    Nv5FakeModem modem = CatalogueModem();
    const byte Secret = Nv5ConfigValueFlags.Supported | Nv5ConfigValueFlags.Secret;
    for (int word = 1; word < 4; word++) {
      modem.Settings.Add(new($"CH1_KEY_W{word}", (uint)(111 + word), Nv5ConfigValueTypes.Int32,
          Secret));
    }
    modem.Settings.Add(new("CH2_KEY_W0", 222, Nv5ConfigValueTypes.Int32, Secret));
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach(new Nv5SettingsSnapshots());
    Assert.True(editor.SetValue("CH1_KEY_W0", "-5"));
    bool? prepared = null;
    editor.Prepared += (valid, _, _) => prepared = valid;
    editor.Prepare();
    Assert.True(bench.Pump(() => prepared != null || editor.Failed));

    Assert.True(prepared, editor.StatusText);
    Assert.Equal(new[] { "CH1_KEY_W0", "CH1_KEY_W1", "CH1_KEY_W2", "CH1_KEY_W3" },
        modem.Staged.Keys.Order(StringComparer.Ordinal));
    Assert.Equal(unchecked((uint)-5), modem.Staged["CH1_KEY_W0"]);
    Assert.Equal(113u, modem.Staged["CH1_KEY_W2"]);
    editor.Discard();
  }

  [Fact]
  public void Save_commits_once_survives_the_restart_and_verifies_activation() {
    Nv5FakeModem modem = CatalogueModem();
    modem.CommitModel = true;
    var bench = new Bench(modem);
    Nv5NativeSettingsEditor editor = bench.Attach(new Nv5SettingsSnapshots());
    bool? finished = null;
    editor.ConfigurationFinished += success => finished = success;
    Assert.True(editor.SetValue("CH1_FREQ_HZ", "915000000"));
    editor.Save();
    Assert.True(bench.Pump(() => editor.StatusText.Contains("Waiting for restart",
        StringComparison.Ordinal)), editor.StatusText);
    Assert.True(editor.AwaitingReconnect);
    Assert.Equal(1, modem.Commits);

    modem.Reboot();
    editor.ObserveTarget(modem.Peer with { TransportEpoch = modem.Peer.TransportEpoch + 1 }, 30);
    Assert.True(bench.Pump(() => finished != null));
    Assert.True(finished, editor.StatusText);
    Assert.Equal("Saved, restarted and verified active.", editor.StatusText);
    Assert.True(bench.Pump(() => editor.Ready && editor.ValuesReadBack));
    Assert.Equal(1, modem.Commits);
    Assert.Equal(915000000u, Number(editor, "CH1_FREQ_HZ"));
    Assert.False(editor.HasPendingChanges());
    Assert.StartsWith("Read ", editor.StatusText, StringComparison.Ordinal);
  }

  [Fact]
  public void Real_firmware_core_reads_saves_restarts_and_rereads_through_the_editor() {
    string? fixture = Environment.GetEnvironmentVariable("MP_NV5_CONFIG_FIXTURE");
    if (string.IsNullOrEmpty(fixture)) {
      return;
    }
    NvModemMavlinkDialect.Register();
    using var server = System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(fixture) {
          RedirectStandardInput = true,
          RedirectStandardOutput = true,
          UseShellExecute = false,
        })!;
    uint now = 1000;
    var editor = new Nv5NativeSettingsEditor(() => now, () => 255, 190);
    var queue = new Queue<Nv5ConfigurationFrame>();
    editor.SendFrame += (frame, _) => queue.Enqueue(frame);
    Target target = new Nv5FakeModem().Peer;
    bool Pump(Func<bool> done) {
      for (int step = 0; step < 20000 && !done(); step++) {
        if (!queue.TryDequeue(out Nv5ConfigurationFrame? frame)) {
          now += 20;
          editor.Service();
          continue;
        }
        byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
            (MAVLink.MAVLINK_MSG_ID)frame.MessageId, frame.Payload, false, 255, 190);
        server.StandardInput.Write($"{now} {Convert.ToHexString(bytes).ToLowerInvariant()}\n");
        server.StandardInput.Flush();
        while (server.StandardOutput.ReadLine() is { } line && line.Trim() != ".") {
          editor.Receive(new MAVLink.MavlinkParse().ReadPacket(
              new MemoryStream(Convert.FromHexString(line.Trim())))!, target.Route);
        }
      }
      return done();
    }

    editor.ObserveTarget(target, 10000);
    Assert.True(Pump(() => editor.Ready), editor.StatusText);
    Assert.True(editor.ReadValues.Count > 20, editor.StatusText);
    uint saveMs = Number(editor, "MAV_SAVE_MS") == 3000 ? 2500u : 3000u;
    Assert.True(editor.SetValue("MAV_SAVE_MS", saveMs.ToString(
        System.Globalization.CultureInfo.InvariantCulture)));
    bool? finished = null;
    editor.ConfigurationFinished += success => finished = success;
    editor.Save();
    Assert.True(Pump(() => editor.StatusText.Contains("Waiting for restart",
        StringComparison.Ordinal) || editor.Failed), editor.StatusText);
    Assert.False(editor.Failed, editor.StatusText);

    server.StandardInput.Write("REBOOT\n");
    server.StandardInput.Flush();
    Assert.Equal(".", server.StandardOutput.ReadLine()?.Trim());
    target = target with { TransportEpoch = target.TransportEpoch + 1 };
    editor.ObserveTarget(target, 30);
    Assert.True(Pump(() => finished != null), editor.StatusText);
    Assert.True(finished, editor.StatusText);
    Assert.True(Pump(() => editor.Ready && editor.ValuesReadBack), editor.StatusText);
    Assert.Equal(saveMs, Number(editor, "MAV_SAVE_MS"));
    Assert.False(editor.HasPendingChanges());
    server.StandardInput.Close();
    Assert.True(server.WaitForExit(10_000));
    Assert.Equal(0, server.ExitCode);
  }

  [Theory]
  [InlineData(Nv5ConfigValueTypes.UInt32, "0x10", true, 16u)]
  [InlineData(Nv5ConfigValueTypes.UInt32, "010", true, 8u)]
  [InlineData(Nv5ConfigValueTypes.UInt32, "-1", false, 0u)]
  [InlineData(Nv5ConfigValueTypes.UInt32, "4294967296", false, 0u)]
  [InlineData(Nv5ConfigValueTypes.Int32, "-2147483648", true, 0x80000000u)]
  [InlineData(Nv5ConfigValueTypes.Int32, "2147483648", false, 0u)]
  [InlineData(Nv5ConfigValueTypes.Float32, "-0", true, 0u)]
  [InlineData(Nv5ConfigValueTypes.Float32, "inf", false, 0u)]
  public void Native_values_encode_like_the_firmware_tools(byte type, string text, bool valid,
      uint raw) {
    var value = new Nv5ConfigurationClient.Value("X", new byte[4], 1, 0, type,
        Nv5ConfigValueFlags.Supported);
    Assert.Equal(valid, Nv5NativeSettingsEditor.Encode(value, text, out byte[] bytes));
    if (valid) {
      Assert.Equal(raw, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
    }
  }

  private static Nv5FakeModem TwoSettingModem() => new() {
    Settings = [new("CH1_FREQ_HZ", 868000123), new("CH1_RNG_EN", 0)],
    RejectValidate = true,
  };

  private static Nv5FakeModem CatalogueModem() => new() {
    Settings = Nv5FakeModem.Catalogue(transmitsOnRadio1: true),
    Digest = 0x5a,
  };

  private static bool IsControl(Nv5ConfigurationFrame frame, byte operation) =>
      frame.Payload is Nv5ConfigControlMessage control && control.Operation == operation;

  private static uint Number(Nv5NativeSettingsEditor editor, string name) =>
      BinaryPrimitives.ReadUInt32LittleEndian(editor.CandidateValues()[name].Bytes);

  private static string Name(byte[] bytes) {
    int end = Array.IndexOf(bytes, (byte)0);
    return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
  }

  /// <summary>Editors served by one fake modem on a fake clock. Requests are answered in the
  /// order sent, never from inside the editor call that sent them.</summary>
  private sealed class Bench(Nv5FakeModem modem) {
    private readonly List<(Nv5NativeSettingsEditor Editor, Queue<Nv5ConfigurationFrame> Queue)>
        _editors = [];
    private uint _now = 1000;

    internal Nv5FakeModem Modem { get; } = modem;
    internal int Full => Modem.Settings.Count;
    internal event Action<Nv5NativeSettingsEditor, Nv5ConfigurationFrame>? Sent;

    /// <summary>Swallows a request instead of answering it; for a modem that restarts.</summary>
    internal Func<Nv5NativeSettingsEditor, Nv5ConfigurationFrame, bool>? Drop { get; set; }

    internal Nv5NativeSettingsEditor Add(Nv5SettingsSnapshots? store) {
      var editor = new Nv5NativeSettingsEditor(() => _now, () => 255, 190) { Snapshots = store };
      var queue = new Queue<Nv5ConfigurationFrame>();
      editor.SendFrame += (frame, _) => {
        queue.Enqueue(frame);
        Sent?.Invoke(editor, frame);
      };
      _editors.Add((editor, queue));
      return editor;
    }

    internal Nv5NativeSettingsEditor Attach(Nv5SettingsSnapshots? store = null) {
      Nv5NativeSettingsEditor editor = Add(store);
      editor.ObserveTarget(Modem.Peer, 10000);
      Assert.True(Pump(() => editor.Ready), editor.StatusText);
      return editor;
    }

    internal bool Pump(Func<bool> done, int steps = 50000) {
      for (int step = 0; step < steps && !done(); step++) {
        bool answered = false;
        foreach ((Nv5NativeSettingsEditor editor, Queue<Nv5ConfigurationFrame> queue)
            in _editors.ToList()) {
          if (!queue.TryDequeue(out Nv5ConfigurationFrame? frame)) {
            continue;
          }
          answered = true;
          if (Drop?.Invoke(editor, frame) == true) {
            continue;
          }
          foreach (MAVLink.MAVLinkMessage reply in Modem.Answer(frame)) {
            editor.Receive(reply, Modem.Peer.Route);
          }
        }
        if (!answered) {
          _now += 20;
          foreach ((Nv5NativeSettingsEditor editor, _) in _editors.ToList()) {
            editor.Service();
          }
        }
      }
      return done();
    }

    /// <summary>Settings the firmware derives, which a new boot reads again.</summary>
    internal int Derived(int first = 0, int count = -1) =>
        Modem.Settings.Skip(first).Take(count < 0 ? Full : count)
            .Count(setting => (setting.Flags & Nv5ConfigValueFlags.ReadOnly) != 0);
  }
}

/// <summary>
/// In-process NV5 modem: one committed catalogue behind the native configuration transaction
/// protocol. With <see cref="CommitModel"/> a commit is durable at once, restarts on
/// <see cref="Reboot"/> and keeps no receipt across the restart, as the firmware does.
/// </summary>
internal sealed class Nv5FakeModem {
  internal sealed class Setting(string name, uint raw, byte type = Nv5ConfigValueTypes.UInt32,
      byte flags = Nv5ConfigValueFlags.Supported) {
    internal string Name { get; } = name;
    internal uint Raw { get; set; } = raw;
    internal byte Type { get; } = type;
    internal byte Flags { get; } = flags;
  }

  internal Target Peer { get; set; } = new(Enumerable.Repeat((byte)'U', 18).ToArray(),
      "verified-peer", 1, NvModemCapabilities.ConfigurationTransactions, 10, 11, true);
  internal List<Setting> Settings { get; set; } = [];
  internal Dictionary<string, uint> Staged { get; } = new(StringComparer.Ordinal);
  internal List<Nv5ConfigurationFrame> Seen { get; } = [];
  internal uint CandidateHash { get; set; } = 700;
  internal ulong BootNonce { get; set; } = 0x123456789abcdef0UL;
  internal ulong Revision { get; set; } = 17;

  /// <summary>Fill byte of the reported configuration digests; zero reports none.</summary>
  internal byte Digest { get; set; }

  internal bool BusyBegin { get; set; }
  internal bool RejectValidate { get; set; }
  internal bool CommitModel { get; set; }
  internal int RefuseReadBegins { get; set; }
  internal bool SingleReader { get; set; }
  internal int ReadBegins { get; private set; }
  internal int Reads { get; private set; }
  internal int Commits { get; private set; }

  private bool _readOpen;
  private bool _restartRequired;
  private ulong _activeRevision = 17;
  private byte _activeDigest;
  private ulong _receiptBoot;

  internal void Set(string name, uint raw) => Settings.Single(setting => setting.Name == name)
      .Raw = raw;

  /// <summary>Changes a setting as a committed save does: under a new revision and digest.</summary>
  internal void Commit(string name, uint raw) {
    Set(name, raw);
    Revision++;
    _activeRevision = Revision;
    Digest++;
    _activeDigest = Digest;
  }

  internal void Reboot() {
    BootNonce++;
    _readOpen = false;
    _restartRequired = false;
    _receiptBoot = 0;
    _activeRevision = Revision;
    _activeDigest = Digest;
  }

  internal IEnumerable<MAVLink.MAVLinkMessage> Answer(Nv5ConfigurationFrame request) {
    Seen.Add(request);
    if (_activeDigest == 0 && _activeRevision == Revision) {
      _activeDigest = Digest;
    }
    var reply = new Nv5ConfigResultMessage {
      BootNonce = BootNonce,
      CommittedRevision = Revision,
      ActiveRevision = _activeRevision,
      SnapshotRevision = Revision,
      CommittedDigest = Filled(Digest),
      ActiveDigest = Filled(_activeDigest),
      SnapshotDigest = Filled(Digest),
      TransactionDigest = new byte[32],
      ErrorName = new byte[16],
      CatalogId = 1234,
      SettingCount = (ushort)Settings.Count,
      CommittedKnown = 1,
      Capabilities = 3,
      RestartRequired = (byte)(_restartRequired ? 1 : 0),
      TargetSystem = 255,
      TargetComponent = 190,
      SchemaVersion = 1,
      State = Nv5ConfigStates.Idle,
    };
    if (request.Payload is Nv5ConfigItemMessage item) {
      int end = Array.IndexOf(item.Name, (byte)0);
      Staged[Encoding.ASCII.GetString(item.Name, 0, end < 0 ? 16 : end)] =
          BinaryPrimitives.ReadUInt32LittleEndian(item.Data);
      reply.ClientNonce = item.ClientNonce;
      reply.Token = item.Token;
      reply.Sequence = item.Sequence;
      reply.NextSequence = item.Sequence + 1;
      reply.Operation = Nv5ConfigOperations.Set;
      reply.State = Nv5ConfigStates.Staged;
      yield return Encode(NvModemMessageIds.Nv5ConfigResult, reply);
      yield break;
    }
    var control = (Nv5ConfigControlMessage)request.Payload;
    reply.ClientNonce = control.ClientNonce;
    reply.Token = control.Token;
    reply.Sequence = control.Sequence;
    reply.Operation = control.Operation;
    reply.Scope = control.Scope;
    reply.NextSequence = control.Sequence + 1;
    switch (control.Operation) {
      case Nv5ConfigOperations.ReadBegin:
        ReadBegins++;
        reply.Token = 55;
        if (RefuseReadBegins > 0 || (SingleReader && _readOpen)) {
          RefuseReadBegins = Math.Max(0, RefuseReadBegins - 1);
          reply.Token = 0;
          reply.Result = Nv5ConfigResults.Busy;
        } else {
          _readOpen = true;
        }
        break;
      case Nv5ConfigOperations.ReadEnd:
        _readOpen = false;
        break;
      case Nv5ConfigOperations.Begin:
        Staged.Clear();
        reply.Result = BusyBegin ? Nv5ConfigResults.Busy : Nv5ConfigResults.Ok;
        reply.Token = BusyBegin ? 0UL : 99UL;
        reply.NextSequence = 1;
        reply.TransactionRevision = Revision;
        reply.State = Nv5ConfigStates.Staged;
        break;
      case Nv5ConfigOperations.Validate:
        reply.TransactionRevision = Revision;
        Array.Fill(reply.TransactionDigest, (byte)0xab);
        if (RejectValidate) {
          reply.State = Nv5ConfigStates.Staged;
          reply.Result = Nv5ConfigResults.InvalidConfiguration;
          reply.Channel1LinkHash = 12345678;
        } else {
          reply.State = Nv5ConfigStates.Validated;
          reply.Channel1LinkHash = reply.Channel2LinkHash = CandidateHash;
        }
        break;
      case Nv5ConfigOperations.Commit:
        Commits++;
        if (CommitModel) {
          foreach ((string stagedName, uint stagedRaw) in Staged) {
            Set(stagedName, stagedRaw);
          }
          Revision++;
          Digest = 0xab;
          _restartRequired = true;
          _receiptBoot = BootNonce;
          Receipt(ref reply);
        }
        break;
      case Nv5ConfigOperations.Status when CommitModel && control.Token == 99:
        if (_receiptBoot == BootNonce) {
          Receipt(ref reply);
        } else {
          // Receipts are not durable across a restart.
          reply.Result = Nv5ConfigResults.NotFound;
        }
        break;
    }
    yield return Encode(NvModemMessageIds.Nv5ConfigResult, reply);
    if (control.Operation != Nv5ConfigOperations.Read) {
      yield break;
    }
    Reads++;
    Setting setting = Settings[control.Index];
    var name = new byte[16];
    Encoding.ASCII.GetBytes(setting.Name).CopyTo(name, 0);
    var data = new byte[48];
    BinaryPrimitives.WriteUInt32LittleEndian(data, setting.Raw);
    yield return Encode(NvModemMessageIds.Nv5ConfigValue, new Nv5ConfigValueMessage {
      BootNonce = BootNonce,
      ClientNonce = control.ClientNonce,
      Token = control.Token,
      Revision = Revision,
      Sequence = control.Sequence,
      CatalogId = 1234,
      Index = control.Index,
      SettingCount = (ushort)Settings.Count,
      SettingId = (ushort)(control.Index + 1),
      TotalLength = 4,
      Length = 4,
      Name = name,
      Data = data,
      ValueType = setting.Type,
      Flags = setting.Flags,
      Scope = control.Scope,
      TargetSystem = 255,
      TargetComponent = 190,
      SchemaVersion = 1,
    });
  }

  /// <summary>Both radios' link and distance settings; radio 1 of a transmitter pairs with a
  /// receiver. Firmware before the selectable measuring end has no CHn_RNG_MGR.</summary>
  internal static List<Setting> Catalogue(bool transmitsOnRadio1, bool choosesMeasuringEnd = true) {
    const byte Secret = Nv5ConfigValueFlags.Supported | Nv5ConfigValueFlags.Secret;
    const byte ReadOnly = Nv5ConfigValueFlags.Supported | Nv5ConfigValueFlags.ReadOnly;
    List<Setting> settings = [
      new("CH1_ROLE", transmitsOnRadio1 ? 1u : 0u),
      new("CH1_FREQ_HZ", 868000000),
      new("CH1_PWR_DBM", 10, Nv5ConfigValueTypes.Int32),
      new("CH1_KEY_W0", 111, Nv5ConfigValueTypes.Int32, Secret),
      new("CH1_LINK_HASH", 500, Nv5ConfigValueTypes.UInt32, ReadOnly),
      new("CH1_PEER_HASH", 0),
      new("CH1_RNG_EN", 0),
      new("CH1_RNG_PER", 1024),
      new("CH1_RNG_PHASE", 0),
      new("CH1_RNG_N", 1),
      new("CH1_RNG_PAD_MS", 0),
      new("CH1_RNG_SPAN", 0),
      new("CH1_RNG_PWR", unchecked((uint)-9), Nv5ConfigValueTypes.Int32),
      new("CH1_RNG_ADDR", 0x32101222),
      new("CH2_ROLE", transmitsOnRadio1 ? 0u : 1u),
      new("CH2_FREQ_HZ", 433920000),
      new("CH2_LINK_HASH", 600, Nv5ConfigValueTypes.UInt32, ReadOnly),
      new("CH2_PEER_HASH", 600),
      new("CH2_RNG_EN", 1),
      new("CH2_RNG_PER", 2048),
      new("CH2_RNG_PHASE", 0),
      new("CH2_RNG_N", 5),
      new("CH2_RNG_PAD_MS", 20),
      new("CH2_RNG_SPAN", 2500000),
      new("CH2_RNG_PWR", 3, Nv5ConfigValueTypes.Int32),
      new("CH2_RNG_ADDR", 0x00c0ffee),
    ];
    if (choosesMeasuringEnd) {
      settings.Add(new("CH1_RNG_MGR", 0));
      settings.Add(new("CH2_RNG_MGR", 0));
    }
    return settings;
  }

  private void Receipt(ref Nv5ConfigResultMessage reply) {
    reply.State = Nv5ConfigStates.CommittedRestartRequired;
    reply.ReceiptBootNonce = _receiptBoot;
    reply.TransactionRevision = reply.CommittedRevision = Revision;
    reply.CommittedDigest = Filled(Digest);
    reply.TransactionDigest = Filled(0xab);
    reply.RestartRequired = 1;
  }

  private static byte[] Filled(byte value) => Enumerable.Repeat(value, 32).ToArray();

  private MAVLink.MAVLinkMessage Encode(uint id, object payload) {
    NvModemMavlinkDialect.Register();
    byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
        (MAVLink.MAVLINK_MSG_ID)id, payload, false, Peer.System, Peer.Component);
    return new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes))!;
  }
}
