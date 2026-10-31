using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// P/Invoke bindings for the Rust dataflash log core
    /// (rust/crates/dflog-ffi). All failures degrade to "not available" so
    /// callers can fall back to the managed scanner.
    /// </summary>
    internal static class DFLogNative
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        const string Dll = "dflog_ffi";

        /// <summary>the ABI this build expects; the library built from
        /// rust/crates/dflog-ffi by the BuildDflogNative MSBuild target must
        /// report it</summary>
        internal const uint AbiVersion = 6;

        /// <summary>chunk size for copying a stream into a native image</summary>
        const int ImageChunkSize = 1024 * 1024;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern uint dflog_abi_version();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_image_new(ulong len, out IntPtr image, out IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_image_free(IntPtr image);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_open_image(IntPtr image, out IntPtr file);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_file_index(IntPtr file, out IntPtr offsets, out IntPtr types, out ulong count);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_last_error(byte[] buf, UIntPtr cap);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_open(byte[] pathUtf8, out IntPtr file);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_close(IntPtr file);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_get_columns(IntPtr file, byte[] typeUtf8, byte[] fieldsUtf8, out IntPtr columns);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_get_columns_filtered(IntPtr file, byte[] typeUtf8, byte[] fieldsUtf8,
            int hasInstance, long instance, out IntPtr columns);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_columns_free(IntPtr columns);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_get_array_column(IntPtr file, byte[] typeUtf8, byte[] fieldUtf8, out IntPtr column);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern void dflog_array_column_free(IntPtr column);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int dflog_time_base(IntPtr file, out long gpsStartUnixMs, out long msOffset);

        [StructLayout(LayoutKind.Sequential)]
        struct NativeColumns
        {
            public ulong rows;
            public uint cols;
            public IntPtr linenos;
            public IntPtr values;
            // followed by rust-owned storage; opaque to this side
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeArrayColumn
        {
            public ulong rows;
            public uint elems;
            public IntPtr linenos;
            public IntPtr values;
            // followed by rust-owned storage; opaque to this side
        }

        static byte[] Utf8Z(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

        /// <summary>
        /// A log held open by the native side for typed column queries
        /// (phase B). Not thread-safe; guard externally like DFLogBuffer does.
        /// </summary>
        public sealed class ColumnReader : IDisposable
        {
            IntPtr _file;

            ColumnReader(IntPtr file)
            {
                _file = file;
            }

            public static ColumnReader Open(string path)
            {
                if (!Available)
                    return null;

                try
                {
                    var rc = dflog_open(Utf8Z(path), out var file);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_open failed ({0}): {1}", rc, LastError());
                        return null;
                    }

                    return new ColumnReader(file);
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_open failed", ex);
                    return null;
                }
            }

            /// <summary>
            /// Copy the first <paramref name="length"/> bytes of
            /// <paramref name="stream"/> into a native image and index them,
            /// so the native side sees exactly the bytes the managed reader
            /// sees - never a path that may now name a different file.
            /// Returns null (never throws) when the library is unavailable,
            /// the image cannot be allocated, or the stream fails or ends
            /// before <paramref name="length"/> bytes (the file shrank since
            /// it was measured). The stream position is restored.
            /// </summary>
            public static ColumnReader FromStream(Stream stream, long length)
            {
                if (!Available || stream == null || length < 0)
                    return null;

                long position;
                try
                {
                    position = stream.Position;
                }
                catch (Exception ex)
                {
                    log.Warn("cannot read the log stream position", ex);
                    return null;
                }

                var image = IntPtr.Zero;
                try
                {
                    var rc = dflog_image_new((ulong)length, out image, out var data);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_image_new({0}) failed ({1}): {2}", length, rc, LastError());
                        return null;
                    }

                    stream.Position = 0;
                    var buffer = new byte[Math.Min(length, ImageChunkSize)];
                    long copied = 0;
                    while (copied < length)
                    {
                        var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - copied));
                        if (read <= 0)
                        {
                            log.WarnFormat("log stream ended after {0} of {1} bytes; it shrank since it was measured",
                                copied, length);
                            return null;
                        }

                        Marshal.Copy(buffer, 0, new IntPtr(data.ToInt64() + copied), read);
                        copied += read;
                    }

                    // the image is consumed whether or not the open succeeds
                    rc = dflog_open_image(image, out var file);
                    image = IntPtr.Zero;
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_open_image failed ({0}): {1}", rc, LastError());
                        return null;
                    }

                    return new ColumnReader(file);
                }
                catch (Exception ex)
                {
                    log.Warn("reading the log into a native image failed", ex);
                    return null;
                }
                finally
                {
                    if (image != IntPtr.Zero)
                        dflog_image_free(image);

                    try
                    {
                        // a stream disposed meanwhile reports CanSeek false
                        if (stream.CanSeek)
                            stream.Position = position;
                    }
                    catch (IOException ex)
                    {
                        log.Warn("cannot restore the log stream position", ex);
                    }
                }
            }

            /// <summary>
            /// Copy the record index this reader built: the byte offset and
            /// message type of every record, in scan order. Returns false
            /// (never throws) on failure.
            /// </summary>
            public bool TryGetIndex(out long[] offsets, out byte[] types)
            {
                offsets = null;
                types = null;

                if (_file == IntPtr.Zero)
                    return false;

                try
                {
                    var rc = dflog_file_index(_file, out var offsetsPtr, out var typesPtr, out var count);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_file_index failed ({0}): {1}", rc, LastError());
                        return false;
                    }

                    if (count > int.MaxValue)
                    {
                        log.WarnFormat("dflog index too large for managed copy: {0}", count);
                        return false;
                    }

                    var n = (int)count;
                    offsets = new long[n];
                    types = new byte[n];
                    if (n > 0)
                    {
                        Marshal.Copy(offsetsPtr, offsets, 0, n);
                        Marshal.Copy(typesPtr, types, 0, n);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_file_index failed", ex);
                    offsets = null;
                    types = null;
                    return false;
                }
            }

            /// <summary>
            /// Whether this reader indexed exactly the records at
            /// <paramref name="lineOffsets"/>, record for record - the check
            /// that its column line numbers mean the same rows as the caller's
            /// index. Logs a warning on a mismatch.
            /// </summary>
            public bool IndexMatches(IReadOnlyList<long> lineOffsets)
            {
                if (!TryGetIndex(out var offsets, out _))
                    return false;

                var matches = offsets.Length == lineOffsets.Count;
                for (var i = 0; matches && i < offsets.Length; i++)
                    matches = offsets[i] == lineOffsets[i];

                if (!matches)
                    log.WarnFormat("native index ({0} records) does not match the managed index ({1}); " +
                                   "the log changed since it was indexed", offsets.Length, lineOffsets.Count);
                return matches;
            }

            /// <summary>
            /// Decode all records of <paramref name="type"/> into one f64
            /// column per requested field, plus the global record index per
            /// row. Returns false (never throws) on any failure.
            /// </summary>
            public bool TryGetColumns(string type, string[] fields, out long[] linenos, out double[][] columns)
            {
                return TryGetColumns(type, fields, null, out linenos, out columns);
            }

            /// <summary>
            /// <see cref="TryGetColumns(string,string[],out long[],out double[][])"/>
            /// limited to rows of one <paramref name="instance"/> value (the
            /// field carrying the '#' unit id, e.g. IMU.I). Fails when the
            /// type has no instance field.
            /// </summary>
            public bool TryGetColumns(string type, string[] fields, long? instance, out long[] linenos,
                out double[][] columns)
            {
                linenos = null;
                columns = null;

                if (_file == IntPtr.Zero)
                    return false;

                var handle = IntPtr.Zero;
                try
                {
                    var rc = dflog_get_columns_filtered(_file, Utf8Z(type), Utf8Z(string.Join(",", fields)),
                        instance.HasValue ? 1 : 0, instance ?? 0, out handle);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_get_columns({0}) failed ({1}): {2}", type, rc, LastError());
                        return false;
                    }

                    var native = Marshal.PtrToStructure<NativeColumns>(handle);
                    if (native.rows > int.MaxValue)
                        return false;

                    var rows = (int)native.rows;
                    linenos = new long[rows];
                    if (rows > 0)
                        Marshal.Copy(native.linenos, linenos, 0, rows);

                    columns = new double[native.cols][];
                    for (var c = 0; c < native.cols; c++)
                    {
                        columns[c] = new double[rows];
                        if (rows > 0)
                            Marshal.Copy(new IntPtr(native.values.ToInt64() + (long)c * rows * sizeof(double)), columns[c], 0, rows);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_get_columns failed", ex);
                    linenos = null;
                    columns = null;
                    return false;
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                        dflog_columns_free(handle);
                }
            }

            /// <summary>
            /// Decode the `a` (int16[32]) array field of every record of
            /// <paramref name="type"/>, one short[] per row, plus the global
            /// record index per row. Returns false (never throws) on failure.
            /// </summary>
            public bool TryGetArrayColumn(string type, string field, out long[] linenos, out short[][] rows)
            {
                linenos = null;
                rows = null;

                if (_file == IntPtr.Zero)
                    return false;

                var handle = IntPtr.Zero;
                try
                {
                    var rc = dflog_get_array_column(_file, Utf8Z(type), Utf8Z(field), out handle);
                    if (rc != 0)
                    {
                        log.WarnFormat("dflog_get_array_column({0}.{1}) failed ({2}): {3}", type, field, rc,
                            LastError());
                        return false;
                    }

                    var native = Marshal.PtrToStructure<NativeArrayColumn>(handle);
                    if (native.rows > int.MaxValue)
                        return false;

                    var count = (int)native.rows;
                    var elems = (int)native.elems;
                    linenos = new long[count];
                    if (count > 0)
                        Marshal.Copy(native.linenos, linenos, 0, count);

                    rows = new short[count][];
                    for (var r = 0; r < count; r++)
                    {
                        rows[r] = new short[elems];
                        Marshal.Copy(new IntPtr(native.values.ToInt64() + (long)r * elems * sizeof(short)), rows[r], 0, elems);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_get_array_column failed", ex);
                    linenos = null;
                    rows = null;
                    return false;
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                        dflog_array_column_free(handle);
                }
            }

            /// <summary>
            /// Wall-clock correlation from the log's first valid GPS fix.
            /// Returns false when the library is unavailable or the log has
            /// no usable fix.
            /// </summary>
            public bool TryGetTimeBase(out long gpsStartUnixMs, out long msOffset)
            {
                gpsStartUnixMs = 0;
                msOffset = 0;

                if (_file == IntPtr.Zero)
                    return false;

                try
                {
                    return dflog_time_base(_file, out gpsStartUnixMs, out msOffset) == 0;
                }
                catch (Exception ex)
                {
                    log.Warn("dflog_time_base failed", ex);
                    return false;
                }
            }

            public void Dispose()
            {
                if (_file != IntPtr.Zero)
                {
                    dflog_close(_file);
                    _file = IntPtr.Zero;
                }
            }
        }

        static readonly Lazy<bool> _available = new Lazy<bool>(() =>
        {
            try
            {
                var abi = dflog_abi_version();
                if (abi != AbiVersion)
                {
                    log.WarnFormat("dflog_ffi ABI {0} does not match expected {1}", abi, AbiVersion);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Debug("dflog_ffi not available: " + ex.Message);
                return false;
            }
        });

        public static bool Available => _available.Value;

        static string LastError()
        {
            try
            {
                var buf = new byte[1024];
                var n = dflog_last_error(buf, (UIntPtr)buf.Length);
                if (n > 0)
                    return System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
            catch
            {
            }

            return "(unknown)";
        }
    }
}
