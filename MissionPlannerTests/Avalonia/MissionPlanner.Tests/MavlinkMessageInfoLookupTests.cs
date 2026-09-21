namespace MissionPlanner.Tests;

public class MavlinkMessageInfoLookupTests {
  [Fact]
  public void Every_table_entry_resolves_like_the_linear_scan() {
    MAVLink.message_info[] table = MAVLink.MAVLINK_MESSAGE_INFOS;

    foreach (MAVLink.message_info entry in table) {
      AssertSame(LinearScan(table, entry.msgid), table.GetMessageInfo(entry.msgid));
    }
  }

  [Fact]
  public void Unknown_id_returns_the_default_entry() {
    MAVLink.message_info info = MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(uint.MaxValue);

    Assert.Null(info.name);
    Assert.Null(info.type);
    Assert.Equal(0u, info.msgid);
  }

  [Fact]
  public void First_entry_wins_in_the_indexed_global_table() {
    MAVLink.message_info[] original = MAVLink.MAVLINK_MESSAGE_INFOS;

    try {
      // Plugins extend the table by appending, sometimes with an id that already exists.
      MAVLink.MAVLINK_MESSAGE_INFOS =
          [.. original, new MAVLink.message_info(0, "APPENDED_DUPLICATE", 1, 1, 1, typeof(int))];

      Assert.Equal("HEARTBEAT", MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(0).name);
    } finally {
      MAVLink.MAVLINK_MESSAGE_INFOS = original;
    }
  }

  [Fact]
  public void First_entry_wins_in_a_scanned_custom_array() {
    MAVLink.message_info[] table = [
      new(7, "FIRST", 11, 1, 1, typeof(int)),
      new(8, "OTHER", 12, 2, 2, typeof(long)),
      new(7, "SECOND", 13, 3, 3, typeof(short)),
    ];

    Assert.Equal("FIRST", table.GetMessageInfo(7).name);
    Assert.Equal("OTHER", table.GetMessageInfo(8).name);
  }

  [Fact]
  public void Replaced_global_table_is_picked_up() {
    MAVLink.message_info[] original = MAVLink.MAVLINK_MESSAGE_INFOS;
    const uint NewId = 16_000_001;
    Assert.Null(original.GetMessageInfo(NewId).name);

    try {
      MAVLink.MAVLINK_MESSAGE_INFOS =
          [.. original, new MAVLink.message_info(NewId, "LOOKUP_TEST", 1, 4, 4, typeof(int))];

      Assert.Equal("LOOKUP_TEST", MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(NewId).name);
      Assert.Equal("HEARTBEAT", MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(0).name);
    } finally {
      MAVLink.MAVLINK_MESSAGE_INFOS = original;
    }

    Assert.Null(MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(NewId).name);
  }

  [Fact]
  public void Stale_reference_to_a_replaced_table_still_resolves_its_own_entries() {
    MAVLink.message_info[] original = MAVLink.MAVLINK_MESSAGE_INFOS;
    const uint MarkerId = 16_000_002;

    try {
      // The replacement keeps every real entry, so a parser still running in the background
      // never sees a table with messages missing.
      MAVLink.MAVLINK_MESSAGE_INFOS =
          [.. original, new MAVLink.message_info(MarkerId, "STALE_TEST", 1, 4, 4, typeof(int))];

      Assert.Null(original.GetMessageInfo(MarkerId).name);
      Assert.Equal("HEARTBEAT", original.GetMessageInfo(0).name);
      Assert.Equal("ATTITUDE", original.GetMessageInfo(30).name);
      Assert.Equal("STALE_TEST", MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(MarkerId).name);
    } finally {
      MAVLink.MAVLINK_MESSAGE_INFOS = original;
    }

    Assert.Equal("HEARTBEAT", MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(0).name);
    Assert.Null(MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(MarkerId).name);
  }

  [Fact]
  public void Alternating_arrays_each_resolve_their_own_entries() {
    MAVLink.message_info[] custom = [new(0, "NOT_HEARTBEAT", 1, 1, 1, typeof(int))];

    for (int round = 0; round < 3; round++) {
      Assert.Equal("NOT_HEARTBEAT", custom.GetMessageInfo(0).name);
      Assert.Equal("HEARTBEAT", MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(0).name);
      Assert.Null(custom.GetMessageInfo(30).name);
      Assert.Equal("ATTITUDE", MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(30).name);
    }
  }

  [Fact]
  public void Concurrent_lookups_on_two_arrays_stay_consistent() {
    MAVLink.message_info[] table = MAVLink.MAVLINK_MESSAGE_INFOS;
    MAVLink.message_info[] custom = [new(30, "CUSTOM_30", 1, 1, 1, typeof(int))];
    int mismatches = 0;

    Parallel.For(0, 50_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index => {
      MAVLink.message_info expected = table[index % table.Length];
      if (table.GetMessageInfo(expected.msgid).name != LinearScan(table, expected.msgid).name) {
        Interlocked.Increment(ref mismatches);
      }
      if ((index & 7) == 0 && custom.GetMessageInfo(30).name != "CUSTOM_30") {
        Interlocked.Increment(ref mismatches);
      }
    });

    Assert.Equal(0, mismatches);
  }

  // The pre-index body of GetMessageInfo.
  private static MAVLink.message_info LinearScan(MAVLink.message_info[] source, uint msgid) {
    foreach (MAVLink.message_info item in source) {
      if (item.msgid == msgid) {
        return item;
      }
    }
    return new MAVLink.message_info();
  }

  private static void AssertSame(MAVLink.message_info expected, MAVLink.message_info actual) {
    Assert.Equal(expected.msgid, actual.msgid);
    Assert.Equal(expected.name, actual.name);
    Assert.Equal(expected.crc, actual.crc);
    Assert.Equal(expected.minlength, actual.minlength);
    Assert.Equal(expected.length, actual.length);
    Assert.Equal(expected.type, actual.type);
  }
}
