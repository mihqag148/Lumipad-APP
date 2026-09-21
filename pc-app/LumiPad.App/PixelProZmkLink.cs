using HidSharp;
using System.IO;
using System.Text;

namespace LumiPad.App;

/// <summary>
/// Native Zephyr/ZMK companion HID transport for PIXEL PRO.
///
/// The normal ZMK keyboard interface remains owned by Windows. LumiPad opens
/// the second vendor HID interface (64-byte payload, report ID 0) only for
/// connection diagnostics and key/navigation events.
/// </summary>
public sealed class PixelProZmkLink : IDeviceLink
{
    private const int VendorPayloadBytes = 64;
    private const int HidSharpReportBytes = VendorPayloadBytes + 1;

    private readonly ProductDefinition _product;
    private HidDevice? _device;
    private HidStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private string _connectionName = "";

    public PixelProZmkLink(ProductDefinition product)
    {
        _product = product;
    }

    public bool IsConnected => _stream is not null;
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

        if (!_product.UsbVendorId.HasValue ||
            !_product.UsbProductId.HasValue)
        {
            Log("ERROR", "PIXEL PRO ZMK has no USB VID/PID configured.");
            return null;
        }

        IEnumerable<HidDevice> candidates =
            DeviceList.Local.GetHidDevices(
                _product.UsbVendorId.Value,
                _product.UsbProductId.Value);

        foreach (HidDevice device in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int inputLength;
            int outputLength;
            int featureLength;

            try
            {
                inputLength = device.GetMaxInputReportLength();
                outputLength = device.GetMaxOutputReportLength();
                featureLength = device.GetMaxFeatureReportLength();
            }
            catch (Exception ex)
            {
                Log("WARN", $"HID descriptor read failed: {ex.Message}");
                continue;
            }

            string productName = Safe(() => device.GetProductName(), _product.Name);
            string serial = Safe(() => device.GetSerialNumber(), "");
            string manufacturer = Safe(() => device.GetManufacturer(), "");

            Log(
                "INFO",
                $"HID candidate VID=0x{device.VendorID:X4} PID=0x{device.ProductID:X4} " +
                $"PRODUCT=\"{productName}\" SERIAL=\"{serial}\" " +
                $"IN={inputLength} OUT={outputLength} FEATURE={featureLength}");

            // The phase-1 vendor interface has 64-byte Input + Feature reports.
            // HidSharp includes the report-ID byte, therefore both appear as 65.
            // The keyboard interface is deliberately skipped by this filter.
            if (inputLength < HidSharpReportBytes ||
                featureLength < HidSharpReportBytes)
            {
                continue;
            }

            if (!device.TryOpen(out HidStream stream))
            {
                Log("WARN", "PIXEL PRO vendor HID interface could not be opened.");
                continue;
            }

            try
            {
                stream.ReadTimeout = 500;
                stream.WriteTimeout = 500;

                string? hello = ReadFeatureHello(stream, featureLength);

                _device = device;
                _stream = stream;
                FirmwareHello =
                    !string.IsNullOrWhiteSpace(hello)
                        ? hello
                        : "PIXELPRO|ZMK|unknown";
                ProtocolVersion =
                    FirmwareHello.StartsWith(
                        "PIXELPRO|ZMK|",
                        StringComparison.Ordinal)
                        ? 1
                        : 0;

                _connectionName = $"ZMK USB · {productName}";

                Log(
                    "INFO",
                    $"Connected {_connectionName}; manufacturer=\"{manufacturer}\"; " +
                    $"serial=\"{serial}\"; hello=\"{FirmwareHello}\"; " +
                    $"vendor usage expected 0x{_product.RawUsagePage:X4}/0x{_product.RawUsageId:X4}");

                StartReader(stream, inputLength);
                await Task.Yield();
                return _connectionName;
            }
            catch (Exception ex)
            {
                Log("WARN", $"PIXEL PRO vendor HID probe failed: {ex.Message}");
                stream.Dispose();
            }
        }

        Log(
            "WARN",
            $"PIXEL PRO ZMK vendor HID not found at " +
            $"VID=0x{_product.UsbVendorId.Value:X4} PID=0x{_product.UsbProductId.Value:X4}.");
        return null;
    }

    private string? ReadFeatureHello(HidStream stream, int featureLength)
    {
        try
        {
            byte[] feature = new byte[Math.Max(featureLength, HidSharpReportBytes)];
            feature[0] = 0;
            stream.GetFeature(feature);
            string hello = DecodeAscii(feature, 1, feature.Length - 1);

            if (!string.IsNullOrWhiteSpace(hello))
            {
                Log("INFO", $"PIXEL PRO feature report: {hello}");
                return hello;
            }
        }
        catch (Exception ex)
        {
            // Enumeration + the dedicated report shape are sufficient to keep
            // the link usable; report the control-transfer failure explicitly.
            Log("WARN", $"PIXEL PRO feature HELLO failed: {ex.Message}");
        }

        return null;
    }

    private void StartReader(HidStream stream, int inputLength)
    {
        _readCts = new CancellationTokenSource();
        CancellationToken token = _readCts.Token;

        _readTask = Task.Run(
            () => ReadLoop(stream, Math.Max(inputLength, HidSharpReportBytes), token),
            token);
    }

    private void ReadLoop(
        HidStream stream,
        int inputLength,
        CancellationToken token)
    {
        byte[] report = new byte[inputLength];

        while (!token.IsCancellationRequested)
        {
            try
            {
                int count = stream.Read(report, 0, report.Length);
                if (count <= 1)
                    continue;

                string message = DecodeAscii(report, 1, count - 1);
                if (string.IsNullOrWhiteSpace(message))
                    continue;

                string category =
                    message.StartsWith("KEY|", StringComparison.Ordinal)
                        ? "KEY"
                        : message.StartsWith("NAV|", StringComparison.Ordinal)
                            ? "NAV"
                            : "HID";

                Log(category, message);
            }
            catch (TimeoutException)
            {
                // Expected while no key events are pending.
            }
            catch (IOException ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Log("ERROR", $"PIXEL PRO HID disconnected: {ex.Message}");
                    LinkError?.Invoke(ex.Message);
                }
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Log("WARN", $"PIXEL PRO HID read error: {ex.Message}");
                }
            }
        }
    }

    private static string DecodeAscii(byte[] data, int offset, int count)
    {
        int end = offset;
        int limit = Math.Min(data.Length, offset + count);

        while (end < limit && data[end] != 0)
            end++;

        return end > offset
            ? Encoding.ASCII.GetString(data, offset, end - offset)
                .Trim('\r', '\n', ' ')
            : "";
    }

    private static string Safe(Func<string> getter, string fallback)
    {
        try
        {
            string value = getter();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    public Task<string?> ConnectBluetoothAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> PromoteToUsbIfAvailableAsync(
        CancellationToken cancellationToken = default) =>
        IsConnected
            ? Task.FromResult<string?>(_connectionName)
            : ConnectUsbAsync(cancellationToken);

    public void Disconnect()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

        HidStream? stream = _stream;
        _stream = null;
        _device = null;

        try { stream?.Dispose(); } catch { }

        _readTask = null;
        _connectionName = "";
        FirmwareHello = "";
        ProtocolVersion = 0;
    }

    // Phase 1 exposes keyboard input + diagnostics only.
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

    public Task RestartKeyboardAsync() => Task.CompletedTask;
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
