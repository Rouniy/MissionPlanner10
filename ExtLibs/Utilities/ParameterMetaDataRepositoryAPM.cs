using System;
using System.Configuration;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using log4net;

namespace MissionPlanner.Utilities
{
    public static class ParameterMetaDataRepositoryAPM
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static XDocument _parameterMetaDataXML;

        private static volatile MetaDataIndex _index;

        /// <summary>
        /// Initializes a new instance of the <see cref="ParameterMetaDataRepository"/> class.
        /// </summary>
        public static void CheckLoad()
        {
            if (_parameterMetaDataXML == null)
                Reload();
        }

        public static void Reload()
        {
            string paramMetaDataXMLFileName = String.Format("{0}{1}", Settings.GetUserDataDirectory(), "ParameterMetaData.xml");

            string paramMetaDataXMLFileNameBackup = String.Format("{0}{1}{2}", Settings.GetRunningDirectory(),
                Path.DirectorySeparatorChar, "ParameterMetaDataBackup.xml");

            try
            {
                log.Debug(paramMetaDataXMLFileName);

                if (File.Exists(paramMetaDataXMLFileName))
                    _parameterMetaDataXML = XDocument.Load(paramMetaDataXMLFileName);

            }
            catch (Exception ex)
            {
                log.Error(ex);
            }

            try
            {
                log.Debug(paramMetaDataXMLFileNameBackup);
                // error loading the good file, load the backup
                if (File.Exists(paramMetaDataXMLFileNameBackup) && _parameterMetaDataXML == null)
                {
                    _parameterMetaDataXML = XDocument.Load(paramMetaDataXMLFileNameBackup);
                    Console.WriteLine("Using backup param data");
                }
            }
            catch
            {
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
            CheckLoad();

            XDocument document = _parameterMetaDataXML;
            if (document != null)
            {
                // Use this to find the endpoint node we are looking for
                // Either it will be pulled from a file in the ArduPlane hierarchy or the ArduCopter hierarchy
                try
                {
                    MetaDataIndex index = _index;
                    if (index == null || !ReferenceEquals(index.Document, document))
                    {
                        index = new MetaDataIndex(document);
                        _index = index;
                    }

                    return index.Find(nodeKey, metaKey, vechileType);
                }
                catch
                {
                } // Exception System.ArgumentException: '' is an invalid expanded name.
            }

            return string.Empty;
        }

        // Lookup table for one loaded document, rebuilt when Reload installs another. A vehicle
        // element has thousands of parameter children and XContainer.Element scans them linearly,
        // which made every lookup proportional to the size of the file.
        internal sealed class MetaDataIndex
        {
            // Vehicle element name to one name-to-parameter map per vehicle element, in
            // document order.
            private readonly Dictionary<XName, List<Dictionary<XName, XElement>>> _vehicles =
                new Dictionary<XName, List<Dictionary<XName, XElement>>>();

            public MetaDataIndex(XDocument document)
            {
                Document = document;

                XElement root = document.Element("Params");
                if (root == null)
                {
                    return;
                }

                foreach (XElement vehicle in root.Elements())
                {
                    if (!_vehicles.TryGetValue(
                            vehicle.Name, out List<Dictionary<XName, XElement>> vehicles))
                    {
                        vehicles = new List<Dictionary<XName, XElement>>(1);
                        _vehicles.Add(vehicle.Name, vehicles);
                    }

                    // Only the first child with a given name was ever consulted.
                    var parameters = new Dictionary<XName, XElement>();
                    foreach (XElement parameter in vehicle.Elements())
                    {
                        if (!parameters.ContainsKey(parameter.Name))
                        {
                            parameters.Add(parameter.Name, parameter);
                        }
                    }

                    vehicles.Add(parameters);
                }
            }

            public XDocument Document { get; }

            // Throws for a key that is not a valid XML name, as the XContainer lookups did; the
            // caller turns that into an empty answer.
            public string Find(string nodeKey, string metaKey, string vehicleType)
            {
                XName vehicleName = vehicleType;
                XName nodeName = nodeKey;
                XName metaName = metaKey;
                if (vehicleName == null || nodeName == null || metaName == null)
                {
                    return string.Empty;
                }

                if (_vehicles.TryGetValue(
                        vehicleName, out List<Dictionary<XName, XElement>> vehicles))
                {
                    foreach (Dictionary<XName, XElement> parameters in vehicles)
                    {
                        if (parameters.TryGetValue(nodeName, out XElement node) && node.HasElements)
                        {
                            XElement metaValue = node.Element(metaName);
                            if (metaValue != null)
                            {
                                return metaValue.Value;
                            }
                        }
                    }
                }

                return string.Empty;
            }
        }
    }
}