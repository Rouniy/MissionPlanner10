using System;
using System.Configuration;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using log4net;
using SharpCompress.Compressors.Xz;

namespace MissionPlanner.Utilities
{
    public static class ParameterMetaDataRepositoryAPMpdef
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static Dictionary<string,XDocument> _parameterMetaDataXML = new Dictionary<string, XDocument>();

        private static XDocument _localParameterMetaDataXML;

        internal const string LocalParameterMetaDataFileName = "ParameterMetaDataLocal.xml";

        private static string[] vehicles = new[]
        {
             "SITL", "AP_Periph", "ArduSub", "Rover", "ArduCopter",
            "ArduPlane", "AntennaTracker", "Blimp", "Heli"      
        };

        private static string[] vehicles_versioned = new[] 
        {
            "Copter", "Plane", "Rover", "Sub", "Tracker"
        };

        static string url = "https://autotest.ardupilot.org/Parameters/{0}/apm.pdef.xml.gz";

        static string urlversioned = "https://autotest.ardupilot.org/Parameters/versioned/{0}/stable-{1}/apm.pdef.xml";

        static ParameterMetaDataRepositoryAPMpdef()
        {
            ReloadLocal();
            _ = GetMetaData();
        }

        private static void ReloadLocal()
        {
            var fileName = Path.Combine(Settings.GetRunningDirectory(), LocalParameterMetaDataFileName);

            try
            {
                if (File.Exists(fileName))
                    _localParameterMetaDataXML = XDocument.Load(fileName);
            }
            catch (Exception ex)
            {
                log.Error(fileName);
                log.Error(ex);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ParameterMetaDataRepository"/> class.
        /// </summary>
        public static void CheckLoad(string vehicle = "")
        {
            if (!_parameterMetaDataXML.ContainsKey(vehicle))
                Reload(vehicle);
        }

        public static async Task GetMetaDataVersioned(Version version)
        {
            List<Task> tlist = new List<Task>();

            vehicles_versioned.ForEach(a =>
            {
                try
                {
                    var newurl = String.Format(urlversioned, a, version.ToString());
                    var file = Path.Combine(Settings.GetDataDirectory(), a + version.ToString() + ".apm.pdef.xml");
                    if (File.Exists(file))
                        if (new FileInfo(file).LastWriteTime.AddDays(7) > DateTime.Now)
                            return;
                    var dltask = Download.getFilefromNetAsync(newurl, file);
                    tlist.Add(dltask);
                }
                catch (Exception ex) { log.Error(ex); }
            });

            await Task.WhenAll(tlist);

            vehicles_versioned.ForEach(a =>
            {
                try
                {
                    Reload(a + version.ToString());

                    var veh = vehicles.First(b => b.Contains(a));

                    if(_parameterMetaDataXML.ContainsKey(a + version.ToString()))
                        _parameterMetaDataXML[veh] = _parameterMetaDataXML[a + version.ToString()];
                }
                catch (Exception ex) { log.Error(ex); }
            });
        }

        public static async Task GetMetaData(bool force = false)
        {
            List<Task> tlist = new List<Task>();

            vehicles.ForEach(a =>
            {
                try
                {
                    var newurl = String.Format(url, a);
                    // try the gzipped version first
                    var file = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml.gz");
                    if(File.Exists(file))
                        if (new FileInfo(file).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                    // try just the xml
                    var file2 = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml");
                    if (File.Exists(file2))
                        if (new FileInfo(file2).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                    var dltask = Download.getFilefromNetAsync(newurl, file);
                    tlist.Add(dltask);
                }
                catch (Exception ex) { log.Error(ex); }
            });

            await Task.WhenAll(tlist);

            vehicles.ForEach(a =>
            {
                try
                {
                    var fileout = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml");
                    var fileouttemp = Path.Combine(Path.GetTempFileName());
                    var file = Path.Combine(Settings.GetDataDirectory(), a + ".apm.pdef.xml.gz");
                    if (File.Exists(file))
                    {
                        // drop out to prevent unnessary fileio at startup
                        if (File.Exists(fileout) && new FileInfo(fileout).LastWriteTime.AddDays(7) > DateTime.Now && !force)
                            return;
                        using (var read = File.OpenRead(file))
                        {
                            //if (XZStream.IsXZStream(read))
                            {
                                read.Position = 0;
                                var stream = new GZipStream(read, CompressionMode.Decompress);
                                //var stream = new XZStream(read);
                                using (var outst = File.Open(fileouttemp, FileMode.Create))
                                {
                                    stream.CopyTo(outst);
                                }
                                // move after good decompress
                                File.Delete(fileout);
                                File.Move(fileouttemp, fileout);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            });

            Reset();
        }

        public static void Reset()
        {
            _parameterMetaDataXML.Clear();
        }

        public static void Reload(string vehicle = "")
        {
            string paramMetaDataXMLFileName =
                String.Format("{0}{1}", Settings.GetDataDirectory(), vehicle + ".apm.pdef.xml");

            try
            {
                if (File.Exists(paramMetaDataXMLFileName))
                {
                    _parameterMetaDataXML[vehicle] = XDocument.Load(paramMetaDataXMLFileName);
                }

            }
            catch (System.Xml.XmlException ex) 
            {
                try
                {
                    if (File.Exists(paramMetaDataXMLFileName))
                        File.Delete(paramMetaDataXMLFileName);
                }
                catch { }
                log.Error(paramMetaDataXMLFileName);
                log.Error(ex);
            }
            catch (Exception ex)
            {
                log.Error(paramMetaDataXMLFileName);
                log.Error(ex);
            }
        }

        /// <summary>
        /// Gets the parameter meta data.
        /// </summary>
        /// <param name="nodeKey">The node key.</param>
        /// <param name="metaKey">The meta key.</param>
        /// <returns></returns>
        public static string GetParameterMetaData(string nodeKey, string metaKey, string vechileType)
        {
            // remap names
            if (vechileType == "ArduCopter2")
                vechileType = "ArduCopter";
            if (vechileType == "ArduRover")
                vechileType = "Rover";
            if (vechileType == "ArduTracker")
                vechileType = "AntennaTracker";

            CheckLoad(vechileType);

            // remap keys
            if (metaKey == ParameterMetaDataConstants.DisplayName)
                metaKey = "humanName";
            if (metaKey == ParameterMetaDataConstants.Description)
                metaKey = "documentation";
            if (metaKey == ParameterMetaDataConstants.User)
                metaKey = "user";

            _parameterMetaDataXML.TryGetValue(vechileType, out var downloadedParameterMetaData);

            try
            {
                return ParameterMetaDataPdefReader.ResolveParameterMetaData(
                    _localParameterMetaDataXML,
                    downloadedParameterMetaData,
                    nodeKey,
                    metaKey,
                    vechileType);
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }

            return string.Empty;
        }
    }

    internal static class ParameterMetaDataPdefReader
    {
        internal static string ResolveParameterMetaData(
            XDocument localParameterMetaData,
            XDocument downloadedParameterMetaData,
            string nodeKey,
            string metaKey,
            string vehicleType)
        {
            var localAnswer = ReadParameterMetaData(
                localParameterMetaData, nodeKey, metaKey, vehicleType);
            return localAnswer != string.Empty
                ? localAnswer
                : ReadParameterMetaData(downloadedParameterMetaData, nodeKey, metaKey, vehicleType);
        }

        // One holder per document. The table holds its keys weakly, so an index goes away with its
        // document when Reset, Reload or a versioned download replaces the document.
        private static readonly ConditionalWeakTable<XDocument, PdefIndexHolder> _indexes =
            new ConditionalWeakTable<XDocument, PdefIndexHolder>();

        private static readonly ConditionalWeakTable<XDocument, PdefIndexHolder>.CreateValueCallback
            _createHolder = document => new PdefIndexHolder(document);

        private static readonly List<IndexedParam> _noParams = new List<IndexedParam>(0);

        // Builds the index of one document on first use and drops it when the document changes.
        // Production documents are not edited after load; this keeps the index honest if one is.
        private sealed class PdefIndexHolder
        {
            private readonly object _lock = new object();
            private readonly XDocument _document;
            private bool _subscribed;
            private PdefIndex _index;

            public PdefIndexHolder(XDocument document)
            {
                _document = document;
            }

            public PdefIndex GetIndex()
            {
                lock (_lock)
                {
                    // The table can construct a holder on two threads and keep only one, so the
                    // subscription is made here, by the holder that was kept, and only once.
                    if (!_subscribed)
                    {
                        _document.Changed += OnDocumentChanged;
                        _subscribed = true;
                    }

                    if (_index == null)
                    {
                        _index = new PdefIndex(_document);
                    }

                    return _index;
                }
            }

            private void OnDocumentChanged(object sender, XObjectChangeEventArgs e)
            {
                lock (_lock)
                {
                    _index = null;
                }
            }
        }

        private readonly struct IndexedParam
        {
            public IndexedParam(int ordinal, XElement element)
            {
                Ordinal = ordinal;
                Element = element;
            }

            public int Ordinal { get; }

            public XElement Element { get; }
        }

        // Maps the name attribute to the elements that carry it, in document order. A document
        // holds a few thousand param elements and a parameter page asks for thousands of
        // name and key combinations, most of which do not exist, so scanning per lookup is
        // quadratic.
        private sealed class PdefIndex
        {
            private readonly Dictionary<string, List<IndexedParam>> _byName =
                new Dictionary<string, List<IndexedParam>>();

            private readonly List<IndexedParam> _unnamed = new List<IndexedParam>();

            public PdefIndex(XDocument document)
            {
                XElement root = document.Element("paramfile");
                if (root == null)
                {
                    return;
                }

                int ordinal = 0;
                foreach (XElement param in root.Elements()
                             .SelectMany(section => section.Elements())
                             .Where(parameters => parameters.HasAttributes)
                             .SelectMany(parameters => parameters.Elements()))
                {
                    string name = param.Attribute("name")?.Value;
                    List<IndexedParam> list;
                    if (name == null)
                    {
                        list = _unnamed;
                    }
                    else if (!_byName.TryGetValue(name, out list))
                    {
                        list = new List<IndexedParam>(1);
                        _byName.Add(name, list);
                    }

                    list.Add(new IndexedParam(ordinal++, param));
                }
            }

            // Returns the elements named vehicleKey or nodeKey, in document order. A null nodeKey
            // matches elements without a name attribute, as the comparison in the scan did.
            public List<IndexedParam> Find(string vehicleKey, string nodeKey)
            {
                _byName.TryGetValue(vehicleKey, out List<IndexedParam> scoped);

                List<IndexedParam> unscoped = _unnamed;
                if (nodeKey != null)
                {
                    _byName.TryGetValue(nodeKey, out unscoped);
                }

                if (scoped == null || scoped.Count == 0)
                {
                    return unscoped ?? _noParams;
                }

                if (unscoped == null || unscoped.Count == 0)
                {
                    return scoped;
                }

                var merged = new List<IndexedParam>(scoped.Count + unscoped.Count);
                int scopedIndex = 0;
                int unscopedIndex = 0;
                while (scopedIndex < scoped.Count && unscopedIndex < unscoped.Count)
                {
                    merged.Add(scoped[scopedIndex].Ordinal < unscoped[unscopedIndex].Ordinal
                        ? scoped[scopedIndex++]
                        : unscoped[unscopedIndex++]);
                }

                while (scopedIndex < scoped.Count)
                {
                    merged.Add(scoped[scopedIndex++]);
                }

                while (unscopedIndex < unscoped.Count)
                {
                    merged.Add(unscoped[unscopedIndex++]);
                }

                return merged;
            }
        }

        private static string ReadParameterMetaData(
            XDocument parameterMetaData,
            string nodeKey,
            string metaKey,
            string vehicleType)
        {
            if (parameterMetaData == null)
            {
                return string.Empty;
            }

            PdefIndex index = _indexes.GetValue(parameterMetaData, _createHolder).GetIndex();
            foreach (IndexedParam indexedParam in index.Find(vehicleType + ":" + nodeKey, nodeKey))
            {
                XElement param = indexedParam.Element;

                var attribute = param.Attribute(metaKey);
                if (attribute != null)
                    return attribute.Value;

                if (metaKey == ParameterMetaDataConstants.Values)
                {
                    return string.Join(",", param.Elements("values")
                        .Elements("value")
                        .Select(value => $"{value.Attribute("code")?.Value}:{value.Value}"));
                }

                var field = param.Elements("field")
                    .FirstOrDefault(element => element.Attribute("name")?.Value == metaKey);
                if (field != null)
                    return field.Value;
            }

            return string.Empty;
        }
    }
}
