using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace LumiPad.App;

public readonly record struct PixelRgbColor(byte R, byte G, byte B)
{
    public string Hex => $"{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Native ESP32-S2 USB CDC transport for PIXEL PRO.
/// The HID keyboard remains independent; LumiPad owns only the CDC interface.
/// </summary>
public sealed class PixelProCdcLink : IDeviceLink
{
    private readonly ProductDefinition _product;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private SerialPort? _port;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private string _connectionName = "";

    public PixelProCdcLink(ProductDefinition product)
    {
        _product = product;
    }

    public bool IsConnected => _port?.IsOpen == true;
    public bool IsUsbConnected => IsConnected;
    public bool IsBluetoothConnected => false;
    public string ConnectionName => _connectionName;

    public event Action<string>? LinkError;
    public event Action<string, string>? Diagnostic;
    public event Action<int, bool, int>? KeyStateChanged;
    public event Action<int, int, int, int>? MacroTriggered;
    public event Action<int, int, int, int>? ActionTriggered;

    public string FirmwareHello { get; private set; } = "";
    public int ProtocolVersion { get; private set; }
    private int _activeProfile;
    public string? LastScreensaverError { get; private set; }

    public bool SupportsDiagnostics => true;
    public bool SupportsMemoryInfo => true;
    public bool SupportsPanelInfo => true;
    public bool SupportsSaverState => true;
    public bool SupportsProfileSwitch => true;
    public bool SupportsActions => false;
    public bool SupportsVariableArtwork => false;
    public bool SupportsBatteryInfo => false;
    public bool SupportsPcMonitor => false;

    private void Log(string level, string message) =>
        Diagnostic?.Invoke(level, message);

    public static bool IsDevicePresent(ProductDefinition product)
    {
        if (!product.UsbVendorId.HasValue || !product.UsbProductId.HasValue)
            return false;

        string vid = $"VID_{product.UsbVendorId.Value:X4}";
        string pid = $"PID_{product.UsbProductId.Value:X4}";
        var livePorts = SerialPort.GetPortNames()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = Convert.ToString(obj["Name"]) ?? "";
                string pnp = Convert.ToString(obj["PNPDeviceID"]) ?? "";

                if (!pnp.Contains(vid, StringComparison.OrdinalIgnoreCase) ||
                    !pnp.Contains(pid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = Regex.Match(
                    name,
                    @"\((COM\d+)\)",
                    RegexOptions.IgnoreCase);

                if (match.Success && livePorts.Contains(match.Groups[1].Value))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public Task<string?> AutoDetectAsync(
        CancellationToken cancellationToken = default) =>
        ConnectUsbAsync(cancellationToken);

    public async Task<string?> ConnectUsbAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected &&
                FirmwareHello.StartsWith("PIXELPRO|", StringComparison.Ordinal))
            {
                return _connectionName;
            }

            DisconnectInternal();

            List<PortCandidate> metadata = GetPortCandidates();
            string[] allPorts = SerialPort.GetPortNames()
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            List<string> preferred = metadata
                .Where(x => x.IsPreferred)
                .Select(x => x.PortName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // When Windows exposes the VID/PID metadata, probe only the PIXEL PRO
            // CDC port. This prevents touching unrelated Arduino/serial devices.
            List<string> ordered = preferred.Count > 0
                ? preferred
                : metadata.Select(x => x.PortName)
                    .Concat(allPorts)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            Log(
                "INFO",
                $"PIXEL PRO CDC probe: {ordered.Count} candidate port(s): " +
                string.Join(", ", ordered));

            foreach (string portName in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SerialPort? candidate = null;
                try
                {
                    candidate = new SerialPort(
                        portName,
                        115200,
                        Parity.None,
                        8,
                        StopBits.One)
                    {
                        NewLine = "\n",
                        ReadTimeout = 250,
                        WriteTimeout = 1000,
                        // Start with both control lines low, then move only
                        // through non-boot states after Open. Normal LumiPad
                        // sessions keep DTR+RTS asserted; the ESP32-S2 boot
                        // sequence is emitted only by TryEnterRomBootloaderAsync.
                        DtrEnable = false,
                        RtsEnable = false
                    };

                    Log("INFO", $"Opening PIXEL PRO candidate {portName}");
                    candidate.Open();

                    candidate.DtrEnable = true;
                    await Task.Delay(20, cancellationToken);
                    candidate.RtsEnable = true;

                    // Give Windows usbser + TinyUSB time to settle with the
                    // normal connected line state.
                    await Task.Delay(330, cancellationToken);

                    candidate.DiscardInBuffer();
                    candidate.DiscardOutBuffer();

                    string? hello = await ProbeHelloAsync(
                        candidate,
                        cancellationToken);

                    if (hello is null)
                    {
                        Log("INFO", $"{portName}: no PIXELPRO HELLO response");
                        candidate.Close();
                        candidate.Dispose();
                        continue;
                    }

                    _port = candidate;
                    candidate = null;
                    FirmwareHello = hello;
                    ProtocolVersion = ParseProtocolVersion(hello);
                    _connectionName = $"USB CDC · {portName}";

                    PortCandidate? info = metadata.FirstOrDefault(
                        x => string.Equals(
                            x.PortName,
                            portName,
                            StringComparison.OrdinalIgnoreCase));

                    Log(
                        "INFO",
                        $"CONNECTED {_connectionName}; protocol={ProtocolVersion}; " +
                        $"hello=\"{FirmwareHello}\"; pnp=\"{info?.PnpDeviceId ?? "unknown"}\"");

                    StartReader();
                    return _connectionName;
                }
                catch (OperationCanceledException)
                {
                    try { candidate?.Close(); } catch { }
                    candidate?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    Log("WARN", $"{portName}: {ex.Message}");
                    try { candidate?.Close(); } catch { }
                    try { candidate?.Dispose(); } catch { }
                }
            }

            Log(
                "WARN",
                "PIXEL PRO CDC not found. Expected PIXELPRO|1|... from the " +
                "VID_303A/PID_80C2 CDC interface.");
            return null;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<string?> ProbeHelloAsync(
        SerialPort port,
        CancellationToken cancellationToken)
    {
        // Two attempts make reconnect robust if Windows opens the interface
        // immediately after the ESP32-S2 has just reset.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Log("INFO", $"{port.PortName} => HELLO (attempt {attempt})");
            port.WriteLine("HELLO");

            string? hello = await ReadHelloAsync(
                port,
                TimeSpan.FromMilliseconds(1800),
                cancellationToken);

            if (hello is not null)
                return hello;

            if (attempt == 1)
            {
                await Task.Delay(250, cancellationToken);
                try { port.DiscardInBuffer(); } catch { }
            }
        }

        return null;
    }

    private async Task<string?> ReadHelloAsync(
        SerialPort port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string line = port.ReadLine().Trim('\0', '\r', '\n', ' ');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Log("INFO", $"{port.PortName} <= {line}");

                if (line.StartsWith("PIXELPRO|", StringComparison.Ordinal))
                    return line;
            }
            catch (TimeoutException)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        return null;
    }

    private static int ParseProtocolVersion(string hello)
    {
        string[] parts = hello.Split('|');
        return parts.Length > 1 && int.TryParse(parts[1], out int v) ? v : 0;
    }

    private void StartReader()
    {
        SerialPort? port = _port;
        if (port is null)
            return;

        _readCts?.Cancel();
        _readCts?.Dispose();

        _readCts = new CancellationTokenSource();
        CancellationToken token = _readCts.Token;
        _readTask = Task.Run(() => ReadLoop(port, token), token);
    }

    private void ReadLoop(SerialPort port, CancellationToken token)
    {
        while (!token.IsCancellationRequested && port.IsOpen)
        {
            try
            {
                string line = port.ReadLine().Trim('\0', '\r', '\n', ' ');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string category =
                    line.StartsWith("KEY|", StringComparison.Ordinal) ||
                    line.StartsWith("KEYS|", StringComparison.Ordinal)
                        ? "KEY"
                        : "CDC";

                if (line.StartsWith("KEY|", StringComparison.Ordinal))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[1], out int keyIndex))
                    {
                        bool down = string.Equals(
                            parts[2],
                            "DOWN",
                            StringComparison.OrdinalIgnoreCase);
                        int layer = 0;

                        foreach (string part in parts.Skip(3))
                        {
                            if (part.StartsWith("L=", StringComparison.Ordinal) &&
                                int.TryParse(part[2..], out int parsedLayer))
                            {
                                layer = parsedLayer;
                            }
                        }

                        KeyStateChanged?.Invoke(keyIndex - 1, down, layer);
                    }
                }
                else if (line.StartsWith("MACRO|", StringComparison.Ordinal))
                {
                    string[] parts = line.Split('|');

                    if (parts.Length >= 2 &&
                        int.TryParse(parts[1], out int slot))
                    {
                        int key = 0;
                        int profile = 0;
                        int layer = 0;

                        foreach (string part in parts.Skip(2))
                        {
                            if (part.StartsWith("KEY=", StringComparison.Ordinal))
                                _ = int.TryParse(part[4..], out key);
                            else if (part.StartsWith("P=", StringComparison.Ordinal))
                                _ = int.TryParse(part[2..], out profile);
                            else if (part.StartsWith("L=", StringComparison.Ordinal))
                                _ = int.TryParse(part[2..], out layer);
                        }

                        MacroTriggered?.Invoke(slot, key, profile, layer);
                    }
                }
                else if (line.StartsWith("ACTION|", StringComparison.Ordinal))
                {
                    string[] parts = line.Split('|');

                    if (parts.Length >= 2 &&
                        int.TryParse(parts[1], out int actionId))
                    {
                        int key = 0;
                        int profile = 0;
                        int layer = 0;

                        foreach (string part in parts.Skip(2))
                        {
                            if (part.StartsWith("KEY=", StringComparison.Ordinal))
                                _ = int.TryParse(part[4..], out key);
                            else if (part.StartsWith("P=", StringComparison.Ordinal))
                                _ = int.TryParse(part[2..], out profile);
                            else if (part.StartsWith("L=", StringComparison.Ordinal))
                                _ = int.TryParse(part[2..], out layer);
                        }

                        ActionTriggered?.Invoke(actionId, key, profile, layer);
                    }
                }

                Log(category, line);
            }
            catch (TimeoutException)
            {
            }
            catch (IOException ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Log("ERROR", $"PIXEL PRO CDC disconnected: {ex.Message}");
                    LinkError?.Invoke(ex.Message);
                }

                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    Log("WARN", $"PIXEL PRO CDC read error: {ex.Message}");
            }
        }
    }

    private sealed record PortCandidate(
        string PortName,
        string PnpDeviceId,
        bool IsPreferred);

    private List<PortCandidate> GetPortCandidates()
    {
        var result = new List<PortCandidate>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = Convert.ToString(obj["Name"]) ?? "";
                string pnp = Convert.ToString(obj["PNPDeviceID"]) ?? "";
                Match match = Regex.Match(
                    name,
                    @"\((COM\d+)\)",
                    RegexOptions.IgnoreCase);

                if (!match.Success)
                    continue;

                bool preferred =
                    _product.UsbVendorId.HasValue &&
                    _product.UsbProductId.HasValue &&
                    pnp.Contains(
                        $"VID_{_product.UsbVendorId.Value:X4}",
                        StringComparison.OrdinalIgnoreCase) &&
                    pnp.Contains(
                        $"PID_{_product.UsbProductId.Value:X4}",
                        StringComparison.OrdinalIgnoreCase);

                result.Add(new PortCandidate(
                    match.Groups[1].Value,
                    pnp,
                    preferred));
            }
        }
        catch (Exception ex)
        {
            Log("WARN", $"WMI COM enumeration failed: {ex.Message}");
        }

        return result;
    }

    public Task<string?> ConnectBluetoothAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> PromoteToUsbIfAvailableAsync(
        CancellationToken cancellationToken = default) =>
        IsConnected
            ? Task.FromResult<string?>(_connectionName)
            : ConnectUsbAsync(cancellationToken);

    private async Task StopReaderAsync()
    {
        _readCts?.Cancel();

        Task? task = _readTask;
        if (task is not null)
            await Task.WhenAny(task, Task.Delay(700));

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
    }

    private async Task<string?> RequestLineAsync(
        string command,
        string expectedPrefix,
        CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            SerialPort? port = _port;
            if (port?.IsOpen != true)
                return null;

            await StopReaderAsync();

            try
            {
                port.ReadTimeout = 300;
                port.WriteTimeout = 1000;
                try { port.DiscardInBuffer(); } catch { }

                Log("TX", command);
                port.WriteLine(command);

                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);

                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        string line = port.ReadLine().Trim('\0', '\r', '\n', ' ');
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        Log("CDC", line);

                        if (line.StartsWith(expectedPrefix, StringComparison.Ordinal))
                            return line;

                        if (line.StartsWith("ERR|", StringComparison.Ordinal))
                            return line;
                    }
                    catch (TimeoutException)
                    {
                        await Task.Delay(20, cancellationToken);
                    }
                }

                return null;
            }
            finally
            {
                if (port.IsOpen)
                    StartReader();
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<IReadOnlyList<PixelProKeyBinding>?> GetKeymapAsync(
        int profile,
        int layer,
        CancellationToken cancellationToken = default)
    {
        if (profile < 0 || profile > 19 || layer < 0 || layer > 3)
            return null;

        string? line = await RequestLineAsync(
            $"GET_KEYMAP|{profile}|{layer}",
            $"KEYMAP|{profile}|{layer}|",
            cancellationToken);

        string prefix = $"KEYMAP|{profile}|{layer}|";
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        string[] entries = line[prefix.Length..].Split(',');
        if (entries.Length != 8)
            return null;

        var result = new List<PixelProKeyBinding>(8);

        foreach (string entry in entries)
        {
            string[] parts = entry.Split(':');
            if (parts.Length != 3 ||
                !ushort.TryParse(parts[1], out ushort code) ||
                !byte.TryParse(parts[2], out byte modifiers))
            {
                return null;
            }

            PixelProKeyBinding? binding = parts[0] switch
            {
                "K" when code <= byte.MaxValue =>
                    PixelProKeyBinding.Keyboard((byte)code, modifiers),
                "C" =>
                    PixelProKeyBinding.Consumer(code),
                "L" when code <= 3 &&
                         Enum.IsDefined(typeof(PixelProLayerAction), modifiers) =>
                    PixelProKeyBinding.Layer(
                        (byte)code,
                        (PixelProLayerAction)modifiers),
                "M" when code <= 19 =>
                    PixelProKeyBinding.Macro((byte)code),
                "A" when code is >= 1 and <= 32 && modifiers == 0 =>
                    PixelProKeyBinding.Action((byte)code),
                "T" when code == 0 && modifiers == 0 =>
                    PixelProKeyBinding.Transparent(),
                "D" when code == 0 && modifiers == 0 =>
                    PixelProKeyBinding.Disabled(),
                _ => null
            };

            if (binding is null)
                return null;

            result.Add(binding);
        }

        return result;
    }

    public Task<IReadOnlyList<PixelProKeyBinding>?> GetKeymapAsync(
        int layer = 0,
        CancellationToken cancellationToken = default) =>
        GetKeymapAsync(0, layer, cancellationToken);

    public async Task<bool> SetKeymapAsync(
        int profile,
        int layer,
        IReadOnlyList<PixelProKeyBinding> bindings,
        CancellationToken cancellationToken = default)
    {
        if (profile < 0 || profile > 19 ||
            layer < 0 || layer > 3 ||
            bindings.Count != 8)
        {
            return false;
        }

        static string Serialize(PixelProKeyBinding binding) =>
            binding.Type switch
            {
                PixelProKeyBindingType.Keyboard =>
                    $"K:{binding.Code}:{binding.Modifiers & 0x0F}",
                PixelProKeyBindingType.Consumer =>
                    $"C:{binding.Code}:0",
                PixelProKeyBindingType.Layer =>
                    $"L:{binding.Code}:{binding.Modifiers}",
                PixelProKeyBindingType.Macro =>
                    $"M:{binding.Code}:0",
                PixelProKeyBindingType.Action =>
                    $"A:{binding.Code}:0",
                PixelProKeyBindingType.Transparent =>
                    "T:0:0",
                _ => "D:0:0"
            };

        string command =
            $"SET_KEYMAP|{profile}|{layer}|" +
            string.Join(",", bindings.Select(Serialize));

        string? response = await RequestLineAsync(
            command,
            $"OK|KEYMAP|{profile}|{layer}",
            cancellationToken);

        return string.Equals(
            response,
            $"OK|KEYMAP|{profile}|{layer}",
            StringComparison.Ordinal);
    }

    public Task<bool> SetKeymapAsync(
        int layer,
        IReadOnlyList<PixelProKeyBinding> bindings,
        CancellationToken cancellationToken = default) =>
        SetKeymapAsync(0, layer, bindings, cancellationToken);

    public Task<bool> SetKeymapAsync(
        IReadOnlyList<PixelProKeyBinding> bindings,
        CancellationToken cancellationToken = default) =>
        SetKeymapAsync(0, 0, bindings, cancellationToken);

    public void SetProfileLayer(int profile, int layer)
    {
        profile = Math.Clamp(profile, 0, 19);
        layer = Math.Clamp(layer, 0, 3);
        _activeProfile = profile;
        SendCommand($"SET_PROFILE|{profile}|{layer}");
    }

    public async Task<string?> GetMacroAsync(
        int index,
        CancellationToken cancellationToken = default)
    {
        if (index < 0 || index > 19)
            return null;

        string prefix = $"MACRO|{index}|";
        string? line = await RequestLineAsync(
            $"GET_MACRO|{index}",
            prefix,
            cancellationToken);

        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        string hex = line[prefix.Length..];

        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            return System.Text.Encoding.ASCII.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SetMacroAsync(
        int index,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (index < 0 || index > 19)
            return false;

        string ascii = new(
            (text ?? "")
                .Where(ch => ch >= 0x20 && ch <= 0x7E)
                .Take(80)
                .ToArray());

        string hex = Convert.ToHexString(
            System.Text.Encoding.ASCII.GetBytes(ascii));

        string? response = await RequestLineAsync(
            $"SET_MACRO|{index}|{hex}",
            $"OK|MACRO|{index}",
            cancellationToken);

        return string.Equals(
            response,
            $"OK|MACRO|{index}",
            StringComparison.Ordinal);
    }

    public async Task<bool> ResetKeymapAsync(
        CancellationToken cancellationToken = default)
    {
        string? response = await RequestLineAsync(
            "RESET_KEYMAP",
            "OK|KEYMAP_RESET",
            cancellationToken);

        return string.Equals(
            response,
            "OK|KEYMAP_RESET",
            StringComparison.Ordinal);
    }

    private void SendCommand(string command)
    {
        SerialPort? port = _port;
        if (port?.IsOpen != true)
            return;

        try
        {
            port.WriteLine(command);
            Log("TX", command);
        }
        catch (Exception ex)
        {
            Log("WARN", $"PIXEL PRO CDC write failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        DisconnectInternal();
    }

    private static void ClosePortWithoutBootloaderSequence(
        SerialPort? port)
    {
        if (port is null)
            return;

        try
        {
            if (port.IsOpen)
            {
                // Clear any stale Arduino-ESP32 CDC reboot state without ever
                // issuing the final !DTR/!RTS boot trigger. Finish at
                // DTR+RTS=true so the subsequent Close cannot complete the
                // four-state ROM-boot sequence.
                port.DtrEnable = true;
                port.RtsEnable = true;
                Thread.Sleep(10);
                port.DtrEnable = false;
                Thread.Sleep(10);
                port.DtrEnable = true;
                Thread.Sleep(10);
                port.RtsEnable = false;
                Thread.Sleep(10);
                port.RtsEnable = true;
                Thread.Sleep(10);

                port.Close();
            }
        }
        catch
        {
        }
        finally
        {
            try { port.Dispose(); } catch { }
        }
    }

    private void DisconnectInternal()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

        SerialPort? port = _port;
        _port = null;

        ClosePortWithoutBootloaderSequence(
            port);

        _readTask = null;
        _connectionName = "";
        FirmwareHello = "";
        ProtocolVersion = 0;
    }

    public void SendNowPlaying(NowPlayingData data) { }
    public void ClearNowPlaying() { }

    public async Task<bool> SendScreensaverAnimationAsync(
        ScreensaverAnimation animation,
        IProgress<int>? progress = null)
    {
        LastScreensaverError = null;

        if (!IsConnected)
        {
            LastScreensaverError = "PIXEL PRO is not connected.";
            return false;
        }

        if (PixelProScreensaverMediaService.TryGetEncodedGif(
                animation,
                out byte[] encodedGif,
                out ScreensaverScaleMode scaleMode))
        {
            return await Task.Run(
                    () => SendEncodedGifAsync(
                        encodedGif,
                        scaleMode,
                        progress))
                .ConfigureAwait(false);
        }

        if (PixelProScreensaverMediaService.TryGetEncodedJpeg(
                animation,
                out byte[] encodedJpeg,
                out int jpegWidth,
                out int jpegHeight))
        {
            return await Task.Run(
                    () => SendEncodedJpegAsync(
                        encodedJpeg,
                        jpegWidth,
                        jpegHeight,
                        progress))
                .ConfigureAwait(false);
        }

        bool staticImage =
            animation.PixelFormat == ScreensaverPixelFormat.Rgb565;

        bool valid =
            staticImage
                ? animation.Frames.Count == 1 &&
                  animation.Width ==
                      PixelProScreensaverMediaService.PanelWidth &&
                  animation.Height ==
                      PixelProScreensaverMediaService.PanelHeight &&
                  animation.Frames[0].Length ==
                      PixelProScreensaverMediaService.PanelWidth *
                      PixelProScreensaverMediaService.PanelHeight * 2
                : animation.PixelFormat ==
                      ScreensaverPixelFormat.Rgb332 &&
                  animation.Frames.Count is >= 1 and <=
                      PixelProScreensaverMediaService.MaxFrames &&
                  animation.Width ==
                      PixelProScreensaverMediaService.Width &&
                  animation.Height ==
                      PixelProScreensaverMediaService.Height &&
                  animation.Frames.All(
                      x => x.Length ==
                           PixelProScreensaverMediaService.Width *
                           PixelProScreensaverMediaService.Height);

        if (!valid)
            throw new InvalidOperationException(
                "Invalid PIXEL PRO screensaver format.");

        int[] durations =
            Enumerable.Range(0, animation.Frames.Count)
                .Select(i =>
                    animation.FrameDurationsMs.Count ==
                    animation.Frames.Count
                        ? animation.FrameDurationsMs[i]
                        : animation.FrameIntervalMs)
                .Select(ms =>
                    Math.Clamp(
                        ms,
                        PixelProScreensaverMediaService.MinFrameIntervalMs,
                        5000))
                .ToArray();

        string format = staticImage ? "RGB565" : "RGB332";
        string durationCsv = string.Join(",", durations);

        await _commandGate.WaitAsync();
        try
        {
            SerialPort? port = _port;
            if (port?.IsOpen != true)
                return false;

            await StopReaderAsync();

            try
            {
                port.ReadTimeout = 300;
                port.WriteTimeout = 3000;
                try { port.DiscardInBuffer(); } catch { }

                string begin =
                    $"SAVBEGIN|{animation.Frames.Count}|" +
                    $"{animation.Width}|{animation.Height}|" +
                    $"{format}|{durationCsv}";

                Log("TX", begin);
                port.WriteLine(begin);

                string? beginAck =
                    await ReadExpectedLineAsync(
                        port,
                        "OK|SAVBEGIN",
                        TimeSpan.FromSeconds(5));

                if (!string.Equals(
                        beginAck,
                        "OK|SAVBEGIN",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                const int RawChunkSize = 300;

                long totalBytes =
                    animation.Frames.Sum(
                        frame => (long)frame.Length);

                long sentBytes = 0;

                for (int frameIndex = 0;
                     frameIndex < animation.Frames.Count;
                     frameIndex++)
                {
                    byte[] frame =
                        animation.Frames[frameIndex];

                    for (int offset = 0;
                         offset < frame.Length;
                         offset += RawChunkSize)
                    {
                        int len =
                            Math.Min(
                                RawChunkSize,
                                frame.Length - offset);

                        string encoded =
                            Convert.ToBase64String(
                                frame,
                                offset,
                                len);

                        port.WriteLine(
                            $"SAVDATA|{frameIndex}|{offset}|{encoded}");

                        sentBytes += len;

                        progress?.Report(
                            (int)Math.Clamp(
                                sentBytes * 95 / Math.Max(1, totalBytes),
                                0,
                                95));

                        if (((offset / RawChunkSize) & 0x0F) == 0x0F)
                            await Task.Yield();
                    }

                    string expected =
                        $"OK|SAVFRAME|{frameIndex}";

                    string? frameAck =
                        await ReadExpectedLineAsync(
                            port,
                            expected,
                            TimeSpan.FromSeconds(8));

                    if (!string.Equals(
                            frameAck,
                            expected,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                port.WriteLine("SAVEND");

                string? finalAck =
                    await ReadExpectedLineAsync(
                        port,
                        "OK|SAVER|READY",
                        TimeSpan.FromSeconds(8));

                bool ready =
                    string.Equals(
                        finalAck,
                        "OK|SAVER|READY",
                        StringComparison.Ordinal);

                if (ready)
                {
                    progress?.Report(100);
                }
                else
                {
                    LastScreensaverError =
                        string.IsNullOrWhiteSpace(finalAck)
                            ? "PIXEL PRO did not confirm the screensaver upload."
                            : $"PIXEL PRO rejected the screensaver upload: {finalAck}";
                }

                return ready;
            }
            finally
            {
                if (port.IsOpen)
                    StartReader();
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<bool> SendEncodedGifAsync(
        byte[] gifBytes,
        ScreensaverScaleMode scaleMode,
        IProgress<int>? progress)
    {
        (long Total, long Used, long Free, long Flash)? capacity =
            await ReadSaverCapacityAsync();

        if (capacity is not null)
        {
            long required =
                gifBytes.LongLength + 4096;

            if (required > capacity.Value.Free)
            {
                LastScreensaverError =
                    $"GIF file is {gifBytes.LongLength / 1048576.0:0.00} MB. " +
                    $"PIXEL PRO LittleFS media partition has {capacity.Value.Free / 1048576.0:0.00} MB free " +
                    $"of {capacity.Value.Total / 1048576.0:0.00} MB, while the physical flash chip is " +
                    $"{capacity.Value.Flash / 1048576.0:0.00} MB. " +
                    "GIF is already a compressed format; the original file cannot fit this media partition.";

                Log("WARN", LastScreensaverError);
                return false;
            }
        }


        if (gifBytes.Length < 10 ||
            gifBytes[0] != (byte)'G' ||
            gifBytes[1] != (byte)'I' ||
            gifBytes[2] != (byte)'F')
        {
            throw new InvalidDataException(
                "Invalid GIF payload.");
        }

        int sourceWidth =
            gifBytes[6] |
            (gifBytes[7] << 8);

        int sourceHeight =
            gifBytes[8] |
            (gifBytes[9] << 8);

        if (sourceWidth <= 0 ||
            sourceHeight <= 0 ||
            sourceWidth > 1024 ||
            sourceHeight > 1024)
        {
            throw new InvalidOperationException(
                "PIXEL PRO GIF canvas must be between 1×1 and 1024×1024.");
        }

        string scaleToken =
            scaleMode.ToString().ToUpperInvariant();

        await _commandGate.WaitAsync();
        try
        {
            SerialPort? port = _port;
            if (port?.IsOpen != true)
                return false;

            await StopReaderAsync();

            try
            {
                port.ReadTimeout = 500;
                port.WriteTimeout = 5000;

                try { port.DiscardInBuffer(); } catch { }

                string begin =
                    $"SAVGIFBEGIN|{gifBytes.Length}|" +
                    $"{sourceWidth}|{sourceHeight}|" +
                    $"{scaleToken}";

                Log("TX", begin);
                port.WriteLine(begin);

                string? beginAck =
                    await ReadExpectedLineAsync(
                        port,
                        "OK|SAVGIFBEGIN",
                        TimeSpan.FromSeconds(8));

                if (!string.Equals(
                        beginAck,
                        "OK|SAVGIFBEGIN",
                        StringComparison.Ordinal))
                {
                    SetSaverProtocolError(
                        beginAck,
                        gifBytes);
                    return false;
                }

                const int RawChunkSize = 1024;

                for (int offset = 0;
                     offset < gifBytes.Length;
                     offset += RawChunkSize)
                {
                    int len =
                        Math.Min(
                            RawChunkSize,
                            gifBytes.Length - offset);

                    string encoded =
                        Convert.ToBase64String(
                            gifBytes,
                            offset,
                            len);

                    port.WriteLine(
                        $"SAVGIFDATA|{offset}|{encoded}");

                    int nextOffset = offset + len;

                    string expected =
                        $"OK|SAVGIFDATA|{nextOffset}";

                    string? ack =
                        await ReadExpectedLineAsync(
                            port,
                            expected,
                            TimeSpan.FromSeconds(5));

                    if (!string.Equals(
                            ack,
                            expected,
                            StringComparison.Ordinal))
                    {
                        SetSaverProtocolError(
                            ack,
                            gifBytes);
                        return false;
                    }

                    progress?.Report(
                        (int)Math.Clamp(
                            nextOffset * 95L /
                            Math.Max(1, gifBytes.Length),
                            0,
                            95));
                }

                port.WriteLine("SAVGIFEND");

                string? finalAck =
                    await ReadExpectedLineAsync(
                        port,
                        "OK|SAVER|READY",
                        TimeSpan.FromSeconds(10));

                bool ready =
                    string.Equals(
                        finalAck,
                        "OK|SAVER|READY",
                        StringComparison.Ordinal);

                if (ready)
                    progress?.Report(100);

                return ready;
            }
            finally
            {
                if (port.IsOpen)
                    StartReader();
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<bool> SendEncodedJpegAsync(
        byte[] jpegBytes,
        int width,
        int height,
        IProgress<int>? progress)
    {
        (long Total, long Used, long Free, long Flash)? capacity =
            await ReadSaverCapacityAsync();

        if (capacity is not null &&
            jpegBytes.LongLength + 4096 >
            capacity.Value.Free)
        {
            LastScreensaverError =
                $"JPEG file is {jpegBytes.LongLength / 1048576.0:0.00} MB but PIXEL PRO has " +
                $"{capacity.Value.Free / 1048576.0:0.00} MB free for media.";
            return false;
        }

        if (jpegBytes.Length < 4 ||
            jpegBytes[0] != 0xFF ||
            jpegBytes[1] != 0xD8)
        {
            throw new InvalidDataException(
                "Invalid JPEG payload.");
        }

        await _commandGate.WaitAsync();
        try
        {
            SerialPort? port = _port;
            if (port?.IsOpen != true)
                return false;

            await StopReaderAsync();

            try
            {
                port.ReadTimeout = 500;
                port.WriteTimeout = 5000;
                try { port.DiscardInBuffer(); } catch { }

                string begin =
                    $"SAVJPGBEGIN|{jpegBytes.Length}|{width}|{height}";

                Log("TX", begin);
                port.WriteLine(begin);

                string? beginAck =
                    await ReadExpectedLineAsync(
                        port,
                        "OK|SAVJPGBEGIN",
                        TimeSpan.FromSeconds(8));

                if (!string.Equals(
                        beginAck,
                        "OK|SAVJPGBEGIN",
                        StringComparison.Ordinal))
                {
                    LastScreensaverError =
                        string.IsNullOrWhiteSpace(beginAck)
                            ? "PIXEL PRO did not answer JPEG upload start."
                            : $"PIXEL PRO rejected JPEG upload: {beginAck}";
                    return false;
                }

                const int RawChunkSize = 1024;

                for (int offset = 0;
                     offset < jpegBytes.Length;
                     offset += RawChunkSize)
                {
                    int len =
                        Math.Min(
                            RawChunkSize,
                            jpegBytes.Length - offset);

                    string encoded =
                        Convert.ToBase64String(
                            jpegBytes,
                            offset,
                            len);

                    port.WriteLine(
                        $"SAVJPGDATA|{offset}|{encoded}");

                    int nextOffset =
                        offset + len;

                    string expected =
                        $"OK|SAVJPGDATA|{nextOffset}";

                    string? ack =
                        await ReadExpectedLineAsync(
                            port,
                            expected,
                            TimeSpan.FromSeconds(5));

                    if (!string.Equals(
                            ack,
                            expected,
                            StringComparison.Ordinal))
                    {
                        LastScreensaverError =
                            string.IsNullOrWhiteSpace(ack)
                                ? "PIXEL PRO stopped answering during JPEG upload."
                                : $"PIXEL PRO rejected JPEG data: {ack}";
                        return false;
                    }

                    progress?.Report(
                        (int)Math.Clamp(
                            nextOffset * 95L /
                            Math.Max(1, jpegBytes.Length),
                            0,
                            95));
                }

                port.WriteLine("SAVJPGEND");

                string? finalAck =
                    await ReadExpectedLineAsync(
                        port,
                        "OK|SAVER|READY",
                        TimeSpan.FromSeconds(10));

                bool ready =
                    string.Equals(
                        finalAck,
                        "OK|SAVER|READY",
                        StringComparison.Ordinal);

                if (ready)
                    progress?.Report(100);
                else
                    LastScreensaverError =
                        string.IsNullOrWhiteSpace(finalAck)
                            ? "PIXEL PRO did not confirm JPEG storage."
                            : $"PIXEL PRO rejected JPEG storage: {finalAck}";

                return ready;
            }
            finally
            {
                if (port.IsOpen)
                    StartReader();
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<(long Total, long Used, long Free, long Flash)?>
        ReadSaverCapacityAsync()
    {
        string? line =
            await RequestLineAsync(
                "SAVERINFO",
                "SAVERINFO|");

        if (line is null ||
            !line.StartsWith(
                "SAVERINFO|",
                StringComparison.Ordinal))
        {
            return null;
        }

        long total = 0;
        long used = 0;
        long free = 0;
        long flash = 0;

        foreach (string part in line.Split('|').Skip(1))
        {
            string[] kv = part.Split('=', 2);

            if (kv.Length != 2 ||
                !long.TryParse(kv[1], out long value))
            {
                continue;
            }

            switch (kv[0])
            {
                case "TOTAL": total = value; break;
                case "USED": used = value; break;
                case "FREE": free = value; break;
                case "FLASH": flash = value; break;
            }
        }

        return (total, used, free, flash);
    }

    private void SetSaverProtocolError(
        string? response,
        byte[] gifBytes)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            LastScreensaverError =
                "PIXEL PRO did not answer the screensaver upload command.";
            return;
        }

        if (response.StartsWith(
                "ERR|NO_SPACE",
                StringComparison.Ordinal))
        {
            long free = 0;
            long need = gifBytes.LongLength;

            foreach (string part in response.Split('|').Skip(2))
            {
                string[] kv = part.Split('=', 2);

                if (kv.Length == 2 &&
                    long.TryParse(kv[1], out long value))
                {
                    if (kv[0] == "FREE") free = value;
                    if (kv[0] == "NEED") need = value;
                }
            }

            LastScreensaverError =
                $"GIF needs {need / 1048576.0:0.00} MB but PIXEL PRO has only " +
                $"{free / 1048576.0:0.00} MB free for screensaver media.";
            return;
        }

        if (response.StartsWith(
                "ERR|FS_NOT_READY",
                StringComparison.Ordinal))
        {
            LastScreensaverError =
                "PIXEL PRO media storage is not ready.";
            return;
        }

        LastScreensaverError =
            $"PIXEL PRO rejected the GIF upload: {response}";
    }

    private async Task<string?> ReadExpectedLineAsync(
        SerialPort port,
        string expectedPrefix,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string line =
                    port.ReadLine()
                        .Trim('\0', '\r', '\n', ' ');

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Log("CDC", line);

                if (line.StartsWith(
                        expectedPrefix,
                        StringComparison.Ordinal))
                {
                    return line;
                }

                if (line.StartsWith(
                        "ERR|",
                        StringComparison.Ordinal))
                {
                    return line;
                }
            }
            catch (TimeoutException)
            {
                await Task.Delay(5);
            }
        }

        return null;
    }

    public void ClearScreensaverAnimation() =>
        SendCommand("SAVCLEAR");

    public async Task<string?> GetScreensaverStateAsync()
    {
        string? line =
            await RequestLineAsync(
                "SAVERSTATE",
                "SAVERSTATE|");

        if (line is null ||
            !line.StartsWith(
                "SAVERSTATE|",
                StringComparison.Ordinal))
        {
            return null;
        }

        return line["SAVERSTATE|".Length..];
    }

    public async Task ShowScreensaverNowAsync(bool pcMonitor)
    {
        if (pcMonitor)
            return;

        _ = await RequestLineAsync(
            "SAVSHOW",
            "OK|SAVSHOW");
    }

    public async Task SetScreensaverSourceAsync(bool pcMonitor)
    {
        if (pcMonitor)
            return;

        _ = await RequestLineAsync(
            "SAVSOURCE|MEDIA",
            "OK|SAVSOURCE");
    }

    public void SetScreensaverSource(bool pcMonitor)
    {
        if (!pcMonitor)
            SendCommand("SAVSOURCE|MEDIA");
    }

    public void SetScreensaverDelay(int seconds) =>
        SendCommand(
            $"SAVDELAY|{Math.Clamp(seconds, 0, 86400)}");
    public void SetSleepTimeout(int seconds) { }
    public void SetRgbIdleTimeout(int seconds) { }
    public void SetDeepSleepTimeout(int seconds) { }

    public Task<(uint Seq, string Level, string Message)?>
        ReadFirmwareLogAsync(uint afterSeq) =>
        Task.FromResult<(uint, string, string)?>(null);

    public Task<int?> ReadBatteryPercentAsync() =>
        Task.FromResult<int?>(null);

    public async Task<(string Panel, int RefreshHz, int SpiHz, int GifMaxFps)?>
        ReadPanelInfoAsync()
    {
        string? line =
            await RequestLineAsync(
                "PANEL",
                "PANEL|");

        if (line is null ||
            !line.StartsWith("PANEL|", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = line.Split('|');

        if (parts.Length != 5 ||
            !int.TryParse(parts[2], out int refreshHz) ||
            !int.TryParse(parts[3], out int busHz) ||
            !int.TryParse(parts[4], out int gifMaxFps))
        {
            return null;
        }

        return (
            parts[1],
            refreshHz,
            busHz,
            gifMaxFps);
    }

    public async Task<DeviceMemoryUsage?> ReadMemoryUsageAsync()
    {
        if (!IsConnected)
            return null;

        string? line = await RequestLineAsync(
            "MEM",
            "MEM|");

        if (line is null ||
            !line.StartsWith("MEM|", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = line.Split('|');
        if (parts.Length != 7 ||
            !long.TryParse(parts[1], out long flashUsed) ||
            !long.TryParse(parts[2], out long flashTotal) ||
            !long.TryParse(parts[3], out long sramUsed) ||
            !long.TryParse(parts[4], out long sramTotal) ||
            !long.TryParse(parts[5], out long psramUsed) ||
            !long.TryParse(parts[6], out long psramTotal) ||
            flashTotal <= 0 ||
            sramTotal <= 0)
        {
            return null;
        }

        return new DeviceMemoryUsage(
            flashUsed,
            flashTotal,
            sramUsed,
            sramTotal,
            Math.Max(0, psramUsed),
            Math.Max(0, psramTotal));
    }

    public Task<(uint Seq, int ActionId, int Position)?>
        ReadActionEventAsync(uint afterSeq) =>
        Task.FromResult<(uint, int, int)?>(null);

    public void SetActiveProfile(int profile) => SetProfileLayer(profile, 0);

    public async Task<PixelRgbColor[]?> GetPixelRgbProfileAsync(
        int profile,
        CancellationToken cancellationToken = default)
    {
        profile = Math.Clamp(profile, 0, 19);

        string prefix =
            $"RGB_PROFILE|{profile}|";

        string? line =
            await RequestLineAsync(
                $"GET_RGB_PROFILE|{profile}",
                prefix,
                cancellationToken);

        if (line is null ||
            !line.StartsWith(
                prefix,
                StringComparison.Ordinal))
        {
            return null;
        }

        string[] values =
            line[prefix.Length..]
                .Split(',');

        if (values.Length != 8)
            return null;

        var colors =
            new PixelRgbColor[8];

        for (int i = 0; i < colors.Length; i++)
        {
            string hex =
                values[i].Trim();

            if (hex.Length != 6 ||
                !byte.TryParse(
                    hex[..2],
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out byte r) ||
                !byte.TryParse(
                    hex.Substring(2, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out byte g) ||
                !byte.TryParse(
                    hex.Substring(4, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out byte b))
            {
                return null;
            }

            colors[i] =
                new PixelRgbColor(
                    r,
                    g,
                    b);
        }

        return colors;
    }

    public void SetPixelRgbKey(
        int profile,
        int keyIndex,
        PixelRgbColor color)
    {
        profile = Math.Clamp(profile, 0, 19);
        keyIndex = Math.Clamp(keyIndex, 0, 7);

        SendCommand(
            $"RGB_KEY|{profile}|{keyIndex}|{color.R}|{color.G}|{color.B}");
    }

    public void SetPixelRgbAll(
        int profile,
        PixelRgbColor color)
    {
        profile = Math.Clamp(profile, 0, 19);

        SendCommand(
            $"RGB_ALL|{profile}|{color.R}|{color.G}|{color.B}");
    }

    public void SetPixelRgbProfile(
        int profile,
        IReadOnlyList<PixelRgbColor> colors)
    {
        if (colors.Count != 8)
            return;

        profile = Math.Clamp(profile, 0, 19);

        string payload =
            string.Join(
                ",",
                colors.Select(
                    color => color.Hex));

        SendCommand(
            $"RGB_PROFILE_SET|{profile}|{payload}");
    }

    public void SetRgbProfile(int index, int effect, byte r, byte g, byte b)
    {
        // Legacy shared UI compatibility. PIXEL PRO stores RGB by keymap
        // profile; RYNOR's effect/profile protocol is not reused here.
    }

    public void SetEnabled(bool enabled) =>
        SendCommand(
            $"RGB_ENABLE|{(enabled ? 1 : 0)}");

    public void SetBrightness(int percent) =>
        SendCommand(
            $"RGB_BRIGHTNESS|{Math.Clamp(percent, 0, 100)}");

    public void SetSpeed(int percent) { }
    public void SetAutoLayer() { }
    public void SetEffect(int effect) { }

    public void SetSolid(byte r, byte g, byte b) =>
        SetPixelRgbAll(
            _activeProfile,
            new PixelRgbColor(
                r,
                g,
                b));

    public Task RestartKeyboardAsync()
    {
        SendCommand("REBOOT");
        return Task.CompletedTask;
    }

    public async Task<bool> TryEnterRomBootloaderAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            return false;

        string? response =
            await RequestLineAsync(
                    "ARM_BOOTLOADER",
                    "OK|BOOTLOADER_ARMED",
                    cancellationToken)
                .ConfigureAwait(false);

        if (!string.Equals(
                response,
                "OK|BOOTLOADER_ARMED",
                StringComparison.Ordinal))
        {
            return false;
        }

        await _commandGate.WaitAsync(
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            SerialPort? port = _port;
            if (port?.IsOpen != true)
                return false;

            await StopReaderAsync()
                .ConfigureAwait(false);

            bool sequenceStarted = false;

            try
            {
                // Arduino-ESP32's intentional CDC ROM-boot sequence:
                // 01 -> 11 -> 10 -> 00 (DTR,RTS).
                // Firmware keeps this disabled at all other times and only
                // ARM_BOOTLOADER enables it for this update operation.
                port.DtrEnable = false;
                port.RtsEnable = true;
                sequenceStarted = true;
                await Task.Delay(
                        40,
                        cancellationToken)
                    .ConfigureAwait(false);

                port.DtrEnable = true;
                await Task.Delay(
                        40,
                        cancellationToken)
                    .ConfigureAwait(false);

                port.RtsEnable = false;
                await Task.Delay(
                        40,
                        cancellationToken)
                    .ConfigureAwait(false);

                port.DtrEnable = false;
                await Task.Delay(
                        250,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (sequenceStarted)
            {
                // Expected when the application USB device disappears and the
                // ESP32-S2 ROM downloader enumerates in its place.
            }
            catch (InvalidOperationException) when (sequenceStarted)
            {
            }

            SerialPort? oldPort = _port;
            _port = null;

            try
            {
                if (oldPort?.IsOpen == true)
                    oldPort.Close();
            }
            catch
            {
            }

            try { oldPort?.Dispose(); } catch { }

            _readTask = null;
            _connectionName = "";
            FirmwareHello = "";
            ProtocolVersion = 0;

            return sequenceStarted;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task EnterDfuAsync()
    {
        if (!await TryEnterRomBootloaderAsync()
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "This PIXEL PRO firmware does not support automatic ROM BOOT. Use BOOT + RESET once to install the latest firmware.");
        }
    }

    public Task SleepKeyboardAsync() => Task.CompletedTask;
    public Task WakeKeyboardAsync() => Task.CompletedTask;

    public Task<bool> SendPcMonitorConfigAsync(
        string name,
        IReadOnlyList<int>? metricSlots = null) =>
        Task.FromResult(false);

    public Task<bool> SendPcMonitorAsync(
        PcMonitorSnapshot data,
        IReadOnlyList<int>? metricSlots = null) =>
        Task.FromResult(false);

    public Task<bool> ClearPcMonitorAsync() =>
        Task.FromResult(false);

    public void Dispose()
    {
        DisconnectInternal();
        _connectGate.Dispose();
        _commandGate.Dispose();
    }
}
