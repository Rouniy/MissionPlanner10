# NV Modem setup

Setup > NV Modem configures NV4 and NV5 radio modems over the MAVLink connections already open in
Mission Planner. Its behavior follows the modem vendor's reference ground station. Per-endpoint
direct/HUB failover is not part of it, because Mission Planner's shared parser does not retain
per-datagram sender/listener metadata.

## Connection and device identity

The page does not ask for an address or open another UDP, TCP or UART connection. It subscribes to
every open Mission Planner `MAVLinkInterface`, registers the private NV modem MAVLink message
layouts with the shared parser and sends each request back through the exact interface on which the
modem was observed. A device key is therefore:

`MAVLink interface + system id + component id`

This keeps modems with identical MAVLink IDs on different network or serial links independent.
It also supports attached modems forwarded by an NV5 HUB: replies from distinct system/component
IDs are kept on the same observed Mission Planner link, and requests are sent back on that link to
the attached modem's exact MAVLink target. The reference ground station can additionally bind
individual UDP datagrams to private HUB management sender/listener metadata. Mission Planner's
shared parser does not retain per-datagram sender metadata, so its safe equivalent is link
ownership; it deliberately does not guess an IP route that the parser has discarded.
NV4 replies use the same observed Mission Planner link, the equivalent of the reference ground
station's addressed-UDP-route fix. Discovery has no system-ID or component-ID range. Current NV4
and NV5 devices are identified by the periodic `NV_MODEM_INFO` passport (`53016`), including
receive-only or unconfigured hardware with no live radio traffic. Older NV5 firmware falls back to
its private live-status/configuration messages. Unmodified NV4 firmware falls back to `NV_RX_STAT`
or either `UAVCAN_NODE_INFO` signature: the current one has hardware and software major version 4
and a name beginning `TX_` or `RX_`, while the legacy RFM one accepts case-insensitive `NV_TX` or
`NV_RX` prefixes even when old firmware leaves the version majors unset. NV5 parameter-family
signatures are also accepted, while an NV4 parameter can only refine an already identified device,
which avoids false positives. The page
replays all discovery packet types from the shared Mission Planner cache when it is opened after a
modem was already seen, and requests the passport, NV5 status, and CAN node information from every
observed address as well as by broadcast. The private NV modem dialect is registered at application
startup, before any shared connection starts reading, so an early identity/status packet is not
lost while the setup page is still closed. An ordinary `AUTOPILOT_VERSION` is not enough to classify
a flight controller as a modem. NV4 firmware advertises either `REFRESH_SETTING` or the older
`REFRESH_SETTINGS`; the page recognizes both and always writes the advertised name.
The shared passport is accepted only at schema version 1, generation 4 or 5, and with the MAVLink
parameter capability bit set. A nonzero `UID2` is the durable device identity. If an offline modem
returns on the same Mission Planner link with a new system/component address, its parameter and UI
state moves to the new address instead of creating a duplicate. An address change just written by
this page is also accepted after `MAV_SAVE_MS` persistence. Concurrent live duplicates, ambiguous
UIDs, occupied target addresses and identity changes during a write are rejected.

## Settings behavior

The page includes:

- typed, bytewise MAVLink integer parameter decoding and encoding;
- complete NV4/NV5 parameter descriptions from the reference ground station, displayed in the
  **Description / values** column and retained in the port source catalog. The canonical 56-name
  NV4 sketch catalog is pinned by a regression test, while the runtime table remains driven by
  the exact parameters advertised by the connected firmware;
- explicit changed, invalid and read-only state in the parameter table;
- live NV4 or per-radio NV5 link status;
- LR2021/LoRa/FLRC, FHSS, FEC and role presets, staged locally until **Save**;
- current reference acquisition defaults: LoRa uses frame 64, FHSS enabled and scan dwell 5; FLRC
  uses frame 240, FHSS disabled and scan dwell 2; both stage a 40000 kHz FHSS span;
- channel-settings copy from another completely read NV5 modem;
- NV4 32-byte keys accepted as 32 printable ASCII characters or 64 hexadecimal digits (an optional
  `hex:` prefix remains compatible), while display and generation use 64 uppercase hexadecimal
  digits from 32 cryptographically random bytes; NV5 accepts a 16-byte AES key as exactly 32
  case-insensitive hexadecimal digits and normalizes it to uppercase;
- four big-endian MAVLink `INT32` parameters (`CHx_KEY_W0..W3`) for each NV5 key. Their signed
  decimal values preserve the same raw 32 bits; ordinary **Save** writes edited words as exact
  typed `PARAM_SET` values, while **SET KEY** persists a complete key snapshot atomically through
  `NV_ENCRYPTION_KEYS_SET` (`53017`) with idempotent retries and a post-persistence
  `NV_ENCRYPTION_KEYS_ACK` (`53018`);
- receive diversity never mirrors or couples encryption keys: staging, generation and atomic
  **SET KEY** affect only the explicitly selected radio, so Radio 1 and Radio 2 may use different
  AES keys;
- **Revert selected** restores only the selected locally changed parameter to the last value read
  from the modem. It sends no MAVLink message and leaves
  every other staged edit untouched;
- NV4 `ENC_KEY_BITS` is restricted to the only effective firmware value, 128 bits, while all eight
  signed key words and both advertised refresh spellings remain compatible with legacy units;
- RTSP path get/set and transport presets for supported LR2021 configurations;
- transmitter enable/suppress diagnostics and standard MAVLink reboot;
- Mission Planner-compatible `.param` import/export. Exports carry a sensitive-data warning because
  they can contain readable encryption keys and network settings.

Parameter snapshots are deliberately not written to application settings. Selecting another modem
or refreshing the current one clears the visible list before requesting new values. A silent modem
is retried up to six times to cover STM32 Ethernet renegotiation and then reports an error without
blocking connection or device selection. A retry preserves parameter indexes already received
instead of clearing a slow but progressing catalogue. Writes are serialized, acknowledged and
retried; stale or wrongly typed list responses cannot impersonate an `INT32` key write echo. Frame
sizes are limited to 64..496 bytes in steps of 16. If firmware repeatedly echoes the retained value
instead of the requested value, the final error names the parameter and reports both typed values
instead of presenting the rejection as a silent timeout. After
ordinary NV5 `PARAM_SET` completion the page leaves the full reread to **Refresh selected** so it
does not race the `MAV_SAVE_MS` debounce, flash commit and reboot. While a write is in flight the
target selector is locked, and a target/link change during confirmation prevents the operation.

## Native settings on transaction-capable NV5 firmware

The page decides by capability, not by generation. When a modem's passport advertises
configuration transactions (`NV_MODEM_INFO` capability bit 14), the page reads and saves its
settings through the NV5 native configuration protocol (`NV5_CONFIG_CONTROL`, `NV5_CONFIG_ITEM`,
`NV5_CONFIG_RESULT`, `NV5_CONFIG_VALUE`, 53021–53024) and never sends it PARAM requests. The
parameter table, presets, key and RTSP groups and the PARAM save buttons give way to **Modem
settings**; maintenance controls and reboot stay. NV4 modems and NV5 firmware without the bit keep
the PARAM page described above, unchanged. The firmware's ranging, LPS, calibration and capture
messages (53020, 53025–53028, 53030–53033) are registered with the shared parser for the features
that use them.

- **Read.** The complete typed catalogue is read, including inactive PHY settings, in the native
  units (carrier frequency in integer Hz). A modem that reports the same boot, catalogue,
  committed revision and digest as an earlier read is shown at once; after a restart with the same
  revision and digest only derived read-only values, such as the link hash, are read again; a read
  cut short by a restart resumes where it stopped. A modem serves one read session at a time, so a
  refused read is tried again on its own for up to 30 s, and readers sharing the page's snapshot
  store never open two sessions on one modem. **Read settings** and **Refresh selected** always read
  in full.
- **Save and restart.** Only changed values are staged; a changed key word stages all four words of
  that radio's key. The modem validates the complete candidate; a refusal names the field and the
  candidate link hashes. COMMIT is sent at most four times and is never repeated after a restart.
  Success needs this save's durable receipt and a same-UID readback of the committed and active
  revision and digest. A lost receipt with matching settings is reported as uncertain, never as
  saved, and a transaction failure never falls back to PARAM_SET. After success the modem is read
  again.
- **Snapshots.** **Export snapshot…** writes the committed values as JSON, with 64-bit identifiers
  as strings; it includes key words, so the file is created readable by its owner only. **Load
  snapshot…** stages a matching catalogue's values as edits and stages nothing if any field is
  invalid.
- **Transport.** Frames go out on the Mission Planner link where the modem was seen, from the
  application's GCS system id and component 190. Retries resend the identical payload; the
  firmware matches them by transaction nonce, token and sequence. Each modem UID has a transport
  epoch that advances when the passport uptime goes back, so a restart ends the old boot's session.
  While a save is in progress the selected modem cannot be changed.

Automated tests cover the protocol client, the editor against an in-process modem with a commit
model, the page's capability switch for NV4 and NV5, and the layout at narrow widths. With
`MP_NV5_CONFIG_FIXTURE` set to the NV5 firmware's host `nv5-configuration-fixture`, two further
tests exchange real frames with the firmware's configuration core: one drives the client through
commit, restart, recovery and readback, the other reads the real catalogue through the editor,
saves a change, restarts the fixture and reads the change back. They do not cover flash
interruption, physical restart timing or radio behavior.

## Acceptance boundary

The shared Mission Planner parser, custom CRC/layouts, multi-link and HUB target isolation, strict
passport validation, UID2 address migration and conflict guards, NV4 apply transaction, both NV5
key-write paths, exact typed echoes and rejection diagnostics, independent diversity key targeting,
RTSP dirty-state handling, current preset staging, parameter-file roundtrip and slow/silent-device
handling are covered by automated tests. NV4 coverage additionally pins the complete sketch
catalog, parameter-specific descriptions and both apply-trigger spellings. A representative
physical NV4 and NV5
modem on UDP/TCP/UART still require an operator acceptance run, including reboot/reappearance and
real RF/RTSP behavior.
