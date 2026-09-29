//! C ABI over dflog-core for Mission Planner's P/Invoke bindings
//! (ExtLibs/Utilities/DFLogNative.cs).
//!
//! Contract:
//! - Every function returns 0 on success or a negative error code; call
//!   `dflog_last_error` for a UTF-8 message describing the last failure on
//!   the calling thread.
//! - A log reaches the library as an image the caller fills:
//!   `dflog_image_new` allocates it, the caller copies the log's bytes in,
//!   and `dflog_open_image` consumes it into a `DflogFile` (or
//!   `dflog_image_free` releases an image that is never opened). Mission
//!   Planner fills it from the same stream its managed reader uses, so the
//!   native index can never describe a different file than the one a path
//!   resolved to earlier.
//! - The image allocation is fallible (`DFLOG_ERR_ALLOC`); the index the
//!   scan then builds, about 9 bytes per record, grows like any `Vec` and
//!   aborts the process if memory runs out.
//! - `dflog_file_index` borrows an open file's index; the pointers stay
//!   valid until `dflog_close`.
//! - Panics never cross the boundary: they convert to `DFLOG_ERR_PANIC`.
//! - Logs are read into memory, never memory-mapped: see
//!   `dflog_core::LogFile::open` for why.

use std::alloc::{self, Layout};
use std::cell::RefCell;
use std::ffi::CStr;
use std::mem::ManuallyDrop;
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;
use std::ptr::{self, NonNull};

pub const DFLOG_OK: i32 = 0;
pub const DFLOG_ERR_BAD_ARGUMENT: i32 = -1;
pub const DFLOG_ERR_IO: i32 = -2;
pub const DFLOG_ERR_PANIC: i32 = -3;

/// Bumped when the ABI changes shape; checked by the C# side.
pub const DFLOG_ABI_VERSION: u32 = 6;

pub const DFLOG_ERR_NO_TIME_BASE: i32 = -5;

pub const DFLOG_ERR_QUERY: i32 = -4;

/// The allocator refused a log image's size.
pub const DFLOG_ERR_ALLOC: i32 = -6;

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_last_error(message: String) {
    LAST_ERROR.with(|slot| *slot.borrow_mut() = message);
}

#[no_mangle]
pub extern "C" fn dflog_abi_version() -> u32 {
    DFLOG_ABI_VERSION
}

/// A zero-filled buffer the caller fills with a log's bytes before
/// `dflog_open_image` consumes it. It holds the raw parts rather than a
/// `Box<[u8]>`, so no live `Box` aliases the pointer the caller writes
/// through; the box is rebuilt only when the image is opened or freed.
#[derive(Debug)]
pub struct DflogImage {
    ptr: *mut u8,
    len: usize,
}

impl DflogImage {
    /// Zero-filled, so opening it never reads uninitialized bytes whatever
    /// the caller wrote. A zero length allocates nothing (a zero-size
    /// `alloc_zeroed` is undefined behavior).
    fn allocate(len: u64) -> Result<DflogImage, (i32, String)> {
        let len = usize::try_from(len)
            .map_err(|e| (DFLOG_ERR_BAD_ARGUMENT, format!("image length {len}: {e}")))?;
        if len == 0 {
            return Ok(DflogImage {
                ptr: NonNull::dangling().as_ptr(),
                len,
            });
        }
        let layout = Layout::array::<u8>(len)
            .map_err(|e| (DFLOG_ERR_BAD_ARGUMENT, format!("image length {len}: {e}")))?;
        // SAFETY: `layout` has a non-zero size (len > 0 was checked above)
        let ptr = unsafe { alloc::alloc_zeroed(layout) };
        if ptr.is_null() {
            return Err((
                DFLOG_ERR_ALLOC,
                format!("cannot allocate a {len}-byte log image"),
            ));
        }
        Ok(DflogImage { ptr, len })
    }

    fn into_bytes(self) -> Box<[u8]> {
        let image = ManuallyDrop::new(self);
        // SAFETY: the parts come from `allocate` and are reclaimed exactly
        // once: ManuallyDrop keeps Drop from reclaiming them again
        unsafe { reclaim(image.ptr, image.len) }
    }
}

impl Drop for DflogImage {
    fn drop(&mut self) {
        // SAFETY: the parts come from `allocate`; `into_bytes` skips this
        // drop, so they are reclaimed exactly once
        drop(unsafe { reclaim(self.ptr, self.len) });
    }
}

/// # Safety
/// `ptr` and `len` must come from one `DflogImage::allocate` and not have
/// been reclaimed already.
unsafe fn reclaim(ptr: *mut u8, len: usize) -> Box<[u8]> {
    if len == 0 {
        return Box::default();
    }
    // SAFETY: `ptr` was allocated by the global allocator with
    // Layout::array::<u8>(len), the layout a Box<[u8]> of `len` bytes
    // deallocates with, and per the caller contract nothing else owns it
    unsafe { Box::from_raw(ptr::slice_from_raw_parts_mut(ptr, len)) }
}

/// Allocate a zero-filled image of `len` bytes. `data` receives the pointer
/// to write the log's bytes through (valid until the image is opened or
/// freed). Both outputs are null on failure: `DFLOG_ERR_BAD_ARGUMENT` for a
/// length no allocation can have, `DFLOG_ERR_ALLOC` when the allocator
/// refuses it.
///
/// # Safety
/// `out` and `data` must be valid pointers.
#[no_mangle]
pub unsafe extern "C" fn dflog_image_new(
    len: u64,
    out: *mut *mut DflogImage,
    data: *mut *mut u8,
) -> i32 {
    if out.is_null() || data.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: both pointers are non-null and valid per the caller contract
    unsafe {
        *out = ptr::null_mut();
        *data = ptr::null_mut()
    };

    match catch_unwind(|| DflogImage::allocate(len)) {
        Ok(Ok(image)) => {
            let bytes = image.ptr;
            // SAFETY: both pointers are non-null and valid per the caller
            // contract
            unsafe {
                *data = bytes;
                *out = Box::into_raw(Box::new(image))
            };
            DFLOG_OK
        }
        Ok(Err((code, message))) => {
            set_last_error(message);
            code
        }
        Err(_) => {
            set_last_error("panic in dflog_image_new".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Release an image that was never opened. Null is ignored.
///
/// # Safety
/// `image` must come from `dflog_image_new`, must not have been passed to
/// `dflog_open_image`, and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn dflog_image_free(image: *mut DflogImage) {
    if !image.is_null() {
        // SAFETY: `image` came from Box::into_raw in dflog_image_new and is
        // not used again per the caller contract
        drop(unsafe { Box::from_raw(image) });
    }
}

/// Index the image's bytes and keep them for column queries; release the
/// result with `dflog_close`. The image is consumed whether or not this
/// succeeds, so the caller must not free it afterwards.
///
/// # Safety
/// `image` must come from `dflog_image_new` and not have been opened or
/// freed; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn dflog_open_image(image: *mut DflogImage, out: *mut *mut DflogFile) -> i32 {
    if image.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `image` came from Box::into_raw in dflog_image_new and is
    // consumed here exactly once per the caller contract
    let image = unsafe { Box::from_raw(image) };
    if out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    match catch_unwind(AssertUnwindSafe(move || {
        dflog_core::LogFile::from_image(image.into_bytes())
    })) {
        Ok(log) => {
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(Box::new(DflogFile { log })) };
            DFLOG_OK
        }
        Err(_) => {
            set_last_error("panic in dflog_open_image".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Borrow the record index of an open file: `count` records, each with a
/// byte offset (`offsets`) and a message type (`types`), in scan order. The
/// pointers stay valid until `dflog_close`.
///
/// # Safety
/// `file` must be a live `dflog_open`/`dflog_open_image` handle and the out
/// pointers must be valid.
#[no_mangle]
pub unsafe extern "C" fn dflog_file_index(
    file: *const DflogFile,
    offsets: *mut *const u64,
    types: *mut *const u8,
    count: *mut u64,
) -> i32 {
    if file.is_null() || offsets.is_null() || types.is_null() || count.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }

    // SAFETY: `file` is a live handle and the out pointers are valid per
    // the caller contract
    unsafe {
        let index = &(*file).log.index;
        *offsets = index.offsets.as_ptr();
        *types = index.types.as_ptr();
        *count = index.offsets.len() as u64
    };
    DFLOG_OK
}

/// An open log kept resident for typed column queries (phase B).
#[derive(Debug)]
pub struct DflogFile {
    log: dflog_core::LogFile,
}

/// Column-major query result: `values[col * rows + row]`.
#[repr(C)]
#[derive(Debug)]
pub struct DflogColumns {
    pub rows: u64,
    pub cols: u32,
    pub linenos: *const u64,
    pub values: *const f64,
    // rust-owned storage; opaque to the C side beyond `values`
    linenos_vec: Vec<u64>,
    values_vec: Vec<f64>,
}

/// Open the log at `path_utf8` for column queries; release with `dflog_close`.
///
/// # Safety
/// `path_utf8` must be a valid NUL-terminated UTF-8 string and `out` a valid
/// pointer to receive the handle.
#[no_mangle]
pub unsafe extern "C" fn dflog_open(path_utf8: *const c_char, out: *mut *mut DflogFile) -> i32 {
    if path_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    // SAFETY: `path_utf8` is a non-null NUL-terminated string per the
    // caller contract
    let path = match unsafe { CStr::from_ptr(path_utf8) }.to_str() {
        Ok(s) => PathBuf::from(s),
        Err(_) => {
            set_last_error("path is not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    match catch_unwind(AssertUnwindSafe(|| dflog_core::LogFile::open(&path))) {
        Ok(Ok(log)) => {
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(Box::new(DflogFile { log })) };
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(format!("{}: {}", path.display(), err));
            DFLOG_ERR_IO
        }
        Err(_) => {
            set_last_error("panic in dflog_open".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Release a handle returned by `dflog_open` or `dflog_open_image`.
///
/// # Safety
/// `file` must come from `dflog_open` or `dflog_open_image` and not be used
/// afterwards. Null is ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_close(file: *mut DflogFile) {
    if !file.is_null() {
        // SAFETY: `file` came from Box::into_raw in dflog_open or
        // dflog_open_image and is not used again per the caller contract
        drop(unsafe { Box::from_raw(file) });
    }
}

/// Decode the comma-separated `fields_utf8` of every `type_utf8` record into
/// f64 columns. Release the result with `dflog_columns_free`.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the strings must be valid
/// NUL-terminated UTF-8; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn dflog_get_columns(
    file: *const DflogFile,
    type_utf8: *const c_char,
    fields_utf8: *const c_char,
    out: *mut *mut DflogColumns,
) -> i32 {
    // SAFETY: forwarded caller contract
    unsafe { get_columns_impl(file, type_utf8, fields_utf8, None, out) }
}

/// `dflog_get_columns` limited to one instance value (the field whose FMTU
/// unit id is '#') when `has_instance` is non-zero. A type without an
/// instance field fails with `DFLOG_ERR_QUERY`.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the strings must be valid
/// NUL-terminated UTF-8; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn dflog_get_columns_filtered(
    file: *const DflogFile,
    type_utf8: *const c_char,
    fields_utf8: *const c_char,
    has_instance: i32,
    instance: i64,
    out: *mut *mut DflogColumns,
) -> i32 {
    let instance = (has_instance != 0).then_some(instance);
    // SAFETY: forwarded caller contract
    unsafe { get_columns_impl(file, type_utf8, fields_utf8, instance, out) }
}

/// # Safety
/// Same contract as `dflog_get_columns`.
unsafe fn get_columns_impl(
    file: *const DflogFile,
    type_utf8: *const c_char,
    fields_utf8: *const c_char,
    instance: Option<i64>,
    out: *mut *mut DflogColumns,
) -> i32 {
    if file.is_null() || type_utf8.is_null() || fields_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    // SAFETY: both strings are non-null and NUL-terminated per the caller
    // contract
    let (type_name, fields_csv) = match unsafe {
        (
            CStr::from_ptr(type_utf8).to_str(),
            CStr::from_ptr(fields_utf8).to_str(),
        )
    } {
        (Ok(t), Ok(f)) => (t, f),
        _ => {
            set_last_error("type/fields are not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    let fields: Vec<&str> = fields_csv.split(',').collect();
    // SAFETY: `file` is a live dflog_open handle per the caller contract
    let log = unsafe { &(*file).log };

    match catch_unwind(AssertUnwindSafe(|| {
        dflog_core::columns::get_columns_filtered(log, type_name, &fields, instance)
    })) {
        Ok(Ok(cols)) => {
            let mut boxed = Box::new(DflogColumns {
                rows: cols.rows,
                cols: cols.cols,
                linenos: ptr::null(),
                values: ptr::null(),
                linenos_vec: cols.linenos,
                values_vec: cols.values,
            });
            boxed.linenos = boxed.linenos_vec.as_ptr();
            boxed.values = boxed.values_vec.as_ptr();
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(boxed) };
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(err.to_string());
            DFLOG_ERR_QUERY
        }
        Err(_) => {
            set_last_error("panic in dflog_get_columns".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Row-major array-column result: `values[row * elems + e]`, elems = 32 for
/// the `a` (int16[32]) format.
#[repr(C)]
#[derive(Debug)]
pub struct DflogArrayColumn {
    pub rows: u64,
    pub elems: u32,
    pub linenos: *const u64,
    pub values: *const i16,
    // rust-owned storage; opaque to the C side beyond `values`
    linenos_vec: Vec<u64>,
    values_vec: Vec<i16>,
}

/// Decode the `a` (int16[32]) array `field_utf8` of every `type_utf8` record.
/// Release the result with `dflog_array_column_free`.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the strings must be valid
/// NUL-terminated UTF-8; `out` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn dflog_get_array_column(
    file: *const DflogFile,
    type_utf8: *const c_char,
    field_utf8: *const c_char,
    out: *mut *mut DflogArrayColumn,
) -> i32 {
    if file.is_null() || type_utf8.is_null() || field_utf8.is_null() || out.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }
    // SAFETY: `out` is non-null and valid per the caller contract
    unsafe { *out = ptr::null_mut() };

    // SAFETY: both strings are non-null and NUL-terminated per the caller
    // contract
    let (type_name, field) = match unsafe {
        (
            CStr::from_ptr(type_utf8).to_str(),
            CStr::from_ptr(field_utf8).to_str(),
        )
    } {
        (Ok(t), Ok(f)) => (t, f),
        _ => {
            set_last_error("type/field are not valid UTF-8".into());
            return DFLOG_ERR_BAD_ARGUMENT;
        }
    };

    // SAFETY: `file` is a live dflog_open handle per the caller contract
    let log = unsafe { &(*file).log };

    match catch_unwind(AssertUnwindSafe(|| {
        dflog_core::columns::get_array_column(log, type_name, field)
    })) {
        Ok(Ok(col)) => {
            let mut boxed = Box::new(DflogArrayColumn {
                rows: col.rows,
                elems: dflog_core::columns::ARRAY_ELEMS as u32,
                linenos: ptr::null(),
                values: ptr::null(),
                linenos_vec: col.linenos,
                values_vec: col.values,
            });
            boxed.linenos = boxed.linenos_vec.as_ptr();
            boxed.values = boxed.values_vec.as_ptr();
            // SAFETY: `out` is non-null and valid per the caller contract
            unsafe { *out = Box::into_raw(boxed) };
            DFLOG_OK
        }
        Ok(Err(err)) => {
            set_last_error(err.to_string());
            DFLOG_ERR_QUERY
        }
        Err(_) => {
            set_last_error("panic in dflog_get_array_column".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Wall-clock correlation from the log's first valid GPS fix:
/// `gps_start_unix_ms` (UTC) and the board `ms_offset` it corresponds to.
/// Returns `DFLOG_ERR_NO_TIME_BASE` when the log has no usable fix.
///
/// # Safety
/// `file` must be a live `dflog_open` handle; the out pointers must be valid.
#[no_mangle]
pub unsafe extern "C" fn dflog_time_base(
    file: *const DflogFile,
    gps_start_unix_ms: *mut i64,
    ms_offset: *mut i64,
) -> i32 {
    if file.is_null() || gps_start_unix_ms.is_null() || ms_offset.is_null() {
        set_last_error("null argument".into());
        return DFLOG_ERR_BAD_ARGUMENT;
    }

    // SAFETY: `file` is a live dflog_open handle per the caller contract
    let log = unsafe { &(*file).log };
    match catch_unwind(AssertUnwindSafe(|| log.time_base())) {
        Ok(Some(base)) => {
            // SAFETY: the out pointers are non-null and valid per the
            // caller contract
            unsafe {
                *gps_start_unix_ms = base.gps_start_unix_ms;
                *ms_offset = base.ms_offset
            };
            DFLOG_OK
        }
        Ok(None) => {
            set_last_error("no usable gps fix in log".into());
            DFLOG_ERR_NO_TIME_BASE
        }
        Err(_) => {
            set_last_error("panic in dflog_time_base".into());
            DFLOG_ERR_PANIC
        }
    }
}

/// Release a result returned by `dflog_get_array_column`.
///
/// # Safety
/// `column` must come from `dflog_get_array_column` and not be used
/// afterwards. Null is ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_array_column_free(column: *mut DflogArrayColumn) {
    if !column.is_null() {
        // SAFETY: `column` came from Box::into_raw in dflog_get_array_column
        // and is not used again per the caller contract
        drop(unsafe { Box::from_raw(column) });
    }
}

/// Release a result returned by `dflog_get_columns`.
///
/// # Safety
/// `columns` must come from `dflog_get_columns` and not be used afterwards.
/// Null is ignored.
#[no_mangle]
pub unsafe extern "C" fn dflog_columns_free(columns: *mut DflogColumns) {
    if !columns.is_null() {
        // SAFETY: `columns` came from Box::into_raw in dflog_get_columns and
        // is not used again per the caller contract
        drop(unsafe { Box::from_raw(columns) });
    }
}

/// Copy the calling thread's last error message (UTF-8, NUL-terminated) into
/// `buf`. Returns the number of bytes written excluding the NUL, or the
/// required capacity as a negative number when `cap` is too small.
///
/// # Safety
/// `buf` must point to at least `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn dflog_last_error(buf: *mut c_char, cap: usize) -> i32 {
    if buf.is_null() || cap == 0 {
        return DFLOG_ERR_BAD_ARGUMENT;
    }

    LAST_ERROR.with(|slot| {
        let message = slot.borrow();
        let bytes = message.as_bytes();
        if bytes.len() + 1 > cap {
            return -(bytes.len() as i32 + 1);
        }
        // SAFETY: `buf` holds at least `cap` writable bytes per the caller
        // contract, and bytes.len() + 1 <= cap was just checked
        unsafe {
            ptr::copy_nonoverlapping(bytes.as_ptr(), buf as *mut u8, bytes.len());
            *buf.add(bytes.len()) = 0
        };
        bytes.len() as i32
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn corpus(name: &str) -> Vec<u8> {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../testdata")
            .join(name);
        std::fs::read(path).expect("corpus log")
    }

    /// Copy `bytes` through a fresh image and open it, as the C# side does.
    fn open_image(bytes: &[u8]) -> *mut DflogFile {
        let mut image = ptr::null_mut();
        let mut data = ptr::null_mut();
        // SAFETY: the out pointers are valid locals
        let rc = unsafe { dflog_image_new(bytes.len() as u64, &mut image, &mut data) };
        assert_eq!(rc, DFLOG_OK);
        // SAFETY: `data` points at `bytes.len()` writable bytes owned by
        // `image`, which nothing else touches until it is opened
        unsafe { ptr::copy_nonoverlapping(bytes.as_ptr(), data, bytes.len()) };

        let mut file = ptr::null_mut();
        // SAFETY: `image` is fresh from dflog_image_new; `file` is a valid local
        let rc = unsafe { dflog_open_image(image, &mut file) };
        assert_eq!(rc, DFLOG_OK);
        assert!(!file.is_null());
        file
    }

    fn index_of(file: *const DflogFile) -> (Vec<u64>, Vec<u8>) {
        let mut offsets = ptr::null();
        let mut types = ptr::null();
        let mut count = 0u64;
        // SAFETY: `file` is a live handle and the out pointers are valid locals
        let rc = unsafe { dflog_file_index(file, &mut offsets, &mut types, &mut count) };
        assert_eq!(rc, DFLOG_OK);
        let count = usize::try_from(count).unwrap();
        if count == 0 {
            return (Vec::new(), Vec::new());
        }
        // SAFETY: dflog_file_index lends `count` elements of each array,
        // valid until dflog_close, which the caller has not called yet
        unsafe {
            (
                std::slice::from_raw_parts(offsets, count).to_vec(),
                std::slice::from_raw_parts(types, count).to_vec(),
            )
        }
    }

    #[test]
    fn image_round_trip_indexes_like_scan() {
        let bytes = corpus("copter.bin");
        let expected = dflog_core::scan(&bytes);

        let file = open_image(&bytes);
        let (offsets, types) = index_of(file);
        assert_eq!(offsets, expected.offsets);
        assert_eq!(types, expected.types);
        assert_eq!(offsets.len(), 31867);
        // SAFETY: `file` came from dflog_open_image and is not used again
        unsafe { dflog_close(file) };
    }

    #[test]
    fn zero_length_image_opens_to_an_empty_index() {
        let file = open_image(&[]);
        let (offsets, types) = index_of(file);
        assert!(offsets.is_empty() && types.is_empty());
        // SAFETY: `file` came from dflog_open_image and is not used again
        unsafe { dflog_close(file) };
    }

    #[test]
    fn unopened_image_can_be_freed() {
        let mut image = ptr::null_mut();
        let mut data = ptr::null_mut();
        // SAFETY: the out pointers are valid locals
        let rc = unsafe { dflog_image_new(16, &mut image, &mut data) };
        assert_eq!(rc, DFLOG_OK);
        assert!(!image.is_null() && !data.is_null());
        // SAFETY: `image` was never opened and is not used again
        unsafe { dflog_image_free(image) };
    }

    #[test]
    fn truncated_images_open_without_panicking() {
        let bytes = corpus("copter.bin");
        let full = dflog_core::scan(&bytes);
        // mid-way through a record, and mid-way through the first FMT payload
        let mid_record = usize::try_from(full.offsets[1000]).unwrap() + 5;
        let mid_fmt = 3 + 40;

        for cut in [mid_record, mid_fmt] {
            let file = open_image(&bytes[..cut]);
            let (offsets, _) = index_of(file);
            assert!(offsets.len() <= full.offsets.len(), "cut at {cut}");
            // SAFETY: `file` came from dflog_open_image and is not used again
            unsafe { dflog_close(file) };
        }
    }

    #[test]
    fn impossible_or_refused_lengths_fail_without_aborting() {
        // u64::MAX overflows the layout; 1 PiB is a valid layout that no
        // allocator on the supported hosts grants
        for (len, expected) in [
            (u64::MAX, DFLOG_ERR_BAD_ARGUMENT),
            (1 << 50, DFLOG_ERR_ALLOC),
        ] {
            let mut image = ptr::NonNull::<DflogImage>::dangling().as_ptr();
            let mut data = ptr::NonNull::<u8>::dangling().as_ptr();
            // SAFETY: the out pointers are valid locals
            let rc = unsafe { dflog_image_new(len, &mut image, &mut data) };
            assert_eq!(rc, expected, "length {len}");
            assert!(image.is_null() && data.is_null(), "length {len}");
        }
    }

    #[test]
    fn abi_version_is_6() {
        assert_eq!(dflog_abi_version(), 6);
    }
}
