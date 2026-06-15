namespace ShutterAutomation.Services;

/// <summary>
/// Calculates solar azimuth and elevation from latitude, longitude and UTC time.
/// Based on the NOAA solar calculation algorithm (SunCalc-style).
/// </summary>
public class SunPositionService
{
    public record SunPosition(double AzimuthDegrees, double ElevationDegrees);

    public SunPosition Calculate(double latitude, double longitude, DateTime utcTime)
    {
        // Julian date
        double julianDate = ToJulianDate(utcTime);
        double julianCentury = (julianDate - 2451545.0) / 36525.0;

        // Geometric mean longitude and anomaly of the sun
        double geomMeanLongSun = (280.46646 + julianCentury * (36000.76983 + julianCentury * 0.0003032)) % 360.0;
        double geomMeanAnomSun = 357.52911 + julianCentury * (35999.05029 - 0.0001537 * julianCentury);

        // Equation of center
        double geomMeanAnomSunRad = ToRadians(geomMeanAnomSun);
        double equationOfCenter = Math.Sin(geomMeanAnomSunRad) * (1.914602 - julianCentury * (0.004817 + 0.000014 * julianCentury))
                                + Math.Sin(2 * geomMeanAnomSunRad) * (0.019993 - 0.000101 * julianCentury)
                                + Math.Sin(3 * geomMeanAnomSunRad) * 0.000289;

        double sunTrueLong = geomMeanLongSun + equationOfCenter;

        // Apparent longitude
        double omega = 125.04 - 1934.136 * julianCentury;
        double sunAppLong = sunTrueLong - 0.00569 - 0.00478 * Math.Sin(ToRadians(omega));

        // Mean obliquity of the ecliptic
        double meanObliqEcliptic = 23.0 + (26.0 + (21.448 - julianCentury * (46.8150 + julianCentury * (0.00059 - julianCentury * 0.001813))) / 60.0) / 60.0;
        double obliqCorr = meanObliqEcliptic + 0.00256 * Math.Cos(ToRadians(omega));

        // Sun declination
        double sunDeclinRad = Math.Asin(Math.Sin(ToRadians(obliqCorr)) * Math.Sin(ToRadians(sunAppLong)));
        double sunDeclin = ToDegrees(sunDeclinRad);

        // Equation of time (minutes)
        double y = Math.Tan(ToRadians(obliqCorr / 2)) * Math.Tan(ToRadians(obliqCorr / 2));
        double eqOfTime = 4.0 * ToDegrees(
            y * Math.Sin(2 * ToRadians(geomMeanLongSun))
            - 2 * EccentricityEarthOrbit(julianCentury) * Math.Sin(ToRadians(geomMeanAnomSun))
            + 4 * EccentricityEarthOrbit(julianCentury) * y * Math.Sin(ToRadians(geomMeanAnomSun)) * Math.Cos(2 * ToRadians(geomMeanLongSun))
            - 0.5 * y * y * Math.Sin(4 * ToRadians(geomMeanLongSun))
            - 1.25 * EccentricityEarthOrbit(julianCentury) * EccentricityEarthOrbit(julianCentury) * Math.Sin(2 * ToRadians(geomMeanAnomSun))
        );

        // True solar time
        double trueSolarTimeMinutes = (utcTime.TimeOfDay.TotalMinutes + eqOfTime + 4.0 * longitude) % 1440.0;

        // Hour angle
        double hourAngle = trueSolarTimeMinutes < 0
            ? trueSolarTimeMinutes / 4.0 + 180.0
            : trueSolarTimeMinutes / 4.0 - 180.0;

        // Solar zenith angle
        double latRad = ToRadians(latitude);
        double declinRad = ToRadians(sunDeclin);
        double hourAngleRad = ToRadians(hourAngle);

        double cosZenith = Math.Sin(latRad) * Math.Sin(declinRad)
                         + Math.Cos(latRad) * Math.Cos(declinRad) * Math.Cos(hourAngleRad);
        double zenithDeg = ToDegrees(Math.Acos(Math.Clamp(cosZenith, -1.0, 1.0)));
        double elevationDeg = 90.0 - zenithDeg;

        // Atmospheric refraction correction
        elevationDeg += AtmosphericRefraction(elevationDeg);

        // Solar azimuth
        double azimuthDeg;
        double cosAzimuth = (Math.Sin(latRad) * cosZenith - Math.Sin(declinRad))
                           / (Math.Cos(latRad) * Math.Sin(ToRadians(zenithDeg)));
        cosAzimuth = Math.Clamp(cosAzimuth, -1.0, 1.0);

        if (hourAngle > 0)
            azimuthDeg = (ToDegrees(Math.Acos(cosAzimuth)) + 180.0) % 360.0;
        else
            azimuthDeg = (540.0 - ToDegrees(Math.Acos(cosAzimuth))) % 360.0;

        return new SunPosition(azimuthDeg, elevationDeg);
    }

    private static double EccentricityEarthOrbit(double julianCentury)
        => 0.016708634 - julianCentury * (0.000042037 + 0.0000001267 * julianCentury);

    private static double AtmosphericRefraction(double elevationDeg)
    {
        if (elevationDeg > 85.0) return 0;
        if (elevationDeg > 5.0)
            return 58.1 / Math.Tan(ToRadians(elevationDeg))
                   - 0.07 / Math.Pow(Math.Tan(ToRadians(elevationDeg)), 3)
                   + 0.000086 / Math.Pow(Math.Tan(ToRadians(elevationDeg)), 5);
        if (elevationDeg > -0.575)
            return 1735.0 + elevationDeg * (-518.2 + elevationDeg * (103.4 + elevationDeg * (-12.79 + elevationDeg * 0.711)));
        return -20.772 / Math.Tan(ToRadians(elevationDeg));
    }

    private static double ToJulianDate(DateTime utcTime)
    {
        int year = utcTime.Year;
        int month = utcTime.Month;
        int day = utcTime.Day;
        double fractionalDay = (utcTime.Hour + utcTime.Minute / 60.0 + utcTime.Second / 3600.0) / 24.0;

        if (month <= 2) { year--; month += 12; }
        int a = year / 100;
        int b = 2 - a + a / 4;

        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + fractionalDay + b - 1524.5;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
