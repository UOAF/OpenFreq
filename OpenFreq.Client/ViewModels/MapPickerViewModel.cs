using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BruTile.Predefined;
using BruTile.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using NetTopologySuite.Geometries;
using OpenFreq.Utilities;
using OpenFreqClient.Json;
using Brush = Mapsui.Styles.Brush;
using Point = NetTopologySuite.Geometries.Point;
using MapsuiColor = Mapsui.Styles.Color;
using Pen = Mapsui.Styles.Pen;

namespace OpenFreqClient.ViewModels;

public partial class MapPickerViewModel : ViewModelBase
{
    private static readonly HttpClient HttpClient = new();

    [ObservableProperty] public partial double Latitude { get; set; }
    [ObservableProperty] public partial double Longitude { get; set; }
    [ObservableProperty] public partial double Heading { get; set; }
    [ObservableProperty] public partial double Altitude { get; set; }
    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSearching { get; private set; }
    [ObservableProperty] public partial string? SearchError { get; set; }
    [ObservableProperty] public partial bool IsTrackingMode { get; private set; }
    [ObservableProperty] public partial string? TrackedCallsign { get; private set; }

    private WritableLayer? _theaterBoundsLayer;
    private WritableLayer? _positionLayer;
    private Map? _map;
    private readonly string _selectedTheatername;

    public Map Map => _map ??= CreateMap();

    public event EventHandler<(double lat, double lon)>? PositionConfirmed;

    public MapPickerViewModel(double initialLat, double initialLon, string selectedTheaterName)
    {
        Latitude = initialLat;
        Longitude = initialLon;
        _selectedTheatername = selectedTheaterName;
        IsTrackingMode = false;
    }

    /// <summary>
    /// Constructor for tracking mode - displays aircraft with heading and disables position picking
    /// </summary>
    public MapPickerViewModel(double initialLat, double initialLon, double initialHeading, string selectedTheaterName, string? callsign = null)
    {
        Latitude = initialLat;
        Longitude = initialLon;
        Heading = initialHeading;
        _selectedTheatername = selectedTheaterName;
        IsTrackingMode = true;
        TrackedCallsign = callsign;
    }

    private Map CreateMap()
    {
        var map = new Map();

        // Use Carto Voyager tiles - they have English labels globally
        var tileSource = new HttpTileSource(new GlobalSphericalMercator(),
            "https://a.basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png",
            name: "Carto Voyager");
        map.Layers.Add(new TileLayer(tileSource) { Name = "Carto" });

        // Add theater bounds layer (if available)
        _theaterBoundsLayer = CreateTheaterBoundsLayer();
        if (_theaterBoundsLayer != null)
        {
            map.Layers.Add(_theaterBoundsLayer);
        }

        // Add layer for position marker (aircraft icon in tracking mode, dot in picker mode)
        _positionLayer = new WritableLayer
        {
            Name = IsTrackingMode ? "Aircraft" : "Position",
            Style = null  // Disable layer-level style, use only feature styles
        };
        map.Layers.Add(_positionLayer);

        // Set initial view
        (double x, double y) viewCenter;

        if (Latitude == 0 && Longitude == 0)
        {
            // Default to center of Theater if no position set
            var latLon = TheaterCoordinateConverter.GetCenterLatLon(_selectedTheatername);
            viewCenter = SphericalMercator.FromLonLat(latLon.longitude, latLon.latitude);
        }
        else
        {
            viewCenter = SphericalMercator.FromLonLat(Longitude, Latitude);
            UpdatePositionMarker(Latitude, Longitude, Heading);
        }

        map.Navigator.CenterOnAndZoomTo(new MPoint(viewCenter.x, viewCenter.y), map.Navigator.Resolutions[7]);
        return map;
    }

    public void OnMapClicked(MPoint worldPosition)
    {
        // Disable position picking in tracking mode
        if (IsTrackingMode) return;
        
        var lonLat = SphericalMercator.ToLonLat(worldPosition.X, worldPosition.Y);

        Latitude = lonLat.lat;
        Longitude = lonLat.lon;

        UpdatePositionMarker(lonLat.lat, lonLat.lon);
        SearchError = null;
    }

    private void UpdatePositionMarker(double lat, double lon, double? heading = null)
    {
        if (_positionLayer == null) return;

        _positionLayer.Clear();

        var mercator = SphericalMercator.FromLonLat(lon, lat);
        var point = new Point(mercator.x, mercator.y);
        var feature = new GeometryFeature { Geometry = point };

        if (IsTrackingMode)
        {
            var aircraftHeading = heading ?? Heading;
            feature.Styles.Add(new ImageStyle
            {
                Image = new Image
                {
                    Source = "embedded://openfreq-client.Assets.airplane_icon.svg",
                },
                SymbolScale = 0.05,
                SymbolRotation = aircraftHeading,
                Offset = new Offset(0, 0),
                
            });
        }
        else
        {
            // Position picker: simple dot
            feature.Styles.Add(new SymbolStyle
            {
                SymbolScale = 0.7,
                Fill = new Brush(MapsuiColor.FromArgb(255, 220, 53, 69)), // Red for position picking
                Outline = new Pen(MapsuiColor.White, 3)
            });
        }


        _positionLayer.Add(feature);
    }

    /// <summary>
    /// Updates the tracked aircraft position and heading (tracking mode only)
    /// </summary>
    public void UpdateTrackedPosition(double lat, double lon, double heading, double altitude)
    {
        if (!IsTrackingMode) return;

        Latitude = lat;
        Longitude = lon;
        Heading = heading;
        Altitude = altitude;

        UpdatePositionMarker(lat, lon, heading);

        // Pan map to keep aircraft in view
        if (_map == null) return;
        var mercator = SphericalMercator.FromLonLat(lon, lat);
        _map.Navigator.CenterOn(new MPoint(mercator.x, mercator.y));
    }

    [RelayCommand]
    private async Task SearchAddressAsync()
    {
        // Disable search in tracking mode
        if (IsTrackingMode) return;
        
        if (string.IsNullOrWhiteSpace(SearchQuery))
            return;

        IsSearching = true;
        SearchError = null;

        try
        {
            // Use Nominatim
            var url =
                $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(SearchQuery)}&format=json&limit=1";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "OpenFreq/1.0"); // Required by Nominatim

            var response = await HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var results = JsonSerializer.Deserialize(json, ClientJsonContext.Default.NominatimResultArray);
            if (results == null || results.Length == 0)
            {
                SearchError = "No results found";
                return;
            }

            var result = results[0];
            Latitude = result.Latitude;
            Longitude = result.Longitude;

            UpdatePositionMarker(Latitude, Longitude);

            // Pan map to result
            if (_map != null)
            {
                var mercator = SphericalMercator.FromLonLat(Longitude, Latitude);
                _map.Navigator.CenterOnAndZoomTo(new MPoint(mercator.x, mercator.y), _map.Navigator.Resolutions[11]);
            }
        }
        catch (Exception ex)
        {
            SearchError = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmPosition))]
    private void ConfirmPosition()
    {
        PositionConfirmed?.Invoke(this, (Latitude, Longitude));
    }

    private bool CanConfirmPosition() => !IsTrackingMode;

    // Nominatim API response model
    public class NominatimResult
    {
        [JsonPropertyName("lat")]
        public string LatStr { get; set; } = string.Empty;
        [JsonPropertyName("lon")]
        public string LonStr { get; set; } = string.Empty;
        // ReSharper disable once UnusedMember.Local
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        // Helper properties to convert strings to doubles
        public double Latitude => double.Parse(LatStr, CultureInfo.InvariantCulture);
        public double Longitude => double.Parse(LonStr, CultureInfo.InvariantCulture);
    }

    private WritableLayer? CreateTheaterBoundsLayer()
    {
        try
        {
            // Get pre-calculated corners from TheaterCoordinateConverter
            var corners = TheaterCoordinateConverter.GetTheaterCornersLatLon(_selectedTheatername);

            // Convert lat/lon corners to Spherical Mercator for map display
            var mercatorCorners = new Coordinate[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                var mercator = SphericalMercator.FromLonLat(corners[i].lon, corners[i].lat);
                mercatorCorners[i] = new Coordinate(mercator.x, mercator.y);
            }

            // Create polygon geometry
            var polygon = new Polygon(new LinearRing(mercatorCorners));

            // Create layer with theater bounds
            var layer = new WritableLayer
            {
                Name = "Theater Bounds",
                Style = new VectorStyle
                {
                    Fill = new Brush(MapsuiColor.Transparent),
                    Outline = new Pen(MapsuiColor.FromArgb(255, 128, 128, 128), 2)
                    {
                        PenStyle = PenStyle.ShortDash
                    }
                }
            };

            layer.Add(new GeometryFeature { Geometry = polygon });

            return layer;
        }
        catch (Exception ex)
        {
            SearchError = $"Failed to display theater bounds: {ex.Message}";
            return null;
        }
    }
}