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
parameter capability bit set. A nonzero `UID2` is the durable device identity. If a modem returns
with a new system/component address, on the same Mission Planner link once offline or on another
link once its own connection closed, its parameter and UI state moves to the new address instead
of creating a duplicate. Records left on closed connections are earlier routes to the same modem:
they never count as live duplicates and are retired when it moves. An address change just written
by this page is accepted at once: through `PARAM_SET` after `MAV_SAVE_MS` persistence, and through
a native save of `MAV_SYS_ID` (system ID) or `LOCAL_IP_4` (component ID). A second live address on
one link, ambiguous UIDs, occupied target addresses and identity changes during a write are
rejected. Two open links to one modem keep a record each.

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
`NV5_CONFIG_RESULT`, `NV5_CONFIG_VALUE`, 53021–53024). The parameter table, presets, key and RTSP
groups and the PARAM save buttons give way to **Modem settings**; maintenance controls and reboot
stay. NV4 modems and NV5 firmware without the bit keep the PARAM page described above, unchanged.
The firmware's ranging, LPS, calibration and capture messages (53020, 53025–53028, 53030–53033)
are registered with the shared parser; the Distance group below shows the ranging telemetry
(`NV5_DISTANCE`, 53020) and the LPS reports (`NV5_LPS_STATUS` and `NV5_LPS_BEACON`, 53030–53031).

Capability is known only from the passport. A modem first heard through other telemetry has its
PARAM read deferred while the page requests the passport: the read is skipped if the passport
shows configuration transactions, and sent at once if it does not. Firmware that never sends a
passport is read after 3 s, as before. A transaction-capable modem whose passport arrives later
than that is read the legacy way until it does: a read-only `PARAM_REQUEST_LIST`, repeated after
each 2 s without a reply, seven requests at most. From its passport on it is configured natively
and sent no further PARAM traffic. Modem commands and a native save never overlap: maintenance
commands and reboot wait while a native save is in progress, and **Save and restart** waits while
a modem command awaits its acknowledgement.

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
- **Snapshots.** **Export snapshot…** writes the values of the last read as JSON, labelled with
  that read's catalogue and committed revision and with 64-bit identifiers as strings; until the
  modem is read again after a save, export and import are unavailable. The file includes key
  words, so it is created readable by its owner only: mode 0600 on Linux and macOS, and on
  Windows a protected access list granting only the current user. Those permissions are read back
  from the new file before anything is written to it; a destination that cannot keep them, such
  as a FAT32 or exFAT drive, is refused and its earlier file is left as it was.
  **Load snapshot…** stages a matching catalogue's values as edits and stages nothing if any field
  is invalid.
- **Transport.** Frames go out on the Mission Planner link where the modem was seen, from the
  application's GCS system id and component 190. Retries resend the identical payload; the
  firmware matches them by transaction nonce, token and sequence. Each request goes out as soon as
  the previous one is answered, so a read takes round trips rather than service intervals. Each
  modem UID has a transport epoch that advances when the passport uptime goes back, so a restart
  ends the old boot's session. While a save is in progress the selected modem cannot be changed.
- **Distance settings** shows only the pair ranging settings (`CH?_RNG_*`) of both radios; the
  `LPS_RNG_` settings belong to the LPS field.

## Distance between two modems

The **Distance** group of a transaction-capable modem lists, for each radio, whether ranging is on
and whether this modem measures or answers, the reading and the latest burst, laid out like Live
radio status. It has no per-modem switch, because ranging is always switched for both ends of a
link: **Configure distance pair…** opens the pair window, and the line beside it says why the
window cannot open now, for example while the modem is read or has unsaved edits.

- **Readings.** The measuring modem reports `NV5_DISTANCE`. A reading has signed metres, its
  validity and its age in whole seconds; a reading that stops arriving turns STALE, and missing
  telemetry is named only once it is overdue. A correction is shown only when the modem proves it
  applied one (restored bank, applied status, epoch, revision, identifier, finite values); the raw
  range stays beside it, and a raw reading says why a stored correction does not apply. Only a
  failure is red. The counts belong to the latest burst: the measuring modem shows how many of its
  exchanges were valid, timed out or late, the answering modem how many it answered.
- **Pair window.** The selected modem is Modem 1. Modem 2 is chosen from every other verified,
  transaction-capable modem that is online, and a modem heard later joins the list; the window
  opens on the one whose live radio roles and carrier look like the other end of a link. No modem
  outside the pair is read or written. While the window is open the page's own controls wait, and
  the window's editors follow both modems through their restarts. A modem also heard on a second
  open link keeps the link the window uses; it moves only once that link closes.
- **Form.** Radio, **Measured by** (`CHn_RNG_MGR`: the data receiver or the data transmitter;
  firmware without the setting always measures on the receiver, and two modems of which only one
  has it are not saved), **Measure distance on this radio link** (`CHn_RNG_EN`), period (a power of
  two from 2 to 8388608 data packets; a stored value outside that list is shown, not replaced),
  exchanges per burst (1–29), additional margin (0–40 ms, `CHn_RNG_PAD_MS`), hop span in kHz
  (unused with one exchange), ranging power (−19 to 22 dBm), a nonzero hexadecimal address, and
  under **Advanced** the phase, which must stay below the period and is flagged rather than
  rewritten when the period shrinks. The line under the exchanges previews the data pause each
  burst takes. Enter in a field only ends the edit, and the mouse wheel changes only the field with
  the keyboard focus.
- **Saving.** The two radios need opposite TX/RX roles. Both modems stage the same values and
  validate; their candidate link hashes must match. Each then stages the other's hash as its peer
  proof (`CHn_PEER_HASH`) and validates again. The answering modem commits first and the measuring
  one once the first save is durable; both restart and are read again, after which the pair can be
  changed again. Each modem commits atomically, but the pair is not one transaction: a partial or
  uncertain outcome names each modem's outcome and asks for both modems to be read before
  retrying. Once a commit has gone out, the window stays open until every committed save is
  confirmed or has failed; a modem not yet committed when the other one fails is not committed at
  all. A check that fails before either commit aborts both transactions and gives each table back
  what it showed before the save, edits made in its tab included; the form keeps its values.
  Close waits until every such abort is answered, including one that waits for a request still in
  flight, and then closes the window on its own: a closed window sends nothing more, and the modem
  would stay busy until its writer lease ran out. A save begun meanwhile keeps the window open for
  its outcome. A modem's refusal is named with the field and link hashes it reported. A modem
  whose settings changed after it was read must be read again before either modem saves. The
  measuring end of a running pair changes only with ranging switched off first.
- **Switching off.** Clearing the box and saving switches ranging off on both modems. When ranging
  is on somewhere and the modems are not both read, one of them restarted within the last minute,
  or one of them lacks the margin setting, each modem that may range saves only `CHn_RNG_EN = 0`
  and `CHn_PEER_HASH = 0` on its own, without a read or a peer check; a restart before its commit
  begins it again on the next boot, for up to two minutes. A modem whose read settings at the
  committed revision say ranging is off, with no telemetry saying it is on, is left alone.
- **Compare.** The selected radio's settings of both modems side by side: differing shared link
  settings are highlighted, settings the two ends may set differently and those pair setup writes
  are listed without a highlight, roles stay on top, and key words are compared without showing
  them. The summary says whether the link fingerprints match and what to change when they do not.
  The modem tabs hold the complete tables, keys included; edits made there are saved with the pair.
  When a modem uses the radio for LPS, the form says so: the LPS burst takes the same exchanges,
  span and power, and pair ranging cannot run on that radio meanwhile.

Automated tests cover the protocol client, the editor against an in-process modem with a commit
model, the page's capability switch for NV4 and NV5, the Distance group and the pair window
against in-process modems, and the layout at narrow widths. With `MP_NV5_CONFIG_FIXTURE` set to
the NV5 firmware's host `nv5-configuration-fixture`, further tests exchange real frames with the
firmware's configuration core: one drives the client through commit, restart, recovery and
readback; one reads the real catalogue through the editor, saves a change, restarts the fixture
and reads the change back; one switches ranging off without a read; one pairs two fixture
instances through the window, including the peer proof; and one saves three instances as one LPS
field through the LPS field window, below. The host fixture has no LR2021 radio, on which alone
the firmware allows ranging on, so that pair is saved with ranging off. The tests do not cover
flash interruption, physical restart timing or radio behavior.

## LPS field

A modem with LPS settings (`LPS_ROLE` and the field it belongs to) shows its LPS role in the
Distance group, which is then titled **Distance and LPS**. A radio that LPS uses shows LPS in its
row instead of pair ranging, since one radio never runs both: a rover's row has its position, or
why there is none, and the latest range to each beacon; a beacon's row has its latest answer. The
pair window keeps showing each radio's own ranging state. **Configure LPS field…**, on its own
line of the group, opens the LPS field window, and the line beside it says what the window does or
why it cannot open now, as for the pair window. The two windows are never open together.

- **Reports and freshness.** `NV5_LPS_STATUS` and `NV5_LPS_BEACON` count only from the modem's
  verified route and UID, and a beacon row only with the exact capture of the latest status; a
  report never renews an older one. The page asks the selected modem, and the window each of its
  modems, whether the reports are current: an exact-target `MAV_CMD_REQUEST_MESSAGE` for 53030
  with a fresh random 64-bit nonce in params 2–4 (24, 24 and 16 bits), retried after three seconds
  and refreshed after five. The answer bounds the age of later reports, allowing 500 ppm of
  relative drift and 2 ms of clock uncertainty, and trust ends ten seconds after the last answer.
  A position is shown only while its reports are confirmed current and its own validity has not
  run out, and it expires between reports. Nothing is asked once a commit has gone out, and a
  commit or a new route withdraws the reports until the modem is back. A range says its age once
  it is two seconds old; after three seconds without a report the row shows only that silence, in
  red. Calibration is optional: a fix says which of its ranges were calibrated and is never held
  back for it. This is freshness correlation, not authentication, and nothing here feeds the
  flight controller.
- **Window.** The selected modem is Modem 1; every other verified, transaction-capable modem that
  is online follows as Modem 2, 3 and so on, and a modem heard later joins. The tabs read `Field`,
  then `Modem N · system:component`, each with that modem's complete settings for review only. The
  left column lists the modems with their role (Off, Rover or Beacon), radio and, for a beacon, the
  survey row it answers for. Under each modem a line says what saving does to it, including
  whether its radio keeps its data link (an LPS reservation, `CHn_RSV_EN`) or LPS takes the radio
  over. A radio that is not fitted or not an LR2021 is listed greyed, with the reason. The rover's
  flight controller IDs, launch height and, under Advanced, its launch-height and range-noise
  uncertainty follow, with the burst settings of its radio, which the pair window sets. The launch
  height is the surveyed rover antenna height above the WGS 84 ellipsoid at launch minus the field
  origin's ellipsoid height; saving it does not tie the barometer, which `+LPS_TIE` does in the
  modem's configuration mode while the flight controller is disarmed and still. A modem whose
  firmware has no LPS settings is listed and left alone.
- **Field.** The field shown is the one Modem 1 holds, or the first modem that holds one, until it
  is edited; **Show** takes it from another modem. Values are shown in operator units and saved
  exactly: degrees with seven decimals, metres with three, the datum and addresses in hexadecimal,
  and survey uncertainty as standard deviations, whose squares the modem stores. The covariance
  terms and the ranging radio settings are behind their own switches. **Import…** and **Export…**
  use the field document, which holds the survey and the ranging radio settings but no keys or
  network settings, and is read only whole; a hexadecimal value in it is shown and compared as the
  decimal the modem reads back. Rows past the beacon count are saved as zeros, as the modems store
  them. The window keeps a generation that a modem already holds with exactly this survey, so a
  modem joining the field needs no change elsewhere; otherwise it chooses one no listed modem uses.
  Radio-only changes keep the generation.
- **Saving.** Only modems whose settings change are saved, and only once every listed modem has
  been read back in full; the save checks the roles, survey rows and readback again rather than
  trusting the button. **Verify and save N modems** saves one modem at a time, beacons before the
  rover: each is checked, committed, restarted and read back in full, and the readback must be the
  configuration the modem reported active, before the next begins. The group is not one
  transaction. If a modem's settings changed while it waited, or a modem fails, saving stops:
  modems saved before it keep their new settings, the others are not attempted, and the failed
  modem is read again before the same window can save once more. A commit that was sent but not
  confirmed is shown as an unknown outcome until that read says whether it took effect. Close waits
  while a modem is being saved, and for a check stopped before its commit to be cancelled on its
  modem.

## Acceptance boundary

The shared Mission Planner parser, custom CRC/layouts, multi-link and HUB target isolation, strict
passport validation, UID2 address migration and conflict guards, NV4 apply transaction, both NV5
key-write paths, exact typed echoes and rejection diagnostics, independent diversity key targeting,
RTSP dirty-state handling, current preset staging, parameter-file roundtrip and slow/silent-device
handling are covered by automated tests. Distance pair setup with ranging on, real distance
readings and their burst timing need two modems with LR2021 radios and an operator run; LPS
positions, ranges and their freshness on real radios need beacon and rover modems with LR2021
radios, and the barometer tie a flight controller. NV4 coverage
additionally pins the complete sketch catalog, parameter-specific descriptions and both
apply-trigger spellings. A representative physical NV4 and NV5 modem on UDP/TCP/UART still require
an operator acceptance run, including reboot/reappearance and real RF/RTSP behavior.
