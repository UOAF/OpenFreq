using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
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

namespace OpenFreqClient.ViewModels;

public partial class MapPickerViewModel : ViewModelBase
{
    private static readonly HttpClient HttpClient = new();

    [ObservableProperty] public partial double Latitude { get; set; }
    [ObservableProperty] public partial double Longitude { get; set; }
    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSearching { get; private set; }
    [ObservableProperty] public partial string? SearchError { get; set; }

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

        // Add layer for position marker
        _positionLayer = new WritableLayer
        {
            Name = "Position",
            Style = new SymbolStyle
            {
                SymbolScale = 0.5,
                Fill = new Brush(Color.FromArgb(255, 220, 53, 69)), // Bootstrap danger red
                Outline = new Pen(Color.White, 3)
            }
        };
        map.Layers.Add(_positionLayer);

        // Set initial view
        (double x, double y) viewCenter;

        if (Latitude == 0 && Longitude == 0)
        {
            // Default to center of Theater if no position set
            var latLon = TheaterCoordinateConverter.CenterLatLon(_selectedTheatername);
            viewCenter = SphericalMercator.FromLonLat(latLon.longitude, latLon.latitude);
        }
        else
        {
            viewCenter = SphericalMercator.FromLonLat(Longitude, Latitude);
            UpdatePositionMarker(Latitude, Longitude);
        }

        map.Navigator.CenterOnAndZoomTo(new MPoint(viewCenter.x, viewCenter.y), map.Navigator.Resolutions[7]);
        return map;
    }

    public void OnMapClicked(MPoint worldPosition)
    {
        var lonLat = SphericalMercator.ToLonLat(worldPosition.X, worldPosition.Y);

        Latitude = lonLat.lat;
        Longitude = lonLat.lon;

        UpdatePositionMarker(lonLat.lat, lonLat.lon);
        SearchError = null;
    }

    private void UpdatePositionMarker(double lat, double lon)
    {
        if (_positionLayer == null) return;

        _positionLayer.Clear();

        var mercator = SphericalMercator.FromLonLat(lon, lat);
        var point = new Point(mercator.x, mercator.y);
        var feature = new GeometryFeature { Geometry = point };

        _positionLayer.Add(feature);
    }

    [RelayCommand]
    private async Task SearchAddressAsync()
    {
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
            var results = JsonSerializer.Deserialize<NominatimResult[]>(json);

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
                _map.Navigator?.CenterOnAndZoomTo(new MPoint(mercator.x, mercator.y), _map.Navigator.Resolutions[11]);
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

    [RelayCommand]
    private void ConfirmPosition()
    {
        PositionConfirmed?.Invoke(this, (Latitude, Longitude));
    }

    // Nominatim API response model
    // ReSharper disable once ClassNeverInstantiated.Local
    private class NominatimResult
    {
        public string lat { get; set; } = string.Empty;
        public string lon { get; set; } = string.Empty;
        public string display_name { get; set; } = string.Empty;

        // Helper properties to convert strings to doubles
        public double Latitude => double.Parse(lat, CultureInfo.InvariantCulture);
        public double Longitude => double.Parse(lon, CultureInfo.InvariantCulture);
    }

    private WritableLayer? CreateTheaterBoundsLayer()
    {
        try
        {
            // Get pre-calculated corners from TheaterCoordinateConverter
            var corners = TheaterCoordinateConverter.GetTheaterCornersLatLon(_selectedTheatername!);

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
                    Fill = new Brush(Color.Transparent),
                    Outline = new Pen(Color.FromArgb(255, 128, 128, 128), 2)
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