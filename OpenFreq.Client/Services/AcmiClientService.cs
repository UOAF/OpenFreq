using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqClient;
using OpenFreqClient.Models;

namespace OpenFreq.Services.Acmi;

/// <summary>
/// ACMI client service that tracks aircraft position data
/// </summary>
public class AcmiClientService : IAcmiClientService
{
    private const int DEFAULT_PORT = 42674;
    
    private readonly ILogger<AcmiClientService> _logger;
    private readonly ConcurrentDictionary<string, AcmiAircraft> _trackedAircraft = new();
    
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    
    private string _serverAddress = string.Empty;
    private int _serverPort;
    private string _password = string.Empty;
    private int _maxRetries = 3;
    
    private DateTime _referenceTime = DateTime.UnixEpoch;
    private double _relativeTime;
    
    private readonly object _statusLock = new();
    private AcmiConnectionStatus _status = AcmiConnectionStatus.Disconnected;
    
    // Track all encountered callsigns (objectId -> callsign)
    private readonly ConcurrentDictionary<string, string> _allEncounteredCallsigns = new();
    // Currently selected aircraft for tracking
    private string? _selectedAircraftId;
    
    /// <summary>Fired when connection status changes</summary>
    public event EventHandler<AcmiConnectionEventArgs>? ConnectionStatusChanged;
    
    /// <summary>Fired when connection is established</summary>
    public event EventHandler<AcmiConnectionEventArgs>? Connected;
    
    /// <summary>Fired when connection is lost</summary>
    public event EventHandler<AcmiConnectionEventArgs>? ConnectionLost;
    
    /// <summary>Fired when a new aircraft is discovered</summary>
    public event EventHandler<AcmiAircraftDiscoveredEventArgs>? AircraftDiscovered;
    
    /// <summary>Removes an aircraft from tracking</summary>
    public void RemoveTrackingForAircraft(string? objectId)
    {
        if (string.IsNullOrEmpty(objectId))
            return;
            
        if (_trackedAircraft.TryRemove(objectId, out var aircraft))
        {
            _logger.LogInformation("Removed tracking for aircraft: {ObjectId} ({CallSign})", 
                objectId, aircraft.CallSign ?? "Unknown");
                
            // If this was the selected aircraft, clear selection
            if (_selectedAircraftId == objectId)
            {
                _selectedAircraftId = null;
            }
        }
    }

    /// <summary>Current connection status</summary>
    public AcmiConnectionStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
        private set
        {
            lock (_statusLock)
            {
                if (_status != value)
                {
                    var oldStatus = _status;
                    _status = value;
                    _logger.LogInformation("ACMI status: {OldStatus} -> {NewStatus}", oldStatus, value);
                }
            }
        }
    }

    /// <summary>Read-only collection of currently tracked aircraft</summary>
    public IReadOnlyDictionary<string, AcmiAircraft> TrackedAircraft => _trackedAircraft;
    
    public AcmiClientService(ILogger<AcmiClientService> logger)
    {
        _logger = logger;
    }

    /// <summary>Connects to the ACMI server</summary>
    public async Task<bool> ConnectAsync(string connectionString, string password = "", int maxRetries = -1)
    {
        if (Status == AcmiConnectionStatus.Connected || Status == AcmiConnectionStatus.Connecting)
        {
            _logger.LogWarning("Already connected or connecting");
            return false;
        }
        
        var ipPort = Util.ResolveAddress(connectionString, DEFAULT_PORT);
        
        _serverAddress = ipPort.ipAddress;
        _serverPort = ipPort.port;
        _password = string.IsNullOrEmpty(password) ? "0" : password;
        _maxRetries = maxRetries;
        
        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ConnectionLoopAsync(_cts.Token));

        return true;
    }

    public void CancelConnectionAttempts()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS already disposed, ignore
        }
        
        Status = AcmiConnectionStatus.Disconnected;
        RaiseConnectionStatusChanged(AcmiConnectionStatus.Disconnected, "Disconnected");
    }

    /// <summary>Disconnects from the ACMI server</summary>
    public async Task DisconnectAsync()
    {
        // Cancel the token source if it exists and hasn't been disposed
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS already disposed, ignore
        }
        
        _stream?.Dispose();
        _client?.Dispose();
        var ctsToDispose = _cts;
        _cts = null;
        ctsToDispose?.Dispose();
        
        _stream = null;
        _client = null;
        _receiveTask = null;
        
        // Clear persistent buffer to avoid data leaking between connections
        _persistentBuffer.Clear();

        Status = AcmiConnectionStatus.Disconnected;
        RaiseConnectionStatusChanged(AcmiConnectionStatus.Disconnected, "Disconnected");
    }

    /// <summary>Gets an aircraft by its object ID</summary>
    public AcmiAircraft? GetAircraft(string objectId) => 
        _trackedAircraft.GetValueOrDefault(objectId);

    /// <summary>Gets all aircraft currently tracked</summary>
    public IEnumerable<AcmiAircraft> GetAllAircraft() => _trackedAircraft.Values.ToList();

    public void AddTrackingForAircraft(string? objectId)
    {
        if (string.IsNullOrEmpty(objectId)) return;
        _trackedAircraft.TryAdd(objectId, new AcmiAircraft{ObjectId = objectId});
    }

    /// <summary>Clears all tracked aircraft</summary>
    public void ClearAircraft()
    {
        _trackedAircraft.Clear();
        _allEncounteredCallsigns.Clear();
        _selectedAircraftId = null;
        _logger.LogInformation("Cleared all tracked aircraft");
    }

    /// <summary>Gets all encountered callsigns as a dictionary of objectId -> callsign</summary>
    public IReadOnlyDictionary<string, string> GetAllEncounteredCallsigns() => 
        _allEncounteredCallsigns;

    /// <summary>Gets a list of all unique callsigns encountered</summary>
    public IEnumerable<string> GetCallsignsList() => 
        _allEncounteredCallsigns.Values.Distinct().OrderBy(c => c);

    /// <summary>Selects an aircraft to track by its object ID</summary>
    public bool SelectAircraftById(string objectId)
    {
        if (_trackedAircraft.ContainsKey(objectId))
        {
            _selectedAircraftId = objectId;
            _logger.LogInformation("Selected aircraft: {ObjectId}", objectId);
            return true;
        }
        return false;
    }

    /// <summary>Selects an aircraft to track by its callsign</summary>
    public bool SelectAircraftByCallsign(string callsign)
    {
        var objectId = _allEncounteredCallsigns
            .FirstOrDefault(kvp => kvp.Value.Equals(callsign, StringComparison.OrdinalIgnoreCase))
            .Key;
            
        if (!string.IsNullOrEmpty(objectId))
        {
            _selectedAircraftId = objectId;
            _logger.LogInformation("Selected aircraft by callsign: {CallSign} (ID: {ObjectId})", callsign, objectId);
            return true;
        }
        return false;
    }

    /// <summary>Gets the currently selected aircraft</summary>
    public AcmiAircraft? GetSelectedAircraft() => 
        _selectedAircraftId != null ? GetAircraft(_selectedAircraftId) : null;

    /// <summary>Clears the aircraft selection</summary>
    public void ClearSelection()
    {
        _selectedAircraftId = null;
        _logger.LogInformation("Cleared aircraft selection");
    }

    /// <summary>Gets the object ID of the currently selected aircraft</summary>
    public string? SelectedAircraftId => _selectedAircraftId;

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        int retryCount = 0;
        const int retryDelaySeconds = 5;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Status = AcmiConnectionStatus.Connecting;
                RaiseConnectionStatusChanged(AcmiConnectionStatus.Connecting, 
                    $"Connecting (attempt {retryCount + 1})");

                _logger.LogInformation("Connecting to {Address}:{Port}", _serverAddress, _serverPort);

                _client = new TcpClient();
                await _client.ConnectAsync(_serverAddress, _serverPort, cancellationToken);
                _stream = _client.GetStream();

                if (!await PerformHandshakeAsync(cancellationToken))
                {
                    _logger.LogError("Handshake failed");
                    Status = AcmiConnectionStatus.Failed;
                    RaiseConnectionStatusChanged(AcmiConnectionStatus.Failed, "Handshake failed");
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                    retryCount++;
                    continue;
                }

                _logger.LogInformation("ACMI handshake successful");
                Status = AcmiConnectionStatus.Connected;
                RaiseConnectionStatusChanged(AcmiConnectionStatus.Connected, "Connected");
                RaiseConnected("Connected to ACMI server");
                
                retryCount = 0;

                await ProcessDataStreamAsync(cancellationToken);

                Status = AcmiConnectionStatus.Disconnected;
                RaiseConnectionLost("Connection lost");
                RaiseConnectionStatusChanged(AcmiConnectionStatus.Disconnected, "Connection lost");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection error");
                Status = AcmiConnectionStatus.Failed;
                RaiseConnectionStatusChanged(AcmiConnectionStatus.Failed, $"Error: {ex.Message}");
                
                retryCount++;
                if (_maxRetries > 0 && retryCount >= _maxRetries)
                {
                    _logger.LogError("Max retries ({Max}) reached", _maxRetries);
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                _stream?.Dispose();
                _client?.Dispose();
                _stream = null;
                _client = null;
            }
        }
    }

    private async Task<bool> PerformHandshakeAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
            return false;

        try
        {
            string handshakeMsg = $"XtraLib.Stream.0\nTacview.RealTimeTelemetry.0\nOpenFreq\n{_password}\0";
            byte[] handshakeBytes = Encoding.UTF8.GetBytes(handshakeMsg);
            
            _logger.LogDebug("Sending handshake: {Length} bytes", handshakeBytes.Length);
            _logger.LogDebug("Handshake content: {Content}", 
                handshakeMsg.Replace("\0", "\\0").Replace("\n", "\\n"));
            
            await _stream.WriteAsync(handshakeBytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);

            _logger.LogDebug("Waiting for handshake response...");
            var response = await ReadUntilAsync('\0', cancellationToken);
            
            if (response == null)
            {
                _logger.LogError("No handshake response received");
                return false;
            }

            _logger.LogDebug("Received handshake response: {Length} bytes", response.Length);
            _logger.LogDebug("Response content: {Content}", 
                response.Replace("\0", "\\0").Replace("\n", "\\n"));

            if (response.StartsWith("XtraLib.Stream.0\nTacview.RealTimeTelemetry.0\n"))
            {
                _logger.LogInformation("Handshake accepted");
                return true;
            }

            _logger.LogError("Invalid handshake response. Expected to start with 'XtraLib.Stream.0\\nTacview.RealTimeTelemetry.0\\n'");
            _logger.LogError("Actual response: {Response}", 
                response.Length > 100 ? response.Substring(0, 100) + "..." : response);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handshake error");
            return false;
        }
    }

    private async Task ProcessDataStreamAsync(CancellationToken cancellationToken)
    {
        int lineCount = 0;
        int validLines = 0;
        int invalidLines = 0;
        int lastLogLine = 0;

        try
        {
            _logger.LogDebug("Starting ACMI data stream processing");
            
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    _logger.LogWarning("Connection closed by server (received null line)");
                    break;
                }

                lineCount++;
                bool wasValid = ProcessLine(line);
                
                if (wasValid)
                    validLines++;
                else
                    invalidLines++;

                // Log every 1000 lines or if we've accumulated 10+ invalid lines since last log
                if (lineCount % 1000 == 0 || (invalidLines - lastLogLine >= 10 && invalidLines % 10 == 0))
                {
                    _logger.LogDebug("Processed {Count} lines ({Valid} valid, {Invalid} invalid), {Aircraft} aircraft, buffer: {BufferSize} bytes", 
                        lineCount, validLines, invalidLines, _trackedAircraft.Count, _persistentBuffer.Count);
                    lastLogLine = invalidLines;
                }
            }
            
            if (invalidLines > 0)
            {
                _logger.LogInformation(
                    "Stream ended: {Total} lines processed, {Invalid} invalid lines skipped ({Percent:F1}% error rate)", 
                    lineCount, invalidLines, (invalidLines * 100.0 / lineCount));
            }
            else
            {
                _logger.LogInformation("Stream ended: {Total} lines processed, all valid", lineCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data stream error");
            throw;
        }
    }

    private bool ProcessLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        // Skip file headers
        if (line.StartsWith("FileType") || line.StartsWith("FileVersion"))
            return true;  // Valid but ignored

        // Time update
        if (line[0] == '#')
        {
            if (double.TryParse(line.AsSpan(1), NumberStyles.Float, 
                CultureInfo.InvariantCulture, out double time))
            {
                _relativeTime = time;
                return true;
            }
            return false;
        }

        // Object removal
        if (line[0] == '-')
        {
            string removeId = line.Substring(1).Trim();
            if (IsValidObjectId(removeId))
            {
                _trackedAircraft.TryRemove(removeId, out _);
                return true;
            }
            _logger.LogWarning("Invalid removal ID: {Id}", removeId);
            return false;
        }

        // Object update - must have comma
        var span = line.AsSpan();
        int firstComma = span.IndexOf(',');
        if (firstComma < 0)
        {
            // No comma found - completely malformed line
            _logger.LogDebug("Malformed line (no comma): {Line}", 
                line.Length > 80 ? line.Substring(0, 80) + "..." : line);
            return false;
        }

        // Extract object ID (everything before first comma)
        var objectIdSpan = span.Slice(0, firstComma);
        
        // Check for empty or whitespace-only object ID
        if (objectIdSpan.IsWhiteSpace() || objectIdSpan.Length == 0)
        {
            _logger.LogWarning("Empty object ID in line: {Line}", 
                line.Length > 80 ? line.Substring(0, 80) + "..." : line);
            return false;
        }
        
        var objectId = objectIdSpan.ToString().Trim();
        
        // Check if "object ID" looks like a property instead (missing object ID)
        // Properties have format: PropertyName=Value
        if (objectId.Contains('='))
        {
            _logger.LogWarning("Line appears to be missing object ID (starts with property): {Line}", 
                line.Length > 80 ? line.Substring(0, 80) + "..." : line);
            return false;
        }
        
        // Check if "object ID" looks like transform data (contains pipes)
        if (objectId.Contains('|'))
        {
            _logger.LogWarning("Line appears to be corrupted (object ID contains pipes): {Line}", 
                line.Length > 80 ? line.Substring(0, 80) + "..." : line);
            return false;
        }
        
        // Validate object ID format (decimal or hexadecimal)
        if (!IsValidObjectId(objectId))
        {
            _logger.LogWarning("Invalid object ID format '{ObjectId}': {Line}", 
                objectId, line.Length > 80 ? line.Substring(0, 80) + "..." : line);
            return false;
        }
        
        // Global properties (objectId = 0)
        if (objectId == "0")
        {
            ParseGlobalProperties(span.Slice(firstComma + 1));
            return true;
        }

        // Update or create aircraft
        bool isNewAircraft = false;
        AcmiAircraft aircraftData;
        
        if (_trackedAircraft.ContainsKey(objectId))
        {
            _trackedAircraft.TryGetValue(objectId, out aircraftData!);
            
            // this should never happen but let's be sure
            if (aircraftData == null)
            {
                aircraftData = new AcmiAircraft { ObjectId = objectId };
                _trackedAircraft[objectId] = aircraftData;
                isNewAircraft = true;
            }
        }
        else
        {
            // New aircraft discovered
            aircraftData = new AcmiAircraft { ObjectId = objectId };
            _trackedAircraft[objectId] = aircraftData;
            isNewAircraft = true;
        }
        
        ParseAircraftProperties(aircraftData, span.Slice(firstComma + 1));
        
        // Track callsign and fire discovery event for new aircraft
        if (isNewAircraft && !string.IsNullOrEmpty(aircraftData.CallSign))
        {
            _allEncounteredCallsigns.TryAdd(objectId, aircraftData.CallSign);
            RaiseAircraftDiscovered(aircraftData);
        }
        else if (!string.IsNullOrEmpty(aircraftData.CallSign))
        {
            // Update callsign if changed
            _allEncounteredCallsigns.AddOrUpdate(objectId, aircraftData.CallSign, (_, _) => aircraftData.CallSign);
        }

        return true;
    }

    private static bool IsValidObjectId(string objectId)
    {
        if (string.IsNullOrEmpty(objectId))
            return false;
        
        // Object ID can be decimal (9341) or hexadecimal (ff000079b4)
        // Valid characters: 0-9, a-f, A-F
        foreach (char c in objectId)
        {
            if (!char.IsDigit(c) && 
                !((c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }
        
        return true;
    }

    private void ParseGlobalProperties(ReadOnlySpan<char> properties)
    {
        // Only care about ReferenceTime
        int refTimeStart = properties.IndexOf("ReferenceTime=".AsSpan());
        if (refTimeStart >= 0)
        {
            refTimeStart += 14; // Length of "ReferenceTime="
            int refTimeEnd = properties.Slice(refTimeStart).IndexOf(',');
            var refTimeSpan = refTimeEnd < 0 
                ? properties.Slice(refTimeStart) 
                : properties.Slice(refTimeStart, refTimeEnd);
            
            if (DateTime.TryParseExact(refTimeSpan, "yyyy-M-dTHH:mm:ssZ", 
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var refTime))
            {
                _referenceTime = refTime.ToUniversalTime();
            }
        }
    }

    private void ParseAircraftProperties(AcmiAircraft aircraft, ReadOnlySpan<char> properties)
    {
        aircraft.LastUpdate = _referenceTime.AddSeconds(_relativeTime);
        

        // Parse only essential properties
        int pos = 0;
        while (pos < properties.Length)
        {
            int nextComma = properties.Slice(pos).IndexOf(',');
            int propEnd = nextComma < 0 ? properties.Length : pos + nextComma;
            var prop = properties.Slice(pos, propEnd - pos);
            
            int equals = prop.IndexOf('=');
            if (equals > 0)
            {
                var key = prop.Slice(0, equals);
                var value = prop.Slice(equals + 1);

                // Only parse essential fields
                if (key.SequenceEqual("T".AsSpan()))
                {
                    ParseTransform(aircraft.Transform, value);
                }
                else if (key.SequenceEqual("Name".AsSpan()))
                {
                    aircraft.Name = value.ToString().Replace('+', ' ');
                }
                else if (key.SequenceEqual("Pilot".AsSpan()))
                {
                    aircraft.Pilot = value.ToString();
                }
                else if (key.SequenceEqual("CallSign".AsSpan()))
                {
                    aircraft.CallSign = value.ToString();
                }
                else if (key.SequenceEqual("Type".AsSpan()))
                {
                    aircraft.Type = value.ToString();
                }
                else if (key.SequenceEqual("Coalition".AsSpan()))
                {
                    aircraft.Coalition = value.ToString();
                }
                else if (key.SequenceEqual("Mach".AsSpan()))
                {
                    try
                    {
                        aircraft.Mach = float.Parse(value.ToString());
                    }
                    catch (Exception e)
                    {
                        _logger.LogError("{ToString}", e.ToString());
                       aircraft.Mach = 0;
                    }
                }
                // Skip all other properties (IAS, CAS, AOA, Health, etc.)
            }

            pos = propEnd + 1;
        }
    }

    private void ParseTransform(AircraftTransform transform, ReadOnlySpan<char> tValue)
    {
        int pipeCount = 0;
        foreach (var t in tValue)
        {
            if (t == '|')
                pipeCount++;
        }

        try
        {
            Span<Range> ranges = stackalloc Range[10];
            int partCount = tValue.Split(ranges, '|');

            switch (pipeCount)
            {
                case 4: // Simple flat: lon|lat|alt|u|v
                    if (partCount >= 5)
                    {
                        transform.Longitude = ParseDoubleOrDefault(tValue[ranges[0]]);
                        transform.Latitude = ParseDoubleOrDefault(tValue[ranges[1]]);
                        transform.Altitude = ParseDoubleOrDefault(tValue[ranges[2]]);
                        transform.U = ParseDoubleOrDefault(tValue[ranges[3]]);
                        transform.V = ParseDoubleOrDefault(tValue[ranges[4]]);
                    }
                    break;

                case 5: // Spherical (or flat with extra pipe)
                    if (partCount >= 5)
                    {
                        transform.Longitude = ParseDoubleOrDefault(tValue[ranges[0]]);
                        transform.Latitude = ParseDoubleOrDefault(tValue[ranges[1]]);
                        transform.Altitude = ParseDoubleOrDefault(tValue[ranges[2]]);
                        transform.U = ParseDoubleOrDefault(tValue[ranges[3]]);
                        transform.V = ParseDoubleOrDefault(tValue[ranges[4]]);
                    }
                    break;

                case 8: // Complex: lon|lat|alt|roll|pitch|yaw|u|v|heading
                    if (partCount >= 9)
                    {
                        transform.Longitude = ParseDoubleOrDefault(tValue[ranges[0]]);
                        transform.Latitude = ParseDoubleOrDefault(tValue[ranges[1]]);
                        transform.Altitude = ParseDoubleOrDefault(tValue[ranges[2]]);
                        transform.Roll = ParseDoubleOrDefault(tValue[ranges[3]]);
                        transform.Pitch = ParseDoubleOrDefault(tValue[ranges[4]]);
                        transform.Yaw = ParseDoubleOrDefault(tValue[ranges[5]]);
                        transform.U = ParseDoubleOrDefault(tValue[ranges[6]]);
                        transform.V = ParseDoubleOrDefault(tValue[ranges[7]]);
                        transform.Heading = ParseDoubleOrDefault(tValue[ranges[8]]);
                    }
                    break;
            }

        }
        catch
        {
            // Ignore parse errors
        }
    }

    private static double ParseDoubleOrDefault(ReadOnlySpan<char> span)
    {
        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) 
            ? result 
            : 0.0;
    }

    private readonly List<byte> _persistentBuffer = new();
    
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken) => 
        await ReadUntilAsync('\n', cancellationToken);

    private async Task<string?> ReadUntilAsync(char separator, CancellationToken cancellationToken)
    {
        if (_stream == null)
            return null;

        byte separatorByte = (byte)separator;
        byte[] readBuffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // First, check if we already have a complete line in our buffer
                int separatorIndex = _persistentBuffer.IndexOf(separatorByte);
                
                if (separatorIndex >= 0)
                {
                    // We have a complete line!
                    byte[] lineBytes = _persistentBuffer.GetRange(0, separatorIndex).ToArray();
                    
                    // Remove the line and separator from buffer
                    _persistentBuffer.RemoveRange(0, separatorIndex + 1);
                    
                    // Convert to string
                    string line = Encoding.UTF8.GetString(lineBytes);
                    
                    // For regular lines (\n separator), trim whitespace
                    // For handshake (\0 separator), don't trim
                    if (separator == '\n')
                    {
                        line = line.Replace("\r", "").Trim();
                        
                        // Skip empty lines
                        if (string.IsNullOrEmpty(line))
                            continue; // Check buffer again for next line
                    }
                    
                    return line;
                }
                
                // No complete line in buffer, read more data
                int bytesRead = await _stream.ReadAsync(readBuffer, 0, readBuffer.Length, cancellationToken);
                
                if (bytesRead == 0)
                {
                    // Connection closed
                    return null;
                }

                // Add new data to persistent buffer
                for (int i = 0; i < bytesRead; i++)
                {
                    _persistentBuffer.Add(readBuffer[i]);
                }
                
                // Loop back to check if we now have a complete line
            }

            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O error while reading stream");
            return null;
        }
    }

    private void RaiseConnectionStatusChanged(AcmiConnectionStatus status, string message)
    {
        ConnectionStatusChanged?.Invoke(this, new AcmiConnectionEventArgs
        {
            Status = status,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }

    private void RaiseConnected(string message)
    {
        Connected?.Invoke(this, new AcmiConnectionEventArgs
        {
            Status = AcmiConnectionStatus.Connected,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }

    private void RaiseConnectionLost(string message)
    {
        Status = AcmiConnectionStatus.Disconnected;
        ConnectionLost?.Invoke(this, new AcmiConnectionEventArgs
        {
            Status = AcmiConnectionStatus.Disconnected,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
    }

    private void RaiseAircraftDiscovered(AcmiAircraft aircraft)
    {
        AircraftDiscovered?.Invoke(this, new AcmiAircraftDiscoveredEventArgs
        {
            Aircraft = aircraft,
            Timestamp = DateTime.UtcNow
        });
        
        _logger.LogInformation("New aircraft discovered: {ObjectId} - {CallSign} ({Name})",
            aircraft.ObjectId, aircraft.CallSign ?? "Unknown", aircraft.Name ?? "Unknown");
    }

    public void Dispose()
    {
        DisconnectAsync().Wait(500);
        Status = AcmiConnectionStatus.Disconnected;
        GC.SuppressFinalize(this);
    }

    public void Start()
    {
        // not used, we connect explicitly
    }

    public void Stop()
    {
        DisconnectAsync().Wait(500);
        Status = AcmiConnectionStatus.Disconnected;
    }
}

/// <summary>
/// Event args for when a new aircraft is discovered
/// </summary>
public class AcmiAircraftDiscoveredEventArgs : EventArgs
{
    public required AcmiAircraft Aircraft { get; init; }
    public DateTime Timestamp { get; init; }
}