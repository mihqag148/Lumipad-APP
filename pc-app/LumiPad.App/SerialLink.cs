using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace LumiPad.App;

public sealed class SerialLink : IDeviceLink
{
    private static readonly Guid ServiceUuid = Guid.Parse("D8A90001-6B5A-4C3B-9F2A-7C4E4C554D49");
    private static readonly Guid CharacteristicUuid = Guid.Parse("D8A90002-6B5A-4C3B-9F2A-7C4E4C554D49");

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private int _exclusiveMediaTransfer;

    private SerialPort? _port;
    private BluetoothLEDevice? _bleDevice;
    private GattDeviceService? _bleService;
    private GattCharacteristic? _bleCharacteristic;
    private int _blePayloadSize = 20;

    private string _connectionName = "";
    private string _lastBitmapKey = "";
    private string _lastArtworkKey = "";
    private string _lastNowPlayingKey = "";
    private bool _lastNowPlayingPlaying;
    private DateTimeOffset _lastNowPlayingSent = DateTimeOffset.MinValue;
    private readonly object _pendingNowPlayingLock = new();
    private NowPlayingData? _pendingNowPlaying;
    private int _mediaGeneration;
    private int _protocolVersion;
    private readonly HashSet<string> _capabilities =
        new(StringComparer.OrdinalIgnoreCase);
    private int _consecutiveLinkFailures;
    private string _lastUsbPortSignature = "";

    private bool IsExclusiveMediaTransferActive =>
        System.Threading.Volatile.Read(
            ref _exclusiveMediaTransfer) != 0;

    private bool IsBleTransportConnected =>
        _bleCharacteristic is not null &&
        _bleDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public bool IsConnected => IsBleTransportConnected || _port?.IsOpen == true;
    public bool IsUsbConnected => _port?.IsOpen == true;
    public bool IsBluetoothConnected => IsBleTransportConnected;
    public string ConnectionName => _connectionName;
    public event Action<string>? LinkError;
    public event Action<string, string>? Diagnostic;
    public string FirmwareHello { get; private set; } = "";
    public int ProtocolVersion => _protocolVersion;
    public bool SupportsDiagnostics =>
        SupportsCapability("LOG");
    public bool SupportsMemoryInfo =>
        SupportsCapability("MEM");
    public bool SupportsPanelInfo =>
        SupportsCapability("PANEL");
    public bool SupportsSaverState =>
        SupportsCapability("SAVERSTATE");
    public bool SupportsProfileSwitch =>
        SupportsCapability("PROFILE");
    public bool SupportsProfileCatalog =>
        SupportsCapability("PROFILECAT");
    public bool SupportsPowerState =>
        SupportsCapability("POWERSTATE");
    public bool SupportsHibernate =>
        SupportsCapability("HIBERNATE");
    public bool SupportsActions =>
        SupportsCapability("ACTION");
    public bool SupportsVariableArtwork =>
        SupportsCapability("ARTVAR");
    public bool SupportsBatteryInfo =>
        SupportsCapability("BAT");
    public bool SupportsPcMonitor =>
        SupportsCapability("PCMON");
    public bool SupportsFastMedia =>
        _protocolVersion >= 4 &&
        SupportsCapability("MEDIAFAST");
    public bool SupportsExternalFlash =>
        _protocolVersion >= 8 &&
        SupportsCapability("EXTFLASH");
    public bool SupportsAssetStore =>
        _protocolVersion >= 8 &&
        SupportsCapability("ASSETSTORE");
    public bool SupportsExactGifTiming =>
        _protocolVersion >= 9 &&
        SupportsCapability("GIFSOURCE");
    public bool SupportsRawGif =>
        _protocolVersion >= 10 &&
        SupportsCapability("GIFRAW");
    public bool SupportsBinaryGifUpload =>
        _protocolVersion >= 10 &&
        SupportsCapability("GIFBIN");
    public bool SupportsFlowControlledBinaryGifUpload =>
        _protocolVersion >= 10 &&
        SupportsCapability("GIFBIN3");

    private bool SupportsCapability(string name) =>
        _protocolVersion >= 3 &&
        (_capabilities.Count == 0 || _capabilities.Contains(name));

    private void SetFirmwareHello(string hello)
    {
        FirmwareHello = hello ?? "";
        _protocolVersion = 0;
        _capabilities.Clear();

        string[] parts = FirmwareHello.Split('|');
        if (parts.Length >= 2 &&
            string.Equals(parts[0], "LUMIPAD", StringComparison.Ordinal) &&
            int.TryParse(parts[1], out int version))
        {
            _protocolVersion = version;
        }

        foreach (string part in parts)
        {
            if (!part.StartsWith("CAPS=", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string cap in part["CAPS=".Length..].Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                _capabilities.Add(cap);
            }
        }

        // Protocol v3 defines these capabilities as the baseline. BLE status
        // reads may only contain LUMIPAD|3|SAVER:* rather than the CAPS field.
        if (_protocolVersion >= 3 && _capabilities.Count == 0)
        {
            foreach (string cap in new[]
                     {
                         "MEM", "PANEL", "LOG", "SAVERSTATE",
                         "PROFILE", "ACTION", "ARTVAR", "BAT", "PCMON"
                     })
            {
                _capabilities.Add(cap);
            }

            // Protocol v4 adds the paced WriteWithoutResponse media stream.
            // Keeping it behind v4 prevents new apps from using the fast path
            // against older protocol-v3 firmware whose BLE status read has no
            // explicit CAPS field.
            if (_protocolVersion >= 4)
                _capabilities.Add("MEDIAFAST");

            if (_protocolVersion >= 5)
                _capabilities.Add("PROFILECAT");

            if (_protocolVersion >= 6)
                _capabilities.Add("POWERSTATE");

            if (_protocolVersion >= 7)
                _capabilities.Add("HIBERNATE");

            if (_protocolVersion >= 8)
            {
                _capabilities.Add("EXTFLASH");
                _capabilities.Add("ASSETSTORE");
            }

            if (_protocolVersion >= 9)
                _capabilities.Add("GIFSOURCE");

            if (_protocolVersion >= 10)
            {
                _capabilities.Add("GIFRAW");
                _capabilities.Add("GIFBIN");
            }
        }

        Log(
            "INFO",
            $"Firmware protocol v{_protocolVersion}; caps=" +
            string.Join(",", _capabilities));
    }

    private static HashSet<string> ReservedPixelProPorts()
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = Convert.ToString(obj["Name"]) ?? "";
                string pnp = Convert.ToString(obj["PNPDeviceID"]) ?? "";

                if (!pnp.Contains("VID_303A", StringComparison.OrdinalIgnoreCase) ||
                    !pnp.Contains("PID_80C2", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = Regex.Match(
                    name,
                    @"\((COM\d+)\)",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                    ports.Add(match.Groups[1].Value);
            }
        }
        catch
        {
        }

        return ports;
    }

    private static IEnumerable<string> RynorUsbPorts()
    {
        HashSet<string> reserved = ReservedPixelProPorts();

        return SerialPort.GetPortNames()
            .Where(name => !reserved.Contains(name))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    private static string CurrentUsbPortSignature() =>
        string.Join(";", RynorUsbPorts());

    private void RecordLinkSuccess() =>
        _consecutiveLinkFailures = 0;

    private void RecordLinkFailure(string context, Exception ex)
    {
        _consecutiveLinkFailures++;
        Log(
            "WARN",
            $"{context} failed ({_consecutiveLinkFailures}/3): {ex.Message}");

        bool transportGone =
            (_port is not null && !_port.IsOpen) ||
            (_port is null && _bleCharacteristic is null);

        if (!transportGone && _consecutiveLinkFailures < 3)
            return;

        LinkError?.Invoke(ex.Message);
        Disconnect();
    }

    private void Log(string level, string message) =>
        Diagnostic?.Invoke(level, message);

    public async Task<string?> AutoDetectAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
                return _connectionName;

            Log("INFO", "Auto detect started");
            Disconnect();

            // Prefer the dedicated USB CDC link when the keyboard is physically
            // connected. It is much faster for media/screensaver transfers.
            var usb = await TryUsbAsync(cancellationToken);
            if (usb is not null)
                return usb;

            return await TryBluetoothAsync(cancellationToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<string?> ConnectUsbAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_port?.IsOpen == true)
                return _connectionName;

            Disconnect();
            return await TryUsbAsync(cancellationToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<string?> ConnectBluetoothAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsBleTransportConnected)
                return _connectionName;

            Disconnect();
            return await TryBluetoothAsync(cancellationToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<string?> PromoteToUsbIfAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            // Do not tear down BLE in the middle of a Now Playing/artwork packet.
            // Wait for both media and normal command writers to become idle, then
            // switch transports atomically from the app's point of view.
            await _mediaGate.WaitAsync(cancellationToken);
        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                if (_port?.IsOpen == true)
                {
                    string usbName = $"USB · {_port.PortName}";

                    if (_bleDevice is not null)
                    {
                        _bleDevice.ConnectionStatusChanged -=
                            OnBleConnectionStatusChanged;
                    }

                    _bleCharacteristic = null;
                    _blePayloadSize = 20;
                    _bleService?.Dispose();
                    _bleService = null;
                    _bleDevice?.Dispose();
                    _bleDevice = null;
                    _connectionName = usbName;

                    Log("INFO", $"Promoted companion link to {usbName}");
                    return usbName;
                }

                string currentPorts = CurrentUsbPortSignature();
                if (string.Equals(
                        currentPorts,
                        _lastUsbPortSignature,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                _lastUsbPortSignature = currentPorts;

                string previousName = _connectionName;
                string? usb = await TryUsbAsync(cancellationToken);

                if (usb is null)
                {
                    _connectionName = previousName;
                    return null;
                }

                // USB becomes the active companion-app transport. Keep media
                // state and cached artwork keys intact so a cable insertion
                // does not restart the Now Playing pipeline.
                if (_bleDevice is not null)
                {
                    _bleDevice.ConnectionStatusChanged -=
                        OnBleConnectionStatusChanged;
                }

                _bleCharacteristic = null;
                _blePayloadSize = 20;
                _bleService?.Dispose();
                _bleService = null;
                _bleDevice?.Dispose();
                _bleDevice = null;

                Log("INFO", $"Promoted companion link to {usb}");
                return usb;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            _mediaGate.Release();
        }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<string?> TryBluetoothAsync(CancellationToken cancellationToken)
    {
        // Windows can expose a BLE HID keyboard through different device
        // interfaces depending on whether it is already connected, merely
        // paired, or cached. Probe paired devices first, then the complete BLE
        // device set. This fixes cases where the keyboard works as HID but the
        // companion app cannot see the custom GATT service.
        string[] selectors =
        [
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelector()
        ];

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string selector in selectors)
        {
            try
            {
                DeviceInformationCollection devices =
                    await DeviceInformation.FindAllAsync(selector);

                Log("INFO", $"BLE scan returned {devices.Count} device(s)");

                foreach (var info in devices)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!seenIds.Add(info.Id))
                        continue;

                    BluetoothLEDevice? candidate = null;
                    GattDeviceService? service = null;

                    try
                    {
                        candidate = await BluetoothLEDevice.FromIdAsync(info.Id);
                        if (candidate is null)
                            continue;

                        var services = await candidate.GetGattServicesForUuidAsync(
                            ServiceUuid,
                            BluetoothCacheMode.Uncached);

                        if (services.Status != GattCommunicationStatus.Success ||
                            services.Services.Count == 0)
                        {
                            candidate.Dispose();
                            continue;
                        }

                        service = services.Services[0];
                        var chars = await service.GetCharacteristicsForUuidAsync(
                            CharacteristicUuid,
                            BluetoothCacheMode.Uncached);

                        if (chars.Status != GattCommunicationStatus.Success ||
                            chars.Characteristics.Count == 0)
                        {
                            service.Dispose();
                            candidate.Dispose();
                            continue;
                        }

                        var characteristic = chars.Characteristics[0];
                        var props = characteristic.CharacteristicProperties;
                        bool canWrite =
                            (props & GattCharacteristicProperties.Write) != 0 ||
                            (props & GattCharacteristicProperties.WriteWithoutResponse) != 0;

                        if (!canWrite)
                        {
                            Log("WARN", $"BLE characteristic is not writable: {info.Name}");
                            service.Dispose();
                            candidate.Dispose();
                            continue;
                        }

                        // Do not reject a valid LumiPad service just because a
                        // one-shot status read is temporarily unavailable. The
                        // service + characteristic UUID pair uniquely identifies
                        // this firmware. Read the hello/status opportunistically.
                        string hello = "LUMIPAD|3";
                        if ((props & GattCharacteristicProperties.Read) != 0)
                        {
                            try
                            {
                                var read = await characteristic.ReadValueAsync(
                                    BluetoothCacheMode.Uncached);

                                if (read.Status == GattCommunicationStatus.Success)
                                {
                                    using var reader = DataReader.FromBuffer(read.Value);
                                    string value =
                                        reader.ReadString(reader.UnconsumedBufferLength)
                                              .Trim('\0', '\r', '\n', ' ');

                                    if (!string.IsNullOrWhiteSpace(value))
                                        hello = value;
                                }
                                else
                                {
                                    Log("WARN", $"BLE status read: {read.Status}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("WARN", $"BLE status read failed: {ex.Message}");
                            }
                        }

                        try
                        {
                            service.Session.MaintainConnection = true;
                        }
                        catch
                        {
                            // Older Windows builds can reject MaintainConnection;
                            // normal GATT operations still keep the link usable.
                        }

                        _bleDevice = candidate;
                        _bleService = service;
                        _bleCharacteristic = characteristic;

                        try
                        {
                            _blePayloadSize = Math.Clamp(
                                (int)service.Session.MaxPduSize - 3,
                                20,
                                244);
                        }
                        catch
                        {
                            _blePayloadSize = 20;
                        }

                        candidate.ConnectionStatusChanged += OnBleConnectionStatusChanged;

                        SetFirmwareHello(hello);
                        RecordLinkSuccess();
                        _lastUsbPortSignature = CurrentUsbPortSignature();
                        _connectionName =
                            $"Bluetooth · {(!string.IsNullOrWhiteSpace(info.Name) ? info.Name : candidate.Name)}";

                        if (string.IsNullOrWhiteSpace(_connectionName.TrimEnd()))
                            _connectionName = "Bluetooth · RYNOR ONE";

                        Log("INFO",
                            $"Connected {_connectionName}; {FirmwareHello}; BLE payload={_blePayloadSize} bytes");
                        return _connectionName;
                    }
                    catch (Exception ex)
                    {
                        Log("WARN", $"BLE probe {info.Name}: {ex.Message}");
                        service?.Dispose();
                        candidate?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("WARN", $"Bluetooth selector failed: {ex.Message}");
            }
        }

        Log("WARN", "Bluetooth LumiPad service not found");
        return null;
    }

    private void OnBleConnectionStatusChanged(
        BluetoothLEDevice sender,
        object args)
    {
        // Ignore late callbacks from a BLE object that belonged to an older
        // session. Otherwise a stale disconnect can tear down a fresh reconnect.
        if (!ReferenceEquals(sender, _bleDevice) ||
            sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
        {
            return;
        }

        Log("WARN", "Bluetooth disconnected");
        LinkError?.Invoke("Bluetooth disconnected");
        Disconnect();
    }

    private async Task<string?> TryUsbAsync(CancellationToken cancellationToken)
    {
        foreach (var name in RynorUsbPorts())
        {
            cancellationToken.ThrowIfCancellationRequested();

            SerialPort? candidate = null;
            try
            {
                candidate = new SerialPort(name, 115200)
                {
                    ReadTimeout = 300,
                    WriteTimeout = 300,
                    DtrEnable = true,
                    NewLine = "\n"
                };

                candidate.Open();
                await Task.Delay(120, cancellationToken);

                candidate.DiscardInBuffer();
                candidate.WriteLine("HELLO");

                var response = await Task.Run(() =>
                {
                    try { return candidate.ReadLine().Trim(); }
                    catch { return ""; }
                }, cancellationToken);

                if (response.StartsWith("LUMIPAD|", StringComparison.Ordinal))
                {
                    _port = candidate;
                    SetFirmwareHello(response);
                    RecordLinkSuccess();
                    _lastUsbPortSignature = CurrentUsbPortSignature();
                    _connectionName = $"USB · {name}";
                    Log("INFO", $"Connected {_connectionName}; {FirmwareHello}");
                    return _connectionName;
                }
            }
            catch (Exception ex)
            {
                Log("WARN", $"USB probe {name}: {ex.Message}");
            }

            candidate?.Dispose();
        }

        Log("WARN", "USB LumiPad CDC not found");
        return null;
    }

    private async Task<bool> EnsureUsbForBulkAsync(
        CancellationToken cancellationToken = default)
    {
        if (_port?.IsOpen == true)
            return true;

        foreach (var name in RynorUsbPorts())
        {
            cancellationToken.ThrowIfCancellationRequested();

            SerialPort? candidate = null;
            try
            {
                candidate = new SerialPort(name, 115200)
                {
                    ReadTimeout = 350,
                    WriteTimeout = 1200,
                    DtrEnable = true,
                    NewLine = "\n"
                };

                candidate.Open();
                await Task.Delay(80, cancellationToken);

                candidate.DiscardInBuffer();
                candidate.WriteLine("HELLO");

                string response = await Task.Run(() =>
                {
                    try { return candidate.ReadLine().Trim(); }
                    catch { return ""; }
                }, cancellationToken);

                if (response.StartsWith("LUMIPAD|", StringComparison.Ordinal))
                {
                    _port = candidate;
                    return true;
                }
            }
            catch
            {
            }

            candidate?.Dispose();
        }

        return false;
    }

    private async Task SendUsbLineAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (_port?.IsOpen != true)
                throw new IOException("LumiPad USB link is not available.");

            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            _port.Write(data, 0, data.Length);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<string> SendUsbSaverLineAsync(
        string line,
        bool quiet = false)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (_port?.IsOpen != true)
                throw new IOException("LumiPad USB link is not available.");

            _port.ReadTimeout = 10000;
            _port.WriteTimeout = 10000;

            byte[] data =
                Encoding.UTF8.GetBytes(
                    line + "\n");

            // SerialPort.Write can block while the MCU erases/writes a flash
            // page. Keep that wait off the WPF dispatcher so the app never
            // looks frozen during a media transfer.
            await Task.Run(
                () => _port.Write(
                    data,
                    0,
                    data.Length));

            string ack =
                await Task.Run(
                    () => _port.ReadLine().Trim());

            bool isError =
                ack.EndsWith(
                    "|ERROR",
                    StringComparison.Ordinal);

            bool importantAck =
                isError ||
                ack.EndsWith(
                    "|BEGIN",
                    StringComparison.Ordinal) ||
                ack.EndsWith(
                    "|READY",
                    StringComparison.Ordinal);

            if (!quiet || importantAck)
            {
                Log(
                    isError ? "ERROR" : "FW",
                    $"USB <- {ack}");
            }

            bool knownAck =
                ack.StartsWith(
                    "SAVACK|",
                    StringComparison.Ordinal) ||
                ack.StartsWith(
                    "ASSETACK|",
                    StringComparison.Ordinal) ||
                ack.StartsWith(
                    "GIFACK|",
                    StringComparison.Ordinal);

            if (!knownAck)
            {
                throw new IOException(
                    $"Unexpected LumiPad USB response: {ack}");
            }

            if (isError)
            {
                throw new IOException(
                    "LumiPad rejected a media/storage chunk.");
            }

            return ack;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Disconnect()
    {
        if (_port is not null)
        {
            try { _port.Close(); } catch { }
            _port.Dispose();
            _port = null;
        }

        BluetoothLEDevice? bleDevice = _bleDevice;
        GattDeviceService? bleService = _bleService;

        // Clear active references first so any callback racing with disposal is
        // recognized as stale by OnBleConnectionStatusChanged.
        _bleCharacteristic = null;
        _bleService = null;
        _bleDevice = null;
        _blePayloadSize = 20;

        if (bleDevice is not null)
            bleDevice.ConnectionStatusChanged -= OnBleConnectionStatusChanged;

        bleService?.Dispose();
        bleDevice?.Dispose();

        if (!string.IsNullOrWhiteSpace(_connectionName))
            Log("INFO", $"Disconnected {_connectionName}");

        _connectionName = "";
        FirmwareHello = "";
        _protocolVersion = 0;
        _capabilities.Clear();
        _consecutiveLinkFailures = 0;
        _lastBitmapKey = "";
        _lastArtworkKey = "";
        _lastNowPlayingKey = "";
        _lastNowPlayingSent = DateTimeOffset.MinValue;
        lock (_pendingNowPlayingLock)
        {
            _pendingNowPlaying = null;
            _mediaGeneration++;
        }
    }

    public void SendNowPlaying(NowPlayingData data)
    {
        if (!IsConnected)
            return;

        string key = $"{data.SourceName}\u001F{data.Title}\u001F{data.Artist}";
        bool trackChanged =
            !string.Equals(key, _lastNowPlayingKey, StringComparison.Ordinal);
        bool important =
            trackChanged ||
            data.IsPlaying != _lastNowPlayingPlaying;

        int generation;
        lock (_pendingNowPlayingLock)
        {
            if (trackChanged)
                _mediaGeneration++;

            generation = _mediaGeneration;
        }

        if (!important &&
            DateTimeOffset.UtcNow - _lastNowPlayingSent <
                TimeSpan.FromMilliseconds(1600))
        {
            return;
        }

        // If artwork/text from the previous track is still being sent,
        // remember only the newest state. It will be pushed immediately when
        // the current transfer completes instead of waiting for another poll.
        if (!_mediaGate.Wait(0))
        {
            lock (_pendingNowPlayingLock)
                _pendingNowPlaying = data;
            return;
        }

        _ = SendNowPlayingAsync(data, key, generation);
    }

    private async Task SendNowPlayingAsync(
        NowPlayingData data,
        string key,
        int generation)
    {
        try
        {
            var source = Uri.EscapeDataString(data.SourceName ?? "MUSIC");
            var title = Uri.EscapeDataString(data.Title ?? "");
            var artist = Uri.EscapeDataString(data.Artist ?? "");

            string nowPlayingLine =
                $"NP|{Math.Max(0, (long)data.Position.TotalMilliseconds)}|" +
                $"{Math.Max(0, (long)data.Duration.TotalMilliseconds)}|" +
                $"{(data.IsPlaying ? 1 : 0)}|{source}|{title}|{artist}";

            if (!await SendRealtimeLineAsync(nowPlayingLine))
                return;

            _lastNowPlayingKey = key;
            _lastNowPlayingPlaying = data.IsPlaying;
            _lastNowPlayingSent = DateTimeOffset.UtcNow;

            string bitmapKey = $"{data.Title}\u001F{data.Artist}";
            if (!string.Equals(bitmapKey, _lastBitmapKey, StringComparison.Ordinal))
            {
                if (await SendUnicodeBitmapsAsync(
                        data.Title ?? "",
                        data.Artist ?? "",
                        generation))
                {
                    _lastBitmapKey = bitmapKey;
                }
            }

            if (data.ArtworkRgb332 is { Length: 5776 } artwork &&
                !string.Equals(bitmapKey, _lastArtworkKey, StringComparison.Ordinal))
            {
                if (await SendArtworkAsync(
                        artwork,
                        generation,
                        nowPlayingLine))
                {
                    _lastArtworkKey = bitmapKey;
                }
            }

            // Refresh the state after the heavier title/artwork transfer so
            // the firmware never ages out of the music page during a track
            // transition on a busy Bluetooth link.
            await SendRealtimeLineAsync(nowPlayingLine);
        }
        finally
        {
            _mediaGate.Release();

            NowPlayingData? pending = null;
            lock (_pendingNowPlayingLock)
            {
                pending = _pendingNowPlaying;
                _pendingNowPlaying = null;
            }

            if (pending is not null && IsConnected)
                SendNowPlaying(pending);
        }
    }

    private bool IsMediaGenerationCurrent(int generation)
    {
        lock (_pendingNowPlayingLock)
            return generation == _mediaGeneration;
    }

    private static bool ContainsNonBasicLatin(string value) =>
        value.Any(ch => ch > 0x7F);

    private async Task<bool> SendUnicodeBitmapsAsync(
        string title,
        string artist,
        int generation)
    {
        try
        {
            var titleBitmap = TextBitmapRenderer.RenderScrollable(
                title, 206, 480, 24, 16, true);
            var artistBitmap = TextBitmapRenderer.RenderScrollable(
                artist, 206, 360, 20, 13, false);

            bool bluetooth = _bleCharacteristic is not null;

            // On Bluetooth, short plain-ASCII titles can be rendered directly
            // by the firmware's Montserrat labels. Avoiding bitmap transfer
            // saves several KB on most track changes. Long/Unicode text still
            // uses the bitmap path so scrolling and Vietnamese/Unicode remain
            // correct.
            bool sendTitle =
                !bluetooth ||
                titleBitmap.Width > 206 ||
                ContainsNonBasicLatin(title);

            bool sendArtist =
                !bluetooth ||
                artistBitmap.Width > 206 ||
                ContainsNonBasicLatin(artist);

            if (sendTitle &&
                !await SendTextBitmapAsync(
                    "T", titleBitmap, 24, generation))
            {
                return false;
            }

            if (sendArtist &&
                !await SendTextBitmapAsync(
                    "A", artistBitmap, 20, generation))
            {
                return false;
            }

            Log(
                "INFO",
                $"Now Playing text: title={(sendTitle ? "bitmap" : "native")} " +
                $"{titleBitmap.Width}px, artist={(sendArtist ? "bitmap" : "native")} " +
                $"{artistBitmap.Width}px");

            return true;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Now Playing text transfer failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SendTextBitmapAsync(
        string kind,
        RenderedTextBitmap bitmap,
        int height,
        int generation)
    {
        byte[] packed = Convert.FromHexString(bitmap.Hex);
        bool fastBle =
            _bleCharacteristic is not null &&
            SupportsFastMedia;
        int rawChunk = fastBle ? 240 : 360;
        var started = DateTimeOffset.UtcNow;

        if (!await SendRealtimeLineAsync(
                $"TXTBEGIN|{kind}|{bitmap.Width}|{height}|{packed.Length}"))
        {
            return false;
        }

        for (int offset = 0; offset < packed.Length; offset += rawChunk)
        {
            if (!IsMediaGenerationCurrent(generation))
                return false;

            int len = Math.Min(rawChunk, packed.Length - offset);

            if (fastBle)
            {
                // Packed text is already 1-bit. Base64 adds ~33% overhead,
                // versus HEX's 100% overhead in the legacy protocol.
                string base64 =
                    Convert.ToBase64String(packed, offset, len);

                if (!await SendFastMediaLineAsync(
                        $"TXTCHUNK64|{kind}|{offset}|{base64}"))
                {
                    return false;
                }
            }
            else
            {
                string hex =
                    Convert.ToHexString(packed, offset, len);

                if (!await SendRealtimeLineAsync(
                        $"TXTCHUNK|{kind}|{offset}|{hex}"))
                {
                    return false;
                }
            }
        }

        // Let the controller drain queued write commands before the
        // acknowledged TXTEND request. ATT preserves ordering; this short
        // guard also avoids overrunning conservative Windows BLE stacks.
        if (fastBle)
            await Task.Delay(8);

        bool ok =
            await SendRealtimeLineAsync($"TXTEND|{kind}");

        if (ok)
        {
            Log(
                "INFO",
                $"Now Playing text {kind} sent in " +
                $"{(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms " +
                $"({packed.Length} bytes, BLE payload={_blePayloadSize}, " +
                $"fast={fastBle})");
        }

        return ok;
    }

    private static byte[] DownsampleRgb332(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return source;

        var target = new byte[targetWidth * targetHeight];

        for (int y = 0; y < targetHeight; y++)
        {
            double srcY =
                ((y + 0.5) * sourceHeight / targetHeight) - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(srcY), 0, sourceHeight - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
            double fy = Math.Clamp(srcY - y0, 0.0, 1.0);

            for (int x = 0; x < targetWidth; x++)
            {
                double srcX =
                    ((x + 0.5) * sourceWidth / targetWidth) - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(srcX), 0, sourceWidth - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                double fx = Math.Clamp(srcX - x0, 0.0, 1.0);

                static (double R, double G, double B) Decode(byte v) =>
                    (
                        (((v >> 5) & 0x07) * 255.0) / 7.0,
                        (((v >> 2) & 0x07) * 255.0) / 7.0,
                        ((v & 0x03) * 255.0) / 3.0
                    );

                var c00 = Decode(source[y0 * sourceWidth + x0]);
                var c10 = Decode(source[y0 * sourceWidth + x1]);
                var c01 = Decode(source[y1 * sourceWidth + x0]);
                var c11 = Decode(source[y1 * sourceWidth + x1]);

                double r0 = c00.R + (c10.R - c00.R) * fx;
                double g0 = c00.G + (c10.G - c00.G) * fx;
                double b0 = c00.B + (c10.B - c00.B) * fx;
                double r1 = c01.R + (c11.R - c01.R) * fx;
                double g1 = c01.G + (c11.G - c01.G) * fx;
                double b1 = c01.B + (c11.B - c01.B) * fx;

                byte r = (byte)Math.Clamp(
                    (int)Math.Round(r0 + (r1 - r0) * fy), 0, 255);
                byte g = (byte)Math.Clamp(
                    (int)Math.Round(g0 + (g1 - g0) * fy), 0, 255);
                byte b = (byte)Math.Clamp(
                    (int)Math.Round(b0 + (b1 - b0) * fy), 0, 255);

                target[y * targetWidth + x] =
                    (byte)(((r >> 5) << 5) |
                           ((g >> 5) << 2) |
                           (b >> 6));
            }
        }

        return target;
    }

    private async Task<bool> SendArtworkAsync(
        byte[] artwork,
        int generation,
        string heartbeatLine)
    {
        try
        {
            const int fullSize = 76;
            const int bleSize = 48;
            const int rawChunk = 240;

            bool compactBle =
                _bleCharacteristic is not null &&
                SupportsVariableArtwork;
            bool fastBle =
                _bleCharacteristic is not null &&
                SupportsFastMedia;
            var started = DateTimeOffset.UtcNow;

            int width = compactBle ? bleSize : fullSize;
            int height = compactBle ? bleSize : fullSize;
            byte[] payload = compactBle
                ? DownsampleRgb332(
                    artwork,
                    fullSize,
                    fullSize,
                    bleSize,
                    bleSize)
                : artwork;

            string begin = compactBle
                ? $"ARTBEGIN|{payload.Length}|{width}|{height}"
                : $"ARTBEGIN|{payload.Length}";

            if (!await SendRealtimeLineAsync(begin))
                return false;

            int chunkNumber = 0;
            for (int offset = 0; offset < payload.Length; offset += rawChunk)
            {
                if (!IsMediaGenerationCurrent(generation))
                    return false;

                int len = Math.Min(rawChunk, payload.Length - offset);
                string base64 =
                    Convert.ToBase64String(payload, offset, len);
                string line =
                    $"ARTCHUNK|{offset}|{base64}";

                bool ok = fastBle
                    ? await SendFastMediaLineAsync(line)
                    : await SendRealtimeLineAsync(line);

                if (!ok)
                    return false;

                chunkNumber++;

                // Only legacy acknowledged transfers need a periodic media
                // heartbeat. The v4 fast path completes far sooner.
                if (!fastBle && (chunkNumber % 6) == 0)
                {
                    if (!await SendRealtimeLineAsync(heartbeatLine))
                        return false;
                }
            }

            if (fastBle)
                await Task.Delay(8);

            if (!await SendRealtimeLineAsync("ARTEND"))
                return false;

            Log(
                "INFO",
                $"Now Playing artwork sent in " +
                $"{(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms: " +
                $"{width}x{height}, {payload.Length} bytes, " +
                $"BLE payload={_blePayloadSize}, fast={fastBle}");

            return true;
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Now Playing artwork transfer failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendScreensaverAnimationAsync(
        ScreensaverAnimation animation,
        IProgress<int>? progress = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("LumiPad is not connected.");

        await _connectGate.WaitAsync();
        try
        {
            await _mediaGate.WaitAsync();
            System.Threading.Interlocked.Exchange(
                ref _exclusiveMediaTransfer,
                1);

            try
            {
            // Large RYNOR media always prefers the dedicated USB CDC link.
            // BLE remains a byte-for-byte fallback when USB is unavailable.
            bool useUsb = await EnsureUsbForBulkAsync();

            // Native RYNOR GIF path: upload the original GIF file exactly as
            // selected. No reduced frame pack, FPS resampling, RYQ1
            // recompression, or compatibility quality ladder.
            if (ScreensaverMediaService.TryGetRawGifSource(
                    animation,
                    out RynorRawGifSource rawGif))
            {
                if (!SupportsRawGif)
                {
                    Log(
                        "WARN",
                        "Connected RYNOR firmware is too old for native GIF storage.");
                    return false;
                }

                return await SendRynorRawGifAsync(
                    rawGif,
                    useUsb,
                    progress);
            }

            // Static images remain a single full-panel RGB565 asset.
            if (animation.PixelFormat != ScreensaverPixelFormat.Rgb565 ||
                animation.Frames.Count != 1 ||
                animation.Width != ScreensaverMediaService.StaticWidth ||
                animation.Height != ScreensaverMediaService.StaticHeight)
            {
                throw new InvalidOperationException(
                    "RYNOR ONE accepts native GIF assets or one 320x172 RGB565 image.");
            }

            byte[] image = animation.Frames[0];
            int expectedBytes =
                animation.Width * animation.Height * 2;

            if (image.Length != expectedBytes)
                throw new InvalidOperationException(
                    "Invalid RGB565 static image size.");

            const int rawChunk = 240;
            int staticTotalChunks =
                (image.Length + rawChunk - 1) / rawChunk;
            int sent = 0;

            Log(
                "INFO",
                $"Static saver upload: {animation.Width}x{animation.Height} RGB565, " +
                $"{image.Length} bytes, transport={(useUsb ? "USB" : "BLE")}");

            string staticBegin =
                $"IMGBEGIN|{image.Length}";

            if (useUsb)
                await SendUsbSaverLineAsync(staticBegin);
            else
                await SendLineAsync(staticBegin);

            for (int offset = 0;
                 offset < image.Length;
                 offset += rawChunk)
            {
                int len =
                    Math.Min(
                        rawChunk,
                        image.Length - offset);

                string payload =
                    Convert.ToBase64String(
                        image,
                        offset,
                        len);

                string line =
                    $"IMGCHUNK|{offset}|{payload}";

                if (useUsb)
                    await SendUsbSaverLineAsync(line);
                else
                    await SendBulkLineAsync(line);

                if (!useUsb)
                    await Task.Delay(2);

                sent++;
                progress?.Report(
                    (int)Math.Round(
                        sent *
                        100.0 /
                        staticTotalChunks));
            }

            if (useUsb)
            {
                string finalAck =
                    await SendUsbSaverLineAsync("IMGEND");

                if (!finalAck.EndsWith(
                        "|READY",
                        StringComparison.Ordinal))
                {
                    Log(
                        "ERROR",
                        $"Static saver final ACK not READY: {finalAck}");
                    return false;
                }

                progress?.Report(100);
                return true;
            }

            await SendLineAsync("IMGEND");
            await Task.Delay(120);

            if (_bleCharacteristic is null)
                return false;

            string status =
                await ReadBleStatusAsync();

            bool ready =
                status.Contains(
                    "SAVER:READY",
                    StringComparison.Ordinal);

            Log(
                ready ? "INFO" : "ERROR",
                $"BLE static saver verify: {status}");

            if (ready)
                progress?.Report(100);

            return ready;
        }
            finally
            {
                System.Threading.Interlocked.Exchange(
                    ref _exclusiveMediaTransfer,
                    0);
                _mediaGate.Release();
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<bool> SendRynorRawGifAsync(
        RynorRawGifSource source,
        bool useUsb,
        IProgress<int>? progress)
    {
        if (!SupportsRawGif ||
            string.IsNullOrWhiteSpace(source.Path) ||
            !File.Exists(source.Path) ||
            source.Length < 13 ||
            source.Length > ScreensaverMediaService.MaxRawGifBytes)
        {
            return false;
        }

        long actualLength =
            new FileInfo(source.Path).Length;

        if (actualLength != source.Length)
        {
            Log(
                "ERROR",
                "RYNOR GIF source changed after it was selected.");
            return false;
        }

        DateTimeOffset started =
            DateTimeOffset.UtcNow;

        if (useUsb && _port?.IsOpen == true)
        {
            await _writeGate.WaitAsync();
            try
            {
                SerialPort? port = _port;
                if (port?.IsOpen != true)
                    return false;

                bool useFlowControl =
                    SupportsFlowControlledBinaryGifUpload;

                var result =
                    await Task.Run(
                        () =>
                        {
                            int previousReadTimeout =
                                port.ReadTimeout;
                            int previousWriteTimeout =
                                port.WriteTimeout;

                            try
                            {
                                port.ReadTimeout = 20000;
                                port.WriteTimeout = 20000;
                                port.DiscardInBuffer();

                                string beginCommand =
                                    useFlowControl
                                        ? $"GIFBIN3BEGIN|{source.Length}|{(int)source.ScaleMode}\n"
                                        : $"GIFBEGIN|{source.Length}|{(int)source.ScaleMode}\n";

                                byte[] beginBytes =
                                    Encoding.UTF8.GetBytes(
                                        beginCommand);

                                port.Write(
                                    beginBytes,
                                    0,
                                    beginBytes.Length);

                                string beginAck =
                                    port.ReadLine().Trim();

                                if (!string.Equals(
                                        beginAck,
                                        "GIFACK|BEGIN",
                                        StringComparison.Ordinal))
                                {
                                    return (
                                        Ready: false,
                                        Sent: 0L,
                                        Ack: beginAck,
                                        Mode: useFlowControl
                                            ? "binary-flow"
                                            : "chunked");
                                }

                                long sent = 0;
                                int lastPercent = -1;

                                void ReportProgress()
                                {
                                    if (progress is null)
                                        return;

                                    int percent =
                                        (int)Math.Clamp(
                                            sent * 100L /
                                            Math.Max(
                                                1L,
                                                source.Length),
                                            0L,
                                            100L);

                                    if (percent == lastPercent)
                                        return;

                                    lastPercent = percent;
                                    progress.Report(percent);
                                }

                                if (useFlowControl)
                                {
                                    const int blockSize = 1024;
                                    byte[] buffer =
                                        new byte[blockSize];

                                    using var stream =
                                        new FileStream(
                                            source.Path,
                                            FileMode.Open,
                                            FileAccess.Read,
                                            FileShare.Read,
                                            blockSize,
                                            FileOptions.SequentialScan);

                                    while (sent < source.Length)
                                    {
                                        int wanted =
                                            (int)Math.Min(
                                                blockSize,
                                                source.Length - sent);

                                        int filled = 0;
                                        while (filled < wanted)
                                        {
                                            int read =
                                                stream.Read(
                                                    buffer,
                                                    filled,
                                                    wanted - filled);

                                            if (read <= 0)
                                                break;

                                            filled += read;
                                        }

                                        if (filled != wanted)
                                        {
                                            return (
                                                Ready: false,
                                                Sent: sent,
                                                Ack: "SHORT_READ",
                                                Mode: "binary-flow");
                                        }

                                        port.Write(
                                            buffer,
                                            0,
                                            filled);

                                        sent += filled;

                                        string ack =
                                            port.ReadLine().Trim();

                                        string expected =
                                            sent < source.Length
                                                ? $"GIFACK|BLOCK|{sent}"
                                                : "GIFACK|READY";

                                        if (!string.Equals(
                                                ack,
                                                expected,
                                                StringComparison.Ordinal))
                                        {
                                            return (
                                                Ready: false,
                                                Sent: sent,
                                                Ack: ack,
                                                Mode: "binary-flow");
                                        }

                                        ReportProgress();
                                    }

                                    return (
                                        Ready: true,
                                        Sent: sent,
                                        Ack: "GIFACK|READY",
                                        Mode: "binary-flow");
                                }

                                const int rawChunkSize = 840;
                                byte[] chunk =
                                    new byte[rawChunkSize];

                                using (var stream =
                                    new FileStream(
                                        source.Path,
                                        FileMode.Open,
                                        FileAccess.Read,
                                        FileShare.Read,
                                        rawChunkSize,
                                        FileOptions.SequentialScan))
                                {
                                    while (true)
                                    {
                                        int count =
                                            stream.Read(
                                                chunk,
                                                0,
                                                chunk.Length);

                                        if (count <= 0)
                                            break;

                                        string base64 =
                                            Convert.ToBase64String(
                                                chunk,
                                                0,
                                                count);

                                        byte[] line =
                                            Encoding.UTF8.GetBytes(
                                                $"GIFCHUNK|{sent}|{base64}\n");

                                        port.Write(
                                            line,
                                            0,
                                            line.Length);

                                        string ack =
                                            port.ReadLine().Trim();

                                        if (!string.Equals(
                                                ack,
                                                "GIFACK|CHUNK",
                                                StringComparison.Ordinal))
                                        {
                                            return (
                                                Ready: false,
                                                Sent: sent,
                                                Ack: ack,
                                                Mode: "chunked");
                                        }

                                        sent += count;
                                        ReportProgress();
                                    }
                                }

                                if (sent != source.Length)
                                {
                                    return (
                                        Ready: false,
                                        Sent: sent,
                                        Ack: "SHORT_WRITE",
                                        Mode: "chunked");
                                }

                                byte[] endBytes =
                                    Encoding.UTF8.GetBytes(
                                        "GIFEND\n");

                                port.Write(
                                    endBytes,
                                    0,
                                    endBytes.Length);

                                string finalAck =
                                    port.ReadLine().Trim();

                                bool ready =
                                    string.Equals(
                                        finalAck,
                                        "GIFACK|READY",
                                        StringComparison.Ordinal);

                                if (ready)
                                {
                                    sent = source.Length;
                                    ReportProgress();
                                }

                                return (
                                    Ready: ready,
                                    Sent: sent,
                                    Ack: finalAck,
                                    Mode: "chunked");
                            }
                            finally
                            {
                                try
                                {
                                    port.ReadTimeout =
                                        previousReadTimeout;
                                    port.WriteTimeout =
                                        previousWriteTimeout;
                                }
                                catch
                                {
                                }
                            }
                        });

                double seconds =
                    Math.Max(
                        0.001,
                        (DateTimeOffset.UtcNow - started)
                            .TotalSeconds);

                Log(
                    result.Ready
                        ? "INFO"
                        : "ERROR",
                    $"RYNOR raw GIF USB {result.Mode}: " +
                    $"{result.Sent}/{source.Length} bytes in {seconds:F2}s " +
                    $"({result.Sent / 1024.0 / seconds:F0} KiB/s), " +
                    $"ack={result.Ack}");

                if (result.Ready)
                    progress?.Report(100);

                return result.Ready;
            }
            catch (Exception ex)
            {
                Log(
                    "ERROR",
                    $"RYNOR raw GIF USB upload failed: {ex.Message}");
                return false;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        // BLE remains a byte-for-byte fallback. Keep progress throttled to
        // integer percentages so a large GIF cannot flood the WPF dispatcher.
        const int rawChunkSize = 180;
        long offset = 0;
        int lastBlePercent = -1;

        try
        {
            await SendLineAsync(
                $"GIFBEGIN|{source.Length}|{(int)source.ScaleMode}");

            byte[] buffer =
                new byte[rawChunkSize];

            using var stream =
                new FileStream(
                    source.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    rawChunkSize,
                    FileOptions.SequentialScan);

            while (true)
            {
                int count =
                    await stream.ReadAsync(
                        buffer.AsMemory(
                            0,
                            buffer.Length));

                if (count <= 0)
                    break;

                string base64 =
                    Convert.ToBase64String(
                        buffer,
                        0,
                        count);

                await SendBulkLineAsync(
                    $"GIFCHUNK|{offset}|{base64}");

                offset += count;

                int percent =
                    (int)Math.Clamp(
                        offset * 100L /
                        Math.Max(
                            1L,
                            source.Length),
                        0L,
                        100L);

                if (percent != lastBlePercent)
                {
                    lastBlePercent = percent;
                    progress?.Report(percent);
                }

                await Task.Delay(2);
            }

            if (offset != source.Length)
                return false;

            await SendLineAsync("GIFEND");
            await Task.Delay(180);

            if (_bleCharacteristic is null)
                return false;

            string status =
                await ReadBleStatusAsync();

            bool ready =
                status.Contains(
                    "SAVER:READY",
                    StringComparison.Ordinal);

            Log(
                ready ? "INFO" : "ERROR",
                $"RYNOR raw GIF BLE verify: {status}");

            if (ready)
                progress?.Report(100);

            return ready;
        }
        catch (Exception ex)
        {
            Log(
                "ERROR",
                $"RYNOR raw GIF BLE upload failed: {ex.Message}");
            return false;
        }
    }

    private async Task<string> ReadBleStatusAsync()
    {
        var characteristic = _bleCharacteristic;
        if (characteristic is null)
            return "";

        var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (read.Status != GattCommunicationStatus.Success)
            return "";

        using var reader = DataReader.FromBuffer(read.Value);
        return reader.ReadString(reader.UnconsumedBufferLength);
    }

    public async Task<(uint Seq, string Level, string Message)?> ReadFirmwareLogAsync(
        uint afterSeq)
    {
        if (IsExclusiveMediaTransferActive ||
            !SupportsDiagnostics)
            return null;

        string response;

        try
        {
            if (_port?.IsOpen == true)
            {
                await _writeGate.WaitAsync();
                try
                {
                    _port.ReadTimeout = 900;
                    _port.DiscardInBuffer();
                    byte[] data = Encoding.UTF8.GetBytes($"LOG|{afterSeq}\n");
                    _port.Write(data, 0, data.Length);
                    response = await Task.Run(() => _port.ReadLine().Trim());
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            else if (_bleCharacteristic is not null)
            {
                await SendLineAsync($"LOG|{afterSeq}");
                await Task.Delay(35);
                response = await ReadBleStatusAsync();
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Read firmware log failed: {ex.Message}");
            return null;
        }

        if (response.StartsWith("LOG|NONE|", StringComparison.Ordinal))
            return null;

        string[] parts = response.Split('|', 4);
        if (parts.Length != 4 ||
            parts[0] != "LOG" ||
            !uint.TryParse(parts[1], out uint seq))
        {
            return null;
        }

        return (seq, parts[2], parts[3]);
    }

    public async Task<string?> GetScreensaverStateAsync()
    {
        if (IsExclusiveMediaTransferActive ||
            !SupportsSaverState)
            return null;

        try
        {
            if (_port?.IsOpen == true)
            {
                await _writeGate.WaitAsync();
                try
                {
                    _port.ReadTimeout = 900;
                    _port.DiscardInBuffer();

                    byte[] data =
                        Encoding.UTF8.GetBytes("SAVERSTATE\n");
                    _port.Write(data, 0, data.Length);

                    string response =
                        await Task.Run(() => _port.ReadLine().Trim());

                    if (response.StartsWith(
                            "SAVERSTATE|",
                            StringComparison.Ordinal))
                    {
                        return response["SAVERSTATE|".Length..];
                    }

                    Log("WARN",
                        $"Unexpected USB saver state response: {response}");
                    return null;
                }
                finally
                {
                    _writeGate.Release();
                }
            }

            if (_bleCharacteristic is not null)
            {
                await _writeGate.WaitAsync();
                try
                {
                    var characteristic = _bleCharacteristic;
                    if (characteristic is null)
                        return null;

                    byte[] data =
                        Encoding.UTF8.GetBytes("SAVERSTATE\n");

                    using var writer = new DataWriter();
                    writer.WriteBytes(data);

                    var writeStatus =
                        await characteristic.WriteValueAsync(
                            writer.DetachBuffer(),
                            GattWriteOption.WriteWithResponse);

                    if (writeStatus != GattCommunicationStatus.Success)
                    {
                        Log("WARN",
                            $"BLE saver state write failed: {writeStatus}");
                        return null;
                    }

                    // Keep the write/read pair under one gate so MEM/PANEL/LOG
                    // requests cannot overwrite the single GATT status slot.
                    await Task.Delay(35);

                    string response = await ReadBleStatusAsync();
                    if (response.StartsWith(
                            "SAVERSTATE|",
                            StringComparison.Ordinal))
                    {
                        return response["SAVERSTATE|".Length..];
                    }

                    Log("WARN",
                        $"Unexpected BLE saver state response: {response}");
                    return null;
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            Log("WARN", $"Read screensaver state failed: {ex.Message}");
        }

        return null;
    }

    public Task ShowScreensaverNowAsync(bool pcMonitor) =>
        SendLineAsync($"CFG|SAVERNOW|{(pcMonitor ? 1 : 0)}");

    public void ShowScreensaverNow() =>
        _ = ShowScreensaverNowAsync(false);

    public async Task<int?> ReadBatteryPercentAsync()
    {
        if (IsExclusiveMediaTransferActive ||
            !IsConnected ||
            !SupportsBatteryInfo)
            return null;

        string response;

        await _writeGate.WaitAsync();
        try
        {
            if (_port?.IsOpen == true)
            {
                _port.ReadTimeout = 800;
                _port.DiscardInBuffer();
                byte[] data = Encoding.UTF8.GetBytes("BAT\n");
                _port.Write(data, 0, data.Length);
                response =
                    await Task.Run(() => _port.ReadLine().Trim());
            }
            else if (_bleCharacteristic is not null)
            {
                var characteristic = _bleCharacteristic;
                byte[] data = Encoding.UTF8.GetBytes("BAT\n");

                using var writer = new DataWriter();
                writer.WriteBytes(data);

                var status = await characteristic.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithResponse);

                if (status != GattCommunicationStatus.Success)
                    return null;

                await Task.Delay(35);
                response = await ReadBleStatusAsync();
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log("WARN", $"Read battery failed: {ex.Message}");
            return null;
        }
        finally
        {
            _writeGate.Release();
        }

        string[] parts = response.Split('|');
        if (parts.Length != 2 ||
            !string.Equals(parts[0], "BAT", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out int percent))
            return null;

        return Math.Clamp(percent, 0, 100);
    }

    public async Task<(string Panel, int RefreshHz, int SpiHz, int GifMaxFps)?>
        ReadPanelInfoAsync()
    {
        if (IsExclusiveMediaTransferActive ||
            !SupportsPanelInfo)
            return null;

        string response;

        try
        {
            if (_port?.IsOpen == true)
            {
                await _writeGate.WaitAsync();
                try
                {
                    _port.ReadTimeout = 800;
                    _port.DiscardInBuffer();
                    byte[] data = Encoding.UTF8.GetBytes("PANEL\n");
                    _port.Write(data, 0, data.Length);
                    response = await Task.Run(() => _port.ReadLine().Trim());
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            else if (_bleCharacteristic is not null)
            {
                await SendLineAsync("PANEL");
                await Task.Delay(40);
                response = await ReadBleStatusAsync();
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log("WARN", $"Read panel info failed: {ex.Message}");
            return null;
        }

        string[] parts = response.Split('|');
        if (parts.Length != 5 ||
            parts[0] != "PANEL" ||
            !int.TryParse(parts[2], out int refreshHz) ||
            !int.TryParse(parts[3], out int spiHz) ||
            !int.TryParse(parts[4], out int gifMaxFps))
        {
            return null;
        }

        return (parts[1], refreshHz, spiHz, gifMaxFps);
    }

    public async Task<DeviceMemoryUsage?> ReadMemoryUsageAsync()
    {
        if (IsExclusiveMediaTransferActive ||
            !SupportsMemoryInfo)
            return null;

        string response;

        if (_port?.IsOpen == true)
        {
            await _writeGate.WaitAsync();
            try
            {
                _port.ReadTimeout = 800;
                _port.DiscardInBuffer();
                byte[] data = Encoding.UTF8.GetBytes("MEM\n");
                _port.Write(data, 0, data.Length);
                response = await Task.Run(() => _port.ReadLine().Trim());
            }
            catch
            {
                return null;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        else if (_bleCharacteristic is not null)
        {
            try
            {
                await SendLineAsync("MEM");
                await Task.Delay(35);
                response = await ReadBleStatusAsync();
            }
            catch
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        string[] parts = response.Split('|');
        if (parts.Length != 5 ||
            !string.Equals(parts[0], "MEM", StringComparison.Ordinal) ||
            !long.TryParse(parts[1], out long flashUsed) ||
            !long.TryParse(parts[2], out long flashTotal) ||
            !long.TryParse(parts[3], out long ramUsed) ||
            !long.TryParse(parts[4], out long ramTotal) ||
            flashTotal <= 0 ||
            ramTotal <= 0)
        {
            return null;
        }

        return new DeviceMemoryUsage(
            flashUsed,
            flashTotal,
            ramUsed,
            ramTotal,
            0,
            0);
    }

    public async Task<(uint Seq, int ActionId, int Position)?>
        ReadActionEventAsync(uint afterSeq)
    {
        if (!SupportsActions || !IsConnected)
            return null;

        if (!_mediaGate.Wait(0))
            return null;

        string? response = null;

        await _writeGate.WaitAsync();
        try
        {
            string command = $"ACTION|{afterSeq}\n";
            byte[] data = Encoding.UTF8.GetBytes(command);

            if (_port?.IsOpen == true)
            {
                try
                {
                    _port.ReadTimeout = 300;
                    _port.Write(data, 0, data.Length);
                    response = await Task.Run(() => _port.ReadLine().Trim());
                    RecordLinkSuccess();
                }
                catch (TimeoutException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    RecordLinkFailure("Action poll", ex);
                    return null;
                }
            }
            else if (_bleCharacteristic is not null)
            {
                try
                {
                    var characteristic = _bleCharacteristic;
                    using var writer = new DataWriter();
                    writer.WriteBytes(data);

                    var status = await characteristic.WriteValueAsync(
                        writer.DetachBuffer(),
                        GattWriteOption.WriteWithResponse);

                    if (status != GattCommunicationStatus.Success)
                        return null;

                    await Task.Delay(25);
                    response = await ReadBleStatusAsync();
                    RecordLinkSuccess();
                }
                catch (Exception ex)
                {
                    RecordLinkFailure("BLE action poll", ex);
                    return null;
                }
            }
        }
        finally
        {
            _writeGate.Release();
            _mediaGate.Release();
        }

        if (string.IsNullOrWhiteSpace(response) ||
            response.StartsWith("ACTION|NONE|", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = response.Split('|');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], "ACTION", StringComparison.Ordinal) ||
            !uint.TryParse(parts[1], out uint seq) ||
            !int.TryParse(parts[2], out int actionId) ||
            !int.TryParse(parts[3], out int position))
        {
            return null;
        }

        return (seq, actionId, position);
    }

    public void ClearScreensaverAnimation() =>
        _ = SendLineAsync("SAVCLEAR");

    public void ClearNowPlaying()
    {
        _lastBitmapKey = "";
        _lastArtworkKey = "";
        _ = SendLineAsync("CLEAR");
    }

    public void SetRgbState(bool enabled, int brightness, int speed,
                            bool autoByLayer, int effect,
                            byte r, byte g, byte b) =>
        _ = SendLineAsync(
            $"RGB|STATE|{(enabled ? 1 : 0)}|" +
            $"{Math.Clamp(brightness, 5, 50)}|" +
            $"{Math.Clamp(speed, 10, 100)}|" +
            $"{(autoByLayer ? 1 : 0)}|" +
            $"{Math.Clamp(effect, 0, 4)}|{r}|{g}|{b}");

    public void SetRgbProfile(int index, int effect, byte r, byte g, byte b) =>
        _ = SendLineAsync(
            $"RGB|PROFILE|{RynorProfiles.Clamp(index)}|" +
            $"{Math.Clamp(effect, 0, 4)}|{r}|{g}|{b}");

    public void SetEnabled(bool enabled) => _ = SendLineAsync($"RGB|EN|{(enabled ? 1 : 0)}");
    public void SetBrightness(int percent) => _ = SendLineAsync($"RGB|BRI|{Math.Clamp(percent, 5, 50)}");
    public void SetSpeed(int percent) => _ = SendLineAsync($"RGB|SPD|{Math.Clamp(percent, 10, 100)}");
    public void SetAutoLayer() => _ = SendLineAsync("RGB|AUTO");
    public void SetEffect(int effect) => _ = SendLineAsync($"RGB|FX|{effect}");
    public void SetSolid(byte r, byte g, byte b) => _ = SendLineAsync($"RGB|SOLID|{r}|{g}|{b}");

    public void SetWallpaper(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2) =>
        _ = SendLineAsync($"CFG|WALL|{r1}|{g1}|{b1}|{r2}|{g2}|{b2}");

    public void SetScreensaver(bool enabled, int style, int delaySeconds,
                               byte r1, byte g1, byte b1,
                               byte r2, byte g2, byte b2) =>
        _ = SendLineAsync(
            $"CFG|SAVER|{(enabled ? 1 : 0)}|{style}|{delaySeconds}|" +
            $"{r1}|{g1}|{b1}|{r2}|{g2}|{b2}");

    public Task SetScreensaverSourceAsync(bool pcMonitor) =>
        SendLineAsync($"CFG|SAVERSRC|{(pcMonitor ? 1 : 0)}");

    public void SetScreensaverSource(bool pcMonitor) =>
        _ = SetScreensaverSourceAsync(pcMonitor);

    public void SetScreensaverDelay(int seconds) =>
        _ = SendLineAsync($"CFG|SAVERDELAY|{Math.Max(0, seconds)}");

    public void SetSleepTimeout(int seconds) =>
        _ = SendLineAsync($"CFG|SLEEP|{Math.Max(0, seconds)}");

    public void SetRgbIdleTimeout(int seconds) =>
        _ = SendLineAsync($"CFG|RGBIDLE|{Math.Max(0, seconds)}");

    public void SetDeepSleepTimeout(int seconds) =>
        _ = SendLineAsync($"CFG|DEEPSLEEP|{Math.Max(0, seconds)}");

    public void SetHibernateTimeout(int seconds) =>
        _ = SendLineAsync($"CFG|HIBERNATE|{Math.Max(0, seconds)}");

    private async Task<string?> RequestProfileMetadataAsync(
        string command,
        string context)
    {
        if (!IsConnected || !SupportsProfileSwitch)
            return null;

        string response;

        if (!_mediaGate.Wait(0))
            return null;

        await _writeGate.WaitAsync();
        try
        {
            if (_port?.IsOpen == true)
            {
                _port.ReadTimeout = 500;
                _port.DiscardInBuffer();
                byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                _port.Write(data, 0, data.Length);
                response = await Task.Run(() => _port.ReadLine().Trim());
            }
            else if (_bleCharacteristic is not null)
            {
                var characteristic = _bleCharacteristic;
                byte[] data = Encoding.UTF8.GetBytes(command + "\n");

                using var writer = new DataWriter();
                writer.WriteBytes(data);

                var status = await characteristic.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithResponse);

                if (status != GattCommunicationStatus.Success)
                    return null;

                await Task.Delay(25);
                response = await ReadBleStatusAsync();
            }
            else
            {
                return null;
            }
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log("WARN", $"{context} failed: {ex.Message}");
            return null;
        }
        finally
        {
            _writeGate.Release();
            _mediaGate.Release();
        }

        return response;
    }

    public async Task<string?> ReadPowerStateAsync()
    {
        if (!SupportsPowerState)
            return null;

        string? response = await RequestProfileMetadataAsync(
            "POWER",
            "Read power state");

        if (string.IsNullOrWhiteSpace(response))
            return null;

        string[] parts = response.Split('|', 2);
        if (parts.Length != 2 ||
            !string.Equals(parts[0], "POWER", StringComparison.Ordinal))
        {
            return null;
        }

        string state = parts[1].Trim().ToUpperInvariant();
        return state is "AWAKE" or "SLEEP" or "DEEP"
            ? state
            : null;
    }

    public async Task<bool?> ReadSoftSleepStateAsync()
    {
        string? state = await ReadPowerStateAsync();
        return state is null
            ? null
            : !string.Equals(state, "AWAKE", StringComparison.Ordinal);
    }

    public async Task<(
        bool Ready,
        int GifBytes,
        int AssetBytes,
        int ReserveBytes,
        bool AssetReady,
        int AssetUsed)?> ReadExternalStorageInfoAsync()
    {
        if (!SupportsExternalFlash)
            return null;

        string? response = await RequestProfileMetadataAsync(
            "STORAGE",
            "Read external storage");

        if (string.IsNullOrWhiteSpace(response))
            return null;

        string[] parts = response.Split('|');
        if (parts.Length != 7 ||
            !string.Equals(parts[0], "STORAGE", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out int ready) ||
            !int.TryParse(parts[2], out int gifBytes) ||
            !int.TryParse(parts[3], out int assetBytes) ||
            !int.TryParse(parts[4], out int reserveBytes) ||
            !int.TryParse(parts[5], out int assetReady) ||
            !int.TryParse(parts[6], out int assetUsed))
        {
            return null;
        }

        return (
            ready != 0,
            Math.Max(0, gifBytes),
            Math.Max(0, assetBytes),
            Math.Max(0, reserveBytes),
            assetReady != 0,
            Math.Max(0, assetUsed));
    }

    public async Task<bool> SendExternalAssetPackAsync(
        byte[] payload,
        IProgress<int>? progress = null)
    {
        if (!SupportsAssetStore ||
            !IsConnected ||
            payload is null ||
            payload.Length < 1 ||
            payload.Length > (2 * 1024 * 1024 - 4096))
        {
            return false;
        }

        bool useUsb = _port?.IsOpen == true;
        int rawChunkSize = useUsb ? 720 : 180;
        int totalChunks =
            (payload.Length + rawChunkSize - 1) /
            rawChunkSize;

        await _mediaGate.WaitAsync();
        try
        {
            string begin = $"ASSETBEGIN|{payload.Length}";

            if (useUsb)
            {
                string ack = await SendUsbSaverLineAsync(begin);
                if (!ack.EndsWith("|BEGIN", StringComparison.Ordinal))
                    return false;
            }
            else
            {
                await SendLineAsync(begin);
            }

            int sent = 0;

            for (int offset = 0;
                 offset < payload.Length;
                 offset += rawChunkSize)
            {
                int len =
                    Math.Min(
                        rawChunkSize,
                        payload.Length - offset);

                string base64 =
                    Convert.ToBase64String(
                        payload,
                        offset,
                        len);

                string line =
                    $"ASSETCHUNK|{offset}|{base64}";

                if (useUsb)
                {
                    string ack =
                        await SendUsbSaverLineAsync(line);

                    if (!ack.EndsWith(
                            "|CHUNK",
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                else
                {
                    await SendBulkLineAsync(line);
                    await Task.Delay(2);
                }

                sent++;
                progress?.Report(
                    (int)Math.Round(
                        sent * 100.0 /
                        Math.Max(1, totalChunks)));
            }

            if (useUsb)
            {
                string ack =
                    await SendUsbSaverLineAsync("ASSETEND");

                return ack.EndsWith(
                    "|READY",
                    StringComparison.Ordinal);
            }

            await SendLineAsync("ASSETEND");
            await Task.Delay(120);

            if (_bleCharacteristic is null)
                return false;

            string status =
                await ReadBleStatusAsync();

            return status.StartsWith(
                "ASSET|READY|",
                StringComparison.Ordinal);
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    public async Task<string[]?> ReadProfileCatalogAsync()
    {
        if (!SupportsProfileCatalog)
            return null;

        var names = new string[RynorProfiles.Count];

        for (int requested = 0; requested < RynorProfiles.Count; requested++)
        {
            string? response = await RequestProfileMetadataAsync(
                $"PROFILEINFO|{requested}",
                $"Read profile {requested + 1}");

            if (string.IsNullOrWhiteSpace(response))
                return null;

            string[] parts = response.Split('|', 3);
            if (parts.Length < 3 ||
                !string.Equals(
                    parts[0],
                    "PROFILEINFO",
                    StringComparison.Ordinal) ||
                !int.TryParse(parts[1], out int returned) ||
                returned != requested)
            {
                return null;
            }

            names[requested] =
                string.IsNullOrWhiteSpace(parts[2])
                    ? RynorProfiles.DefaultName(requested)
                    : parts[2].Trim();
        }

        return names;
    }

    public async Task<(int Index, string Name)?> ReadActiveProfileAsync()
    {
        string? response = await RequestProfileMetadataAsync(
            "PROFILE",
            "Read active profile");

        if (string.IsNullOrWhiteSpace(response))
            return null;

        string[] parts = response.Split('|', 3);
        if (parts.Length < 2 ||
            !string.Equals(parts[0], "PROFILE", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out int index))
        {
            return null;
        }

        index = RynorProfiles.Clamp(index);
        string name =
            parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2])
                ? parts[2].Trim()
                : RynorProfiles.DefaultName(index);

        return (index, name);
    }

    public void SetActiveProfile(int profile)
    {
        if (!SupportsProfileSwitch)
            return;

        _ = SendLineAsync(
            $"CFG|PROFILE|{RynorProfiles.Clamp(profile)}");
    }

    public Task RestartKeyboardAsync() =>
        SendLineAsync("SYS|RESTART");

    public Task EnterDfuAsync() =>
        SendLineAsync("SYS|DFU");

    public Task SleepKeyboardAsync() =>
        SendLineAsync("SYS|SLEEP");

    public Task WakeKeyboardAsync() =>
        SendLineAsync("SYS|WAKE");

    private async Task SendBulkLineAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");

            if (_bleCharacteristic is not null)
            {
                // Screensaver data must be lossless. WriteWithoutResponse can
                // overrun the Windows/BLE transmit queue during a long GIF
                // upload and still report success to the app. Use acknowledged
                // ATT writes so every fragment reaches the nRF52840 in order.
                int chunkSize = _blePayloadSize;

                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(data.AsSpan(offset, len).ToArray());

                    var status = await _bleCharacteristic.WriteValueAsync(
                        writer.DetachBuffer(),
                        GattWriteOption.WriteWithResponse);

                    if (status != GattCommunicationStatus.Success)
                        throw new IOException($"Bluetooth bulk write failed: {status}");
                }

                return;
            }

            if (_port?.IsOpen == true)
                _port.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            RecordLinkFailure("Bulk transfer", ex);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<bool> SendPcMonitorConfigAsync(
        string name,
        IReadOnlyList<int>? metricSlots = null)
    {
        if (!IsConnected || !SupportsPcMonitor)
            return Task.FromResult(false);

        string safe = new string(
            (name ?? "MY PC")
                .Where(c => c >= ' ' && c <= '~' && c != '|')
                .Take(18)
                .ToArray());

        if (string.IsNullOrWhiteSpace(safe))
            safe = "MY PC";

        int[] slots =
            metricSlots is { Count: 6 }
                ? metricSlots.Select(v => Math.Clamp(v, 0, 11)).ToArray()
                : [0, 3, 6, 9, 10, 11];

        string layout =
            string.Join("|", slots.Select(v => v.ToString()));

        return SendRealtimeLineAsync(
            $"PCCFG|{safe}|{layout}");
    }

    public async Task<bool> SendPcMonitorAsync(
        PcMonitorSnapshot data,
        IReadOnlyList<int>? metricSlots = null)
    {
        if (!IsConnected || !SupportsPcMonitor)
            return false;

        static int I(double value) => (int)Math.Round(value);
        static int N(double? value) =>
            value.HasValue ? (int)Math.Round(value.Value) : -1;

        int usedMb =
            (int)Math.Clamp(
                Math.Round(data.MemoryUsedGb * 1024d),
                0,
                int.MaxValue);
        int totalMb =
            (int)Math.Clamp(
                Math.Round(data.MemoryTotalGb * 1024d),
                0,
                int.MaxValue);
        int downKbps =
            (int)Math.Clamp(
                Math.Round(data.NetworkDownloadMbps * 1000d),
                0,
                int.MaxValue);
        int upKbps =
            (int)Math.Clamp(
                Math.Round(data.NetworkUploadMbps * 1000d),
                0,
                int.MaxValue);

        int[] slots =
            metricSlots is { Count: 6 }
                ? metricSlots.Select(v => Math.Clamp(v, 0, 11)).ToArray()
                : [0, 3, 6, 9, 10, 11];

        string layout =
            string.Join("|", slots.Select(v => v.ToString()));

        string line =
            $"PCMON|{I(data.CpuLoad)}|{N(data.CpuTemperature)}|" +
            $"{N(data.CpuClockMHz)}|{N(data.GpuLoad)}|" +
            $"{N(data.GpuTemperature)}|{N(data.GpuClockMHz)}|" +
            $"{I(data.MemoryLoad)}|{usedMb}|{totalMb}|" +
            $"{downKbps}|{upKbps}|{data.Fps ?? -1}|" +
            layout;

        return await SendRealtimeLineAsync(line);
    }

    public Task<bool> ClearPcMonitorAsync() =>
        SendRealtimeLineAsync("PCMONCLR");

    private async Task<bool> SendFastMediaLineAsync(string line)
    {
        if (IsExclusiveMediaTransferActive)
            return false;

        // Protocol v4 fast path is only used for media payload chunks.
        // Begin/end/control packets still use acknowledged writes.
        if (_bleCharacteristic is null || !SupportsFastMedia)
            return await SendRealtimeLineAsync(line);

        await _writeGate.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            var characteristic = _bleCharacteristic;
            int chunkSize = Math.Max(20, _blePayloadSize);

            // Windows can queue WriteWithoutResponse far faster than the radio
            // can drain it. Pace short bursts instead of sleeping after every
            // fragment. Small-MTU links get a more conservative burst.
            int burstFragments = chunkSize <= 32 ? 3 : 6;
            int pauseMs = chunkSize <= 32 ? 4 : 2;
            int burstCount = 0;

            for (int offset = 0; offset < data.Length; offset += chunkSize)
            {
                int len = Math.Min(chunkSize, data.Length - offset);
                using var writer = new DataWriter();
                writer.WriteBytes(data.AsSpan(offset, len).ToArray());

                var status = await characteristic.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithoutResponse);

                if (status != GattCommunicationStatus.Success)
                {
                    throw new IOException(
                        $"Bluetooth fast-media write failed: {status}");
                }

                burstCount++;
                if ((burstCount % burstFragments) == 0 &&
                    offset + len < data.Length)
                {
                    await Task.Delay(pauseMs);
                }
            }

            RecordLinkSuccess();
            return true;
        }
        catch (Exception ex)
        {
            RecordLinkFailure("Fast media write", ex);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<bool> SendRealtimeLineAsync(string line)
    {
        if (IsExclusiveMediaTransferActive)
            return false;

        await _writeGate.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");

            if (_bleCharacteristic is not null)
            {
                var characteristic = _bleCharacteristic;
                int chunkSize = _blePayloadSize;

                // Track metadata and artwork must be lossless. Use the larger
                // negotiated ATT payload, but keep acknowledged writes so a
                // dropped packet can never leave the firmware stuck on an old
                // title or with an incomplete album image.
                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(data.AsSpan(offset, len).ToArray());

                    var status = await characteristic.WriteValueAsync(
                        writer.DetachBuffer(),
                        GattWriteOption.WriteWithResponse);

                    if (status != GattCommunicationStatus.Success)
                        throw new IOException(
                            $"Bluetooth realtime write failed: {status}");
                }

                RecordLinkSuccess();
                return true;
            }

            if (_port?.IsOpen == true)
            {
                _port.Write(data, 0, data.Length);
                RecordLinkSuccess();
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            RecordLinkFailure("Realtime link write", ex);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SendLineAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");

            if (_bleCharacteristic is not null)
            {
                var characteristic = _bleCharacteristic;

                // Use the negotiated ATT payload while keeping acknowledged
                // writes for reliable control commands.
                int chunkSize = _blePayloadSize;
                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(data.AsSpan(offset, len).ToArray());

                    var status = await characteristic.WriteValueAsync(
                        writer.DetachBuffer(), GattWriteOption.WriteWithResponse);

                    if (status != GattCommunicationStatus.Success)
                        throw new IOException($"Bluetooth write failed: {status}");
                }

                RecordLinkSuccess();
                return;
            }

            if (_port?.IsOpen == true)
            {
                _port.Write(data, 0, data.Length);
                RecordLinkSuccess();
            }
        }
        catch (Exception ex)
        {
            RecordLinkFailure("Link write", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        Disconnect();
        _connectGate.Dispose();
        _writeGate.Dispose();
        _mediaGate.Dispose();
    }
}
