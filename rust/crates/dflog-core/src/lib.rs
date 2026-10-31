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
use std::fmt;
use std::fs::File;
use std::io::{self, Read};
use std::path::Path;

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

/// A log's bytes, owned. Debug prints the length, not the contents.
struct Image(Vec<u8>);

impl fmt::Debug for Image {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Image").field("len", &self.0.len()).finish()
    }
}

/// A scanned log kept open for typed column queries.
#[derive(Debug)]
pub struct LogFile {
    image: Image,
    pub index: LogIndex,
    /// FMT definitions by message type id, last definition per id winning.
    pub fmts: HashMap<u8, FmtDef>,
    /// message name -> type id, last FMT per name winning (C# logformat)
    pub name_to_id: HashMap<String, u8>,
}

impl LogFile {
    /// Read the log at `path` into memory, up to the length it has when
    /// opened; bytes a writer appends meanwhile are left out.
    ///
    /// Logs are never memory-mapped: Mission Planner opens logs another
    /// process may still be writing, and on Unix touching a mapped page past
    /// a truncated end of file raises SIGBUS, which no panic handler can
    /// catch.
    pub fn open(path: &Path) -> io::Result<LogFile> {
        Ok(Self::build(Image(read_file(path)?)))
    }

    /// Take ownership of an in-memory log image without copying it.
    pub fn from_image(image: Box<[u8]>) -> LogFile {
        Self::build(Image(image.into_vec()))
    }

    /// Open a copy of an in-memory log image (fuzzing, and callers that hold
    /// only a borrowed buffer).
    pub fn open_bytes(data: &[u8]) -> io::Result<LogFile> {
        Ok(Self::build(Image(data.to_vec())))
    }

    fn build(image: Image) -> LogFile {
        let data: &[u8] = &image.0;
        let index = scan(data);

        // re-read the FMT payloads the scan indexed (type 0x80 records)
        let mut fmts = HashMap::new();
        let mut name_to_id = HashMap::new();
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

        LogFile {
            image,
            index,
            fmts,
            name_to_id,
        }
    }

    pub fn data(&self) -> &[u8] {
        &self.image.0
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

/// Scan the log at `path`, read into memory for the scan (never mapped, for
/// the reason given on [`LogFile::open`]).
pub fn scan_file(path: &Path) -> io::Result<LogIndex> {
    Ok(scan(&read_file(path)?))
}

/// Read the file at `path` up to the length it has when opened; bytes a
/// writer appends during the read are left out. A failed allocation for the
/// bytes is an `ErrorKind::OutOfMemory` error, but the index a scan builds
/// afterwards grows like any `Vec` and aborts if memory runs out.
fn read_file(path: &Path) -> io::Result<Vec<u8>> {
    let file = File::open(path)?;
    let len = file.metadata()?.len();
    read_prefix(file, len)
}

/// Read at most `len` bytes from `reader` into a buffer reserved once,
/// exactly and fallibly: a reader holding more stops at `len`, one holding
/// less ends early, and neither grows the buffer.
fn read_prefix(reader: impl Read, len: u64) -> io::Result<Vec<u8>> {
    let capacity =
        usize::try_from(len).map_err(|e| io::Error::new(io::ErrorKind::OutOfMemory, e))?;
    let mut bytes = Vec::new();
    bytes.try_reserve_exact(capacity)?;
    reader.take(len).read_to_end(&mut bytes)?;
    Ok(bytes)
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
    fn empty_file_opens_as_an_empty_log() {
        let dir = std::env::temp_dir().join(format!("dflog-empty-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("empty.bin");
        std::fs::write(&path, []).unwrap();

        let log = LogFile::open(&path).unwrap();
        assert!(log.index.is_empty());
        assert!(log.data().is_empty());
        assert!(scan_file(&path).unwrap().is_empty());

        drop(log);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn read_prefix_stops_at_the_sampled_length_without_growing() {
        let data: Vec<u8> = (0..100u8).collect();
        // The capacity checks rely on Vec reporting exactly what
        // try_reserve_exact requested, which std does today; growth would
        // at least double it.

        // a writer appended 40 bytes after the length was sampled
        let bytes = read_prefix(data.as_slice(), 60).unwrap();
        assert_eq!(bytes, &data[..60]);
        assert_eq!(
            bytes.capacity(),
            60,
            "the buffer grew past the sampled length"
        );

        // a file that shrank below the sampled length ends early
        let short = read_prefix(&data[..30], 60).unwrap();
        assert_eq!(short, &data[..30]);
        assert_eq!(short.capacity(), 60);

        // the empty-file case reserves nothing and reads nothing
        let empty = read_prefix(data.as_slice(), 0).unwrap();
        assert!(empty.is_empty());
        assert_eq!(empty.capacity(), 0);
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
