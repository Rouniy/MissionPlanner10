//! Deterministic mutation fuzzing:
//! a fast always-on regression net asserting that no input - corrupted,
//! truncated, spliced or random - can panic the scanner, the column
//! decoders or the text renderer. The coverage-guided campaign lives in
//! rust/fuzz (cargo fuzz, run under WSL); anything it finds gets distilled
//! into a case here.

use std::panic::{catch_unwind, AssertUnwindSafe};

use dflog_core::{columns, render, scan, LogFile};

/// xorshift64* - deterministic, no external deps, no wall clock
struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.0 = x;
        x.wrapping_mul(0x2545F4914F6CDD1D)
    }

    fn below(&mut self, n: usize) -> usize {
        (self.next() % n.max(1) as u64) as usize
    }
}

fn mutate(data: &mut Vec<u8>, rng: &mut Rng) {
    for _ in 0..1 + rng.below(16) {
        match rng.below(6) {
            0 => {
                // flip a byte
                if !data.is_empty() {
                    let at = rng.below(data.len());
                    data[at] ^= rng.next() as u8;
                }
            }
            1 => {
                // truncate
                data.truncate(rng.below(data.len() + 1));
            }
            2 => {
                // insert random bytes
                let at = rng.below(data.len() + 1);
                for _ in 0..1 + rng.below(8) {
                    data.insert(at, rng.next() as u8);
                }
            }
            3 => {
                // delete a span
                if !data.is_empty() {
                    let at = rng.below(data.len());
                    let n = 1 + rng.below((data.len() - at).min(32));
                    data.drain(at..at + n);
                }
            }
            4 => {
                // duplicate a span elsewhere
                if data.len() >= 4 {
                    let at = rng.below(data.len() - 3);
                    let n = 1 + rng.below((data.len() - at).min(64));
                    let span: Vec<u8> = data[at..at + n].to_vec();
                    let to = rng.below(data.len() + 1);
                    for (i, b) in span.into_iter().enumerate() {
                        data.insert(to + i, b);
                    }
                }
            }
            _ => {
                // plant a header, sometimes an FMT header
                let at = rng.below(data.len() + 1);
                data.insert(at, 0xA3);
                data.insert(at + 1, 0x95);
                if rng.below(2) == 0 {
                    data.insert(at + 2, 0x80);
                }
            }
        }
    }
}

/// run everything that parses over one input; must never panic
fn exercise(data: &[u8]) {
    let index = scan(data);
    assert_eq!(index.offsets.len(), index.types.len());

    let mut sink = std::io::sink();
    let _ = render::convert(data, &mut sink);

    if let Ok(log) = LogFile::open_bytes(data) {
        let names: Vec<String> = log.name_to_id.keys().cloned().collect();
        for name in names.iter().take(8) {
            if let Some(fmt) = log.name_to_id.get(name).and_then(|id| log.fmts.get(id)) {
                let labels: Vec<&str> = fmt.labels.iter().map(|s| s.as_str()).take(3).collect();
                if !labels.is_empty() {
                    let _ = columns::get_columns(&log, name, &labels);
                    let _ = columns::get_array_column(&log, name, labels[0]);
                    let _ = columns::get_columns_filtered(&log, name, &labels, Some(0));
                }
            }
        }

        // the general access layer, the time correlation and units metadata
        for record in log.records().take(64) {
            let _ = record.values();
        }
        let _ = log.time_base();
        let _ = log.units();
    }
}

fn seeds() -> Vec<Vec<u8>> {
    let corpus =
        std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../testdata/copter.bin");
    let copter = std::fs::read(&corpus).expect("corpus copter.bin");

    vec![
        copter[..8192.min(copter.len())].to_vec(),
        copter[copter.len() / 2..(copter.len() / 2 + 8192).min(copter.len())].to_vec(),
        vec![0xA3, 0x95, 0x80],
        Vec::new(),
    ]
}

#[test]
fn mutated_inputs_never_panic() {
    let seeds = seeds();
    let mut rng = Rng(0x9E3779B97F4A7C15);

    for iteration in 0..3000u32 {
        let mut data = seeds[rng.below(seeds.len())].clone();
        mutate(&mut data, &mut rng);

        let result = catch_unwind(AssertUnwindSafe(|| exercise(&data)));
        if result.is_err() {
            let path = std::env::temp_dir().join(format!("dflog-fuzz-fail-{iteration}.bin"));
            let _ = std::fs::write(&path, &data);
            panic!(
                "iteration {iteration} panicked; input saved to {}",
                path.display()
            );
        }
    }
}

/// distilled regression cases; grows with every fuzzer finding
#[test]
fn known_edge_inputs_never_panic() {
    let cases: Vec<Vec<u8>> = vec![
        Vec::new(),
        vec![0xA3],
        vec![0xA3, 0x95],
        vec![0xA3, 0x95, 0x80],
        // FMT that declares its own type with a tiny length
        {
            let mut v = vec![0xA3, 0x95, 0x80];
            let mut fmt = [0u8; 86];
            fmt[0] = 0x80;
            fmt[1] = 1;
            v.extend_from_slice(&fmt);
            v.extend_from_slice(&[0xA3, 0x95, 0x80]);
            v
        },
        // record type defined with length 2 (the C# new byte[-1] throw path)
        {
            let mut v = vec![0xA3, 0x95, 0x80];
            let mut fmt = [0u8; 86];
            fmt[0] = 0x42;
            fmt[1] = 2;
            fmt[2..5].copy_from_slice(b"BAD");
            v.extend_from_slice(&fmt);
            v.extend_from_slice(&[0xA3, 0x95, 0x42, 1, 2, 3]);
            v
        },
        // format string longer than the payload
        {
            let mut v = vec![0xA3, 0x95, 0x80];
            let mut fmt = [0u8; 86];
            fmt[0] = 0x43;
            fmt[1] = 5; // 2 payload bytes, but format wants 8
            fmt[2..5].copy_from_slice(b"SML");
            fmt[6..7].copy_from_slice(b"q");
            fmt[22..23].copy_from_slice(b"A");
            v.extend_from_slice(&fmt);
            v.extend_from_slice(&[0xA3, 0x95, 0x43, 9, 9]);
            v
        },
    ];

    for (i, case) in cases.iter().enumerate() {
        let result = catch_unwind(AssertUnwindSafe(|| exercise(case)));
        assert!(result.is_ok(), "edge case {i} panicked");
    }
}
