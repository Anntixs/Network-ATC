using NetworkAtc.Plugins;

namespace NetworkAtc.Core.Geo;

public static class GeoMath
{
    public const double EarthRadiusNm = 3440.065;
    private const double Rad = Math.PI / 180;

    public static double DistanceNm(GeoPoint a, GeoPoint b)
    {
        double dLat = (b.Latitude - a.Latitude) * Rad, dLon = (b.Longitude - a.Longitude) * Rad;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(a.Latitude * Rad) * Math.Cos(b.Latitude * Rad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Sqrt(Math.Min(1, h)));
    }

    /// <summary>Initial true bearing from a to b, degrees 0..360.</summary>
    public static double BearingDeg(GeoPoint a, GeoPoint b)
    {
        double lat1 = a.Latitude * Rad, lat2 = b.Latitude * Rad, dLon = (b.Longitude - a.Longitude) * Rad;
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) / Rad + 360) % 360;
    }

    /// <summary>Point at a distance and true bearing from a start point.</summary>
    public static GeoPoint Offset(GeoPoint from, double bearingDeg, double distanceNm)
    {
        double d = distanceNm / EarthRadiusNm, brg = bearingDeg * Rad;
        double lat1 = from.Latitude * Rad, lon1 = from.Longitude * Rad;
        double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(brg));
        double lon2 = lon1 + Math.Atan2(Math.Sin(brg) * Math.Sin(d) * Math.Cos(lat1), Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return new GeoPoint(lat2 / Rad, ((lon2 / Rad + 540) % 360) - 180);
    }
}

/// <summary>
/// Maps geographic coordinates to a flat plane in nautical miles around a center point
/// (azimuthal equidistant projection, accurate for the few hundred miles a radar shows).
/// x grows to the east, y grows to the north.
/// </summary>
public sealed class Projection(GeoPoint center)
{
    private const double Rad = Math.PI / 180;
    private readonly double _sinLat0 = Math.Sin(center.Latitude * Rad);
    private readonly double _cosLat0 = Math.Cos(center.Latitude * Rad);

    public GeoPoint Center { get; } = center;

    public (double X, double Y) ToPlane(GeoPoint p)
    {
        double lat = p.Latitude * Rad, dLon = (p.Longitude - Center.Longitude) * Rad;
        double cosC = _sinLat0 * Math.Sin(lat) + _cosLat0 * Math.Cos(lat) * Math.Cos(dLon);
        cosC = Math.Clamp(cosC, -1, 1);
        double c = Math.Acos(cosC);
        double k = c < 1e-12 ? 1 : c / Math.Sin(c);
        double x = k * Math.Cos(lat) * Math.Sin(dLon);
        double y = k * (_cosLat0 * Math.Sin(lat) - _sinLat0 * Math.Cos(lat) * Math.Cos(dLon));
        return (x * GeoMath.EarthRadiusNm, y * GeoMath.EarthRadiusNm);
    }

    public GeoPoint FromPlane(double xNm, double yNm)
    {
        double x = xNm / GeoMath.EarthRadiusNm, y = yNm / GeoMath.EarthRadiusNm;
        double c = Math.Sqrt(x * x + y * y);
        if (c < 1e-12) return Center;
        double lat = Math.Asin(Math.Cos(c) * _sinLat0 + y * Math.Sin(c) * _cosLat0 / c);
        double lon = Center.Longitude * Rad + Math.Atan2(x * Math.Sin(c), c * _cosLat0 * Math.Cos(c) - y * _sinLat0 * Math.Sin(c));
        return new GeoPoint(lat / Rad, ((lon / Rad + 540) % 360) - 180);
    }
}
