//! ArduPilot dataflash (`.bin`) log indexing.
//!
//! A byte-exact port of the index
//! scan performed by MissionPlanner's `BinaryLog.ReadMessageTypeOffset` /
//! `DFLogBuffer.setlinecount` (binary branch). The C# implementation is the
//! behavioral reference; every quirk below is intentional parity:
//!
//! - Records are found by scanning for the 0xA3 0x95 header with the same
//!   three-state machine, so a corrupted stream resyncs at exactly the same
//!   offsets as the C# scanner (including *inside* the payloads of message
//!   types whose FMT has not been seen yet).
//! - A record whose type has no known length is still indexed, but its
//!   payload is not skipped.
//! - An FMT record registers its target type's length even when nonsense; a
//!   registered length of 1 or 2 makes later records of that type throw in
//!   C# (`new byte[size - 3]`), which drops the record and resumes the scan -
//!   mirrored here by not emitting the record.
//! - A record with type 0 at offset 0 is discarded (C# uses `(0, 0)` as its
//!   end-of-stream sentinel).
//! - An FMT payload truncated by end-of-file is zero-padded, as the C# side's
//!   partial `Stream.Read` into a zeroed array does.

use std::collections::HashMap;
use std::fs::File;
use std::io;
use std::path::Path;

use memmap2::Mmap;

pub mod access;
pub mod columns;
pub mod render;
pub mod time;
pub mod units;

pub const HEAD_BYTE1: u8 = 0xA3;
pub const HEAD_BYTE2: u8 = 0x95;
const FMT_TYPE: u8 = 0x80;
/// log_Format payload: type(1) + length(1) + name(4) + format(16) + labels(64)
const FMT_PAYLOAD_LEN: usize = 86;

/// Index of every record found in a dataflash log.
#[derive(Debug, Default)]
pub struct LogIndex {
    /// Byte offset of each record's 0xA3 header, in scan order.
    pub offsets: Vec<u64>,
    /// Message type byte of each record, parallel to `offsets`.
    pub types: Vec<u8>,
}

/// One FMT definition as the scanner saw it (last definition wins per name,
/// matching the C# dictionaries).
#[derive(Debug, Clone)]
pub struct FmtDef {
    pub id: u8,
    /// full record length including the 3 header bytes
    pub length: usize,
    pub name: String,
    pub format: String,
    pub labels: Vec<String>,
}

fn ascii_trim_nul(bytes: &[u8]) -> String {
    let text: String = bytes.iter().map(|&b| b as char).collect();
    text.trim_matches('\0').to_string()
}

/// A scanned log kept open for typed column queries.
#[derive(Debug)]
pub struct LogFile {
    map: Mmap,
    pub index: LogIndex,
    /// FMT definitions by message type id, last definition per id winning.
    pub fmts: HashMap<u8, FmtDef>,
    /// message name -> type id, last FMT per name winning (C# logformat)
    pub name_to_id: HashMap<String, u8>,
}

impl LogFile {
    pub fn open(path: &Path) -> io::Result<LogFile> {
        let file = File::open(path)?;
        // mmap of an empty file fails; give it one anonymous zero byte
        let map = if file.metadata()?.len() == 0 {
            memmap2::MmapMut::map_anon(1)?.make_read_only()?
        } else {
            // SAFETY: read-only mapping, same caveats as scan_file
            unsafe { Mmap::map(&file)? }
        };
        let len = file.metadata()?.len() as usize;
        Self::build(map, len)
    }

    /// Open an in-memory log image (fuzzing, and stream-backed callers that
    /// have no file to map).
    pub fn open_bytes(data: &[u8]) -> io::Result<LogFile> {
        let mut map = memmap2::MmapMut::map_anon(data.len().max(1))?;
        map[..data.len()].copy_from_slice(data);
        let len = data.len();
        Self::build(map.make_read_only()?, len)
    }

    fn build(map: Mmap, len: usize) -> io::Result<LogFile> {
        let index = scan(&map[..len]);

        // re-read the FMT payloads the scan indexed (type 0x80 records)
        let mut fmts = HashMap::new();
        let mut name_to_id = HashMap::new();
        let data = &map[..len];
        for (i, &t) in index.types.iter().enumerate() {
            if t != FMT_TYPE {
                continue;
            }
            let start = index.offsets[i] as usize + 3;
            let take = FMT_PAYLOAD_LEN.min(data.len().saturating_sub(start));
            let mut payload = [0u8; FMT_PAYLOAD_LEN];
            payload[..take].copy_from_slice(&data[start..start + take]);
            let def = FmtDef {
                id: payload[0],
                length: payload[1] as usize,
                name: ascii_trim_nul(&payload[2..6]),
                format: ascii_trim_nul(&payload[6..22]),
                labels: ascii_trim_nul(&payload[22..86])
                    .split(',')
                    .map(|s| s.trim().to_string())
                    .collect(),
            };
            name_to_id.insert(def.name.clone(), def.id);
            fmts.insert(def.id, def);
        }

        Ok(LogFile {
            map,
            index,
            fmts,
            name_to_id,
        })
    }

    pub fn data(&self) -> &[u8] {
        &self.map
    }
}

impl LogIndex {
    pub fn len(&self) -> usize {
        self.offsets.len()
    }

    pub fn is_empty(&self) -> bool {
        self.offsets.is_empty()
    }
}

/// Scan a complete in-memory log image.
pub fn scan(data: &[u8]) -> LogIndex {
    // record length per message type, learned from FMT records in scan order
    let mut lengths = [0usize; 256];
    let mut index = LogIndex::default();
    let len = data.len();
    let mut pos = 0usize;

    'outer: while pos < len {
        // header state machine, identical to the C# three-state scanner
        let mut step = 0u8;
        loop {
            if pos >= len {
                break 'outer;
            }
            let b = data[pos];
            pos += 1;
            match step {
                0 => {
                    if b == HEAD_BYTE1 {
                        step = 1;
                    }
                }
                1 => {
                    if b == HEAD_BYTE2 {
                        step = 2;
                    } else {
                        step = 0;
                    }
                }
                _ => {
                    let start = (pos - 3) as u64;
                    if b == FMT_TYPE {
                        let take = FMT_PAYLOAD_LEN.min(len - pos);
                        let mut payload = [0u8; FMT_PAYLOAD_LEN];
                        payload[..take].copy_from_slice(&data[pos..pos + take]);
                        pos += take;
                        lengths[payload[0] as usize] = payload[1] as usize;
                    } else {
                        let size = lengths[b as usize];
                        if size == 0 {
                            // unknown type: indexed, payload not skipped
                        } else if size < 3 {
                            // C# throws on new byte[size - 3]: record dropped
                            break;
                        } else {
                            pos = (pos + (size - 3)).min(len);
                        }
                    }

                    if b == 0 && start == 0 {
                        // C# end-of-stream sentinel value: discarded
                        break;
                    }

                    index.offsets.push(start);
                    index.types.push(b);
                    break;
                }
            }
        }
    }

    index
}

/// Scan a log file via a memory map.
pub fn scan_file(path: &Path) -> io::Result<LogIndex> {
    let file = File::open(path)?;
    if file.metadata()?.len() == 0 {
        return Ok(LogIndex::default());
    }
    // SAFETY: the mapping is read-only and lives only for the duration of the
    // scan; concurrent truncation of the underlying file is undefined in the
    // same way it is for the C# stream-based scanner.
    let map = unsafe { Mmap::map(&file)? };
    Ok(scan(&map))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn testdata(name: &str) -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../testdata")
            .join(name)
    }

    /// Expected counts come from the phase-0 golden snapshots of the C#
    /// parser (tests/MissionPlanner.Utilities.Tests/testdata/goldens).
    #[test]
    fn corpus_counts_match_csharp_goldens() {
        for (name, expected) in [
            ("copter.bin", 31867u64),
            ("plane.bin", 20885),
            ("rover.bin", 26335),
        ] {
            let index = scan_file(&testdata(name)).expect(name);
            assert_eq!(index.len() as u64, expected, "{name}");
            assert_eq!(index.offsets.len(), index.types.len(), "{name}");
        }
    }

    #[test]
    fn empty_input_yields_empty_index() {
        assert!(scan(&[]).is_empty());
        assert!(scan(&[HEAD_BYTE1]).is_empty());
        assert!(scan(&[HEAD_BYTE1, HEAD_BYTE2]).is_empty());
    }

    #[test]
    fn type_zero_at_offset_zero_is_discarded() {
        // A3 95 00 at the very start matches the C# EOF sentinel and is dropped
        let index = scan(&[HEAD_BYTE1, HEAD_BYTE2, 0x00, 0xFF]);
        assert!(index.is_empty());
    }

    #[test]
    fn unknown_type_indexed_without_payload_skip() {
        // two adjacent unknown-type records; the second header begins
        // immediately after the first type byte
        let data = [HEAD_BYTE1, HEAD_BYTE2, 0x42, HEAD_BYTE1, HEAD_BYTE2, 0x43];
        let index = scan(&data);
        assert_eq!(index.offsets, vec![0, 3]);
        assert_eq!(index.types, vec![0x42, 0x43]);
    }
}
