using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace LumiPad.App;

/// <summary>
/// Native ESP32-S2 USB CDC transport for PIXEL PRO.
/// The device exposes a normal HID keyboard independently; LumiPad only owns
/// the companion CDC interface.
/// </summary>
public sealed class PixelProCdcLink : IDeviceLink
{
    private readonly ProductDefinition _product;
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

    public string FirmwareHello { get; private set; } = "";
    public int ProtocolVersion { get; private set; }

    public bool SupportsDiagnostics => true;
    public bool SupportsMemoryInfo => false;
    public bool SupportsPanelInfo => false;
    public bool SupportsSaverState => false;
    public bool SupportsProfileSwitch => false;
    public bool SupportsActions => false;
    public bool SupportsVariableArtwork => false;
    public bool SupportsBatteryInfo => false;
    public bool SupportsPcMonitor => false;

    private void Log(string level, string message) =>
        Diagnostic?.Invoke(level, message);

    public Task<string?> AutoDetectAsync(
        CancellationToken cancellationToken = default) =>
        ConnectUsbAsync(cancellationToken);

    public async Task<string?> ConnectUsbAsync(
        CancellationToken cancellationToken = default)
    {
        Disconnect();

        List<PortCandidate> metadata = GetPortCandidates();
        string[] ports = SerialPort.GetPortNames()
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ordered = metadata
            .Where(x => x.IsPreferred)
            .Select(x => x.PortName)
            .Concat(metadata.Where(x => !x.IsPreferred).Select(x => x.PortName))
            .Concat(ports)
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
                    ReadTimeout = 180,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = false
                };

                candidate.Open();
                await Task.Delay(80, cancellationToken);
                candidate.DiscardInBuffer();
                candidate.DiscardOutBuffer();

                Log("INFO", $"Probing {portName} with HELLO");
                candidate.WriteLine("HELLO");

                string? hello = await ReadHelloAsync(
                    candidate,
                    TimeSpan.FromMilliseconds(1200),
                    cancellationToken);

                if (hello is null)
                {
                    Log("INFO", $"{portName}: no PIXELPRO response");
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
                    $"Connected {_connectionName}; protocol={ProtocolVersion}; " +
                    $"hello=\"{FirmwareHello}\"; pnp=\"{info?.PnpDeviceId ?? "unknown"}\"");

                StartReader();
                return _connectionName;
            }
            catch (OperationCanceledException)
            {
                candidate?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                Log("WARN", $"{portName}: {ex.Message}");
                try { candidate?.Dispose(); } catch { }
            }
        }

        Log(
            "WARN",
            "PIXEL PRO CDC not found. Expected a port that replies " +
            "PIXELPRO|1|... to HELLO.");
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
                    line.StartsWith("KEY|", StringComparison.Ordinal)
                        ? "KEY"
                        : line.StartsWith("KEYS|", StringComparison.Ordinal)
                            ? "KEY"
                            : "CDC";

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
                    pnp.Contains("VID_303A", StringComparison.OrdinalIgnoreCase) &&
                    (!_product.UsbProductId.HasValue ||
                     pnp.Contains(
                         $"PID_{_product.UsbProductId.Value:X4}",
                         StringComparison.OrdinalIgnoreCase));

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
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

        SerialPort? port = _port;
        _port = null;

        try
        {
            if (port?.IsOpen == true)
                port.Close();
            port?.Dispose();
        }
        catch { }

        _readTask = null;
        _connectionName = "";
        FirmwareHello = "";
        ProtocolVersion = 0;
    }

    public void SendNowPlaying(NowPlayingData data) { }
    public void ClearNowPlaying() { }

    public Task<bool> SendScreensaverAnimationAsync(
        ScreensaverAnimation animation,
        IProgress<int>? progress = null) =>
        Task.FromResult(false);

    public void ClearScreensaverAnimation() { }
    public Task<string?> GetScreensaverStateAsync() =>
        Task.FromResult<string?>(null);
    public Task ShowScreensaverNowAsync(bool pcMonitor) =>
        Task.CompletedTask;
    public Task SetScreensaverSourceAsync(bool pcMonitor) =>
        Task.CompletedTask;
    public void SetScreensaverSource(bool pcMonitor) { }
    public void SetScreensaverDelay(int seconds) { }
    public void SetSleepTimeout(int seconds) { }
    public void SetRgbIdleTimeout(int seconds) { }
    public void SetDeepSleepTimeout(int seconds) { }

    public Task<(uint Seq, string Level, string Message)?>
        ReadFirmwareLogAsync(uint afterSeq) =>
        Task.FromResult<(uint, string, string)?>(null);

    public Task<int?> ReadBatteryPercentAsync() =>
        Task.FromResult<int?>(null);

    public Task<(string Panel, int RefreshHz, int SpiHz, int GifMaxFps)?>
        ReadPanelInfoAsync() =>
        Task.FromResult<(string, int, int, int)?>(null);

    public Task<(long FlashUsed, long FlashTotal, long RamUsed, long RamTotal)?>
        ReadMemoryUsageAsync() =>
        Task.FromResult<(long, long, long, long)?>(null);

    public Task<(uint Seq, int ActionId, int Position)?>
        ReadActionEventAsync(uint afterSeq) =>
        Task.FromResult<(uint, int, int)?>(null);

    public void SetActiveProfile(int profile) { }
    public void SetRgbProfile(int index, int effect, byte r, byte g, byte b) { }
    public void SetEnabled(bool enabled) { }
    public void SetBrightness(int percent) { }
    public void SetSpeed(int percent) { }
    public void SetAutoLayer() { }
    public void SetEffect(int effect) { }
    public void SetSolid(byte r, byte g, byte b) { }

    public Task RestartKeyboardAsync()
    {
        SendCommand("REBOOT");
        return Task.CompletedTask;
    }

    public Task EnterDfuAsync() => Task.CompletedTask;
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

    public void Dispose() => Disconnect();
}
