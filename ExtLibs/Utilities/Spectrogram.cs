using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using SharpKml.Dom;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static MissionPlanner.Log.LogOutput;

namespace MissionPlanner.Utilities
{
    public class Spectrogram
    {
        private static readonly log4net.ILog log =
            log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static MemoryCache cache = new MemoryCache(new MemoryCacheOptions() { });

        public static Image<Rgba32> GenerateImage(DFLogBuffer cb, out double[] freqtout,
            out List<(double timeus, double[] value)> allfftdata, string type = "ACC1", string field = "AccX",
            string timeus = "TimeUS", int min = -80, int max = -20)
        {
            Console.WriteLine("GenerateImage " + type + " " + field);
            allfftdata = new List<(double timeus, double[] value)>();

            if (cb.SeenMessageTypes.Contains("ISBH"))
            {
                Console.WriteLine("GenerateImage seen ISBH");
                int sensorno = (type.Contains("1") ? 0 : (type.Contains("2") ? 1 : 2));
                int sensor = type.Contains("ACC") ? 0 : 1;

                (double, (double, double)[]) cachedata;

                double sample_rate = -1;

                (double time, double d)[] data = new (double time, double d)[0];
                if (!cache.TryGetValue(cb.GetHashCode() + sensorno + "" + sensor, out cachedata))
                {
                    int Ns = -1;
                    int type1 = -1;
                    int instance = -1;
                    double multiplier = -1;

                    // typed fast path via the native dflog columns
                    if (TryBatchDataNative(cb, sensorno, sensor, field, out var nativerate, out var nativedata))
                    {
                        sample_rate = nativerate;
                        data = nativedata;
                    }
                    else
                    {
                    data = cb.GetEnumeratorType(new string[] { "ISBH", "ISBD" })
                        .SelectMany(
                            item =>
                            {
                                if (item.msgtype == "ISBH")
                                {
                                    Ns = int.Parse(item.items[cb.dflog.FindMessageOffset(item.msgtype, "N")],
                                        CultureInfo.InvariantCulture);
                                    type1 = int.Parse(item.items[cb.dflog.FindMessageOffset(item.msgtype, "type")],
                                        CultureInfo.InvariantCulture);
                                    instance = int.Parse(
                                        item.items[cb.dflog.FindMessageOffset(item.msgtype, "instance")],
                                        CultureInfo.InvariantCulture);

                                    if (instance != sensorno || type1 != sensor)
                                        return new (double time, double d)[0];

                                    sample_rate = double.Parse(
                                        item.items[cb.dflog.FindMessageOffset(item.msgtype, "smp_rate")],
                                        CultureInfo.InvariantCulture);

                                    multiplier = double.Parse(
                                        item.items[cb.dflog.FindMessageOffset(item.msgtype, "mul")],
                                        CultureInfo.InvariantCulture);
                                }
                                else if (item.msgtype.StartsWith("ISBD"))
                                {
                                    var Nsdata = int.Parse(item.items[cb.dflog.FindMessageOffset(item.msgtype, "N")],
                                        CultureInfo.InvariantCulture);

                                    if (Ns != Nsdata)
                                        return new (double time, double d)[0];

                                    if (instance != sensorno || type1 != sensor)
                                        return new (double time, double d)[0];

                                    //int offsetX = cb.dflog.FindMessageOffset(item.msgtype, "x");
                                    //int offsetY = cb.dflog.FindMessageOffset(item.msgtype, "y");
                                    //int offsetZ = cb.dflog.FindMessageOffset(item.msgtype, "z");
                                    int offsetTime = cb.dflog.FindMessageOffset(item.msgtype, "TimeUS");

                                    double time = double.Parse(item.items[offsetTime], CultureInfo.InvariantCulture);

                                    var ua = (BinaryLog.UnionArray)item.GetRaw(field.ToLower()
                                        .Substring(field.Length - 1));

                                    return ua.Shorts.Select(ds =>
                                        {
                                            double d = ((double)ds / multiplier);
                                            return (time, d);
                                        })
                                        .ToArray();
                                }

                                return new (double time, double d)[0];
                            }).ToArray();
                    }
                    cache.Set(cb.GetHashCode() + sensorno + "" + sensor, (sample_rate, data),
                        new MemoryCacheEntryOptions()
                        {
                            AbsoluteExpirationRelativeToNow = System.TimeSpan.FromMinutes(10)
                        });
                }
                else 
                {
                    sample_rate = cachedata.Item1;
                    data = cachedata.Item2;                
                }


                var bins = 10;
                int N = 1 << bins;

                var fft = new FFT2();

                var freqt = fft.FreqTable(N, (int)sample_rate);
                
                // time , freq , [color] &freqcount

                int totalsamples = data.Count();
                int count = totalsamples / N;
                int done = 0;
                // batch sampling is non continuous
                int divisor = 1;
                count *= divisor;
                var img = new Image<Rgba32>(count, freqt.Length);
                log.Debug("done and count ");

                foreach (var fftdata in data.Windowed(N, divisor))
                {
                    if (fftdata.Count() < N)
                    {
                        break;
                    }

                    var timeusvalue = double.MaxValue;
                    var data1 = new double[N];
                    int c = 0;
                    foreach (var a in fftdata)
                    {
                        timeusvalue = Math.Min(timeusvalue, a.time);
                        data1[c++] = a.d;
                    }

                    var fftanswerz = fft.rin(data1, (uint)bins);

                    allfftdata.Add((timeusvalue, fftanswerz));

                    var i = 0;
                    foreach (var y in freqt)
                    {
                        img[done, (freqt.Length - 1) - i] = GetColor(fftanswerz[i], min, max);
                        i++;
                    }

                    count--;
                    done++;
                }

                freqtout = freqt;

                return img;
            }

            {
                var bins = 10;
                int N = 1 << bins;

                var fft = new FFT2();

                DFLog.DFItem[] acc1data = null;
                if (!cache.TryGetValue(cb.GetHashCode() + type, out acc1data))
                {
                    acc1data = cb.GetEnumeratorType(type).ToArray();
                    cache.Set(cb.GetHashCode() + type, acc1data,
                        new MemoryCacheEntryOptions()
                        {
                            AbsoluteExpirationRelativeToNow = System.TimeSpan.FromMinutes(10)
                        });
                }
                log.Debug(type);
                if (cb.dflog.FindMessageOffset(acc1data[0].msgtype, "TimeUS") == -1)
                    timeus = "SampleUS";

                int totalsamples = acc1data.Count();
                int count = totalsamples / N;
                int done = 0;
                double timedelta = 0;
                // 50% overlap
                int divisor = 4;
                if (count > 2048)
                    divisor = 1;
                count *= divisor;
                Console.WriteLine("Image Size " + count + " " + N / 2);
                var img = new Image<Rgba32>(count, N / 2);
                log.Debug("done and count ");



                foreach (var fftdata in acc1data.Windowed(N, divisor))
                {
                    if (fftdata.Count() < N)
                    {
                        break;
                    }

                    var timeusvalue = double.MaxValue;
                    var timeusvalueend = double.MinValue;
                    var data = new double[N];
                    int c = 0;
                    foreach (var a in fftdata)
                    {
                        var time = Convert.ToDouble(a.GetRaw(timeus));
                        timeusvalue = Math.Min(timeusvalue, time);
                        timeusvalueend = Math.Max(timeusvalueend, time);
                        data[c++] = Convert.ToDouble(a.GetRaw(field));
                    }

                    timedelta = timedelta * 0.99 + ((timeusvalueend - timeusvalue)) * 0.01;

                    var fftanswerz = fft.rin(data, (uint)bins);

                    allfftdata.Add((timeusvalue, fftanswerz));

                    var i = 0;
                    foreach (var y in Enumerable.Range(0, N/2))
                    {
                        img[done, ((N / 2) - 1) - i] = GetColor(fftanswerz[i], min, max);
                        i++;
                    }

                    count--;
                    done++;
                }

                // 1s / (1/ (N/delta))
                var sample_rate = (int)(1000000 / (1.0 / (N / (timedelta))));
                freqtout = fft.FreqTable(N, sample_rate);

                return img;
            }
        }

        /// <summary>
        /// Typed fast path for the ISBH/ISBD batch extraction: numeric and
        /// array columns merged by line number, replaying the header state
        /// machine exactly like the legacy enumerator pass. Returns false
        /// (caller runs the legacy path) when the native path is off or the
        /// columns cannot be fetched.
        /// </summary>
        static bool TryBatchDataNative(DFLogBuffer cb, int sensorno, int sensor, string field,
            out double sampleRate, out (double time, double d)[] data)
        {
            sampleRate = -1;
            data = null;

            // legacy derives the ISBD array field from the last letter, e.g. AccX -> x
            var arrayField = field.ToLower().Substring(field.Length - 1);

            if (!cb.TryGetColumnsNative("ISBH",
                    new[] { "N", "type", "instance", "smp_rate", "mul" },
                    out var hdrLines, out var hdr))
                return false;

            if (!cb.TryGetColumnsNative("ISBD", new[] { "N", "TimeUS" }, out var smpLines, out var smp) ||
                !cb.TryGetArrayColumnNative("ISBD", arrayField, out var arrLines, out var samples))
                return false;

            // the numeric and array queries are indexed by the same row number
            // below, so they must describe the same rows
            if (arrLines.Length != smpLines.Length)
                return false;

            var list = new List<(double time, double d)>(samples.Length * 32);

            int Ns = -1, type1 = -1, instance = -1;
            double multiplier = -1;

            int h = 0, s = 0;
            while (h < hdrLines.Length || s < smpLines.Length)
            {
                var takeHeader = s >= smpLines.Length || (h < hdrLines.Length && hdrLines[h] <= smpLines[s]);
                if (takeHeader)
                {
                    Ns = (int)hdr[0][h];
                    type1 = (int)hdr[1][h];
                    instance = (int)hdr[2][h];
                    if (instance == sensorno && type1 == sensor)
                    {
                        sampleRate = hdr[3][h];
                        multiplier = hdr[4][h];
                    }

                    h++;
                }
                else
                {
                    if ((int)smp[0][s] == Ns && instance == sensorno && type1 == sensor)
                    {
                        var time = smp[1][s];
                        foreach (var sample in samples[s])
                            list.Add((time, sample / multiplier));
                    }

                    s++;
                }
            }

            data = list.ToArray();
            return true;
        }

        static double SCALE = 20 / Math.Log(10);

        static Rgba32 GetRainbowColor(byte i)
        {
            return HSL2RGB(i / 255.0, 0.5, 0.5);
        }

        public static Rgba32 HSL2RGB(double h, double sl, double l)
        {
            double v;
            double r, g, b;

            r = l; // default to gray
            g = l;
            b = l;
            v = (l <= 0.5) ? (l * (1.0 + sl)) : (l + sl - l * sl);
            if (v > 0)
            {
                double m;
                double sv;
                int sextant;
                double fract, vsf, mid1, mid2;

                m = l + l - v;
                sv = (v - m) / v;
                h *= 6.0;
                sextant = (int) h;
                fract = h - sextant;
                vsf = v * sv * fract;
                mid1 = m + vsf;
                mid2 = v - vsf;
                switch (sextant)
                {
                    case 0:
                        r = v;
                        g = mid1;
                        b = m;
                        break;
                    case 1:
                        r = mid2;
                        g = v;
                        b = m;
                        break;
                    case 2:
                        r = m;
                        g = v;
                        b = mid1;
                        break;
                    case 3:
                        r = m;
                        g = mid2;
                        b = v;
                        break;
                    case 4:
                        r = mid1;
                        g = m;
                        b = v;
                        break;
                    case 5:
                        r = v;
                        g = m;
                        b = mid2;
                        break;
                }
            }
            Rgba32 rgb = new Rgba32(0, 0, 0);
            rgb.R = Convert.ToByte(r * 255);
            rgb.G = Convert.ToByte(g * 255);
            rgb.B = Convert.ToByte(b * 255);
            return rgb;
        }

        static Rgba32 GetColor(double actualValue, int min = -80, int max = -20)
        {
            var scale = actualValue;// SCALE * Math.Log(actualValue + double.Epsilon);

            scale = MathHelper.mapConstrained(scale, min, max, 0, 255);

            return GetRainbowColor((byte) (255-scale));
        }
    }
}
