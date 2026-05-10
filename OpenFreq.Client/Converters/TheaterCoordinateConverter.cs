using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using DotSpatial.Projections;

namespace OpenFreq.Utilities
{
    public record TheaterDefinition(string Name, string ProjString, double CenterLat, double CenterLon, string? HeightmapPath = null);

    public static partial class TheaterCoordinateConverter
    {
        private const int HEIGHTMAP_SIZE_M = 1024 * 1000;

        private static readonly Dictionary<string, Theater> Theaters = new()
        {
            ["Korea KTO"] = new Theater(
                "Korea KTO",
                "+proj=tmerc +lon_0=127.5 +ellps=WGS84 +k=0.9996 +units=m +x_0=512000 +y_0=-3.74929e+06",
                38.5, 127.5
            ),
            ["Balkans"] = new Theater(
                "Balkans",
                "+proj=tmerc +lon_0=16.4191 +ellps=WGS84 +k=0.9996 +units=m +x_0=512000 +y_0=-4.1192e+06",
                41.8327, 16.4191
            ),
            ["Ikaros"] = new Theater(
                "Ikaros",
                "+proj=tmerc +lon_0=25 +ellps=WGS84 +k=0.9996 +units=m +x_0=512000 +y_0=-3.69382e+06",
                38, 25
            ),
            ["ITO"] = new Theater(
                "ITO",
                "+proj=tmerc +lon_0=35 +ellps=WGS84 +k=0.9996 +units=m +x_0=512000 +y_0=-3.02844e+06",
                32, 35
            )
        };

        private static string NormalizeProj4(string proj4)
        {
            // we need to replace scientific notation with common C# numbers for DotSpatial
            return MyRegex().Replace(proj4, m => double.Parse(m.Value, CultureInfo.InvariantCulture)
                .ToString("0.################", CultureInfo.InvariantCulture)
            );
        }

        public enum CoordinateSystem
        {
            BMS_HEIGHTMAP_COORDINATE_SYTEM, // Origin top left
            BMS_POSITION_COORDINATE_SYTEM // Origin bottom left
        }

        private class Theater
        {
            public string Name { get; }
            public string ProjString { get; }
            private readonly ProjectionInfo _projectionInfo;
            private readonly ProjectionInfo _wgs84;
            public (double x, double y) CenterProjected { get; private set; }
            
            // Pre-calculated corners in lat/lon (WGS84)
            public (double lat, double lon)[] CornersLatLon { get; }
            public (double lat, double lon) CenterLatLon { get; set; }
            
            public Theater(string name, string projString, double centerLat, double centerLon)
            {
                Name = name;
                ProjString = NormalizeProj4(projString);
                CenterLatLon = (centerLat, centerLon);

                _wgs84 = KnownCoordinateSystems.Geographic.World.WGS1984;
                _projectionInfo = ProjectionInfo.FromProj4String(ProjString);
                
                // Use the authoritative center from theater definition
                CenterProjected = Transform(centerLat, centerLon);
                
                // Pre-calculate corners (in BMS position coordinate system)
                var corners = new[]
                {
                    (x: 0.0, y: 0.0),                                    // Bottom-left
                    (x: (double)HEIGHTMAP_SIZE_M, y: 0.0),              // Bottom-right
                    (x: (double)HEIGHTMAP_SIZE_M, y: (double)HEIGHTMAP_SIZE_M), // Top-right
                    (x: 0.0, y: (double)HEIGHTMAP_SIZE_M),              // Top-left
                    (x: 0.0, y: 0.0)                                     // Close the polygon
                };
                
                CornersLatLon = new (double lat, double lon)[corners.Length];
                for (int i = 0; i < corners.Length; i++)
                {
                    CornersLatLon[i] = InverseTransform(corners[i].x, corners[i].y);
                }
            }

            public (double x, double y) Transform(double latitude, double longitude)
            {
                double[] xy = new[] { longitude, latitude };
                double[] z = new[] { 0.0 };

                Reproject.ReprojectPoints(xy, z, _wgs84, _projectionInfo, 0, 1);

                return (xy[0], xy[1]);
            }

            public (double latitude, double longitude) InverseTransform(double x, double y)
            {
                double[] xy = new[] { x, y };
                double[] z = new[] { 0.0 };

                Reproject.ReprojectPoints(xy, z, _projectionInfo, _wgs84, 0, 1);

                return (xy[1], xy[0]); // Return as (lat, lon)
            }
        }

        /// <summary>
        /// Converts latitude and longitude to heightmap X/Y coordinates for the specified theater.
        /// </summary>
        /// <param name="theaterName">Name of the theater (e.g., "Korea KTO")</param>
        /// <param name="latitude">Latitude in decimal degrees</param>
        /// <param name="longitude">Longitude in decimal degrees</param>
        /// <param name="targetCoordinateSystem">The coordinate system where X and Y will be located in</param>
        /// <returns>Tuple of (X, Y) coordinates in meters</returns>
        /// <exception cref="ArgumentException">Thrown when theater name is not found</exception>
        public static (double x, double y) LatLonToXYMeters(string theaterName, double latitude, double longitude,
            CoordinateSystem targetCoordinateSystem)
        {
            if (!Theaters.TryGetValue(theaterName, out var theater))
            {
                throw new ArgumentException(
                    $"Theater '{theaterName}' not found. Available theaters: {string.Join(", ", Theaters.Keys)}");
            }

            var xy = theater.Transform(latitude, longitude);
            if (targetCoordinateSystem == CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM)
            {
                const double HEIGHTMAP_SIZE_M = 1024000.0;
                const double HALF_SIZE_M = HEIGHTMAP_SIZE_M / 2;
        
                // Heightmap bottom-left corner in projection space (METERS)
                double xOffset = theater.CenterProjected.x - HALF_SIZE_M;
                double yOffset = theater.CenterProjected.y - HALF_SIZE_M;

                // Convert to heightmap coordinates (METERS)
                double heightmapX = xy.x - xOffset;
                double heightmapY = HEIGHTMAP_SIZE_M - (xy.y - yOffset);  // Y-flip

                return (heightmapX, heightmapY);
            }

            return xy;
        }

        /// <summary>
        /// Converts heightmap X/Y coordinates to latitude and longitude for the specified theater.
        /// </summary>
        /// <param name="theaterName">Name of the theater (e.g., "Korea KTO")</param>
        /// <param name="x">X coordinate in meters</param>
        /// <param name="y">Y coordinate in meters</param>
        /// <param name="sourceCoordinateSystem">The coordinate system X and Y is located in</param>
        /// <returns>Tuple of (latitude, longitude) in decimal degrees</returns>
        /// <exception cref="ArgumentException">Thrown when theater name is not found</exception>
        public static (double latitude, double longitude) XYToLatLon(string theaterName, double x, double y,
            CoordinateSystem sourceCoordinateSystem)
        {
            if (!Theaters.TryGetValue(theaterName, out var theater))
            {
                throw new ArgumentException(
                    $"Theater '{theaterName}' not found. Available theaters: {string.Join(", ", Theaters.Keys)}");
            }

            if (sourceCoordinateSystem == CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM)
            {
                y = HEIGHTMAP_SIZE_M - y;
            }

            return theater.InverseTransform(x, y);
        }

        /// <summary>
        /// Gets the center lat/lon for the specified theater.
        /// </summary>
        public static (double latitude, double longitude) GetCenterLatLon(string theaterName)
        {
            if (!Theaters.TryGetValue(theaterName, out var theater))
            {
                throw new ArgumentException(
                    $"Theater '{theaterName}' not found. Available theaters: {string.Join(", ", Theaters.Keys)}");
            }
            return theater.CenterLatLon;
        }
        
        /// <summary>
        /// Gets the corner coordinates (lat/lon) for the specified theater.
        /// Returns 5 points: bottom-left, bottom-right, top-right, top-left, bottom-left (closed polygon).
        /// </summary>
        public static (double lat, double lon)[] GetTheaterCornersLatLon(string theaterName)
        {
            if (!Theaters.TryGetValue(theaterName, out var theater))
            {
                throw new ArgumentException(
                    $"Theater '{theaterName}' not found. Available theaters: {string.Join(", ", Theaters.Keys)}");
            }
            return theater.CornersLatLon;
        }

        /// <summary>
        /// Gets all available theater names.
        /// </summary>
        public static IEnumerable<string> GetAvailableTheaters() => Theaters.Keys;

        /// <summary>
        /// Registers a dynamically-detected theater (e.g., from BMS installation scan).
        /// No-op if name is already registered.
        /// </summary>
        public static void RegisterTheater(TheaterDefinition theater)
        {
            Theaters.TryAdd(theater.Name, new Theater(theater.Name, theater.ProjString, theater.CenterLat, theater.CenterLon));
        }

        // For UI bindings
        public static readonly IEnumerable<string> AllTheaters = ["Korea KTO", "Balkans", "HTO", "ITO"];

        [GeneratedRegex(@"-?\d+(\.\d+)?[eE][+-]?\d+")]
        private static partial Regex MyRegex();

        public static bool IsWithinTheaterBounds(string theaterName, double latitude, double longitude)
        {
            if (!Theaters.TryGetValue(theaterName, out var theater))
            {
                throw new ArgumentException(
                    $"Theater '{theaterName}' not found. Available theaters: {string.Join(", ", Theaters.Keys)}");
            }

            var xy = LatLonToXYMeters(theaterName, latitude, longitude, CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM);
            xy.y = HEIGHTMAP_SIZE_M - xy.y;
            
            const double epsilon = 2;
            return xy.x is >= -epsilon and < HEIGHTMAP_SIZE_M + epsilon &&
                   xy.y is >= -epsilon and < HEIGHTMAP_SIZE_M + epsilon;
        }
    }
}