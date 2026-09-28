using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GeoAPI.CoordinateSystems;
using GeoAPI.CoordinateSystems.Transformations;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace MissionPlanner.Utilities
{
    public struct utmpos
    {
        public static readonly utmpos Zero;
        public double x;
        public double y;
        public int zone;
        public object Tag;

        static CoordinateTransformationFactory ctfac = new CoordinateTransformationFactory();
        static IGeographicCoordinateSystem wgs84 = GeographicCoordinateSystem.WGS84;

        public utmpos(double x, double y, int zone)
        {
            this.x = x;
            this.y = y;
            this.zone = zone;
            this.Tag = null;
        }

        public utmpos(utmpos pos)
        {
            this.x = pos.x;
            this.y = pos.y;
            this.zone = pos.zone;
            this.Tag = null;
        }

        public utmpos(PointLatLngAlt pos)
        {
            double[] dd = pos.ToUTM();
            this.x = dd[0];
            this.y = dd[1];
            this.zone = pos.GetUTMZone();
            this.Tag = null;
        }

        public static implicit operator double[](utmpos a)
        {
            return new double[] { a.x, a.y };
        }

        public static implicit operator PointLatLngAlt(utmpos a)
        {
            return a.ToLLA();
        }

        public PointLatLngAlt ToLLA2()
        {
            GeoUtility.GeoSystem.UTM utm = new GeoUtility.GeoSystem.UTM(Math.Abs(zone), x, y, zone < 0 ? GeoUtility.GeoSystem.Base.Geocentric.Hemisphere.South : GeoUtility.GeoSystem.Base.Geocentric.Hemisphere.North);

            PointLatLngAlt ans = ((GeoUtility.GeoSystem.Geographic)utm);
            if (this.Tag != null)
                ans.Tag = this.Tag.ToString();

            return ans;
        }

        private const int MaxCachedZone = 60;

        private static readonly object _inverseTransformsLock = new object();
        private static readonly Dictionary<int, IMathTransform> _inverseTransforms =
            new Dictionary<int, IMathTransform>();

        internal static int CachedTransformCount
        {
            get
            {
                lock (_inverseTransformsLock)
                {
                    return _inverseTransforms.Count;
                }
            }
        }

        private static IMathTransform CreateInverseTransform(int zone)
        {
            IProjectedCoordinateSystem utm =
                ProjectedCoordinateSystem.WGS84_UTM(Math.Abs(zone), zone >= 0);

            return ctfac.CreateFromCoordinateSystems(wgs84, utm).MathTransform.Inverse();
        }

        // The signed zone carries the hemisphere. A cached transform is safe to share between
        // threads because Transform only reads immutable state; the lock guards the dictionary.
        // WGS84_UTM accepts any zone number, so zones outside the real range are converted
        // without caching to keep the cache bounded when the zone comes from user input.
        private static IMathTransform GetInverseTransform(int zone)
        {
            if (zone < -MaxCachedZone || zone > MaxCachedZone)
            {
                return CreateInverseTransform(zone);
            }

            lock (_inverseTransformsLock)
            {
                if (!_inverseTransforms.TryGetValue(zone, out IMathTransform inverse))
                {
                    inverse = CreateInverseTransform(zone);
                    _inverseTransforms[zone] = inverse;
                }

                return inverse;
            }
        }

        public PointLatLngAlt ToLLA()
        {
            // get leader utm coords
            double[] pll = GetInverseTransform(zone).Transform(this);

            PointLatLngAlt ans = new PointLatLngAlt(pll[1], pll[0]);
            if (this.Tag != null)
                ans.Tag = this.Tag.ToString();

            return ans;
        }

        public static List<utmpos> ToList(List<double[]> input, int zone)
        {
            List<utmpos> data = new List<utmpos>();

            input.ForEach(x => { data.Add(new utmpos(x[0], x[1], zone)); });

            return data;
        }

        public double GetDistance(utmpos b)
        {
            return Math.Sqrt(Math.Pow(Math.Abs(x - b.x), 2) + Math.Pow(Math.Abs(y - b.y), 2));
        }

        public double GetBearing(utmpos b)
        {
            var y = b.y - this.y;
            var x = b.x - this.x;

            return (MathHelper.rad2deg * (Math.Atan2(x, y)) + 360) % 360;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is utmpos))
            {
                return false;
            }
            return (((((utmpos)obj).x == this.x) && (((utmpos)obj).y == this.y)) && obj.GetType().Equals(base.GetType()));
        }

        public static bool operator ==(utmpos left, utmpos right)
        {
            return ((left.x == right.x) && (left.y == right.y) && (left.zone == right.zone));
        }

        public static bool operator !=(utmpos left, utmpos right)
        {
            return !(left == right);
        }

        public override string ToString()
        {
            return "utmpos: " + x + "," + y;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = x.GetHashCode();
                hashCode = (hashCode * 397) ^ y.GetHashCode();
                hashCode = (hashCode * 397) ^ zone;
                return hashCode;
            }
        }

        public bool IsZero { get { if (this == Zero) return true; return false; } }
    }

}
