using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;

namespace MissionPlanner.Utilities
{
    public class DFLogBuffer : IEnumerable<String>, IDisposable
    {
        // used for binary log line conversion
        BinaryLog binlog = new BinaryLog();

        // used for fmt messages
        public DFLog dflog { get; }

        Stream basestream;
        private long _count;
        List<long> linestartoffset = new List<long>();

        /// <summary>
        /// Type and offsets
        /// </summary>
        List<long>[] messageindex = new List<long>[256];

        /// <summary>
        /// Type and line numbers
        /// </summary>
        List<long>[] messageindexline = new List<long>[256];

        bool binary = false;

        static bool? useNativeScan;

        /// <summary>
        /// Use the Rust log core (rust/crates/dflog-ffi) for binary logs when
        /// the native library is present; the managed path remains the
        /// fallback. Precedence: explicit set (tests, config UI) > the
        /// DFLOG_NATIVE environment variable ("1"/"0") > the "dflog_native"
        /// setting > ON by default (platforms without the library fall back
        /// automatically via DFLogNative.Available).
        /// </summary>
        public static bool UseNativeScan
        {
            get
            {
                if (useNativeScan == null)
                    useNativeScan = DefaultUseNativeScan();
                return useNativeScan.Value;
            }
            set { useNativeScan = value; }
        }

        static bool DefaultUseNativeScan()
        {
            var env = Environment.GetEnvironmentVariable("DFLOG_NATIVE");
            if (env == "1")
                return true;
            if (env == "0")
                return false;

            try
            {
                return Settings.Instance.GetBoolean("dflog_native", true);
            }
            catch
            {
                // no settings store (headless/embedded use); still safe -
                // Available gates the actual native calls
                return true;
            }
        }

        /// <summary>Whether the last setlinecount used the native scanner.</summary>
        internal static bool LastScanNative;

        /// <summary>Successful native column queries, so tests can prove a
        /// converted consumer actually took the fast path.</summary>
        internal static long NativeColumnHits;

        DFLogNative.ColumnReader nativeColumns;
        bool nativeColumnsTried;

        object locker = new object();

        /// <summary>
        /// Typed fast path via the native dflog library: decode all
        /// records of <paramref name="type"/> straight from the file into one
        /// double column per field, bypassing the per-row string conversion.
        /// Row order matches GetEnumeratorType(type); linenos are the same
        /// line numbers DFItem.lineno reports. Returns false when the native
        /// library is unavailable or the type/field cannot be decoded
        /// numerically - callers must fall back to the enumerator path.
        /// Note: values are the raw decoded values; the legacy string path
        /// rounds floats to 7 significant digits, this path does not.
        /// </summary>
        public bool TryGetColumnsNative(string type, string[] fields, out long[] linenos, out double[][] columns)
        {
            return TryGetColumnsNative(type, fields, null, out linenos, out columns);
        }

        /// <summary>
        /// <see cref="TryGetColumnsNative(string,string[],out long[],out double[][])"/>
        /// limited to rows of one <paramref name="instance"/> value (the field
        /// carrying the '#' unit id - the same column GetInstanceFieldName
        /// reports). Returns false when the type has no instance field.
        /// </summary>
        public bool TryGetColumnsNative(string type, string[] fields, long? instance, out long[] linenos,
            out double[][] columns)
        {
            linenos = null;
            columns = null;

            if (!binary || !UseNativeScan || string.IsNullOrEmpty(_filename))
                return false;

            lock (locker)
            {
                if (!nativeColumnsTried)
                {
                    nativeColumnsTried = true;
                    nativeColumns = DFLogNative.ColumnReader.Open(_filename);
                }

                var ok = nativeColumns != null &&
                         nativeColumns.TryGetColumns(type, fields, instance, out linenos, out columns);
                if (ok)
                    System.Threading.Interlocked.Increment(ref NativeColumnHits);
                return ok;
            }
        }

        /// <summary>
        /// Typed fast path for `a` (int16[32]) array fields (ISBD batch
        /// samples and friends): decode every record of
        /// <paramref name="type"/> into one short[32] per row. Values are the
        /// raw int16 samples exactly as BinaryLog.UnionArray carries them;
        /// linenos are the same line numbers DFItem.lineno reports. Returns
        /// false when the native library is unavailable or the field is not
        /// an `a` array - callers must fall back to the enumerator path.
        /// </summary>
        public bool TryGetArrayColumnNative(string type, string field, out long[] linenos, out short[][] rows)
        {
            linenos = null;
            rows = null;

            if (!binary || !UseNativeScan || string.IsNullOrEmpty(_filename))
                return false;

            lock (locker)
            {
                if (!nativeColumnsTried)
                {
                    nativeColumnsTried = true;
                    nativeColumns = DFLogNative.ColumnReader.Open(_filename);
                }

                var ok = nativeColumns != null &&
                         nativeColumns.TryGetArrayColumn(type, field, out linenos, out rows);
                if (ok)
                    System.Threading.Interlocked.Increment(ref NativeColumnHits);
                return ok;
            }
        }

        long indexcachelineno = -1;
        String currentindexcache = null;

        public DFLogBuffer(string filename) : this(File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _filename = filename;
        }

        public DFLogBuffer(Stream instream)
        {
            if (instream is FileStream)
                _filename = ((FileStream)instream).Name;

            dflog = new DFLog(this);
            for (int a = 0; a < messageindex.Length; a++)
            {
                messageindex[a] = new List<long>(0);
                messageindexline[a] = new List<long>(0);
            }

            if (instream.CanSeek)
            {
                basestream = instream;
            }
            else
            {
                Console.WriteLine("DFLogBuffer: not seekable - copying to memorystream");
                basestream = new MemoryStream((int)instream.Length);
                instream.CopyTo(basestream);
                basestream.Position = 0;
                instream.Close();
            }

            if (basestream.ReadByte() == BinaryLog.HEAD_BYTE1)
            {
                if (basestream.ReadByte() == BinaryLog.HEAD_BYTE2)
                {
                    binary = true;
                }
            }

            Console.WriteLine("Binary: " + binary);

            // back to start
            basestream.Position = 0;
            DateTime start = DateTime.Now;
            setlinecount();
            Console.WriteLine("DFLogBuffer-linecount: " + Count + " time(ms): " +
                              (DateTime.Now - start).TotalMilliseconds);
            basestream.Position = 0;
        }

        void setlinecount()
        {
            // native-capable opens skip the index cache in both directions: a
            // fresh native index is cheap, and never saving from the native
            // path keeps the cache a pure managed-fallback optimization
            var nativeCapable = binary && UseNativeScan &&
                                !string.IsNullOrEmpty(_filename) && DFLogNative.Available;
            LastScanNative = false;
            // the static above is a test-observable mirror; another buffer
            // constructed concurrently rewrites it, so this instance's own
            // outcome decides whether it saves a cache
            var scannedNative = false;

            CacheSourceIdentity sourceIdentity;
            var hasSourceIdentity = TryGetSourceIdentity(out sourceIdentity);
            var loadedFromCache = !nativeCapable && hasSourceIdentity && LoadCache(sourceIdentity);
            LastLoadFromCache = loadedFromCache;
            if (!loadedFromCache)
            {
                byte[] buffer = new byte[1024 * 1024];

                var lineCount = 0L;
                if (binary)
                {
                    long length = basestream.Length;

                    if (UseNativeScan && !string.IsNullOrEmpty(_filename) &&
                        DFLogNative.TryScan(_filename, out var nativeOffsets, out var nativeTypes))
                    {
                        // the record count is known upfront - size the index once
                        // instead of letting the list double its way there
                        linestartoffset.Capacity = linestartoffset.Count + nativeOffsets.Length;

                        for (int i = 0; i < nativeOffsets.Length; i++)
                        {
                            byte ntype = nativeTypes[i];
                            messageindex[ntype].Add(nativeOffsets[i]);
                            messageindexline[ntype].Add(lineCount);

                            linestartoffset.Add(nativeOffsets[i]);
                            lineCount++;
                        }

                        LastScanNative = true;
                        scannedNative = true;
                    }
                    else
                    {
                        while (basestream.Position < length)
                        {
                            var ans = binlog.ReadMessageTypeOffset(basestream, length);

                            if (ans.MsgType == 0 && ans.Offset == 0)
                                continue;

                            byte type = ans.Item1;
                            messageindex[type].Add(ans.Item2);
                            messageindexline[type].Add(lineCount);

                            linestartoffset.Add(ans.Item2);
                            lineCount++;

                            if (lineCount % 1000000 == 0)
                                Console.WriteLine("reading lines " + lineCount + " " +
                                                  ((basestream.Position / (double)length) * 100.0));
                        }
                    }

                    _count = lineCount;

                    // build fmt line database to pre seed the FMT message
                    messageindexline[128].ForEach(a => dflog.FMTLine(this[(int)a]));

                    try
                    {
                        foreach (var item in dflog.logformat)
                        {
                            var id = item.Value.Id;
                            var type = item.Value.Name;
                            if (messageindex[id].Count != 0)
                                Console.WriteLine("Seen " + type + " count " + messageindex[id].Count);
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    var offset = 0;
                    // first line starts at 0
                    linestartoffset.Add(0);

                    long length = basestream.Length;
                    while (basestream.Position < length)
                    {
                        offset = 0;

                        long startpos = basestream.Position;

                        int read = basestream.Read(buffer, offset, buffer.Length);

                        while (read > 0)
                        {
                            if (buffer[offset] == '\n')
                            {
                                linestartoffset.Add((uint)(startpos + 1 + offset));
                                lineCount++;
                            }

                            offset++;
                            read--;
                        }
                    }

                    _count = lineCount;

                    // create msg cache
                    int b = 0;
                    foreach (var item in this)
                    {
                        var idx = item.IndexOf(',');

                        if (idx <= 0)
                        {
                            b++;
                            continue;
                        }

                        var msgtype = item.Substring(0, idx);

                        if (msgtype == "FMT")
                            dflog.FMTLine(item);

                        if (dflog.logformat.ContainsKey(msgtype))
                        {
                            var type = (byte)dflog.logformat[msgtype].Id;

                            messageindex[type].Add(linestartoffset[b]);
                            messageindexline[type].Add((uint)b);
                        }

                        b++;
                    }
                }

                if (!scannedNative && hasSourceIdentity)
                    SaveCache(sourceIdentity);
            }


            // build fmt line database using type
            foreach (var item in GetEnumeratorType("FMT"))
            {
                try
                {
                    if (item.items == null || item.items.Length == 0)
                        continue;

                    FMT[int.Parse(item["Type"])] = (
                        int.Parse(item["Length"].Trim()),
                        item["Name"].Trim(),
                        item["Format"].Trim(),
                        item.items.Skip(dflog.FindMessageOffset("FMT", "Columns"))
                            .Aggregate((s, s1) => s.Trim() + "," + s1.Trim())
                            .TrimStart(','));

                    dflog.FMTLine(this[item.lineno]);
                }
                catch
                {
                }
            }

            foreach (var item in GetEnumeratorType("FMTU"))
            {
                try
                {
                    if (item.items == null || item.items.Length == 0)
                        continue;

                    FMTU[int.Parse(item["FmtType"])] =
                        new Tuple<string, string>(item["UnitIds"].Trim(), item["MultIds"].Trim());

                    if (item["UnitIds"].Trim().Contains("#"))
                    {
                        InstanceType[int.Parse(item["FmtType"])] =
                            (item["UnitIds"].Trim().IndexOf("#"), new List<string>());
                    }
                }
                catch
                {
                }
            }

            foreach (var b in InstanceType)
            {
                if (!FMT.ContainsKey(b.Key))
                    continue;
                int a = 0;
                foreach (var item in GetEnumeratorType(FMT[b.Key].name))
                {
                    var instancevalue = item.raw[b.Value.index + 1].ToString();

                    if (!b.Value.value.Contains(instancevalue))
                        b.Value.value.Add(instancevalue);

                    if (a > 2000)
                        break;
                    a++;
                }
            }

            if (Unit.Count > 0)
                foreach (var item in GetEnumeratorType("UNIT"))
                {
                    try
                    {
                        Unit[(char)int.Parse(item["Id"])] = item["Label"].Trim();
                    }
                    catch
                    {
                    }
                }

            if (Mult.Count > 0)
                foreach (var item in GetEnumeratorType("MULT"))
                {
                    try
                    {
                        Mult[(char)int.Parse(item["Id"])] = item["Mult"].Trim();
                    }
                    catch
                    {
                    }
                }

            BuildUnitMultiList();

            int limitcount = 0;
            // used to set the firmware type
            foreach (var item in GetEnumeratorType(new[]
                     {
                         "MSG", "PARM"
                     }))
            {
                // must be the string version to do the firmware type detection - binarylog
                var line = this[(int)item.lineno];
                //Console.WriteLine();
                limitcount++;
                if (limitcount > 100000)
                    break;
            }

            // try get gps time - when a dfitem is created and no valid gpstime has been establish the messages are parsed to get a valid gpstime
            // here we just force the parsing of gps messages to get the valid board time to gps time offset
            int gpsa = 0;
            foreach (var item in GetEnumeratorType(new[]
                     {
                         "GPS", "GPS2", "GPSB"
                     }))
            {
                gpsa++;
                int status = 0;
                if (int.TryParse(item["Status"], out status))
                {
                    if (status >= 3)
                        break;
                }

                // get first gps time
                if (gpsa > 2000)
                    break;
            }

            indexcachelineno = -1;
        }

        // index cache file format: gzip over a small header (magic, version,
        // source length and last-write ticks) followed by the raw index longs.
        // Upstream serialized this with BinaryFormatter, which throws
        // unconditionally on modern .NET - saving a large log crashed the open
        // and loading silently never worked.
        const uint CacheMagic = 0x4C444D31; // "1MDL"
        const int CacheVersion = 1;

        struct CacheSourceIdentity
        {
            public long Length;
            public long LastWriteTimeUtcTicks;
        }

        /// <summary>Logs at or above this many bytes get an index cache
        /// (test seam; the product default matches upstream's 300 MB).</summary>
        internal static long CacheThresholdBytes = 1024L * 1024 * 300;

        /// <summary>Whether this buffer loaded its index cache instead of
        /// scanning (test observability).</summary>
        internal bool LastLoadFromCache { get; private set; }

        /// <summary>The cache file used by this buffer (test observability).</summary>
        internal string CacheFilePath => CachePath;

        private string CachePath
        {
            get
            {
                try
                {
                    // Keep the predictable cache out of the shared temp root and
                    // hash the source path so a deep log path cannot exceed the
                    // platform's maximum file-name length.
                    var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(root))
                        root = Path.GetTempPath();
                    var directory = Path.Combine(root, "MissionPlanner", "DFLogCache");
                    Directory.CreateDirectory(directory);

                    byte[] digest;
                    using (var sha = SHA256.Create())
                        digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(_filename)));
                    var name = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
                    return Path.Combine(directory, "index-v" + CacheVersion + "-" + name + ".bin.gz");
                }
                catch
                {
                    // A cache is only an optimization. An unusable cache path
                    // must never create a stray temp file or fail the log open.
                    return string.Empty;
                }
            }
        }

        bool TryGetSourceIdentity(out CacheSourceIdentity identity)
        {
            identity = new CacheSourceIdentity();
            if (string.IsNullOrEmpty(_filename))
                return false;

            try
            {
                var source = new FileInfo(_filename);
                source.Refresh();
                if (!source.Exists)
                    return false;
                identity.Length = source.Length;
                identity.LastWriteTimeUtcTicks = source.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch
            {
                return false;
            }
        }

        bool SourceMatches(CacheSourceIdentity identity)
        {
            CacheSourceIdentity current;
            return TryGetSourceIdentity(out current) &&
                   current.Length == identity.Length &&
                   current.LastWriteTimeUtcTicks == identity.LastWriteTimeUtcTicks;
        }

        private void SaveCache(CacheSourceIdentity sourceIdentity)
        {
            // save cache if file is over the threshold (upstream: 300mb)
            if (sourceIdentity.Length < CacheThresholdBytes || !SourceMatches(sourceIdentity))
                return;

            string temp = null;
            try
            {
                var cachePath = CachePath;
                if (string.IsNullOrEmpty(cachePath))
                    return;

                // A unique sibling temp prevents concurrent opens of the same
                // large log from truncating one another's in-progress cache.
                temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var file = File.Create(temp))
                using (var gs = new GZipStream(file, CompressionMode.Compress))
                using (var writer = new BinaryWriter(gs))
                {
                    writer.Write(CacheMagic);
                    writer.Write(CacheVersion);
                    writer.Write(sourceIdentity.Length);
                    writer.Write(sourceIdentity.LastWriteTimeUtcTicks);

                    writer.Write(_count);
                    WriteLongList(writer, linestartoffset);
                    for (int a = 0; a < messageindex.Length; a++)
                    {
                        WriteLongList(writer, messageindex[a]);
                        WriteLongList(writer, messageindexline[a]);
                    }
                }

                // On platforms where another process can alter an open file,
                // never publish an index assembled while the source changed.
                if (!SourceMatches(sourceIdentity))
                    return;

                // replace through a temp file so a torn write never lands at the
                // cache path; the delete+move pair leaves at worst a moment with
                // no cache, which just means a rescan
                File.Delete(cachePath);
                File.Move(temp, cachePath);
                temp = null;
            }
            catch
            {
                // the cache is an optimization - never fail an open over it
            }
            finally
            {
                if (!string.IsNullOrEmpty(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private bool LoadCache(CacheSourceIdentity sourceIdentity)
        {
            try
            {
                var cachePath = CachePath;
                if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath))
                    return false;

                using (var file = File.OpenRead(cachePath))
                using (var gs = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new BinaryReader(gs))
                {
                    if (reader.ReadUInt32() != CacheMagic || reader.ReadInt32() != CacheVersion)
                        return false;

                    // A cache for an older copy of the log must not survive an
                    // in-place change. The path identifies the source path, so
                    // both length and write time are verified in the header.
                    if (reader.ReadInt64() != sourceIdentity.Length ||
                        reader.ReadInt64() != sourceIdentity.LastWriteTimeUtcTicks)
                        return false;

                    // Treat the cache as untrusted even though it lives in the
                    // user's data directory. Binary records consume at least
                    // three bytes; text logs can contain one-byte empty lines.
                    var maxEntries = binary ? sourceIdentity.Length / 3 + 1 : sourceIdentity.Length + 1;
                    var lineCount = reader.ReadInt64();
                    if (lineCount < 0 || lineCount > maxEntries || lineCount > int.MaxValue)
                        throw new InvalidDataException("implausible cached line count " + lineCount);

                    // A valid cache stores one source offset plus at most two
                    // per-message entries for every indexed line. Bounding the
                    // aggregate prevents 513 individually plausible lists from
                    // multiplying memory use after corruption.
                    var remainingEntries = checked(lineCount * 3 + 1);
                    var offsets = ReadLongList(reader, maxEntries, ref remainingEntries,
                        0, sourceIdentity.Length, strictlyIncreasing: true);
                    var index = new List<long>[messageindex.Length];
                    var indexline = new List<long>[messageindex.Length];
                    for (int a = 0; a < index.Length; a++)
                    {
                        index[a] = ReadLongList(reader, maxEntries, ref remainingEntries,
                            0, sourceIdentity.Length - 1, strictlyIncreasing: true);
                        indexline[a] = ReadLongList(reader, maxEntries, ref remainingEntries,
                            0, lineCount - 1, strictlyIncreasing: true);
                        if (index[a].Count != indexline[a].Count)
                            throw new InvalidDataException("cached offset/line index counts differ");
                    }

                    var expectedOffsetCount = binary ? lineCount : checked(lineCount + 1);
                    if (offsets.Count != expectedOffsetCount)
                        throw new InvalidDataException("cached line-offset count does not match line count");

                    var indexedCount = index.Sum(values => (long)values.Count);
                    if ((binary && indexedCount != lineCount) || (!binary && indexedCount > lineCount))
                        throw new InvalidDataException("cached message count does not match line count");

                    // Version 1 has no extension area. Extra uncompressed data
                    // indicates a foreign or corrupt cache, not a newer format.
                    if (reader.BaseStream.ReadByte() != -1)
                        throw new InvalidDataException("trailing data in index cache");

                    if (!SourceMatches(sourceIdentity))
                        throw new InvalidDataException("source log changed while reading its index cache");

                    // commit only after the whole cache read back cleanly
                    messageindex = index;
                    messageindexline = indexline;
                    linestartoffset = offsets;
                    _count = lineCount;
                }

                // build fmt line database to pre seed the FMT message
                messageindexline[128].ForEach(a => dflog.FMTLine(this[(int)a]));
                return true;
            }
            catch
            {
                // a torn or foreign cache must not leave a half-loaded index
                // behind - the caller's rescan appends into these collections.
                // (dflog.logformat entries FMTLine seeded before the throw are
                // deliberately left alone: nothing enumerates a type whose
                // messageindex is empty, and the rescan re-seeds every real FMT
                // line by key)
                linestartoffset = new List<long>();
                for (int a = 0; a < messageindex.Length; a++)
                {
                    messageindex[a] = new List<long>(0);
                    messageindexline[a] = new List<long>(0);
                }
                _count = 0;
                return false;
            }
        }

        static void WriteLongList(BinaryWriter writer, List<long> values)
        {
            writer.Write(values.Count);
            foreach (var value in values)
                writer.Write(value);
        }

        static List<long> ReadLongList(BinaryReader reader, long maxEntries, ref long remainingEntries,
            long minimumValue, long maximumValue, bool strictlyIncreasing)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > maxEntries || count > remainingEntries)
                throw new InvalidDataException("implausible index list length " + count);
            remainingEntries -= count;

            // Do not turn an untrusted count into a giant immediate allocation.
            // Real lists grow normally after the first modest capacity chunk.
            var values = new List<long>(Math.Min(count, 4096));
            var previous = long.MinValue;
            for (int i = 0; i < count; i++)
            {
                var value = reader.ReadInt64();
                if (value < minimumValue || value > maximumValue ||
                    (strictlyIncreasing && i != 0 && value <= previous))
                    throw new InvalidDataException("invalid cached index value " + value);
                values.Add(value);
                previous = value;
            }
            return values;
        }

        public void SplitLog(int pieces = 0)
        {
            long length = basestream.Length;

            if (pieces > 0)
            {
                long sizeofpiece = length / pieces;

                for (int i = 0; i < pieces; i++)
                {
                    long start = i * sizeofpiece;
                    long end = start + sizeofpiece;

                    using (var file = File.OpenWrite(_filename + "_split" + i + ".bin"))
                    {
                        var type = dflog.logformat["FMT"];

                        var buffer = new byte[1024 * 256];

                        // fmt from entire file
                        messageindex[type.Id].ForEach(a =>
                        {
                            basestream.Seek(a, SeekOrigin.Begin);
                            int read = basestream.Read(buffer, 0, type.Length);
                            file.Write(buffer, 0, read);
                        });

                        type = dflog.logformat["FMTU"];

                        messageindex[type.Id].ForEach(a =>
                        {
                            basestream.Seek(a, SeekOrigin.Begin);
                            int read = basestream.Read(buffer, 0, type.Length);
                            file.Write(buffer, 0, read);
                        });

                        type = dflog.logformat["UNIT"];

                        messageindex[type.Id].ForEach(a =>
                        {
                            basestream.Seek(a, SeekOrigin.Begin);
                            int read = basestream.Read(buffer, 0, type.Length);
                            file.Write(buffer, 0, read);
                        });

                        type = dflog.logformat["MULT"];

                        messageindex[type.Id].ForEach(a =>
                        {
                            basestream.Seek(a, SeekOrigin.Begin);
                            int read = basestream.Read(buffer, 0, type.Length);
                            file.Write(buffer, 0, read);
                        });



                        var min = long.MaxValue;
                        var max = long.MinValue;

                        // got min and max valid
                        linestartoffset.ForEach(a =>
                        {
                            if (a >= start && a < end)
                            {
                                min = Math.Min(min, a);
                                max = Math.Max(max, a);
                            }
                        });

                        basestream.Seek(min, SeekOrigin.Begin);

                        while (basestream.Position < max)
                        {
                            int readsize = (int)Math.Min((end - basestream.Position), buffer.Length);
                            int read = basestream.Read(buffer, 0, readsize);
                            file.Write(buffer, 0, read);
                        }
                    }
                }
            }
            else
            {
                throw new Exception("Invalid pieces parameters");
            }
        }

        private void BuildUnitMultiList()
        {
            foreach (var msgtype in FMT)
            {
                // get unit and mult info
                var fmtu = FMTU.FirstOrDefault(a => a.Key == msgtype.Key);

                if (fmtu.Value == null)
                    continue;

                var units = fmtu.Value.Item1.ToCharArray().Select(a => Unit.FirstOrDefault(b => b.Key == a));
                var multipliers = fmtu.Value.Item2.ToCharArray().Select(a => Mult.FirstOrDefault(b => b.Key == a));
                var binfmts = msgtype.Value.Item3.ToCharArray();
                var itemcount = msgtype.Value.Item4.Split(',').Length;

                if (binfmts.Length != itemcount)
                    continue;

                for (var i = 0; i < itemcount; i++)
                {
                    var field = msgtype.Value.Item4.Split(',')[i].Trim();
                    var unit = units.Skip(i).FirstOrDefault().Value;
                    var binfmt = binfmts[i];
                    var multi = 1.0;
                    double.TryParse(multipliers.Skip(i).FirstOrDefault().Value, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out multi);

                    if (binfmt == 'c' || binfmt == 'C' ||
                        binfmt == 'e' || binfmt == 'E' ||
                        binfmt == 'L')
                    {
                        // these are scaled from the DF format * 100/1e7 etc
                        // to ensure csv's continue to work we dont modify these values
                        // 1 = no change
                        multi = 1;
                    }

                    UnitMultiList.Add(
                        new Tuple<string, string, string, double>(msgtype.Value.Item2, field, unit, multi));
                }
            }
        }

        public List<Tuple<string, string, string, double>> UnitMultiList =
            new List<Tuple<string, string, string, double>>();

        public Dictionary<int, (int index, List<string> value)> InstanceType =
            new Dictionary<int, (int index, List<string> value)>();

        private string _filename = "";

        public Dictionary<int, (int length, string name, string format, string columns)> FMT { get; set; } =
            new Dictionary<int, (int, string, string, string)>();

        public Dictionary<int, Tuple<string, string>> FMTU { get; set; } = new Dictionary<int, Tuple<string, string>>();

        public Dictionary<char, string> Unit { get; set; } = new Dictionary<char, string>();
        public Dictionary<char, string> Mult { get; set; } = new Dictionary<char, string>();

        public DFLog.DFItem this[long indexin]
        {
            get
            {
                if (indexin > int.MaxValue)
                    throw new Exception("index too large");

                var index = (int)indexin;

                long startoffset = linestartoffset[index];
                long endoffset = startoffset;

                if ((index + 1) >= linestartoffset.Count)
                {
                    endoffset = basestream.Length;
                }
                else
                {
                    endoffset = linestartoffset[index + 1];
                }

                int length = (int)(endoffset - startoffset);

                // prevent multi io to file
                lock (locker)
                {
                    if (linestartoffset[index] != basestream.Position)
                        basestream.Seek(linestartoffset[index], SeekOrigin.Begin);

                    if (binary)
                    {
                        var items = binlog.ReadMessageObjects(basestream, endoffset);

                        var answer = new DFLog.DFItem(dflog, items, (int)indexin);

                        return answer;
                    }
                    else
                    {
                        byte[] data = new byte[length];

                        basestream.Read(data, 0, length);

                        return dflog.GetDFItemFromLine(ASCIIEncoding.ASCII.GetString(data), (int)indexin);
                    }


                }
            }
        }

        public String this[int index]
        {
            get
            {
                long startoffset = linestartoffset[index];
                long endoffset = startoffset;

                if ((index + 1) >= linestartoffset.Count)
                {
                    endoffset = basestream.Length;
                }
                else
                {
                    endoffset = linestartoffset[index + 1];
                }

                int length = (int)(endoffset - startoffset);

                // prevent multi io to file
                lock (locker)
                {
                    // return cached value is same index
                    if (indexcachelineno == index)
                        return currentindexcache;

                    if (linestartoffset[index] != basestream.Position)
                        basestream.Seek(linestartoffset[index], SeekOrigin.Begin);

                    if (binary)
                    {
                        var answer = binlog.ReadMessage(basestream, basestream.Length);

                        currentindexcache = answer;
                        indexcachelineno = index;
                    }
                    else
                    {
                        byte[] data = new byte[length];

                        basestream.Read(data, 0, length);

                        currentindexcache = ASCIIEncoding.ASCII.GetString(data);
                        indexcachelineno = index;
                    }

                    return currentindexcache;
                }
            }
            set { throw new NotImplementedException(); }
        }

        public void Clear()
        {
            basestream.Close();
            basestream.Dispose();
            _count = 0;
            linestartoffset.Clear();
            nativeColumns?.Dispose();
            nativeColumns = null;
        }

        public int Count
        {
            get
            {
                if (_count > int.MaxValue) Console.WriteLine("log line count is too large");
                return (int)_count;
            }
        }

        public bool IsReadOnly
        {
            get { return true; }
        }

        public IEnumerable<DFLog.DFItem> GetEnumeratorTypeAll()
        {
            int position = 0; // state
            while (position < Count)
            {
                yield return this[(long)position];
                position++;
            }
        }

        public IEnumerable<DFLog.DFItem> GetEnumeratorType(string type)
        {
            return GetEnumeratorType(new string[] { type });
        }

        public IEnumerable<DFLog.DFItem> GetEnumeratorType(string[] types)
        {
            Dictionary<string, List<string>> instances = new Dictionary<string, List<string>>();

            types.ForEach(x =>
            {
                // match ACC[0] GPS[0] or ACC or GPS
                var m = Regex.Match(x, @"(\w+)(\[([0-9]+)\])?", RegexOptions.None);
                if (m.Success)
                {
                    if (!instances.ContainsKey(m.Groups[1].ToString()))
                        instances[m.Groups[1].ToString()] = new List<string>();

                    instances[m.Groups[1].ToString()].Add(m.Groups[3].Success ? m.Groups[3].ToString() : "");
                }
                else
                {
                    if (!instances.ContainsKey(x))
                        instances[x] = new List<string>();

                    instances[x].Add("");
                }

                // match ACC1  GYR1
                m = Regex.Match(x, @"(\w+)([0-9]+)$", RegexOptions.None);
                if (m.Success)
                {
                    if (!instances.ContainsKey(m.Groups[1].ToString()))
                        instances[m.Groups[1].ToString()] = new List<string>();

                    instances[m.Groups[1].ToString()]
                        .Add(m.Groups[2].Success ? (int.Parse(m.Groups[2].ToString()) - 1).ToString() : "");
                }
            });

            // get the ids for the passed in types
            List<long> slist = new List<long>();
            foreach (var type in instances.Keys)
            {
                if (dflog.logformat.ContainsKey(type))
                {
                    var typeid = (byte)dflog.logformat[type].Id;

                    foreach (var item in messageindexline[typeid])
                    {
                        slist.Add(item);
                    }
                }
            }

            if (types.Length > 1)
                slist.Sort();

            int progress = DateTime.Now.Second;
            // work through list of lines
            foreach (var l in slist)
            {
                if (DateTime.Now.Second != progress)
                {
                    Console.WriteLine(l);
                    progress = DateTime.Now.Second;
                }

                var ans = this[(long)l];
                if (!instances.ContainsKey(ans.msgtype))
                    continue;
                var inst = instances[ans.msgtype];
                // instance was requested, and its not a match
                //if (inst != "" && ans.instance != inst)
                if (!inst.Contains("") && !inst.Contains(ans.instance))
                    continue;
                yield return ans;
            }
        }

        public IEnumerator<String> GetEnumerator()
        {
            int position = 0; // state
            while (position < Count)
            {
                yield return this[position];
                position++;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        public void Dispose()
        {
            basestream.Close();
            linestartoffset.Clear();
            linestartoffset = null;
            messageindex = null;
            nativeColumns?.Dispose();
            nativeColumns = null;
            GC.Collect();
        }

        public bool EndOfStream
        {
            get { return (indexcachelineno >= (linestartoffset.Count - 1)); }
        }

        public List<string> SeenMessageTypes
        {
            get
            {
                List<string> messagetypes = new List<string>();

                for (int a = 0; a < messageindex.Length; a++)
                {
                    if (messageindex[a].Count > 0 && FMT.ContainsKey(a))
                        messagetypes.Add(FMT[a].Item2);
                }

                return messagetypes;
            }
        }

        public Tuple<string, double> GetUnit(string type, string header)
        {
            var answer = UnitMultiList.Where(tuple => tuple.Item1 == type && tuple.Item2 == header);

            if (answer.Count() == 0)
                return new Tuple<string, double>("", 1);

            return new Tuple<string, double>(answer.First().Item3, answer.First().Item4);
        }

        /// <summary>
        /// Label name of the instance column for <paramref name="type"/>
        /// (e.g. "I" for IMU), or null when the type has no instance column.
        /// </summary>
        public string GetInstanceFieldName(string type)
        {
            if (!dflog.logformat.ContainsKey(type))
                return null;

            var typeid = dflog.logformat[type].Id;
            if (!InstanceType.ContainsKey(typeid) || !FMT.ContainsKey(typeid))
                return null;

            var labels = FMT[typeid].columns.Split(',');
            var index = InstanceType[typeid].index;
            if (index < 0 || index >= labels.Length)
                return null;

            return labels[index].Trim();
        }

        /// <summary>
        /// The FMT format character for <paramref name="field"/> of
        /// <paramref name="type"/> (e.g. 'f' for a float, 'M' for a flight
        /// mode the managed decoder renders as text), or null when the type
        /// or field is unknown.
        /// </summary>
        public char? GetFieldFormatChar(string type, string field)
        {
            if (!dflog.logformat.ContainsKey(type))
                return null;

            var typeid = dflog.logformat[type].Id;
            if (!FMT.ContainsKey(typeid))
                return null;

            var labels = FMT[typeid].columns.Split(',');
            var format = FMT[typeid].format;
            for (int i = 0; i < labels.Length && i < format.Length; i++)
            {
                if (labels[i].Trim() == field)
                    return format[i];
            }

            return null;
        }

        public int getInstanceIndex(string type)
        {
            if (!dflog.logformat.ContainsKey(type))
                return -1;

            var typeno = dflog.logformat[type].Id;

            if (!FMTU.ContainsKey(typeno))
                return -1;

            var unittypes = FMTU[typeno].Item1;

            int colinst = unittypes.IndexOf("#") + 1;
            return colinst;
        }
    }
}
