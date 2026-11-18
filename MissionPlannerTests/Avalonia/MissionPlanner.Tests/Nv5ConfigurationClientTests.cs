using System.Diagnostics;
using System.Text;
using MissionPlanner;
using MissionPlanner.Services;
using Xunit.Abstractions;
using Phase = MissionPlanner.Services.Nv5ConfigurationClient.ClientPhase;

namespace MissionPlanner.Tests;

public class Nv5ConfigurationClientTests(ITestOutputHelper output) {
  private const ulong Boot = 0x123456789abcdef0UL;
  private const ulong Nonce = 0xfedcba9876543210UL;
  private const ulong Token = 0x23456789abcdef01UL;

  private Nv5ConfigurationClient _client = new();
  private uint _now = 100;

  [Fact]
  public void Capability_and_verified_identity_are_required() {
    Assert.False(_client.Discover(Target() with { Capabilities = 0 }, _now));
    Assert.False(_client.Discover(Target() with { Verified = false }, _now));
    Assert.False(_client.Discover(Target() with { Uid = new byte[18] }, _now));
    Assert.False(_client.Discover(
        Target() with { Capabilities = NvModemCapabilities.MavlinkParameters }, _now));
    Assert.Null(_client.TakeOutbound(_now));
  }

  [Fact]
  public void Hub_uses_its_own_passport_capability() {
    var hub = Target() with { Product = Nv5ConfigurationClient.ProductKind.Hub };
    Assert.False(_client.Discover(hub, _now));
    hub = hub with { Capabilities = Nv5ConfigurationClient.HubConfigurationTransactions };
    Assert.True(_client.Discover(hub, _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Idle)));
    Assert.Equal(Phase.Ready, _client.Phase);
    Assert.False(_client.Discover(
        hub with { Product = Nv5ConfigurationClient.ProductKind.Modem }, _now));
    Assert.False(_client.Discover(
        hub with { Product = (Nv5ConfigurationClient.ProductKind)255 }, _now));
  }

  [Fact]
  public void Recovery_cannot_change_product_for_the_same_uid() {
    Validated();
    Assert.True(_client.Commit(_now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Committing, Nv5ConfigResults.Accepted)));
    Assert.False(_client.Recover(Target() with {
      Product = Nv5ConfigurationClient.ProductKind.Hub,
      Capabilities = Nv5ConfigurationClient.HubConfigurationTransactions,
    }, _now));
  }

  [Fact]
  public void Missing_receipt_after_confirmed_commit_uses_exact_active_readback() {
    Validated();
    Assert.True(_client.Commit(_now));
    var durable = Response(Next(), Nv5ConfigStates.CommittedRestartRequired);
    durable.ReceiptBootNonce = Boot;
    durable.TransactionRevision = durable.CommittedRevision = 18;
    durable.RestartRequired = 1;
    Array.Fill(durable.TransactionDigest, (byte)0xab);
    Array.Fill(durable.CommittedDigest, (byte)0xab);
    Assert.True(Deliver(durable));
    Assert.Equal(Phase.RestartRequired, _client.Phase);

    Assert.True(_client.Recover(Target(), _now));
    var info = Response(Next(), Nv5ConfigStates.Idle);
    info.BootNonce = Boot + 1;
    Assert.True(Deliver(info));
    _client.Service(_now += 100);
    var missing = Response(Next(), Nv5ConfigStates.Idle, Nv5ConfigResults.NotFound);
    missing.BootNonce = Boot + 1;
    missing.ActiveRevision = missing.CommittedRevision = 18;
    Array.Fill(missing.ActiveDigest, (byte)0xab);
    Array.Fill(missing.CommittedDigest, (byte)0xab);
    Assert.True(Deliver(missing));
    Assert.True(_client.ConfigurationMatchesExpected());
    Assert.Equal(Phase.Active, _client.Phase);

    Nv5ConfigResultMessage current = Copy(missing);
    void Poll(Func<Nv5ConfigResultMessage, Nv5ConfigResultMessage> mutate) {
      Assert.True(_client.Status(_now));
      uint sequence = Response(Next(), Nv5ConfigStates.Idle, Nv5ConfigResults.NotFound).Sequence;
      Nv5ConfigResultMessage reply = Copy(current);
      reply.Sequence = sequence;
      Assert.True(Deliver(mutate(reply)));
    }
    Poll(reply => reply);
    Assert.Equal(Phase.Active, _client.Phase);
    Poll(reply => { reply.ActiveRevision = 19; return reply; });
    Assert.Equal(Phase.Uncertain, _client.Phase);
    Poll(reply => { reply.CommittedRevision = 19; return reply; });
    Assert.Equal(Phase.Uncertain, _client.Phase);
    Poll(reply => { reply.ActiveDigest[0] ^= 1; return reply; });
    Assert.Equal(Phase.Uncertain, _client.Phase);
    Poll(reply => { reply.CommittedDigest[0] ^= 1; return reply; });
    Assert.Equal(Phase.Uncertain, _client.Phase);
    Poll(reply => { reply.CommittedKnown = 0; return reply; });
    Assert.Equal(Phase.Uncertain, _client.Phase);
    Poll(reply => reply);
    Assert.Equal(Phase.Active, _client.Phase);
  }

  [Fact]
  public void Matching_readback_without_commit_confirmation_remains_uncertain() {
    Validated();
    Assert.True(_client.Commit(_now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Committing, Nv5ConfigResults.Accepted)));
    Assert.True(_client.Recover(Target(), _now));
    var info = Response(Next(), Nv5ConfigStates.Idle);
    info.BootNonce = Boot + 1;
    Assert.True(Deliver(info));
    _client.Service(_now += 100);
    var missing = Response(Next(), Nv5ConfigStates.Idle, Nv5ConfigResults.NotFound);
    missing.BootNonce = Boot + 1;
    missing.ActiveRevision = missing.CommittedRevision = 18;
    Array.Fill(missing.ActiveDigest, (byte)0xab);
    Array.Fill(missing.CommittedDigest, (byte)0xab);
    Assert.True(Deliver(missing));
    Assert.True(_client.ConfigurationMatchesExpected());
    Assert.Equal(Phase.Uncertain, _client.Phase);
    Assert.Null(_client.TakeOutbound(_now));

    Assert.True(_client.Status(_now));
    missing.Sequence =
        Response(Next(), Nv5ConfigStates.Idle, Nv5ConfigResults.NotFound).Sequence;
    Assert.True(Deliver(missing));
    Assert.Equal(Phase.Uncertain, _client.Phase);
    Assert.Null(_client.TakeOutbound(_now));
  }

  [Fact]
  public void Rediscovery_rejects_delayed_info_and_keeps_full_width_identifiers() {
    Assert.True(_client.Discover(Target(), _now));
    var old = Response(Next(), Nv5ConfigStates.Idle);
    Assert.True(Deliver(old));
    Assert.True(_client.Discover(Target(), _now));
    var current = Response(Next(), Nv5ConfigStates.Idle);
    current.BootNonce = Boot + 1;
    Assert.False(Deliver(old));
    Assert.True(Deliver(current));

    Assert.True(_client.Begin(Nonce, 17, _now));
    var wire = (Nv5ConfigControlMessage)Next().Payload;
    Assert.Equal(Boot + 1, wire.BootNonce);
    Assert.Equal(Nonce, wire.ClientNonce);
  }

  [Fact]
  public void Begin_is_based_on_the_reviewed_revision_not_on_the_last_reply() {
    Ready();
    Assert.True(_client.Begin(Nonce, 17, _now));
    var refused = Response(Next(), Nv5ConfigStates.Idle, Nv5ConfigResults.StaleRevision);
    refused.CommittedRevision = 18;
    Assert.True(Deliver(refused));
    Assert.Equal(Phase.Ready, _client.Phase);
    Assert.Equal(18UL, _client.Result.CommittedRevision);

    // A retry with the same edits stays based on the revision they were made against.
    Assert.True(_client.Begin(Nonce + 1, 17, _now));
    Assert.Equal(17UL, ((Nv5ConfigControlMessage)Next().Payload).ExpectedRevision);
  }

  [Fact]
  public void Busy_begin_cannot_adopt_another_owners_validated_state() {
    Ready();
    Assert.True(_client.Begin(Nonce, 17, _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Validated, Nv5ConfigResults.Busy)));
    Assert.Equal(Phase.Ready, _client.Phase);
    Assert.False(_client.Stage("MAV_SAVE_MS", Nv5ConfigValueTypes.UInt32, new byte[4], _now));
    Assert.False(_client.Commit(_now));
  }

  [Fact]
  public void Busy_read_begin_cannot_adopt_another_owners_validated_state() {
    Ready();
    Assert.True(_client.BeginRead(Nv5ConfigScopes.Committed, Nonce, _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Validated, Nv5ConfigResults.Busy)));
    Assert.Equal(Phase.Ready, _client.Phase);
    Assert.False(_client.Stage("MAV_SAVE_MS", Nv5ConfigValueTypes.UInt32, new byte[4], _now));
    Assert.False(_client.Commit(_now));
  }

  [Fact]
  public void Text_chunks_cannot_be_overwritten_or_continue_after_abort() {
    Staged();
    Assert.True(_client.Stage("RTSP_PATH", Nv5ConfigValueTypes.Text, Text('x', 95), _now));
    Nv5ConfigurationFrame first = Next();
    var wire = (Nv5ConfigItemMessage)first.Payload;
    Assert.Equal(0, wire.Offset);
    Assert.Equal(48, wire.Length);
    Assert.True(Deliver(Response(first, Nv5ConfigStates.Staged)));
    Assert.False(_client.Stage("RTSP_PATH", Nv5ConfigValueTypes.Text, "other"u8.ToArray(), _now));
    Assert.False(_client.Validate(_now));

    Assert.True(_client.Abort(_now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Aborted)));
    _client.Service(_now += 1000);
    Assert.Null(_client.TakeOutbound(_now));
    Assert.True(_client.Begin(Nonce + 1, 17, _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Staged)));
    _client.Service(_now += 1000);
    Assert.Null(_client.TakeOutbound(_now));
  }

  [Fact]
  public void Retry_resends_the_identical_frame_and_accepted_commit_is_not_durable_success() {
    Validated();
    Assert.True(_client.Commit(_now));
    Nv5ConfigurationFrame first = Next();
    byte[] firstBytes = Bytes(first);
    _client.Service(_now += 1000);
    Nv5ConfigurationFrame? retry = _client.TakeOutbound(_now);
    Assert.Same(first, retry);
    Assert.Equal(firstBytes, Bytes(retry!));

    Assert.True(Deliver(Response(first, Nv5ConfigStates.Committing, Nv5ConfigResults.Accepted)));
    Assert.Equal(Phase.Committing, _client.Phase);
    Assert.False(_client.DurableConfirmed);
    Assert.False(_client.Abort(_now));
    var changed = Target();
    changed.Uid[0] = (byte)'V';
    Assert.False(_client.Recover(changed, _now));
    Assert.True(_client.Recover(Target(), _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Committing)));
    _client.Service(_now += 100);
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Committing)));
    Assert.Equal(Phase.Committing, _client.Phase);
    _client.Service(_now += 100);
    var durable = Response(Next(), Nv5ConfigStates.CommittedRestartRequired);
    durable.ReceiptBootNonce = Boot;
    durable.TransactionRevision = 18;
    durable.CommittedRevision = 18;
    durable.RestartRequired = 1;
    Array.Fill(durable.TransactionDigest, (byte)0xab);
    Assert.True(Deliver(durable));
    Assert.Equal(Phase.RestartRequired, _client.Phase);
  }

  [Fact]
  public void Rediscovery_cancels_pending_commit_retry_and_starts_info_for_only_the_same_uid() {
    Validated();
    Assert.True(_client.Commit(_now));
    Assert.NotNull(_client.TakeOutbound(_now));
    var changed = Target();
    changed.Uid[0] = (byte)'V';
    Assert.False(_client.Recover(changed, _now));
    Assert.True(_client.Pending);

    Assert.True(_client.Recover(Target() with { TransportEpoch = 10 }, _now));
    Nv5ConfigurationFrame frame = Next();
    Assert.Equal(NvModemMessageIds.Nv5ConfigControl, frame.MessageId);
    var info = (Nv5ConfigControlMessage)frame.Payload;
    Assert.Equal(Nv5ConfigOperations.Info, info.Operation);
    Assert.Equal(0UL, info.BootNonce);
    _client.Service(_now += 1000);
    Assert.Equal(Nv5ConfigOperations.Info, ((Nv5ConfigControlMessage)Next().Payload).Operation);
    Assert.False(_client.DurableConfirmed);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void Late_exact_commit_receipt_preserves_recovery_and_proves_attribution(
      bool statusPending) {
    Validated();
    Assert.True(_client.Commit(_now));
    var late = Response(Next(), Nv5ConfigStates.CommittedRestartRequired);
    late.ReceiptBootNonce = Boot;
    late.TransactionRevision = late.CommittedRevision = 18;
    Array.Fill(late.TransactionDigest, (byte)0xab);
    Array.Fill(late.CommittedDigest, (byte)0xab);
    Assert.True(_client.Recover(Target(), _now));
    Nv5ConfigurationFrame infoFrame = Next();
    var activeInfo = Response(infoFrame, Nv5ConfigStates.Idle);
    activeInfo.BootNonce = Boot + 1;
    activeInfo.CommittedRevision = activeInfo.ActiveRevision = 18;
    Array.Fill(activeInfo.CommittedDigest, (byte)0xab);
    Array.Fill(activeInfo.ActiveDigest, (byte)0xab);
    Nv5ConfigurationFrame? status = null;
    if (statusPending) {
      Assert.True(Deliver(activeInfo));
      _client.Service(_now += 100);
      status = Next();
    }
    Phase phase = _client.Phase;

    for (int mismatch = 0; mismatch < 9; mismatch++) {
      Nv5ConfigResultMessage wrong = Copy(late);
      switch (mismatch) {
        case 0: wrong.ClientNonce++; break;
        case 1: wrong.Token++; break;
        case 2: wrong.Sequence++; break;
        case 3: wrong.ReceiptBootNonce++; break;
        case 4: wrong.BootNonce++; break;
        case 5: wrong.TransactionRevision++; break;
        case 6: wrong.TransactionDigest[0] ^= 1; break;
        case 7: wrong.CommittedDigest[0] ^= 1; break;
        default: wrong.TargetComponent++; break;
      }
      Assert.False(Deliver(wrong), $"corruption {mismatch}");
      Assert.False(_client.DurableConfirmed);
    }
    Assert.False(_client.Receive(Encode(late), "wrong-route", Target().TransportEpoch, _now));
    Assert.False(_client.Receive(Encode(late), Target().Route, Target().TransportEpoch + 1,
        _now));
    Assert.True(Deliver(late));
    Assert.True(_client.DurableConfirmed);
    Assert.True(_client.Pending);
    Assert.Equal(phase, _client.Phase);

    if (!statusPending) {
      _client.Service(_now += 1000);
      Assert.Same(infoFrame, _client.TakeOutbound(_now));
      Assert.True(Deliver(activeInfo));
      _client.Service(_now += 100);
      status = Next();
    }
    var noHistory = Response(status!, Nv5ConfigStates.Idle, Nv5ConfigResults.NotFound);
    noHistory.BootNonce = Boot + 1;
    noHistory.CommittedRevision = noHistory.ActiveRevision = 18;
    Array.Fill(noHistory.CommittedDigest, (byte)0xab);
    Array.Fill(noHistory.ActiveDigest, (byte)0xab);
    Assert.True(Deliver(noHistory));
    Assert.Equal(Phase.Active, _client.Phase);
  }

  [Fact]
  public void A_reply_delayed_beyond_the_five_second_link_loss_interval_remains_admissible() {
    Assert.True(_client.Discover(Target(), _now));
    Nv5ConfigurationFrame first = Next();
    for (int retry = 0; retry < 6; retry++) {
      _client.Service(_now += 1000);
      Assert.True(_client.Pending);
      Assert.Same(first, _client.TakeOutbound(_now));
    }
    Assert.True(Deliver(Response(first, Nv5ConfigStates.Idle)));
    Assert.Equal(Phase.Ready, _client.Phase);
  }

  [Fact]
  public void Unanswered_reads_give_up_after_eight_attempts_and_commit_after_four() {
    Assert.True(_client.Discover(Target(), _now));
    int attempts = 0;
    while (_client.TakeOutbound(_now) != null) {
      attempts++;
      _client.Service(_now += 1000);
    }
    Assert.Equal(8, attempts);
    Assert.False(_client.Pending);
    Assert.Equal(Phase.Error, _client.Phase);

    _client = new Nv5ConfigurationClient();
    Validated();
    Assert.True(_client.Commit(_now));
    attempts = 0;
    while (_client.TakeOutbound(_now) != null) {
      attempts++;
      _client.Service(_now += 1000);
    }
    Assert.Equal(4, attempts);
    Assert.Equal(Phase.Uncertain, _client.Phase);
  }

  [Fact]
  public void Receipt_must_match_original_boot_digest_revision_and_verified_active_state() {
    Validated();
    Assert.True(_client.Commit(_now));
    var reply = Response(Next(), Nv5ConfigStates.Active);
    reply.ReceiptBootNonce = Boot + 1;
    reply.TransactionRevision = reply.CommittedRevision = reply.ActiveRevision = 18;
    Array.Fill(reply.TransactionDigest, (byte)0xab);
    Array.Fill(reply.ActiveDigest, (byte)0xab);
    Assert.True(Deliver(reply));
    Assert.Equal(Phase.Uncertain, _client.Phase);

    Assert.True(_client.Recover(Target(), _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Active)));
    _client.Service(_now += 100);
    reply = Response(Next(), Nv5ConfigStates.Active);
    reply.ReceiptBootNonce = Boot;
    reply.TransactionRevision = reply.CommittedRevision = 18;
    reply.ActiveRevision = 17;
    Array.Fill(reply.TransactionDigest, (byte)0xab);
    Assert.True(Deliver(reply));
    Assert.Equal(Phase.Uncertain, _client.Phase);
  }

  [Fact]
  public void Wrong_endpoint_and_requester_targets_are_ignored() {
    Assert.True(_client.Discover(Target(), _now));
    var reply = Response(Next(), Nv5ConfigStates.Idle);
    Assert.False(_client.Receive(Encode(reply), "another-route", Target().TransportEpoch, _now));
    Assert.False(_client.Receive(Encode(reply), Target().Route, Target().TransportEpoch + 1,
        _now));
    Nv5ConfigResultMessage wrong = Copy(reply);
    wrong.TargetComponent = 189;
    Assert.False(Deliver(wrong));
    Assert.True(Deliver(reply));
  }

  [Fact]
  public void Snapshot_chunks_are_immutable_and_publish_only_after_complete_coverage() {
    Ready();
    Assert.True(_client.BeginRead(Nv5ConfigScopes.Committed, Nonce, _now));
    var reply = Response(Next(), Nv5ConfigStates.Idle);
    reply.Token = Token;
    reply.SnapshotRevision = 17;
    Assert.True(Deliver(reply));
    Assert.True(_client.Read(10, _now));
    reply = Response(Next(), Nv5ConfigStates.Idle);
    reply.SnapshotRevision = 17;
    Nv5ConfigValueMessage value = TextValue(reply, totalLength: 95);
    bool Chunk(ushort offset) {
      value.Offset = offset;
      value.Length = (byte)(offset == 0 ? 48 : 47);
      value.Data = new byte[48];
      Array.Fill(value.Data, (byte)'x', 0, value.Length);
      return _client.Receive(Encode(NvModemMessageIds.Nv5ConfigValue, value), Target().Route,
          Target().TransportEpoch, ++_now);
    }

    Assert.True(Chunk(48));
    Assert.Null(_client.TakeValue());
    Assert.True(Deliver(reply));
    Assert.Null(_client.TakeValue());
    Assert.True(Chunk(48));
    Assert.True(Chunk(0));
    Nv5ConfigurationClient.Value? read = _client.TakeValue();
    Assert.NotNull(read);
    Assert.Equal(Text('x', 95), read!.Bytes);
    Assert.Equal(408, read.Id);
    Assert.Equal("RTSP_PATH", read.Name);
  }

  [Fact]
  public void Empty_extra_chunk_cannot_poison_a_whole_field_retry() {
    Ready();
    Assert.True(_client.BeginRead(Nv5ConfigScopes.Committed, Nonce, _now));
    var reply = Response(Next(), Nv5ConfigStates.Idle);
    reply.Token = Token;
    reply.SnapshotRevision = 17;
    Assert.True(Deliver(reply));
    Assert.True(_client.Read(10, _now));
    reply = Response(Next(), Nv5ConfigStates.Idle);
    reply.SnapshotRevision = 17;
    Assert.True(Deliver(reply));
    Nv5ConfigValueMessage value = TextValue(reply, totalLength: 48);
    value.Offset = 48;
    bool DeliverValue() => _client.Receive(Encode(NvModemMessageIds.Nv5ConfigValue, value),
        Target().Route, Target().TransportEpoch, ++_now);

    Assert.False(DeliverValue());
    _client.Service(_now += 1000);
    Assert.NotNull(_client.TakeOutbound(_now));
    value.Offset = 0;
    value.Length = 48;
    value.Data = Text('x', 48);
    Assert.True(DeliverValue());
    Assert.Equal(Text('x', 48), _client.TakeValue()!.Bytes);
  }

  [Fact]
  public void Calibration_client_uses_its_own_messages_capability_and_catalogue() {
    var client = new Nv5ConfigurationClient(
        resource: Nv5ConfigurationClient.ResourceKind.Calibration);
    Assert.False(client.Discover(Target(), _now));
    var target = Target() with {
      Capabilities = NvModemCapabilities.ConfigurationTransactions
          | NvModemCapabilities.CalibrationTransactions,
    };
    Assert.NotEqual("",
        Nv5ConfigurationClient.CalibrationRouteProblem(target with { ViaHub = true }));
    Assert.False(client.Discover(target with { ViaHub = true }, _now));

    Assert.True(client.Discover(target, _now));
    Nv5ConfigurationFrame frame = client.TakeOutbound(_now)!;
    Assert.Equal(NvModemMessageIds.Nv5CalControl, frame.MessageId);
    var control = (Nv5CalControlMessage)frame.Payload;
    Assert.Equal(Nv5ConfigOperations.Info, control.Operation);
    Nv5ConfigResultMessage settings = Response(
        new Nv5ConfigurationFrame(NvModemMessageIds.Nv5ConfigControl, new Nv5ConfigControlMessage {
          Operation = control.Operation,
          Sequence = control.Sequence,
        }), Nv5ConfigStates.Idle);
    Assert.False(client.Receive(Encode(settings), target.Route, target.TransportEpoch, ++_now));

    Assert.True(client.Receive(
        Encode(NvModemMessageIds.Nv5CalResult, CalibrationResult(settings, 0x12345678, 168)),
        target.Route, target.TransportEpoch, ++_now));
    Assert.Equal(Phase.Error, client.Phase);

    client.Reset();
    Assert.True(client.Discover(target, _now));
    control = (Nv5CalControlMessage)client.TakeOutbound(_now)!.Payload;
    settings.Sequence = control.Sequence;
    Assert.True(client.Receive(
        Encode(NvModemMessageIds.Nv5CalResult, CalibrationResult(settings,
            Nv5ConfigurationClient.CalibrationCatalog, Nv5ConfigurationClient.CalibrationEntries)),
        target.Route, target.TransportEpoch, ++_now));
    Assert.Equal(Phase.Ready, client.Phase);
  }

  [Fact]
  public void Real_firmware_configuration_core_exchanges_frames_with_this_client() {
    string? fixture = Environment.GetEnvironmentVariable("MP_NV5_CONFIG_FIXTURE");
    if (string.IsNullOrEmpty(fixture)) {
      output.WriteLine("MP_NV5_CONFIG_FIXTURE not set; skipped.");
      return;
    }
    NvModemMavlinkDialect.Register();
    using var server = Process.Start(new ProcessStartInfo(fixture) {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      UseShellExecute = false,
    })!;
    Nv5ConfigurationClient.Target target = Target();
    void Pump() {
      _client.Service(_now);
      Nv5ConfigurationFrame? frame = _client.TakeOutbound(_now);
      if (frame == null) {
        return;
      }
      byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
          (MAVLink.MAVLINK_MSG_ID)frame.MessageId, frame.Payload, false, 255, 190);
      string request = $"{_now} {Convert.ToHexString(bytes).ToLowerInvariant()}";
      output.WriteLine("> " + request);
      server.StandardInput.Write(request + "\n");
      server.StandardInput.Flush();
      while (server.StandardOutput.ReadLine() is { } line && line.Trim() != ".") {
        output.WriteLine("< " + line);
        MAVLink.MAVLinkMessage reply = new MAVLink.MavlinkParse().ReadPacket(
            new MemoryStream(Convert.FromHexString(line.Trim())))!;
        Assert.True(_client.Receive(reply, target.Route, target.TransportEpoch, _now),
            $"unmatched reply {reply.msgid}");
      }
    }

    Assert.True(_client.Discover(target, _now));
    Pump();
    Assert.Equal(Phase.Ready, _client.Phase);
    Assert.True(_client.Begin(Nonce, _client.Result.CommittedRevision, _now));
    Pump();
    byte[] saveMs = Convert.FromHexString("b80b0000");
    Assert.True(_client.Stage("MAV_SAVE_MS", Nv5ConfigValueTypes.UInt32, saveMs, _now));
    Pump();
    Assert.True(_client.Stage("RTSP_PATH", Nv5ConfigValueTypes.Text, Text('p', 95), _now));
    Pump();
    Pump();
    Assert.True(_client.Validate(_now));
    Pump();
    _now += 100;
    Pump();
    Assert.Equal(Phase.Validated, _client.Phase);
    Assert.True(_client.BeginRead(Nv5ConfigScopes.Candidate, 0, _now));
    Pump();
    _now += 100;
    Pump();
    Assert.True(_client.SnapshotReady);
    Assert.True(_client.Commit(_now));
    Pump();
    _now += 100;
    Pump();
    Assert.Equal(Phase.RestartRequired, _client.Phase);

    server.StandardInput.Write("REBOOT\n");
    server.StandardInput.Flush();
    Assert.Equal(".", server.StandardOutput.ReadLine()?.Trim());
    Assert.True(_client.Recover(target, _now));
    Pump();
    _now += 100;
    Pump();
    Assert.Equal(Phase.Active, _client.Phase);
    Assert.True(_client.BeginRead(Nv5ConfigScopes.Committed, Nonce + 1, _now));
    Pump();
    Assert.True(_client.ConfigurationMatchesExpected());
    Assert.True(_client.Read(3, _now));
    Pump();
    Nv5ConfigurationClient.Value? value = _client.TakeValue();
    Assert.NotNull(value);
    Assert.Equal("MAV_SAVE_MS", value!.Name);
    Assert.Equal(saveMs, value.Bytes);
    server.StandardInput.Close();
    Assert.True(server.WaitForExit(10_000));
    Assert.Equal(0, server.ExitCode);
  }

  private static Nv5ConfigurationClient.Target Target() =>
      new(Text('U', 18), "verified-udp-peer", 9, NvModemCapabilities.ConfigurationTransactions,
          10, 11, true);

  private Nv5ConfigurationFrame Next() {
    Nv5ConfigurationFrame? frame = _client.TakeOutbound(_now);
    Assert.NotNull(frame);
    return frame!;
  }

  private bool Deliver(Nv5ConfigResultMessage reply) =>
      _client.Receive(Encode(reply), Target().Route, Target().TransportEpoch, ++_now);

  private void Ready() {
    Assert.True(_client.Discover(Target(), _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Idle)));
    Assert.Equal(Phase.Ready, _client.Phase);
  }

  private void Staged() {
    Ready();
    Assert.True(_client.Begin(Nonce, 17, _now));
    Assert.True(Deliver(Response(Next(), Nv5ConfigStates.Staged)));
    Assert.Equal(Phase.Staged, _client.Phase);
  }

  private void Validated() {
    Staged();
    Assert.True(_client.Validate(_now));
    var reply = Response(Next(), Nv5ConfigStates.Validated);
    Array.Fill(reply.TransactionDigest, (byte)0xab);
    reply.CandidateRestartRequired = 1;
    Assert.True(Deliver(reply));
    Assert.Equal(Phase.Validated, _client.Phase);
  }

  private static Nv5ConfigResultMessage Response(Nv5ConfigurationFrame request, byte state,
      byte result = Nv5ConfigResults.Ok) {
    var reply = new Nv5ConfigResultMessage {
      BootNonce = Boot,
      State = state,
      Result = result,
      Capabilities = 3,
      CommittedKnown = 1,
      CommittedRevision = 17,
      ActiveRevision = 17,
      SettingCount = 168,
      CatalogId = 0x12345678,
      TargetSystem = 255,
      TargetComponent = 190,
      SchemaVersion = 1,
      CommittedDigest = new byte[32],
      ActiveDigest = new byte[32],
      TransactionDigest = new byte[32],
      SnapshotDigest = new byte[32],
      ErrorName = new byte[16],
    };
    if (request.Payload is Nv5ConfigControlMessage control) {
      reply.Operation = control.Operation;
      reply.ClientNonce = control.ClientNonce;
      reply.Token = control.Token;
      reply.Sequence = control.Sequence;
      reply.Scope = control.Scope;
      reply.NextSequence = control.Operation == Nv5ConfigOperations.Begin ? 1 : control.Sequence + 1;
      if (control.Operation == Nv5ConfigOperations.Begin && result == Nv5ConfigResults.Ok) {
        reply.Token = Token;
        reply.TransactionRevision = 17;
      }
    } else {
      var item = (Nv5ConfigItemMessage)request.Payload;
      reply.Operation = Nv5ConfigOperations.Set;
      reply.ClientNonce = item.ClientNonce;
      reply.Token = item.Token;
      reply.Sequence = item.Sequence;
      reply.NextSequence = item.Sequence + 1;
    }
    return reply;
  }

  private static Nv5ConfigValueMessage TextValue(Nv5ConfigResultMessage read, ushort totalLength) {
    var name = new byte[16];
    Encoding.ASCII.GetBytes("RTSP_PATH").CopyTo(name, 0);
    return new Nv5ConfigValueMessage {
      BootNonce = Boot,
      ClientNonce = Nonce,
      Token = Token,
      Revision = 17,
      Sequence = read.Sequence,
      CatalogId = read.CatalogId,
      Index = 10,
      SettingCount = 168,
      SettingId = 408,
      TotalLength = totalLength,
      Name = name,
      Data = new byte[48],
      ValueType = Nv5ConfigValueTypes.Text,
      Scope = Nv5ConfigScopes.Committed,
      TargetSystem = 255,
      TargetComponent = 190,
      SchemaVersion = 1,
    };
  }

  private static Nv5CalResultMessage CalibrationResult(Nv5ConfigResultMessage settings,
      uint catalog, ushort count) => new() {
    BootNonce = settings.BootNonce,
    ClientNonce = settings.ClientNonce,
    Token = settings.Token,
    Sequence = settings.Sequence,
    NextSequence = settings.NextSequence,
    CommittedRevision = settings.CommittedRevision,
    ActiveRevision = settings.ActiveRevision,
    CatalogId = catalog,
    SettingCount = count,
    Capabilities = settings.Capabilities,
    CommittedDigest = new byte[32],
    ActiveDigest = new byte[32],
    TransactionDigest = new byte[32],
    SnapshotDigest = new byte[32],
    ErrorName = new byte[16],
    Operation = settings.Operation,
    State = settings.State,
    Result = settings.Result,
    CommittedKnown = settings.CommittedKnown,
    TargetSystem = settings.TargetSystem,
    TargetComponent = settings.TargetComponent,
    SchemaVersion = settings.SchemaVersion,
  };

  // Copying a message struct shares its arrays; a test that changes a copy's digest needs its own.
  private static Nv5ConfigResultMessage Copy(Nv5ConfigResultMessage reply) {
    Nv5ConfigResultMessage copy = reply;
    copy.CommittedDigest = (byte[])reply.CommittedDigest.Clone();
    copy.ActiveDigest = (byte[])reply.ActiveDigest.Clone();
    copy.TransactionDigest = (byte[])reply.TransactionDigest.Clone();
    copy.SnapshotDigest = (byte[])reply.SnapshotDigest.Clone();
    copy.ErrorName = (byte[])reply.ErrorName.Clone();
    return copy;
  }

  private static MAVLink.MAVLinkMessage Encode(Nv5ConfigResultMessage reply) =>
      Encode(NvModemMessageIds.Nv5ConfigResult, reply);

  private static MAVLink.MAVLinkMessage Encode(uint id, object payload) {
    NvModemMavlinkDialect.Register();
    byte[] bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
        (MAVLink.MAVLINK_MSG_ID)id, payload, false, 10, 11);
    return new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes))!;
  }

  private static byte[] Bytes(Nv5ConfigurationFrame frame) {
    NvModemMavlinkDialect.Register();
    return new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(
        (MAVLink.MAVLINK_MSG_ID)frame.MessageId, frame.Payload, false, 255, 190, 0);
  }

  private static byte[] Text(char letter, int count) =>
      Enumerable.Repeat((byte)letter, count).ToArray();
}
