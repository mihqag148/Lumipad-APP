using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Input;
using Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaColor = System.Windows.Media.Color;
using IO = System.IO;

namespace LumiPad.App;

public partial class MainWindow : Window
{
    private ProductDefinition _activeProduct = ProductCatalog.RynorOne;
    private IDeviceLink _serial;
    private readonly Dictionary<string, IDeviceLink> _deviceLinks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _connectionPreferences =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _autoReconnectByProduct =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _sleepingByProduct =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _batteryByProduct =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _firmwareLogSeqByProduct =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _actionEventSeqByProduct =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly NowPlayingService _nowPlaying = new();
    private readonly PcMonitorService _pcMonitorService = new();

    private bool _uiReady;
    private bool _lightTheme;
    private string _language = "en";
    private bool _allowExit;
    private bool _trayTipShown;
    private bool _configuratorInitialized;
    private string _loadedConfiguratorUrl = "";

    private bool _autoReconnectEnabled
    {
        get => !_autoReconnectByProduct.TryGetValue(
                   _activeProduct.Id,
                   out bool enabled) || enabled;
        set => _autoReconnectByProduct[_activeProduct.Id] = value;
    }

    private string _connectionPreference
    {
        get => _connectionPreferences.TryGetValue(
                   _activeProduct.Id,
                   out string? value)
               ? value
               : "auto";
        set => _connectionPreferences[_activeProduct.Id] = value;
    }

    private bool _keyboardSleeping
    {
        get => _sleepingByProduct.TryGetValue(
                   _activeProduct.Id,
                   out bool sleeping) && sleeping;
        set => _sleepingByProduct[_activeProduct.Id] = value;
    }

    private uint _lastActionEventSeq
    {
        get => _actionEventSeqByProduct.TryGetValue(
                   _activeProduct.Id,
                   out uint value)
               ? value
               : 0;
        set => _actionEventSeqByProduct[_activeProduct.Id] = value;
    }

    private uint _firmwareLogSeq
    {
        get => _firmwareLogSeqByProduct.TryGetValue(
                   _activeProduct.Id,
                   out uint value)
               ? value
               : 0;
        set => _firmwareLogSeqByProduct[_activeProduct.Id] = value;
    }

    private int? _activeBatteryPercent
    {
        get => _batteryByProduct.TryGetValue(
                   _activeProduct.Id,
                   out int? value)
               ? value
               : null;
        set => _batteryByProduct[_activeProduct.Id] = value;
    }

    private readonly CancellationTokenSource _reconnectCts = new();
    private static readonly HttpClient UpdateHttp = CreateUpdateHttpClient();
    private const string UpdateReleaseApi =
        "https://api.github.com/repos/mihqag148/Lumipad-APP/releases/latest";
    private const string RynorFirmwareReleaseApi =
        "https://api.github.com/repos/mihqag148/RYNOR-ONE/releases/latest";
    private const string PixelProFirmwareReleaseApi =
        "https://api.github.com/repos/mihqag148/PIXEL-PRO/releases/latest";
    private const string PixelProNativeFirmwareAsset =
        "PIXEL_PRO_merged.bin";
    private bool _updateBusy;
    private bool _checkingUpdates;
    private bool _appUpdateAvailable;
    private bool _firmwareUpdateAvailable;
    private string _latestAppVersion = "";
    private string _latestFirmwareVersion = "";
    private string _latestReleaseTag = "";

    private byte _r = 255;
    private byte _g = 120;
    private byte _b = 0;
    private ScreensaverAnimation? _screensaverAnimation;
    // Keep RYNOR and PIXEL media paths isolated. Older builds shared one path,
    // which let a saved PIXEL GIF get reprocessed by the RYNOR 160×86 service
    // during startup before PIXEL PRO became the active product.
    private string? _screensaverMediaPath;
    private string? _rynorScreensaverMediaPath;
    private string? _pixelScreensaverMediaPath;

    private readonly PixelProMainMenuConfig _pixelMainMenu =
        PixelProMainMenuStore.Load();
    private bool _syncingPixelMenuUi;
    private readonly List<System.Windows.Controls.ComboBox> _pixelMenuActionCombos = [];
    private readonly List<System.Windows.Controls.Image> _pixelMenuEditorIcons = [];
    private readonly List<System.Windows.Controls.Image> _pixelMenuPreviewIcons = [];
    private readonly List<TextBlock> _pixelMenuPreviewLabels = [];

    private readonly DispatcherTimer _screensaverPreviewTimer = new();
    private readonly Stopwatch _screensaverPreviewClock = new();
    private readonly DispatcherTimer _pixelRgbPreviewTimer = new();
    private readonly Stopwatch _pixelRgbPreviewClock = new();
    private readonly DispatcherTimer _memoryUsageTimer = new();
    private readonly DispatcherTimer _diagnosticTimer = new();
    private readonly DispatcherTimer _autoProfileTimer = new();
    private readonly DispatcherTimer _runningAppsTimer = new();
    private readonly DispatcherTimer _actionEventTimer = new();
    private readonly DispatcherTimer _productStatusTimer = new();
    private readonly DispatcherTimer _pcMonitorTimer = new();
    private readonly DispatcherTimer _updateCheckTimer = new();
    private readonly List<string> _logLines = new();
    private AutoProfileSettings _autoProfileSettings = new();
    private IReadOnlyList<RunningAppInfo> _runningApps = Array.Empty<RunningAppInfo>();
    private readonly Dictionary<string, ImageSource?> _applicationIconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _lastForegroundAppPath;
    private int _lastAppliedAutoProfile = -1;
    private int _lastAppliedAutoLayer = -1;
    private bool _syncingAutoProfileUi;
    private NowPlayingData? _currentNowPlaying;
    private bool _mediaSeekDragging;
    private bool _syncingMediaUi;
    private bool _volumeMuted;
    private DateTimeOffset _lastVolumeUiSync = DateTimeOffset.MinValue;
    private List<ActionScriptDefinition> _actionScripts = [];
    private bool _loadingActionScriptUi;
    private readonly HashSet<int> _runningActionIds = [];
    private ActionKeymapWindow? _actionKeymapWindow;
    private int _screensaverPreviewIndex;
    private int _rgbEffect = 3;
    private bool _rgbAuto;
    private int _screensaverDelaySeconds = 60;
    private int _sleepDelaySeconds = 120;
    private int _rgbIdleDelaySeconds = 60;
    private int _deepSleepDelaySeconds = 0;
    private int _rgbBrightness = 25;
    private int _rgbSpeed = 50;
    private bool _rgbEnabled = true;
    private int _rgbProfileIndex;
    private RgbProfileSetting[] _rgbProfiles = CreateDefaultRgbProfiles();

    // PIXEL PRO keeps its media/RGB state separate from RYNOR ONE.
    private int _pixelGifMaxFps = PixelProScreensaverMediaService.DefaultGifMaxFps;
    private int _pixelGifMaxDurationSeconds = PixelProScreensaverMediaService.DefaultGifDurationSeconds;
    private int _pixelImageJpegQuality = PixelProScreensaverMediaService.DefaultImageJpegQuality;
    private ScreensaverScaleMode _pixelMediaScaleMode = ScreensaverScaleMode.Fill;
    private int _pixelRgbSelectedKey = -1; // -1 = all 8 keys
    private PixelRgbColor[][] _pixelRgbProfiles = CreateDefaultPixelRgbProfiles();
    private int[] _pixelRgbEffects = Enumerable.Repeat(3, 20).ToArray();
    private int _pixelRgbSpeed = 50;

    // This remains the RYNOR scale preference. PIXEL PRO keeps a separate Windows-style layout preference.
    private ScreensaverScaleMode _screensaverScaleMode = ScreensaverScaleMode.Fill;
    private string _screensaverSource = "Media";

    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _appIcon;
    private sealed record ProductCardVisual(
        ProductDefinition Product,
        Border Card,
        Ellipse Dot,
        TextBlock ConnectionText,
        TextBlock BatteryText);

    private readonly Dictionary<string, ProductCardVisual> _productCardVisuals =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _pcMonitorEnabled = true;
    private int _pcMonitorIntervalMs = 1000;
    private bool _pcMonitorPolling;
    private PcMonitorSnapshot? _lastPcMonitorSnapshot;
    private string _pcMonitorGpuId = "auto";
    private string _pcMonitorConfigName = "MY PC";
    private bool _syncingPcMonitorUi;
    private bool _syncingPcMetricUi;
    private int[] _pcMonitorMetricSlots = [0, 3, 6, 9, 10, 11];

    private sealed class PixelViaKeyVisual
    {
        public required int Index { get; init; }
        public required System.Windows.Controls.Button Button { get; init; }
        public required TextBlock MainText { get; init; }
        public required TextBlock SubText { get; init; }
    }

    private sealed class PixelViaExport
    {
        public int Version { get; set; } = 2;
        public int ProfileIndex { get; set; }
        public string ProfileName { get; set; } = "";
        public PixelProKeyBinding[][] Layers { get; set; } = [];
    }

    private readonly List<PixelViaKeyVisual> _pixelViaKeys = [];
    private readonly PixelProKeyBinding[][][] _pixelProfileMaps =
        Enumerable.Range(0, 20)
            .Select(_ =>
                Enumerable.Range(0, 4)
                    .Select(layer =>
                        Enumerable.Range(0, 8)
                            .Select(i =>
                                layer == 0
                                    ? PixelProKeyBinding.Keyboard((byte)(4 + i))
                                    : PixelProKeyBinding.Transparent())
                            .ToArray())
                    .ToArray())
            .ToArray();
    private readonly bool[,] _pixelLayerLoaded = new bool[20, 4];

    private readonly PixelProProfileCatalog _pixelProfileCatalog =
        PixelProProfileStore.Load();
    private readonly PixelProModifierPositionCatalog _pixelModifierPositions =
        PixelProKeyEditorUiStore.Load();
    private readonly List<PixelProMacroDefinition> _pixelMacros =
        PixelProMacroStore.Load();
    private readonly HashSet<int> _runningPixelMacroSlots = [];

    private int _pixelSelectedProfile;
    private int _pixelSelectedLayer;
    private int _pixelSelectedKey;
    private int _pixelSelectedMacroSlot = 1;
    private int _pixelMacroDragIndex = -1;
    private System.Windows.Point _pixelMacroDragStartPoint;
    private bool _pixelProfileRenameActive;
    private bool _pixelModifierOrderDragging;
    private System.Windows.Controls.Border? _pixelModifierDragCard;
    private System.Windows.Point _pixelModifierDragStart;
    private string _pixelCurrentCategory = "Basic";
    private bool _pixelViaUiBuilt;
    private bool _pixelViaUpdating;

    private static readonly PixelProKeyChoice[] PixelKeyChoices =
        CreatePixelKeyChoices();

    private static readonly (int Id, string Name)[] PcMonitorMetricChoices =
    [
        (0, "CPU usage"),
        (1, "CPU temperature"),
        (2, "CPU clock"),
        (3, "GPU usage"),
        (4, "GPU temperature"),
        (5, "GPU clock"),
        (6, "RAM usage"),
        (7, "RAM used"),
        (8, "RAM total"),
        (9, "Network download"),
        (10, "Network upload"),
        (11, "FPS")
    ];

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "LumiPad-Updater/1.20.8");
        client.DefaultRequestHeaders.CacheControl =
            new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
        client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    public MainWindow()
    {
        foreach (ProductDefinition product in ProductCatalog.All)
        {
            IDeviceLink link =
                DeviceLinkFactory.Create(product);

            _deviceLinks[product.Id] = link;
            _connectionPreferences[product.Id] = "auto";
            _autoReconnectByProduct[product.Id] = true;
            _sleepingByProduct[product.Id] = false;
            _batteryByProduct[product.Id] = null;
            _firmwareLogSeqByProduct[product.Id] = 0;
            _actionEventSeqByProduct[product.Id] = 0;
        }

        _serial = LinkFor(_activeProduct);

        InitializeComponent();

        foreach (ProductDefinition product in ProductCatalog.All)
        {
            AttachDeviceLinkEvents(
                product,
                LinkFor(product));
        }

        BuildPixelProKeymapUi();
        InitializeTrayIcon();

        // Keep the preview on an absolute playback timeline, just like the
        // firmware. RYNOR ONE keeps its existing 25 FPS converter. PIXEL PRO
        // uses its own 480x320/60 FPS media profile without changing RYNOR.
        _screensaverPreviewTimer.Interval =
            TimeSpan.FromMilliseconds(
                ScreensaverMediaService.MinFrameIntervalMs);
        _screensaverPreviewTimer.Tick += (_, _) =>
        {
            if (_screensaverAnimation is null ||
                _screensaverAnimation.PixelFormat != ScreensaverPixelFormat.Rgb332 ||
                _screensaverAnimation.Frames.Count < 2)
            {
                return;
            }

            int frameCount = _screensaverAnimation.Frames.Count;
            int loopMs =
                _screensaverAnimation.FrameDurationsMs.Count == frameCount
                    ? _screensaverAnimation.FrameDurationsMs.Sum()
                    : _screensaverAnimation.FrameIntervalMs * frameCount;

            if (loopMs <= 0)
                return;

            int loopPosition =
                (int)(_screensaverPreviewClock.ElapsedMilliseconds % loopMs);
            int desiredIndex = 0;
            int frameStart = 0;
            int frameDuration = _screensaverAnimation.FrameIntervalMs;
            int boundary = 0;

            for (int i = 0; i < frameCount; i++)
            {
                int duration =
                    _screensaverAnimation.FrameDurationsMs.Count == frameCount
                        ? _screensaverAnimation.FrameDurationsMs[i]
                        : _screensaverAnimation.FrameIntervalMs;

                int minFrameIntervalMs =
                    IsPixelProActive
                        ? PixelProScreensaverMediaService.MinFrameIntervalMs
                        : ScreensaverMediaService.MinFrameIntervalMs;

                duration = Math.Max(
                    minFrameIntervalMs,
                    duration);

                frameStart = boundary;
                boundary += duration;
                desiredIndex = i;
                frameDuration = duration;

                if (loopPosition < boundary)
                    break;
            }

            int nextIndex = (desiredIndex + 1) % frameCount;
            double blend =
                Math.Clamp(
                    (loopPosition - frameStart) /
                    (double)Math.Max(1, frameDuration),
                    0.0,
                    1.0);

            _screensaverPreviewIndex = desiredIndex;
            ScreensaverPreviewImage.Source =
                CreateRgb332InterpolatedBitmap(
                    _screensaverAnimation.Frames[desiredIndex],
                    _screensaverAnimation.Frames[nextIndex],
                    blend,
                    _screensaverAnimation.Width,
                    _screensaverAnimation.Height);
        };

        // PIXEL RGB preview is purely local UI animation. It samples the
        // same effect math as firmware at ~30 FPS and never sends CDC traffic
        // on preview ticks.
        _pixelRgbPreviewTimer.Interval =
            TimeSpan.FromMilliseconds(33);

        _pixelRgbPreviewTimer.Tick += (_, _) =>
            RenderPixelRgbPreview();

        _memoryUsageTimer.Interval = TimeSpan.FromSeconds(5);
        _memoryUsageTimer.Tick += async (_, _) =>
            await UpdateMemoryUsageAsync();
        _memoryUsageTimer.Start();

        _diagnosticTimer.Interval = TimeSpan.FromSeconds(2);
        _diagnosticTimer.Tick += async (_, _) => await PollFirmwareDiagnosticsAsync();
        _diagnosticTimer.Start();

        _autoProfileTimer.Interval = TimeSpan.FromMilliseconds(700);
        _autoProfileTimer.Tick += (_, _) => PollAutoProfile();

        _runningAppsTimer.Interval = TimeSpan.FromSeconds(4);
        _runningAppsTimer.Tick += async (_, _) => await RefreshRunningAppsAsync();

        _actionEventTimer.Interval = TimeSpan.FromMilliseconds(120);
        _actionEventTimer.Tick += async (_, _) => await PollLumiActionAsync();

        _productStatusTimer.Interval = TimeSpan.FromSeconds(10);
        _productStatusTimer.Tick += async (_, _) =>
            await UpdateProductOverviewAsync();

        _pcMonitorTimer.Interval = TimeSpan.FromMilliseconds(_pcMonitorIntervalMs);
        _pcMonitorTimer.Tick += async (_, _) =>
            await PollPcMonitorAsync();

        _updateCheckTimer.Interval = TimeSpan.FromMinutes(30);
        _updateCheckTimer.Tick += async (_, _) =>
            await CheckForUpdatesAsync(silent: true);

        System.Windows.Application.Current.DispatcherUnhandledException += (_, args) =>
        {
            AddLog("ERROR", "APP", $"Unhandled UI exception: {args.Exception}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Dispatcher.Invoke(() => AddLog("FATAL", "APP", $"Unhandled: {args.ExceptionObject}"));

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Dispatcher.Invoke(() =>
                AddLog("ERROR", "APP", $"Unobserved task exception: {args.Exception}"));
            args.SetObserved();
        };

        Loaded += async (_, _) =>
        {
            LoadTheme();
            LoadLanguage();
            LoadAppSettings();
            _autoProfileSettings = AutoProfileService.Load();
            _actionScripts = ActionScriptStore.Load();
            ApplyLanguage();
            InitializePcMetricSelectors();
            ApplyStoredControlValues();
            ApplyAutoProfileUiState();
            RefreshActionScriptsUi();
            BuildProductCards();
            UpdateDeviceConfiguratorUi();
            _uiReady = true;
            BuildPixelMainMenuEditor();
            RefreshPixelMainMenuUi();
            _pixelRgbPreviewClock.Restart();
            _pixelRgbPreviewTimer.Start();
            UpdateSettingsInfo();
            UpdateProductHubUi();
            AddLog("INFO", "APP", "Lumi Macropad started");
            BuildColorWheel();
            SetDeviceControlsEnabled(false);

            _nowPlaying.Updated += data =>
                Dispatcher.Invoke(() => ApplyNowPlaying(data));

            _nowPlaying.Cleared += () =>
                Dispatcher.Invoke(ClearNowPlaying);

            try
            {
                await _nowPlaying.StartAsync();
            }
            catch (Exception ex)
            {
                BottomStatus.Text = L($"Now Playing unavailable: {ex.Message}", $"Không dùng được Now Playing: {ex.Message}");
            }

            foreach (ProductDefinition product in ProductCatalog.All)
            {
                _connectionPreferences[product.Id] = "auto";
                _autoReconnectByProduct[product.Id] = true;
            }

            AddLog(
                "INFO",
                "APP",
                "Parallel auto-connect enabled for RYNOR ONE and PIXEL PRO");

            await ConnectAllProductsAsync();
            await CheckForUpdatesAsync(silent: true);
            _updateCheckTimer.Start();
            _ = AutoReconnectLoopAsync(_reconnectCts.Token);
            _autoProfileTimer.Start();
            _runningAppsTimer.Start();
            _actionEventTimer.Start();
            _productStatusTimer.Start();
            if (_pcMonitorEnabled)
                _pcMonitorTimer.Start();
            await PollPcMonitorAsync(force: true);
            await RefreshRunningAppsAsync();
            await UpdateProductOverviewAsync();
            PollAutoProfile(force: true);
        };

        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private IDeviceLink LinkFor(ProductDefinition product) =>
        _deviceLinks[product.Id];

    private bool IsActiveProduct(ProductDefinition product) =>
        string.Equals(
            _activeProduct.Id,
            product.Id,
            StringComparison.OrdinalIgnoreCase);

    private void BuildProductCards()
    {
        if (ProductCardsPanel is null)
            return;

        ProductCardsPanel.Children.Clear();
        _productCardVisuals.Clear();

        foreach (ProductDefinition product in ProductCatalog.All)
        {
            var button = new System.Windows.Controls.Button
            {
                Tag = product,
                Width = 394,
                Height = 502,
                Padding = new Thickness(0),
                Margin = new Thickness(10),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FocusVisualStyle = null,
                ClipToBounds = true
            };
            button.Click += ProductCard_Click;

            var card = new Border
            {
                Width = 388,
                Height = 496,
                Background =
                    TryFindResource("Card") as System.Windows.Media.Brush,
                BorderBrush =
                    TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(26),
                ClipToBounds = true
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(304)
            });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1)
            });
            root.RowDefinitions.Add(new RowDefinition());

            var preview = new Border
            {
                Background =
                    TryFindResource("Card2") as System.Windows.Media.Brush,
                Padding = new Thickness(24),
                Margin = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(25, 25, 0, 0),
                ClipToBounds = true
            };
            preview.Child = SafeCreateProductPreview(product);
            root.Children.Add(preview);

            var divider = new Border
            {
                Background =
                    TryFindResource("Line") as System.Windows.Media.Brush
            };
            Grid.SetRow(divider, 1);
            root.Children.Add(divider);

            var info = new Grid
            {
                Margin = new Thickness(24, 22, 24, 20)
            };
            info.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            info.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            info.RowDefinitions.Add(new RowDefinition());
            info.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });

            info.Children.Add(new TextBlock
            {
                Text = product.Name,
                FontSize = 25,
                FontWeight = FontWeights.SemiBold
            });

            var subtitle = new TextBlock
            {
                Text = product.Subtitle,
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                FontSize = 12,
                Margin = new Thickness(0, 5, 0, 0)
            };
            Grid.SetRow(subtitle, 1);
            info.Children.Add(subtitle);

            var status = new Grid
            {
                Margin = new Thickness(0, 18, 0, 0)
            };
            status.ColumnDefinitions.Add(new ColumnDefinition());
            status.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            var left = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(
                    MediaColor.FromRgb(99, 99, 102)),
                Margin = new Thickness(0, 0, 8, 0)
            };
            left.Children.Add(dot);

            var connectionText = new TextBlock
            {
                Text = L("Not connected", "Chưa kết nối"),
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            left.Children.Add(connectionText);
            status.Children.Add(left);

            var batteryText = new TextBlock
            {
                Text = "▰ --%",
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(batteryText, 1);
            status.Children.Add(batteryText);

            Grid.SetRow(status, 3);
            info.Children.Add(status);

            Grid.SetRow(info, 2);
            root.Children.Add(info);
            card.Child = root;
            button.Content = card;
            ProductCardsPanel.Children.Add(button);

            _productCardVisuals[product.Id] =
                new ProductCardVisual(
                    product,
                    card,
                    dot,
                    connectionText,
                    batteryText);

        }

        UpdateProductHubUi();
    }

    private UIElement SafeCreateProductPreview(ProductDefinition product)
    {
        try
        {
            return CreateProductPreview(product);
        }
        catch (Exception ex)
        {
            AddLog(
                "WARN",
                "APP",
                $"Product image failed for {product.Name}: {ex.Message}");

            return new Border
            {
                Background =
                    TryFindResource("Card2") as System.Windows.Media.Brush,
                Child = new TextBlock
                {
                    Text = product.Name,
                    FontSize = 24,
                    FontWeight = FontWeights.SemiBold,
                    Foreground =
                        TryFindResource("Text") as System.Windows.Media.Brush,
                    HorizontalAlignment =
                        System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment =
                        VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }
    }

    private static System.Windows.Controls.Image CreateProductHubImage(
        ImageSource source)
    {
        var image =
            new System.Windows.Controls.Image
            {
                Source = source,
                Width = 276,
                Height = 252,
                Stretch = Stretch.Uniform,
                HorizontalAlignment =
                    System.Windows.HorizontalAlignment.Center,
                VerticalAlignment =
                    VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

        RenderOptions.SetBitmapScalingMode(
            image,
            BitmapScalingMode.HighQuality);

        return image;
    }

    private UIElement CreateProductPreview(ProductDefinition product)
    {
        // The two PNG blobs previously embedded in Assets/Products are
        // truncated/corrupt. Use the already embedded, validated product
        // artwork providers instead so single-file publishing cannot damage
        // or partially decode the product-selection previews.
        if (product.Driver == DeviceDriverKind.PixelProCdc)
        {
            return CreateProductHubImage(
                PixelProProductImage.Create());
        }

        return CreateRynorOnePreview();
    }

    private UIElement CreateRynorOnePreview()
    {
        return CreateProductHubImage(
            RynorOneProductImage.Create());
    }

    private async void ProductCard_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not ProductDefinition product)
            return;

        if (!string.Equals(
                _activeProduct.Id,
                product.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            await SwitchActiveProductAsync(product);
        }

        if (product.Driver == DeviceDriverKind.RynorSerial)
        {
            // RYNOR ONE can already be the active product on startup, so opening
            // its card must still reapply RYNOR-specific text and panel state.
            // Keep this guard RYNOR-only so PIXEL PRO behavior is untouched.
            UpdateDeviceConfiguratorUi();
            UpdateProductSpecificText();
        }

        WorkspaceProductTitle.Text = product.Name;
        ProductHub.Visibility = Visibility.Collapsed;
        DeviceWorkspace.Visibility = Visibility.Visible;
        await UpdateProductOverviewAsync();
    }

    private async void BackToProducts_Click(
        object sender,
        RoutedEventArgs e)
    {
        DeviceWorkspace.Visibility = Visibility.Collapsed;
        ProductHub.Visibility = Visibility.Visible;
        await UpdateProductOverviewAsync();
    }

    private void AttachDeviceLinkEvents(
        ProductDefinition product,
        IDeviceLink link)
    {
        link.Diagnostic += (level, message) =>
            Dispatcher.Invoke(() =>
                AddLog(
                    level,
                    product.Name,
                    message));

        link.LinkError += message =>
            Dispatcher.Invoke(() =>
            {
                AddLog(
                    "ERROR",
                    product.Name,
                    message);

                _sleepingByProduct[product.Id] = false;
                _batteryByProduct[product.Id] = null;

                if (IsActiveProduct(product))
                {
                    DeviceStatus.Text =
                        L(
                            "Device link error",
                            "Lỗi kết nối thiết bị");

                    DeviceDot.Fill =
                        new SolidColorBrush(
                            MediaColor.FromRgb(
                                255,
                                69,
                                58));

                    BottomStatus.Text = message;
                    SetDeviceControlsEnabled(false);
                    UpdateTransportIndicators();
                    UpdateSleepButtonUi();
                }

                UpdateProductHubUi();
            });

        if (link is PixelProCdcLink pixel)
        {
            pixel.KeyStateChanged += (index, down, layer) =>
                Dispatcher.Invoke(() =>
                {
                    if (IsActiveProduct(product))
                    {
                        UpdatePixelMatrixTest(
                            index,
                            down,
                            layer);
                    }
                });

            pixel.MacroTriggered += (slot, key, profile, layer) =>
                Dispatcher.BeginInvoke(
                    new Action(async () =>
                        await ExecutePixelMacroAsync(
                            slot,
                            key,
                            profile,
                            layer)));

            pixel.ActionTriggered += (actionId, key, profile, layer) =>
                Dispatcher.BeginInvoke(
                    new Action(async () =>
                        await ExecutePixelActionAsync(
                            actionId,
                            key,
                            profile,
                            layer)));
        }
    }

    private async Task SwitchActiveProductAsync(ProductDefinition product)
    {
        try
        {
            _activeProduct = product;
            _serial = LinkFor(product);

            _screensaverMediaPath =
                string.Equals(
                    product.Id,
                    ProductCatalog.PixelPro.Id,
                    StringComparison.OrdinalIgnoreCase)
                    ? _pixelScreensaverMediaPath
                    : _rynorScreensaverMediaPath;

            _screensaverAnimation = null;
            _screensaverPreviewTimer.Stop();
            _screensaverPreviewClock.Reset();

            _configuratorInitialized = false;
            _loadedConfiguratorUrl = "";

            UpdateDeviceConfiguratorUi();
            UpdateProductSpecificText();

            if (!string.IsNullOrWhiteSpace(_screensaverMediaPath) &&
                System.IO.File.Exists(_screensaverMediaPath))
            {
                await PrepareScreensaverMediaAsync();
            }
            else
            {
                ScreensaverPreviewImage.Source = null;
                ScreensaverPreviewImage.Visibility = Visibility.Collapsed;
                ScreensaverPreviewHint.Visibility = Visibility.Visible;

                if (PixelHomePreviewHint is not null)
                    PixelHomePreviewHint.Visibility =
                        IsPixelProActive
                            ? Visibility.Visible
                            : Visibility.Collapsed;
            }

            SetDeviceControlsEnabled(_serial.IsConnected);
            UpdateTransportIndicators();
            UpdateSleepButtonUi();
            UpdateProductHubUi();
            UpdateSettingsInfo();

            AddLog(
                "INFO",
                "APP",
                $"Active page: {product.Name} ({product.Driver}); connection kept alive");

            if (!_serial.IsConnected)
            {
                await DetectAsync();
            }
            else
            {
                await UpdateMemoryUsageAsync();
                await UpdatePanelInfoAsync();
                await CheckForUpdatesAsync(silent: true);
            }
        }
        catch (Exception ex)
        {
            AddLog(
                "ERROR",
                "APP",
                $"Cannot activate {product.Name}: {ex.Message}");

            BottomStatus.Text = ex.Message;
        }
    }

    private async Task ConnectAllProductsAsync()
    {
        foreach (ProductDefinition product in ProductCatalog.All)
        {
            IDeviceLink link = LinkFor(product);

            if (link.IsConnected)
                continue;

            try
            {
                string? connection =
                    await link.AutoDetectAsync();

                if (connection is null)
                {
                    AddLog(
                        "INFO",
                        product.Name,
                        "Parallel auto-connect: not found");
                    continue;
                }

                AddLog(
                    "INFO",
                    product.Name,
                    $"Parallel auto-connect: {connection}");

                _sleepingByProduct[product.Id] = false;
                _batteryByProduct[product.Id] =
                    await link.ReadBatteryPercentAsync();

                if (IsActiveProduct(product))
                {
                    DeviceStatus.Text = connection;
                    DeviceDot.Fill =
                        new SolidColorBrush(
                            MediaColor.FromRgb(
                                48,
                                209,
                                88));

                    SetDeviceControlsEnabled(true);
                    UpdateTransportIndicators();
                    UpdateSleepButtonUi();
                }
            }
            catch (Exception ex)
            {
                AddLog(
                    "WARN",
                    product.Name,
                    $"Parallel auto-connect failed: {ex.Message}");
            }
        }

        UpdateProductHubUi();
        await UpdateProductOverviewAsync();
    }

    private void UpdateProductSpecificText()
    {
        string productName = _activeProduct.Name;
        UpdateScreensaverProductUi();
        UpdateRgbProductUi();

        if (WorkspaceProductTitle is not null)
            WorkspaceProductTitle.Text = productName;

        if (SendScreensaverButton is not null)
            SendScreensaverButton.Content =
                L($"Send to {productName}", $"Gửi tới {productName}");

        if (ScreensaverMediaInfo is not null &&
            _screensaverAnimation is null)
        {
            ScreensaverMediaInfo.Text =
                IsPixelProActive
                    ? L(
                        "Choose a GIF or image. Final output specs will appear here after processing.",
                        "Chọn GIF hoặc ảnh. Thông số đầu ra sau xử lý sẽ hiển thị tại đây.")
                    : L(
                        $"Converted to a lightweight loop for {productName}.",
                        $"Tự chuyển thành vòng lặp nhẹ cho {productName}.");
        }

        if (IsPixelProActive)
        {
            if (PanelInfoText is not null)
            {
                PanelInfoText.Text =
                    "ILI9486 · 480×320 landscape · i8080 8-bit · refresh cap 60 Hz · GIF ≤60 FPS";
            }
        }
        else
        {
            if (RynorPanelInfoText is not null)
            {
                RynorPanelInfoText.Text =
                    "ST7789 ≈60 Hz default · SPI 32 MHz · GIF ≤25 FPS";
            }
        }

        if (LumiActionDescriptionText is not null)
        {
            LumiActionDescriptionText.Text =
                L(
                    $"Create actions and assign them to {productName} keys.",
                    $"Tạo action và gán vào các phím {productName}.");
        }

        if (PcMonitorEnabledCheckBox is not null)
            PcMonitorEnabledCheckBox.Content =
                L($"Stream to {productName}", $"Gửi tới {productName}");

        if (PcMonitorDisplayTitleText is not null)
            PcMonitorDisplayTitleText.Text = $"{productName.ToUpperInvariant()} DISPLAY";

        if (SettingsDeviceTitleText is not null)
            SettingsDeviceTitleText.Text =
                L($"{productName} status", $"Trạng thái {productName}");
    }

    private void UpdateScreensaverProductUi()
    {
        bool pixel = IsPixelProActive;

        // PIXEL PRO Home uses a compact media card beside a dedicated
        // 480×320 display preview. Screensaver controls move to the full-width
        // row below. RYNOR ONE keeps the original two-column Home layout.
        if (HomeDashboardGrid is not null &&
            HomeDashboardGrid.RowDefinitions.Count >= 3)
        {
            HomeDashboardGrid.RowDefinitions[0].Height =
                pixel
                    ? new GridLength(300)
                    : new GridLength(1, GridUnitType.Star);

            HomeDashboardGrid.RowDefinitions[2].Height =
                pixel
                    ? new GridLength(1, GridUnitType.Star)
                    : GridLength.Auto;

            if (HomeDashboardGrid.ColumnDefinitions.Count >= 3)
            {
                HomeDashboardGrid.ColumnDefinitions[0].Width =
                    pixel
                        ? new GridLength(1, GridUnitType.Star)
                        : new GridLength(1.18, GridUnitType.Star);

                HomeDashboardGrid.ColumnDefinitions[2].Width =
                    pixel
                        ? new GridLength(1, GridUnitType.Star)
                        : new GridLength(0.82, GridUnitType.Star);
            }
        }

        if (HomeMediaCard is not null)
        {
            HomeMediaCard.Padding =
                pixel
                    ? new Thickness(16)
                    : new Thickness(24);

            HomeMediaCard.Height =
                pixel
                    ? 300
                    : double.NaN;
        }

        if (AlbumArtBorder is not null)
        {
            AlbumArtBorder.Width =
                pixel ? 96 : 138;
            AlbumArtBorder.Height =
                pixel ? 96 : 138;
        }

        if (TitleText is not null)
            TitleText.FontSize =
                pixel ? 18 : 23;

        if (ArtistText is not null)
        {
            ArtistText.FontSize =
                pixel ? 12 : 14;
            ArtistText.Margin =
                pixel
                    ? new Thickness(0, 3, 0, 8)
                    : new Thickness(0, 5, 0, 17);
        }

        if (PixelHomePreviewCard is not null)
            PixelHomePreviewCard.Visibility =
                pixel
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        if (HomeScreensaverCard is not null)
        {
            Grid.SetRow(
                HomeScreensaverCard,
                pixel ? 2 : 0);

            Grid.SetColumn(
                HomeScreensaverCard,
                pixel ? 0 : 2);

            Grid.SetColumnSpan(
                HomeScreensaverCard,
                1);

            HomeScreensaverCard.Padding =
                pixel
                    ? new Thickness(18)
                    : new Thickness(22);
        }

        if (HomeMainMenuCard is not null)
        {
            HomeMainMenuCard.Visibility =
                pixel
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            Grid.SetRow(
                HomeMainMenuCard,
                2);

            Grid.SetColumn(
                HomeMainMenuCard,
                2);
        }

        if (ScreensaverPreviewBorder is not null)
        {
            ScreensaverPreviewBorder.Width =
                pixel ? 480 : 360;

            ScreensaverPreviewBorder.Height =
                pixel ? 320 : 193.5;

            ScreensaverPreviewBorder.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        if (ScreensaverPreviewSurface is not null)
        {
            ScreensaverPreviewSurface.Width =
                pixel
                    ? PixelProScreensaverMediaService.PanelWidth
                    : ScreensaverMediaService.StaticWidth;

            ScreensaverPreviewSurface.Height =
                pixel
                    ? PixelProScreensaverMediaService.PanelHeight
                    : ScreensaverMediaService.StaticHeight;
        }

        if (ScreensaverPreviewImage is not null)
        {
            ScreensaverPreviewImage.Stretch =
                System.Windows.Media.Stretch.Fill;

            ScreensaverPreviewImage.StretchDirection =
                System.Windows.Controls.StretchDirection.Both;
        }

        if (ScreensaverPreviewHint is not null &&
            _screensaverAnimation is null)
        {
            ScreensaverPreviewHint.Text =
                pixel
                    ? "480 × 320 · ILI9486 · GIF / Image"
                    : "320 × 172 · Choose a GIF or image";
        }

        if (PixelMediaSettingsPanel is not null)
            PixelMediaSettingsPanel.Visibility =
                pixel
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        if (ScreensaverScalePanel is not null)
            ScreensaverScalePanel.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        _screensaverPreviewTimer.Interval =
            TimeSpan.FromMilliseconds(
                pixel
                    ? PixelProScreensaverMediaService.MinFrameIntervalMs
                    : ScreensaverMediaService.MinFrameIntervalMs);

        // RYNOR keeps _screensaverScaleMode. PIXEL PRO uses its separate
        // Fill/Center selector and always previews a logical 480×320 canvas.
    }

    private void UpdateRgbProductUi()
    {
        bool pixel = IsPixelProActive;

        if (PixelRgbKeyPanel is not null)
            PixelRgbKeyPanel.Visibility =
                pixel
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        if (RynorRgbModeTitle is not null)
            RynorRgbModeTitle.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        if (RynorRgbModeButtons is not null)
            RynorRgbModeButtons.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        if (RynorRgbPresetsTitle is not null)
            RynorRgbPresetsTitle.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        if (RynorRgbPresetsPanel is not null)
            RynorRgbPresetsPanel.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        if (RgbProfileCombo is not null)
            RgbProfileCombo.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        if (PixelRgbSaveProfileCombo is not null)
            PixelRgbSaveProfileCombo.Visibility =
                pixel
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        if (PixelRgbSaveHint is not null)
            PixelRgbSaveHint.Visibility =
                pixel
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        if (RgbProfileTitle is not null)
            RgbProfileTitle.Text =
                pixel
                    ? L("Keymap RGB", "RGB theo keymap")
                    : "RGB Profile";

        if (RgbSaveProfileButton is not null)
            RgbSaveProfileButton.Content =
                pixel
                    ? L("Save / copy RGB to selected profile", "Lưu / copy RGB vào profile đã chọn")
                    : L("Save to profile", "Lưu vào profile");

        if (RgbSpeedSlider is not null)
        {
            RgbSpeedSlider.IsEnabled = true;
            RgbSpeedSlider.Value =
                pixel
                    ? _pixelRgbSpeed
                    : _rgbSpeed;
        }

        UpdatePixelRgbUi();
    }

    private void UpdateProductHubUi()
    {
        foreach (var pair in _productCardVisuals)
        {
            ProductCardVisual visual = pair.Value;
            IDeviceLink link =
                LinkFor(visual.Product);

            bool connected =
                link.IsConnected;

            string connection = connected
                ? link.IsUsbConnected
                    ? L("Connected · USB", "Đã kết nối · USB")
                    : link.IsBluetoothConnected
                        ? L("Connected · Bluetooth", "Đã kết nối · Bluetooth")
                        : L("Connected", "Đã kết nối")
                : L("Not connected", "Chưa kết nối");

            visual.ConnectionText.Text = connection;
            visual.ConnectionText.Foreground =
                TryFindResource(
                    connected
                        ? "TextPrimary"
                        : "Muted")
                    as System.Windows.Media.Brush;

            visual.Dot.Fill =
                new SolidColorBrush(
                    connected
                        ? MediaColor.FromRgb(
                            48,
                            209,
                            88)
                        : MediaColor.FromRgb(
                            99,
                            99,
                            102));

            int? battery =
                _batteryByProduct.TryGetValue(
                    visual.Product.Id,
                    out int? value)
                    ? value
                    : null;

            visual.BatteryText.Text =
                !visual.Product.SupportsBattery
                    ? ""
                    : connected && battery.HasValue
                        ? $"▰ {battery.Value}%"
                        : "▰ --%";

            visual.BatteryText.Foreground =
                TryFindResource(
                    connected && battery.HasValue
                        ? "TextPrimary"
                        : "Muted")
                    as System.Windows.Media.Brush;

            bool active =
                IsActiveProduct(
                    visual.Product);

            visual.Card.BorderBrush =
                TryFindResource(
                    active
                        ? "Accent"
                        : connected
                            ? "TextPrimary"
                            : "Line")
                    as System.Windows.Media.Brush;
        }

        if (ProductHubStatusText is not null)
        {
            string[] online =
                ProductCatalog.All
                    .Select(product =>
                    {
                        IDeviceLink link =
                            LinkFor(product);

                        return link.IsConnected
                            ? $"{product.Name}: {link.ConnectionName}"
                            : $"{product.Name}: {L("Not connected", "Chưa kết nối")}";
                    })
                    .ToArray();

            ProductHubStatusText.Text =
                string.Join("  ·  ", online);
        }

        if (ProductHubVersionText is not null)
        {
            var version =
                System.Reflection.Assembly
                    .GetExecutingAssembly()
                    .GetName()
                    .Version;

            ProductHubVersionText.Text =
                version is null
                    ? "v--"
                    : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private async Task UpdateProductOverviewAsync()
    {
        foreach (ProductDefinition product in ProductCatalog.All)
        {
            IDeviceLink link =
                LinkFor(product);

            if (!link.IsConnected ||
                !product.SupportsBattery)
            {
                _batteryByProduct[product.Id] = null;
                continue;
            }

            try
            {
                _batteryByProduct[product.Id] =
                    await link.ReadBatteryPercentAsync();
            }
            catch
            {
            }
        }

        UpdateProductHubUi();
    }

    private static readonly Dictionary<string, string> Vi = new()
    {
        ["Wireless MacroPad Control"] = "Điều khiển MacroPad không dây",
        ["Choose a product"] = "Chọn sản phẩm",
        ["Connected · USB"] = "Đã kết nối · USB",
        ["Connected · Bluetooth"] = "Đã kết nối · Bluetooth",
        ["Product Hub"] = "Trung tâm sản phẩm",
        ["Home"] = "Trang chủ",
        ["MEDIA"] = "MEDIA",
        ["Nothing Playing"] = "Không có nhạc đang phát",
        ["SCREENSAVER MEDIA"] = "MEDIA BẢO VỆ MÀN HÌNH",
        ["GIF / Image local"] = "GIF / Ảnh trên máy",
        ["Choose a GIF or image"] = "Chọn GIF hoặc ảnh",
        ["No file selected"] = "Chưa chọn tệp",
        ["Converted to a lightweight loop for RYNOR ONE."] = "Tự chuyển thành vòng lặp nhẹ cho RYNOR ONE.",
        ["Scale"] = "Co giãn",
        ["Fill"] = "Lấp đầy",
        ["Fit"] = "Vừa khung",
        ["Stretch"] = "Kéo giãn",
        ["Tile"] = "Lặp ô",
        ["Center"] = "Căn giữa",
        ["Span"] = "Phủ rộng",
        ["Choose GIF / Image"] = "Chọn GIF / Ảnh",
        ["Send to RYNOR ONE"] = "Gửi tới RYNOR ONE",
        ["Clear"] = "Xóa",
        ["The file stays local. Only reduced animation frames are sent."] = "Tệp vẫn nằm trên máy. Chỉ các frame đã giảm được gửi đi.",
        ["Screensaver after"] = "Bảo vệ màn hình sau",
        ["Sleep after"] = "Ngủ sau",
        ["15 seconds"] = "15 giây",
        ["30 seconds"] = "30 giây",
        ["1 minute"] = "1 phút",
        ["2 minutes"] = "2 phút",
        ["5 minutes"] = "5 phút",
        ["10 minutes"] = "10 phút",
        ["15 minutes"] = "15 phút",
        ["30 minutes"] = "30 phút",
        ["Never"] = "Không bao giờ",
        ["Not connected"] = "Chưa kết nối",
        ["Bluetooth preferred; USB fallback."] = "Ưu tiên Bluetooth; USB dự phòng.",
        ["Auto connect · USB first · Bluetooth fallback"] = "Tự động kết nối · ưu tiên USB · Bluetooth dự phòng",
        ["Connect"] = "Kết nối",
        ["Disconnect"] = "Ngắt kết nối",
        ["Restart keyboard"] = "Khởi động lại bàn phím",
        ["Keyboard DFU"] = "Bàn phím DFU",
        ["RGB"] = "RGB",
        ["RGB Profile"] = "Profile RGB",
        ["Save to profile"] = "Lưu vào profile",
        ["Show now"] = "Bật ngay",
        ["Auto"] = "Tự động",
        ["COLOR"] = "MÀU",
        ["Mode"] = "Chế độ",
        ["Static"] = "Tĩnh",
        ["Dynamic"] = "Động",
        ["Reactive"] = "Phản hồi",
        ["Presets"] = "Mẫu có sẵn",
        ["Solid Color"] = "Màu đơn",
        ["Auto by Layer"] = "Tự động theo Layer",
        ["Rainbow"] = "Cầu vồng",
        ["Purple Ping-Pong"] = "Tím qua lại",
        ["Orange Blink"] = "Cam nhấp nháy",
        ["Reactive Splash"] = "Phản hồi khi bấm",
        ["Switch"] = "Bật / Tắt",
        ["Brightness"] = "Độ sáng",
        ["Effect Speed"] = "Tốc độ hiệu ứng",
        ["Turn LEDs off after"] = "Tắt LED sau",
        ["Deep sleep after"] = "Ngủ sâu sau",
        ["UPDATES"] = "CẬP NHẬT",
        ["One-click updates"] = "Cập nhật một chạm",
        ["Check now"] = "Kiểm tra ngay",
        ["LumiPad app"] = "Ứng dụng LumiPad",
        ["Keyboard firmware"] = "Firmware bàn phím",
        ["Checking…"] = "Đang kiểm tra…",
        ["Checking for updates…"] = "Đang kiểm tra cập nhật…",
        ["Update available"] = "Có bản mới",
        ["Up to date"] = "Đã mới nhất",
        ["Connect keyboard to read firmware version"] = "Kết nối bàn phím để đọc phiên bản firmware",
        ["Connect by USB to update firmware"] = "Cắm USB để cập nhật firmware",
        ["Update firmware"] = "Cập nhật firmware",
        ["Update app"] = "Cập nhật ứng dụng",
        ["Ready"] = "Sẵn sàng",
        ["Deep sleep disconnects Bluetooth and uses very little power. Press a key to reboot and reconnect."] = "Ngủ sâu sẽ ngắt Bluetooth và tiết kiệm điện tối đa. Nhấn phím để khởi động lại và kết nối lại.",
        ["1 hour"] = "1 giờ",
        ["2 hours"] = "2 giờ",
        ["Selected color"] = "Màu đã chọn",
        ["Device Config"] = "Device Config",
        ["Embedded native-usb.studio"] = "Device Config tích hợp",
        ["Reload"] = "Tải lại",
        ["Open in Edge"] = "Mở bằng Edge",
        ["Light mode"] = "Chế độ sáng",
        ["Dark mode"] = "Chế độ tối",
        ["Detecting…"] = "Đang tìm…",
        ["Disconnected."] = "Đã ngắt kết nối.",
        ["Restarting…"] = "Đang khởi động lại…",
        ["DFU / Bootloader"] = "DFU / Bootloader",
        ["Ready to upload"] = "Sẵn sàng tải lên",
        ["Uploading…"] = "Đang tải lên…",
        ["Uploaded & verified"] = "Đã tải lên và xác nhận",
        ["Upload failed"] = "Tải lên thất bại",
        ["Not uploaded"] = "Chưa tải lên",
        ["Prepare failed"] = "Xử lý thất bại",
        ["Light mode"] = "Chế độ sáng",
        ["Dark mode"] = "Chế độ tối",
        ["Settings"] = "Cài đặt",
        ["SETTINGS"] = "CÀI ĐẶT",
        ["Appearance & device"] = "Giao diện & thiết bị",
        ["Language"] = "Ngôn ngữ",
        ["Appearance"] = "Giao diện",
        ["VERSION"] = "PHIÊN BẢN",
        ["Firmware"] = "Firmware",
        ["Connection"] = "Kết nối",
        ["Disconnected"] = "Đã ngắt kết nối",
        ["DISPLAY"] = "MÀN HÌNH",
        ["Panel timing"] = "Thông số màn hình",
        ["DIAGNOSTIC LOG"] = "NHẬT KÝ CHẨN ĐOÁN",
        ["App + firmware events and errors"] = "Sự kiện và lỗi của app + firmware",
        ["View log"] = "Xem log",
        ["Hide log"] = "Ẩn log",
        ["Save log"] = "Tải log",
        ["Copy log"] = "Sao chép log",
        ["Clear log"] = "Xóa log",
        ["SYSTEM"] = "HỆ THỐNG",
        ["Flash usage"] = "Sử dụng Flash",
        ["SRAM usage"] = "Sử dụng SRAM",
        ["PSRAM usage"] = "Sử dụng PSRAM",
        ["RAM usage"] = "Sử dụng RAM",
        ["Sleep keyboard"] = "Ngủ bàn phím",
        ["Wake keyboard"] = "Đánh thức bàn phím",
        ["Auto Profile"] = "Auto Profile",
        ["AUTO PROFILE"] = "AUTO PROFILE",
        ["Link Game/App"] = "Liên kết Game/App",
        ["Watching active application"] = "Theo dõi ứng dụng đang hoạt động",
        ["Enabled"] = "Bật",
        ["Default"] = "Mặc định",
        ["Select Application"] = "Chọn ứng dụng",
        ["RUNNING APPS"] = "ỨNG DỤNG ĐANG CHẠY",
        ["Suggestions"] = "Gợi ý",
        ["Refresh"] = "Làm mới",
        ["Actions"] = "Actions",
        ["ACTION / SCRIPT ENGINE"] = "ACTION / SCRIPT ENGINE",
        ["Scripts"] = "Script",
        ["New"] = "Mới",
        ["Delete"] = "Xóa",
        ["SCRIPT EDITOR"] = "TRÌNH SỬA SCRIPT",
        ["Select or create a script"] = "Chọn hoặc tạo một script",
        ["Name"] = "Tên",
        ["Add Step"] = "Thêm bước",
        ["Remove Step"] = "Xóa bước",
        ["Move Up"] = "Lên",
        ["Move Down"] = "Xuống",
        ["Save"] = "Lưu",
        ["Run Test"] = "Chạy thử",
        ["Key Map"] = "Gán phím",
        ["Ready"] = "Sẵn sàng",
    };

    private string L(string en, string vi) => _language == "vi" ? vi : en;

    private string TranslateUiText(string current)
    {
        if (_language == "vi")
        {
            return Vi.TryGetValue(current, out var vi) ? vi : current;
        }

        foreach (var pair in Vi)
        {
            if (pair.Value == current)
                return pair.Key;
        }

        return current;
    }

    private string LanguageFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "language.txt");

    private void LoadLanguage()
    {
        try
        {
            if (System.IO.File.Exists(LanguageFilePath))
            {
                string value = System.IO.File.ReadAllText(LanguageFilePath).Trim().ToLowerInvariant();
                _language = value == "vi" ? "vi" : "en";
            }
        }
        catch
        {
            _language = "en";
        }

        if (LanguageCombo is not null)
        {
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (string.Equals(item.Tag?.ToString(), _language, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }
    }

    private void SaveLanguage()
    {
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(LanguageFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                System.IO.Directory.CreateDirectory(folder);

            System.IO.File.WriteAllText(LanguageFilePath, _language);
        }
        catch
        {
        }
    }

    private void ApplyLanguage()
    {
        var visited = new HashSet<DependencyObject>();
        TranslateElement(this, visited);
        UpdateProductSpecificText();

        if (_trayIcon?.ContextMenuStrip is not null)
        {
            if (_trayIcon.ContextMenuStrip.Items.Count > 0)
                _trayIcon.ContextMenuStrip.Items[0].Text =
                    L("Open LumiPad", "Mở LumiPad");
            if (_trayIcon.ContextMenuStrip.Items.Count > 1)
                _trayIcon.ContextMenuStrip.Items[1].Text =
                    L("Exit", "Thoát");
        }
    }

    private void TranslateElement(
        DependencyObject node,
        HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is TextBlock textBlock &&
            !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            textBlock.Text = TranslateUiText(textBlock.Text);
        }

        if (node is ContentControl contentControl &&
            contentControl.Content is string content &&
            !string.IsNullOrWhiteSpace(content))
        {
            contentControl.Content = TranslateUiText(content);
        }

        if (node is HeaderedContentControl headered &&
            headered.Header is string header &&
            !string.IsNullOrWhiteSpace(header))
        {
            headered.Header = TranslateUiText(header);
        }

        // Logical tree contains content of tabs that have never been selected,
        // which the visual tree alone does not expose.
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependencyChild)
                TranslateElement(dependencyChild, visited);
        }

        // Visual tree catches generated controls/templates that are not present
        // as direct logical children.
        int visualCount = 0;
        try
        {
            visualCount =
                System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            visualCount = 0;
        }

        for (int i = 0; i < visualCount; i++)
        {
            DependencyObject child =
                System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            TranslateElement(child, visited);
        }
    }

    private void LanguageCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (LanguageCombo?.SelectedItem is not ComboBoxItem item)
            return;

        string next = item.Tag?.ToString() == "vi" ? "vi" : "en";

        if (_language == next && _uiReady)
            return;

        _language = next;

        if (_uiReady)
        {
            SaveLanguage();
            ApplyLanguage();
            Dispatcher.BeginInvoke(new Action(ApplyLanguage));
        }
    }

    private string AppSettingsFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "settings.json");

    private sealed class RgbProfileSetting
    {
        public int Effect { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }

    private static RgbProfileSetting[] CreateDefaultRgbProfiles() =>
    [
        new() { Effect = 0, R = 255, G = 120, B = 0 },
        new() { Effect = 1, R = 180, G = 40, B = 255 },
        new() { Effect = 2, R = 255, G = 90, B = 0 },
        new() { Effect = 3, R = 0, G = 170, B = 255 },
        new() { Effect = 3, R = 80, G = 255, B = 100 },
    ];

    private static PixelRgbColor[] CreateDefaultPixelRgbProfile(
        int profile) =>
        Enumerable.Range(0, 8)
            .Select(key =>
            {
                byte r =
                    (byte)Math.Clamp(
                        255 - key * 16,
                        0,
                        255);

                byte g =
                    (byte)Math.Clamp(
                        96 + key * 18,
                        0,
                        255);

                byte b =
                    (byte)Math.Clamp(
                        profile * 5,
                        0,
                        120);

                return new PixelRgbColor(
                    r,
                    g,
                    b);
            })
            .ToArray();

    private static PixelRgbColor[][] CreateDefaultPixelRgbProfiles() =>
        Enumerable.Range(0, 20)
            .Select(CreateDefaultPixelRgbProfile)
            .ToArray();

    private static PixelProKeyBinding[][] CreateDefaultPixelProfileLayers() =>
        Enumerable.Range(0, 4)
            .Select(layer =>
                Enumerable.Range(0, 8)
                    .Select(key =>
                        layer == 0
                            ? PixelProKeyBinding.Keyboard(
                                (byte)(4 + key))
                            : PixelProKeyBinding.Transparent())
                    .ToArray())
            .ToArray();

    private sealed class AppSettings
    {
        public bool RgbEnabled { get; set; } = true;
        public int RgbBrightness { get; set; } = 25;
        public int RgbSpeed { get; set; } = 50;
        public bool RgbAuto { get; set; }
        public int RgbEffect { get; set; } = 3;
        public byte R { get; set; } = 255;
        public byte G { get; set; } = 120;
        public byte B { get; set; }
        public RgbProfileSetting[]? RgbProfiles { get; set; }
        public int PixelGifMaxFps { get; set; } = PixelProScreensaverMediaService.DefaultGifMaxFps;
        public int PixelGifMaxDurationSeconds { get; set; } = PixelProScreensaverMediaService.DefaultGifDurationSeconds;
        public int PixelImageJpegQuality { get; set; } = PixelProScreensaverMediaService.DefaultImageJpegQuality;
        public ScreensaverScaleMode PixelMediaScaleMode { get; set; } = ScreensaverScaleMode.Fill;
        public PixelRgbColor[][]? PixelRgbProfiles { get; set; }
        public int[]? PixelRgbEffects { get; set; }
        public int PixelRgbSpeed { get; set; } = 50;
        public int ScreensaverDelaySeconds { get; set; } = 60;
        public int SleepDelaySeconds { get; set; } = 120;
        public int RgbIdleDelaySeconds { get; set; } = 60;
        public int DeepSleepDelaySeconds { get; set; } = 0;
        public string? ScreensaverMediaPath { get; set; }
        public string? PixelScreensaverMediaPath { get; set; }
        public ScreensaverScaleMode ScreensaverScaleMode { get; set; } = ScreensaverScaleMode.Fill;
        public bool PcMonitorEnabled { get; set; } = true;
        public int PcMonitorIntervalMs { get; set; } = 1000;
        public string PcMonitorGpuId { get; set; } = "auto";
        public string PcMonitorConfigName { get; set; } = "MY PC";
        public int[]? PcMonitorMetricSlots { get; set; }
        public string ScreensaverSource { get; set; } = "Media";
    }

    private void LoadAppSettings()
    {
        try
        {
            if (!System.IO.File.Exists(AppSettingsFilePath))
                return;

            var settings = JsonSerializer.Deserialize<AppSettings>(
                System.IO.File.ReadAllText(AppSettingsFilePath));

            if (settings is null)
                return;

            _rgbEnabled = settings.RgbEnabled;
            _rgbBrightness = Math.Clamp(settings.RgbBrightness, 5, 50);
            _rgbSpeed = Math.Clamp(settings.RgbSpeed, 10, 100);
            _rgbAuto = settings.RgbAuto;
            _rgbEffect = Math.Clamp(settings.RgbEffect, 0, 4);
            _r = settings.R;
            _g = settings.G;
            _b = settings.B;

            if (settings.RgbProfiles is { Length: >= 5 })
            {
                _rgbProfiles = settings.RgbProfiles
                    .Take(5)
                    .Select(p => new RgbProfileSetting
                    {
                        Effect = Math.Clamp(p.Effect, 0, 4),
                        R = p.R,
                        G = p.G,
                        B = p.B
                    })
                    .ToArray();
            }

            _pixelGifMaxFps =
                settings.PixelGifMaxFps is 20 or 25 or 30 or 40 or 50 or 60
                    ? settings.PixelGifMaxFps
                    : PixelProScreensaverMediaService.DefaultGifMaxFps;

            // PIXEL PRO GIF length is intentionally fixed at 10 seconds.
            // Ignore older saved 5/15/20/30 second values from previous builds.
            _pixelGifMaxDurationSeconds =
                PixelProScreensaverMediaService.DefaultGifDurationSeconds;

            _pixelImageJpegQuality =
                Math.Clamp(
                    settings.PixelImageJpegQuality,
                    90,
                    100);

            _pixelMediaScaleMode =
                PixelProScreensaverMediaService.NormalizePixelScale(
                    settings.PixelMediaScaleMode);

            if (settings.PixelRgbProfiles is { Length: >= 20 } savedPixelRgb &&
                savedPixelRgb.Take(20).All(profile => profile is { Length: >= 8 }))
            {
                _pixelRgbProfiles =
                    savedPixelRgb
                        .Take(20)
                        .Select(profile =>
                            profile
                                .Take(8)
                                .ToArray())
                        .ToArray();
            }

            if (settings.PixelRgbEffects is { Length: >= 20 } savedEffects)
            {
                _pixelRgbEffects =
                    savedEffects
                        .Take(20)
                        .Select(effect =>
                            effect is >= 0 and <= 9
                                ? effect
                                : 3)
                        .ToArray();
            }

            _pixelRgbSpeed =
                Math.Clamp(
                    settings.PixelRgbSpeed,
                    10,
                    100);

            _screensaverDelaySeconds = Math.Max(0, settings.ScreensaverDelaySeconds);
            _sleepDelaySeconds = Math.Max(0, settings.SleepDelaySeconds);
            _rgbIdleDelaySeconds = Math.Max(0, settings.RgbIdleDelaySeconds);
            _deepSleepDelaySeconds = Math.Max(0, settings.DeepSleepDelaySeconds);
            _rynorScreensaverMediaPath =
                settings.ScreensaverMediaPath;

            // One-time migration for builds that shared a single media path.
            // Keeping the legacy path available to both products lets PIXEL
            // reprocess it with the correct 480×320 service after selection.
            _pixelScreensaverMediaPath =
                string.IsNullOrWhiteSpace(settings.PixelScreensaverMediaPath)
                    ? settings.ScreensaverMediaPath
                    : settings.PixelScreensaverMediaPath;

            _screensaverMediaPath =
                _rynorScreensaverMediaPath;

            _screensaverScaleMode = settings.ScreensaverScaleMode;
            _screensaverSource =
                string.Equals(settings.ScreensaverSource, "PcMonitor", StringComparison.Ordinal)
                    ? "PcMonitor"
                    : "Media";
            _pcMonitorGpuId =
                string.IsNullOrWhiteSpace(settings.PcMonitorGpuId)
                    ? "auto"
                    : settings.PcMonitorGpuId;
            _pcMonitorConfigName =
                string.IsNullOrWhiteSpace(settings.PcMonitorConfigName)
                    ? "MY PC"
                    : settings.PcMonitorConfigName.Trim();

            if (settings.PcMonitorMetricSlots is { Length: 6 } savedSlots &&
                savedSlots.All(id => id is >= 0 and <= 11))
            {
                _pcMonitorMetricSlots = savedSlots.ToArray();
            }

            _pcMonitorEnabled = settings.PcMonitorEnabled;
            _pcMonitorIntervalMs =
                settings.PcMonitorIntervalMs is 500 or 1000 or 2000
                    ? settings.PcMonitorIntervalMs
                    : 1000;
            _pcMonitorTimer.Interval =
                TimeSpan.FromMilliseconds(_pcMonitorIntervalMs);
        }
        catch
        {
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(AppSettingsFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                System.IO.Directory.CreateDirectory(folder);

            var settings = new AppSettings
            {
                RgbEnabled = LedEnabled?.IsChecked ?? _rgbEnabled,
                RgbBrightness = BrightnessSlider is null ? _rgbBrightness : (int)Math.Round(BrightnessSlider.Value),
                RgbSpeed = RgbSpeedSlider is null ? _rgbSpeed : (int)Math.Round(RgbSpeedSlider.Value),
                RgbAuto = _rgbAuto,
                RgbEffect = _rgbEffect,
                R = _r,
                G = _g,
                B = _b,
                RgbProfiles = _rgbProfiles
                    .Select(p => new RgbProfileSetting
                    {
                        Effect = p.Effect,
                        R = p.R,
                        G = p.G,
                        B = p.B
                    })
                    .ToArray(),
                PixelGifMaxFps = _pixelGifMaxFps,
                PixelGifMaxDurationSeconds = _pixelGifMaxDurationSeconds,
                PixelImageJpegQuality = _pixelImageJpegQuality,
                PixelMediaScaleMode = _pixelMediaScaleMode,
                PixelRgbProfiles = _pixelRgbProfiles
                    .Select(profile => profile.ToArray())
                    .ToArray(),
                PixelRgbEffects = _pixelRgbEffects.ToArray(),
                PixelRgbSpeed = _pixelRgbSpeed,
                ScreensaverDelaySeconds = _screensaverDelaySeconds,
                SleepDelaySeconds = _sleepDelaySeconds,
                RgbIdleDelaySeconds = _rgbIdleDelaySeconds,
                DeepSleepDelaySeconds = _deepSleepDelaySeconds,
                ScreensaverMediaPath = _rynorScreensaverMediaPath,
                PixelScreensaverMediaPath = _pixelScreensaverMediaPath,
                // Preserve RYNOR's scale even while PIXEL PRO is active.
                ScreensaverScaleMode = _screensaverScaleMode,
                PcMonitorEnabled = _pcMonitorEnabled,
                PcMonitorIntervalMs = _pcMonitorIntervalMs,
                PcMonitorGpuId = _pcMonitorGpuId,
                PcMonitorConfigName = _pcMonitorConfigName,
                PcMonitorMetricSlots = _pcMonitorMetricSlots.ToArray(),
                ScreensaverSource = _screensaverSource
            };

            System.IO.File.WriteAllText(
                AppSettingsFilePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void ApplyStoredControlValues()
    {
        if (LedEnabled is not null)
            LedEnabled.IsChecked = _rgbEnabled;

        if (BrightnessSlider is not null)
            BrightnessSlider.Value = _rgbBrightness;

        if (RgbSpeedSlider is not null)
            RgbSpeedSlider.Value = _rgbSpeed;

        SelectComboTag(RgbProfileCombo, _rgbProfileIndex.ToString());
        SelectComboTag(ScreensaverDelayCombo, _screensaverDelaySeconds.ToString());
        SelectComboTag(SleepDelayCombo, _sleepDelaySeconds.ToString());
        SelectComboTag(RgbIdleDelayCombo, _rgbIdleDelaySeconds.ToString());
        SelectComboTag(DeepSleepDelayCombo, _deepSleepDelaySeconds.ToString());
        SelectComboTag(ScreensaverScaleCombo, _screensaverScaleMode.ToString());
        if (PixelGifFpsCombo is not null)
            SelectComboTag(PixelGifFpsCombo, _pixelGifMaxFps.ToString());
        if (PixelGifDurationCombo is not null)
            SelectComboTag(PixelGifDurationCombo, _pixelGifMaxDurationSeconds.ToString());
        if (PixelMediaScaleCombo is not null)
            SelectComboTag(PixelMediaScaleCombo, _pixelMediaScaleMode.ToString());
        if (ScreensaverSourceCombo is not null)
            SelectComboTag(ScreensaverSourceCombo, _screensaverSource);

        if (PcMonitorEnabledCheckBox is not null)
            PcMonitorEnabledCheckBox.IsChecked = _pcMonitorEnabled;
        if (PcMonitorIntervalCombo is not null)
            SelectComboTag(PcMonitorIntervalCombo, _pcMonitorIntervalMs.ToString());
        if (PcMonitorConfigNameText is not null)
            PcMonitorConfigNameText.Text = _pcMonitorConfigName;

        ApplyPcMetricSelections();

        UpdateScreensaverSourceUi();
        UpdatePcMonitorConfigSummary(_lastPcMonitorSnapshot);
        UpdateRgbReadout();
    }

    private static void SelectComboTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        // Keep the original shared/Rynor behavior. PIXEL-specific controls
        // declare SelectedValuePath=Tag and do not need a global workaround.
        combo.SelectedValue = tag;
    }

    private void InitializeTrayIcon()
    {
        _appIcon = CreateLogoIcon();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Lumi Macropad",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Lumi Macropad", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        Icon = Imaging.CreateBitmapSourceFromHIcon(
            _appIcon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(64, 64));
    }

    private static Drawing.Icon CreateLogoIcon()
    {
        using var bitmap = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Black);

            using var border = new Drawing.Pen(Drawing.Color.FromArgb(70, 255, 255, 255), 1);
            g.DrawRectangle(border, 2, 2, 59, 59);

            using var font = new Drawing.Font(
                "Arial",
                20,
                Drawing.FontStyle.Bold,
                Drawing.GraphicsUnit.Pixel);
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(232, 229, 225));

            const string text = "L3D";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush,
                (64 - size.Width) / 2f,
                (64 - size.Height) / 2f - 1f);
        }

        IntPtr handle = bitmap.GetHicon();
        using var temp = Drawing.Icon.FromHandle(handle);
        var icon = (Drawing.Icon)temp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit)
            return;

        e.Cancel = true;
        Hide();

        if (!_trayTipShown && _trayIcon is not null)
        {
            _trayTipShown = true;
            _trayIcon.BalloonTipTitle =
                L("Lumi Macropad is still running", "Lumi Macropad vẫn đang chạy");
            _trayIcon.BalloonTipText =
                L("Now Playing and Bluetooth control continue in the system tray.",
                  "Now Playing và điều khiển Bluetooth vẫn tiếp tục chạy ở khay hệ thống.");
            _trayIcon.ShowBalloonTip(1800);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _allowExit = true;

        _screensaverPreviewTimer.Stop();
        _screensaverPreviewClock.Stop();
        _pixelRgbPreviewTimer.Stop();
        _pixelRgbPreviewClock.Stop();
        _autoProfileTimer.Stop();
        _runningAppsTimer.Stop();
        _actionEventTimer.Stop();
        _productStatusTimer.Stop();
        _pcMonitorTimer.Stop();
        _reconnectCts.Cancel();
        _reconnectCts.Dispose();
        _nowPlaying.Dispose();
        _pcMonitorService.Dispose();

        foreach (IDeviceLink link in _deviceLinks.Values.Distinct())
        {
            try
            {
                link.Dispose();
            }
            catch
            {
            }
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _appIcon?.Dispose();
        _appIcon = null;

        System.Windows.Application.Current.Shutdown();
    }

    private string ThemeFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "theme.txt");

    private void LoadTheme()
    {
        try
        {
            _lightTheme =
                System.IO.File.Exists(ThemeFilePath) &&
                string.Equals(
                    System.IO.File.ReadAllText(ThemeFilePath).Trim(),
                    "light",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            _lightTheme = false;
        }

        ApplyTheme();
    }

    private void SaveTheme()
    {
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(ThemeFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                System.IO.Directory.CreateDirectory(folder);

            System.IO.File.WriteAllText(
                ThemeFilePath,
                _lightTheme ? "light" : "dark");
        }
        catch
        {
        }
    }

    private static void SetResourceColor(string key, string hex)
    {
        if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is MediaColor color)
        {
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }

    private static void SetSystemResourceColor(
        System.Windows.ResourceKey key,
        string hex)
    {
        if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is MediaColor color)
        {
            System.Windows.Application.Current.Resources[key] =
                new SolidColorBrush(color);
        }
    }

    private void ApplyTheme()
    {
        if (_lightTheme)
        {
            SetResourceColor("Bg", "#F2F2F7");
            SetResourceColor("Card", "#FFFFFF");
            SetResourceColor("Card2", "#F8F8FA");
            SetResourceColor("ControlBg", "#F2F2F5");
            SetResourceColor("TextPrimary", "#171719");
            SetResourceColor("Muted", "#6E6E73");
            SetResourceColor("Line", "#D5D5DA");
            SetResourceColor("Accent", "#FF7A00");
            SetResourceColor("Selection", "#F5E8DE");

            SetSystemResourceColor(System.Windows.SystemColors.WindowBrushKey, "#FFFFFF");
            SetSystemResourceColor(System.Windows.SystemColors.ControlBrushKey, "#F2F2F5");
            SetSystemResourceColor(System.Windows.SystemColors.WindowTextBrushKey, "#171719");
            SetSystemResourceColor(System.Windows.SystemColors.ControlTextBrushKey, "#171719");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightBrushKey, "#F5E8DE");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightTextBrushKey, "#171719");
            SetSystemResourceColor(System.Windows.SystemColors.InactiveSelectionHighlightBrushKey, "#ECECEF");

            ThemeButton.Content = "Dark mode";
        }
        else
        {
            SetResourceColor("Bg", "#0D0D0F");
            SetResourceColor("Card", "#16171A");
            SetResourceColor("Card2", "#1B1C20");
            SetResourceColor("ControlBg", "#202126");
            SetResourceColor("TextPrimary", "#ECECF0");
            SetResourceColor("Muted", "#9899A1");
            SetResourceColor("Line", "#303138");
            SetResourceColor("Accent", "#FF7A00");
            SetResourceColor("Selection", "#2B2521");

            SetSystemResourceColor(System.Windows.SystemColors.WindowBrushKey, "#1B1C20");
            SetSystemResourceColor(System.Windows.SystemColors.ControlBrushKey, "#202126");
            SetSystemResourceColor(System.Windows.SystemColors.WindowTextBrushKey, "#ECECF0");
            SetSystemResourceColor(System.Windows.SystemColors.ControlTextBrushKey, "#ECECF0");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightBrushKey, "#2B2521");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightTextBrushKey, "#F4F4F6");
            SetSystemResourceColor(System.Windows.SystemColors.InactiveSelectionHighlightBrushKey, "#252529");

            ThemeButton.Content = "Light mode";
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _lightTheme = !_lightTheme;
        ApplyTheme();

        // Auto Profile rows are created in code and hold concrete resource
        // brushes. Rebuild them after a theme switch so light mode does not
        // leave stale dark cards/icons behind.
        RefreshAutoProfileMappingsUi();
        RefreshRunningAppsUi();

        ApplyLanguage();
        SaveTheme();
    }

    private void ApplyNowPlaying(NowPlayingData data)
    {
        _currentNowPlaying = data;
        _syncingMediaUi = true;

        try
        {
            SourceText.Text = string.IsNullOrWhiteSpace(data.SourceName)
                ? "MEDIA SESSION"
                : data.SourceName;
            TitleText.Text = data.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(data.Artist)
                ? "Unknown Artist"
                : data.Artist;

            var durationMs = Math.Max(0.001, data.Duration.TotalMilliseconds);
            double progress = Math.Clamp(
                data.Position.TotalMilliseconds / durationMs,
                0,
                1);

            if (!_mediaSeekDragging)
                TrackSlider.Value = progress;

            TrackSlider.IsEnabled =
                data.CanSeek && data.Duration > TimeSpan.Zero;

            ElapsedText.Text = FormatTime(data.Position);
            TimeSpan remaining = data.Duration - data.Position;
            DurationText.Text = data.Duration > TimeSpan.Zero
                ? $"-{FormatTime(remaining)}"
                : "0:00";

            PlayButton.Content = data.IsPlaying ? "❚❚" : "▶";
            PlayButton.ToolTip = data.IsPlaying
                ? L("Pause", "Tạm dừng")
                : L("Play", "Phát");

            PrevButton.IsEnabled = data.CanPrevious;
            PlayButton.IsEnabled = data.CanPlayPause;
            NextButton.IsEnabled = data.CanNext;
            ShuffleButton.IsEnabled = data.CanShuffle;
            RepeatButton.IsEnabled = data.CanRepeat;

            RepeatButton.Content = data.RepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track => "↻1",
                MediaPlaybackAutoRepeatMode.List => "↻∞",
                _ => "↻"
            };

            RepeatButton.ToolTip = data.RepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track =>
                    L("Repeat one · click for repeat all", "Lặp 1 bài · bấm để lặp toàn bộ"),
                MediaPlaybackAutoRepeatMode.List =>
                    L("Repeat all · click to turn off", "Lặp toàn bộ · bấm để tắt"),
                _ =>
                    L("Repeat off · click for repeat one", "Đang tắt lặp · bấm để lặp 1 bài")
            };

            ShuffleButton.ToolTip = data.IsShuffleActive
                ? L("Shuffle on", "Xáo trộn đang bật")
                : L("Shuffle off", "Xáo trộn đang tắt");

            SetMediaModeButton(
                ShuffleButton,
                data.CanShuffle && data.IsShuffleActive);
            SetMediaModeButton(
                RepeatButton,
                data.CanRepeat &&
                data.RepeatMode != MediaPlaybackAutoRepeatMode.None);

            if (data.ArtworkRgb332 is { Length: 5776 } artwork)
            {
                AlbumArtImage.Source = CreateArtworkBitmap(artwork);
                AlbumArtImage.Visibility = Visibility.Visible;
                AlbumArtFallback.Visibility = Visibility.Collapsed;
            }
            else
            {
                AlbumArtImage.Source = null;
                AlbumArtImage.Visibility = Visibility.Collapsed;
                AlbumArtFallback.Visibility = Visibility.Visible;
            }

            SyncSystemVolumeUi();
        }
        finally
        {
            _syncingMediaUi = false;
        }

        foreach (IDeviceLink link in _deviceLinks.Values)
        {
            if (!link.IsConnected)
                continue;

            try
            {
                link.SendNowPlaying(data);
            }
            catch (Exception ex)
            {
                AddLog(
                    "WARN",
                    "MEDIA",
                    $"Now Playing send failed on {link.ConnectionName}: {ex.Message}");
            }
        }
    }

    private void ClearNowPlaying()
    {
        _currentNowPlaying = null;
        _mediaSeekDragging = false;
        _syncingMediaUi = true;

        try
        {
            SourceText.Text = "MEDIA SESSION";
            TitleText.Text = "Nothing Playing";
            ArtistText.Text = "LumiPad";
            AlbumArtImage.Source = null;
            AlbumArtImage.Visibility = Visibility.Collapsed;
            AlbumArtFallback.Visibility = Visibility.Visible;

            TrackSlider.Value = 0;
            TrackSlider.IsEnabled = false;
            ElapsedText.Text = "0:00";
            DurationText.Text = "-0:00";

            PrevButton.IsEnabled = false;
            PlayButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            ShuffleButton.IsEnabled = false;
            RepeatButton.IsEnabled = false;

            PlayButton.Content = "▶";
            PlayButton.ToolTip = L("Play", "Phát");
            ShuffleButton.Content = "🔀";
            RepeatButton.Content = "↻";
            SetMediaModeButton(ShuffleButton, false);
            SetMediaModeButton(RepeatButton, false);
            SyncSystemVolumeUi(force: true);
        }
        finally
        {
            _syncingMediaUi = false;
        }

        foreach (IDeviceLink link in _deviceLinks.Values)
        {
            if (!link.IsConnected)
                continue;

            try
            {
                link.ClearNowPlaying();
            }
            catch
            {
            }
        }
    }

    private void SetMediaModeButton(
        System.Windows.Controls.Button button,
        bool active)
    {
        button.Background =
            TryFindResource(active ? "Selection" : "ControlBg")
                as System.Windows.Media.Brush;
        button.BorderBrush =
            TryFindResource(active ? "Accent" : "Line")
                as System.Windows.Media.Brush;
    }

    private void SyncSystemVolumeUi(bool force = false)
    {
        if (!force &&
            DateTimeOffset.UtcNow - _lastVolumeUiSync <
                TimeSpan.FromMilliseconds(650))
        {
            return;
        }

        _lastVolumeUiSync = DateTimeOffset.UtcNow;

        if (!SystemVolumeService.TryGetState(out SystemVolumeState state))
            return;

        VolumeSlider.Value = state.Percent;
        VolumeText.Text = $"{Math.Round(state.Percent):0}%";
        _volumeMuted = state.IsMuted;
        MuteButton.Content =
            state.IsMuted || state.Percent <= 0.5 ? "🔇" : "🔊";
        MuteButton.ToolTip = state.IsMuted
            ? L("Unmute", "Bật tiếng")
            : L("Mute", "Tắt tiếng");
    }

    private static BitmapSource CreateArtworkBitmap(byte[] rgb332) =>
        CreateRgb332Bitmap(rgb332, 76, 76);

    private static BitmapSource CreateRgb332InterpolatedBitmap(
        byte[] first,
        byte[] second,
        double blend,
        int width,
        int height)
    {
        if (first.Length != width * height ||
            second.Length != width * height)
        {
            throw new ArgumentException(
                "RGB332 buffers do not match dimensions.");
        }

        blend = Math.Clamp(blend, 0.0, 1.0);
        int stride = width * 4;
        byte[] bgra = new byte[stride * height];

        for (int i = 0; i < width * height; i++)
        {
            byte a = first[i];
            byte b = second[i];

            int ar = (((a >> 5) & 0x07) * 255) / 7;
            int ag = (((a >> 2) & 0x07) * 255) / 7;
            int ab = ((a & 0x03) * 255) / 3;

            int br = (((b >> 5) & 0x07) * 255) / 7;
            int bg = (((b >> 2) & 0x07) * 255) / 7;
            int bb = ((b & 0x03) * 255) / 3;

            byte r = (byte)Math.Round(ar + (br - ar) * blend);
            byte g = (byte)Math.Round(ag + (bg - ag) * blend);
            byte bl = (byte)Math.Round(ab + (bb - ab) * blend);

            int p = i * 4;
            bgra[p] = bl;
            bgra[p + 1] = g;
            bgra[p + 2] = r;
            bgra[p + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            stride);

        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateRgb565Bitmap(
        byte[] rgb565,
        int width,
        int height)
    {
        if (rgb565.Length != width * height * 2)
            throw new ArgumentException(
                "RGB565 buffer size does not match dimensions.");

        int stride = width * 4;
        byte[] bgra = new byte[stride * height];

        for (int i = 0; i < width * height; i++)
        {
            ushort v = (ushort)(
                rgb565[i * 2] |
                (rgb565[i * 2 + 1] << 8));

            byte r = (byte)((((v >> 11) & 0x1F) * 255) / 31);
            byte g = (byte)((((v >> 5) & 0x3F) * 255) / 63);
            byte b = (byte)(((v & 0x1F) * 255) / 31);

            int p = i * 4;
            bgra[p] = b;
            bgra[p + 1] = g;
            bgra[p + 2] = r;
            bgra[p + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            stride);

        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateRgb332Bitmap(
        byte[] rgb332,
        int width,
        int height)
    {
        if (rgb332.Length != width * height)
            throw new ArgumentException("RGB332 buffer size does not match dimensions.");

        int stride = width * 4;
        byte[] bgra = new byte[stride * height];

        for (int i = 0; i < width * height; i++)
        {
            byte v = rgb332[i];
            byte r = (byte)((((v >> 5) & 0x07) * 255) / 7);
            byte g = (byte)((((v >> 2) & 0x07) * 255) / 7);
            byte b = (byte)(((v & 0x03) * 255) / 3);

            int p = i * 4;
            bgra[p] = b;
            bgra[p + 1] = g;
            bgra[p + 2] = r;
            bgra[p + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            stride);

        bitmap.Freeze();
        return bitmap;
    }


    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private void UpdateTransportIndicators()
    {
        if (HeaderUsbPath is null ||
            HeaderUsbDot is null ||
            HeaderBluetoothPath is null)
        {
            return;
        }

        var active = new SolidColorBrush(
            MediaColor.FromRgb(48, 209, 88));
        var inactive =
            TryFindResource("Muted") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(154, 154, 160));
        var normalBorder =
            TryFindResource("Line") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(58, 58, 60));

        bool usb = _serial.IsUsbConnected;
        bool bluetooth =
            !_serial.IsUsbConnected &&
            _serial.IsBluetoothConnected;

        HeaderUsbPath.Stroke = usb ? active : inactive;
        HeaderUsbDot.Fill = usb ? active : inactive;
        HeaderBluetoothPath.Stroke =
            bluetooth ? active : inactive;

        if (ConnectUsbButton is not null)
            ConnectUsbButton.BorderBrush =
                usb ? active : normalBorder;

        if (ConnectBluetoothButton is not null)
            ConnectBluetoothButton.BorderBrush =
                bluetooth ? active : normalBorder;

        UpdateProductHubUi();
    }

    private void UpdateSleepButtonUi()
    {
        if (SleepKeyboardButton is null ||
            SleepKeyboardIcon is null)
        {
            return;
        }

        var active =
            TryFindResource("Accent") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(255, 122, 0));
        var normal =
            TryFindResource("ControlBg") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(39, 39, 42));
        var text =
            TryFindResource("TextPrimary") as System.Windows.Media.Brush ??
            System.Windows.Media.Brushes.White;

        SleepKeyboardButton.Background =
            _keyboardSleeping ? active : normal;
        SleepKeyboardIcon.Foreground = text;
        SleepKeyboardButton.ToolTip =
            _keyboardSleeping
                ? L("Wake keyboard", "Đánh thức bàn phím")
                : L("Sleep keyboard", "Ngủ bàn phím");
    }

    private void SetDeviceControlsEnabled(bool enabled)
    {
        bool connected = enabled && _serial.IsConnected;
        if (RgbDevicePanel is not null)
            RgbDevicePanel.IsEnabled = connected;

        if (DeviceTimingPanel is not null)
            DeviceTimingPanel.IsEnabled = true;
        if (ScreensaverDelayCombo is not null)
            ScreensaverDelayCombo.IsEnabled = true;
        if (SleepDelayCombo is not null)
            SleepDelayCombo.IsEnabled = true;

        if (SendScreensaverButton is not null)
        {
            SendScreensaverButton.IsEnabled =
                connected &&
                _screensaverAnimation is not null &&
                !string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal);
        }

        if (ShowScreensaverNowButton is not null)
            ShowScreensaverNowButton.IsEnabled = connected;

        if (DisconnectButton is not null)
            DisconnectButton.IsEnabled = connected;

        if (RestartKeyboardButton is not null)
            RestartKeyboardButton.IsEnabled = connected;

        if (KeyboardDfuButton is not null)
            KeyboardDfuButton.IsEnabled = connected;

        if (FirmwareUpdateButton is not null)
        {
            bool pixelRecovery =
                string.Equals(
                    _activeProduct.Id,
                    ProductCatalog.PixelPro.Id,
                    StringComparison.OrdinalIgnoreCase);

            FirmwareUpdateButton.IsEnabled =
                !_updateBusy &&
                _firmwareUpdateAvailable &&
                (pixelRecovery ||
                 (connected && _serial.IsUsbConnected));
        }

        if (AppUpdateButton is not null)
            AppUpdateButton.IsEnabled =
                !_updateBusy && _appUpdateAvailable;

        if (CheckUpdatesButton is not null)
            CheckUpdatesButton.IsEnabled =
                !_updateBusy && !_checkingUpdates;

        if (SleepKeyboardButton is not null)
            SleepKeyboardButton.IsEnabled = connected;

        if (PixelKeymapSaveButton is not null)
        {
            PixelKeymapSaveButton.IsEnabled =
                connected &&
                _activeProduct.Driver == DeviceDriverKind.PixelProCdc;
        }

        UpdateSettingsInfo();
        UpdateTransportIndicators();
        UpdateSleepButtonUi();
        UpdateScreensaverSourceUi();
    }

    private void UpdateSettingsInfo()
    {
        if (AppVersionText is not null)
        {
            var version = System.Reflection.Assembly
                .GetExecutingAssembly().GetName().Version;
            AppVersionText.Text = version is null
                ? "--"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        if (FirmwareVersionText is not null)
        {
            if (string.IsNullOrWhiteSpace(_serial.FirmwareHello))
            {
                FirmwareVersionText.Text = "--";
            }
            else
            {
                string legacy =
                    _serial.ProtocolVersion is > 0 and < 3
                        ? L(" · Legacy compatible", " · Tương thích firmware cũ")
                        : "";

                FirmwareVersionText.Text =
                    $"Protocol v{Math.Max(0, _serial.ProtocolVersion)}{legacy}";
            }
        }

        if (ConnectionInfoText is not null)
        {
            ConnectionInfoText.Text = _serial.IsConnected
                ? _serial.ConnectionName
                : L("Disconnected", "Đã ngắt kết nối");
        }
    }

    private void AddLog(string level, string source, string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{source}] {message}";
        _logLines.Add(line);

        const int maxLines = 1500;
        if (_logLines.Count > maxLines)
            _logLines.RemoveRange(0, _logLines.Count - maxLines);
    }

    private async Task PollFirmwareDiagnosticsAsync()
    {
        if (!_serial.IsConnected)
            return;

        if (!_serial.SupportsDiagnostics)
            return;

        // Keep background diagnostics lightweight so USB/BLE control and
        // Now Playing remain responsive. The full queue is still drained over
        // subsequent ticks.
        for (int i = 0; i < 2; i++)
        {
            var entry = await _serial.ReadFirmwareLogAsync(_firmwareLogSeq);
            if (entry is null)
                break;

            _firmwareLogSeq = entry.Value.Seq;
            AddLog(
                entry.Value.Level == "E" ? "ERROR" :
                entry.Value.Level == "W" ? "WARN" : "INFO",
                "FW",
                $"#{entry.Value.Seq} {entry.Value.Message}");
        }

        UpdateSettingsInfo();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
        AddLog("INFO", "APP", "Log cleared");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _logLines));
            AddLog("INFO", "APP", "Log copied to clipboard");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", "APP", $"Copy log failed: {ex.Message}");
        }
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        var window = new DiagnosticLogWindow(
            string.Join(Environment.NewLine, _logLines),
            L("App + firmware events and errors",
              "Sự kiện và lỗi của app + firmware"))
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("Save LumiPad diagnostic log", "Lưu nhật ký chẩn đoán LumiPad"),
                Filter = "Text log|*.txt|All files|*.*",
                FileName = $"LumiPad-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                AddExtension = true,
                DefaultExt = ".txt"
            };

            if (dialog.ShowDialog() != true)
                return;

            System.IO.File.WriteAllText(
                dialog.FileName,
                string.Join(Environment.NewLine, _logLines));

            AddLog("INFO", "APP", $"Log saved: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", "APP", $"Save log failed: {ex.Message}");
        }
    }

    private async Task UpdateMemoryUsageAsync()
    {
        bool pixel = IsPixelProActive;

        void ResetActiveMemoryText()
        {
            if (pixel)
            {
                if (FlashUsageText is not null)
                    FlashUsageText.Text = "FLASH --";
                if (SramUsageText is not null)
                    SramUsageText.Text = "SRAM --";
                if (PsramUsageText is not null)
                    PsramUsageText.Text = "PSRAM --";
            }
            else
            {
                if (RynorFlashUsageText is not null)
                    RynorFlashUsageText.Text = "FLASH --";
                if (RynorRamUsageText is not null)
                    RynorRamUsageText.Text = "RAM --";
            }
        }

        if (!_serial.IsConnected)
        {
            ResetActiveMemoryText();
            return;
        }

        DeviceMemoryUsage? usage =
            await _serial.ReadMemoryUsageAsync();

        if (usage is null)
        {
            ResetActiveMemoryText();
            return;
        }

        static string PercentText(
            string label,
            long used,
            long total,
            bool notAvailableWhenZero = false)
        {
            if (total <= 0)
            {
                return notAvailableWhenZero
                    ? $"{label} N/A"
                    : $"{label} --";
            }

            double pct =
                Math.Clamp(
                    used * 100.0 / total,
                    0.0,
                    100.0);

            return $"{label} {pct:0.0}%";
        }

        if (pixel)
        {
            if (FlashUsageText is not null)
            {
                double flashPct =
                    usage.Value.FlashTotal > 0
                        ? Math.Clamp(
                            usage.Value.FlashUsed * 100.0 /
                            usage.Value.FlashTotal,
                            0.0,
                            100.0)
                        : 0.0;

                FlashUsageText.Text =
                    usage.Value.FlashTotal > 0
                        ? $"FLASH {flashPct:0.0}% · " +
                          $"{usage.Value.FlashUsed / 1048576.0:0.00}/" +
                          $"{usage.Value.FlashTotal / 1048576.0:0.00} MB"
                        : "FLASH --";
            }

            if (SramUsageText is not null)
            {
                SramUsageText.Text =
                    PercentText(
                        "SRAM",
                        usage.Value.SramUsed,
                        usage.Value.SramTotal);
            }

            if (PsramUsageText is not null)
            {
                PsramUsageText.Text =
                    PercentText(
                        "PSRAM",
                        usage.Value.PsramUsed,
                        usage.Value.PsramTotal,
                        notAvailableWhenZero: true);
            }
        }
        else
        {
            if (RynorFlashUsageText is not null)
            {
                RynorFlashUsageText.Text =
                    PercentText(
                        "FLASH",
                        usage.Value.FlashUsed,
                        usage.Value.FlashTotal);
            }

            if (RynorRamUsageText is not null)
            {
                RynorRamUsageText.Text =
                    PercentText(
                        "RAM",
                        usage.Value.SramUsed,
                        usage.Value.SramTotal);
            }
        }
    }

    private async Task UpdatePanelInfoAsync()
    {
        bool pixel = IsPixelProActive;

        string fallback =
            pixel
                ? "ILI9486 · 480×320 landscape · i8080 8-bit · refresh cap 60 Hz · GIF ≤60 FPS"
                : "ST7789 ≈60 Hz default · SPI 32 MHz · GIF ≤25 FPS";

        void SetActivePanelText(string value)
        {
            if (pixel)
            {
                if (PanelInfoText is not null)
                    PanelInfoText.Text = value;
            }
            else
            {
                if (RynorPanelInfoText is not null)
                    RynorPanelInfoText.Text = value;
            }
        }

        if (!_serial.IsConnected)
        {
            SetActivePanelText(fallback);
            return;
        }

        var info =
            await _serial.ReadPanelInfoAsync();

        if (info is null)
        {
            SetActivePanelText(fallback);
            return;
        }

        if (pixel)
        {
            SetActivePanelText(
                $"{info.Value.Panel} · 480×320 landscape · i8080 8-bit · " +
                $"refresh cap {info.Value.RefreshHz} Hz · GIF ≤{info.Value.GifMaxFps} FPS");
            return;
        }

        double spiMhz =
            info.Value.SpiHz / 1_000_000.0;

        SetActivePanelText(
            $"{info.Value.Panel} ≈{info.Value.RefreshHz} Hz default · " +
            $"SPI {spiMhz:0.#} MHz · GIF ≤{info.Value.GifMaxFps} FPS");
    }

    private async void ConnectUsbButton_Click(object sender, RoutedEventArgs e)
    {
        _connectionPreference = "usb";
        _autoReconnectEnabled = true;
        await DetectAsync();
    }

    private async void ConnectBluetoothButton_Click(object sender, RoutedEventArgs e)
    {
        _connectionPreference = "bluetooth";
        _autoReconnectEnabled = true;
        await DetectAsync();
    }

    private async Task DetectAsync()
    {
        bool pixel =
            _activeProduct.Driver == DeviceDriverKind.PixelProCdc;

        ConnectUsbButton.IsEnabled = false;
        ConnectBluetoothButton.IsEnabled = false;
        AddLog("INFO", "APP", $"Connect requested ({_connectionPreference})");
        DeviceStatus.Text = L("Detecting…", "Đang tìm…");
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
        BottomStatus.Text = pixel
            ? L("Searching PIXEL PRO over USB…", "Đang tìm PIXEL PRO qua USB…")
            : _connectionPreference switch
            {
                "usb" => L("Searching USB…", "Đang tìm USB…"),
                "bluetooth" => L("Searching Bluetooth…", "Đang tìm Bluetooth…"),
                _ => L("Searching USB first, then Bluetooth…", "Đang tìm USB trước, sau đó Bluetooth…")
            };

        var connection = _connectionPreference switch
        {
            "usb" => await _serial.ConnectUsbAsync(),
            "bluetooth" => await _serial.ConnectBluetoothAsync(),
            _ => await _serial.AutoDetectAsync()
        };

        if (connection is null)
        {
            DeviceStatus.Text = L("Not connected", "Chưa kết nối");
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
            BottomStatus.Text = pixel
                ? L(
                    "PIXEL PRO not found. Connect the USB cable.",
                    "Không tìm thấy PIXEL PRO. Hãy cắm cáp USB.")
                : L(
                    "LumiPad not found. Pair the keyboard over Bluetooth, or connect USB as fallback.",
                    "Không tìm thấy LumiPad. Hãy ghép Bluetooth hoặc cắm USB dự phòng.");
            SetDeviceControlsEnabled(false);
            UpdateTransportIndicators();
        }
        else
        {
            _keyboardSleeping = false;
            DeviceStatus.Text = connection;
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(48, 209, 88));
            BottomStatus.Text = connection.StartsWith("Bluetooth", StringComparison.Ordinal)
                ? L("Connected wirelessly. Now Playing and RGB are live.",
                    "Đã kết nối không dây. Now Playing và RGB đang hoạt động.")
                : pixel
                    ? L(
                        "PIXEL PRO connected over USB.",
                        "PIXEL PRO đã kết nối qua USB.")
                    : L(
                        "Connected over USB fallback. Now Playing and RGB are live.",
                        "Đã kết nối qua USB dự phòng. Now Playing và RGB đang hoạt động.");

            _firmwareLogSeq = 0;
            AddLog("INFO", "LINK", $"Connected: {connection}; {_serial.FirmwareHello}");
            SetDeviceControlsEnabled(true);
            _lastAppliedAutoProfile = -1;
            PollAutoProfile(force: true);
            SendAllRgb();
            SendPowerTiming();
            await UpdateMemoryUsageAsync();
            await UpdatePanelInfoAsync();
            UpdateTransportIndicators();
            UpdateSleepButtonUi();
            await CheckForUpdatesAsync(silent: true);
        }

        ConnectUsbButton.IsEnabled = true;
        ConnectBluetoothButton.IsEnabled = !pixel;
        await UpdateProductOverviewAsync();
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _autoReconnectEnabled = false;
        AddLog("INFO", "LINK", "Manual disconnect");
        _serial.Disconnect();
        DeviceStatus.Text = L("Not connected", "Chưa kết nối");
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
        BottomStatus.Text = L("Disconnected.", "Đã ngắt kết nối.");
        _keyboardSleeping = false;
        _activeBatteryPercent = null;
        SetDeviceControlsEnabled(false);
        UpdateTransportIndicators();
        UpdateSleepButtonUi();
        UpdateProductHubUi();
    }

    private async Task AutoReconnectLoopAsync(
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    3000,
                    token);

                foreach (ProductDefinition product in ProductCatalog.All)
                {
                    if (!_autoReconnectByProduct.TryGetValue(
                            product.Id,
                            out bool autoReconnect) ||
                        !autoReconnect)
                    {
                        continue;
                    }

                    IDeviceLink link =
                        LinkFor(product);

                    string preference =
                        _connectionPreferences.TryGetValue(
                            product.Id,
                            out string? storedPreference)
                            ? storedPreference
                            : "auto";

                    if (link.IsConnected)
                    {
                        if (preference == "auto" &&
                            link.IsBluetoothConnected)
                        {
                            string? promoted =
                                await link.PromoteToUsbIfAvailableAsync(
                                    token);

                            if (promoted is not null)
                            {
                                AddLog(
                                    "INFO",
                                    product.Name,
                                    $"Auto-promoted to {promoted}");

                                if (IsActiveProduct(product))
                                {
                                    DeviceStatus.Text =
                                        promoted;

                                    DeviceDot.Fill =
                                        new SolidColorBrush(
                                            MediaColor.FromRgb(
                                                48,
                                                209,
                                                88));

                                    BottomStatus.Text =
                                        L(
                                            "USB detected and selected automatically.",
                                            "Đã phát hiện USB và tự động chuyển sang USB.");

                                    SetDeviceControlsEnabled(true);
                                    SendAllRgb();
                                    SendPowerTiming();
                                    await UpdateMemoryUsageAsync();
                                    await UpdatePanelInfoAsync();
                                    UpdateTransportIndicators();
                                }
                            }
                        }

                        continue;
                    }

                    string? connection =
                        preference switch
                        {
                            "usb" =>
                                await link.ConnectUsbAsync(token),
                            "bluetooth" =>
                                await link.ConnectBluetoothAsync(token),
                            _ =>
                                await link.AutoDetectAsync(token)
                        };

                    if (connection is null)
                        continue;

                    _sleepingByProduct[product.Id] = false;

                    if (product.SupportsBattery)
                    {
                        try
                        {
                            _batteryByProduct[product.Id] =
                                await link.ReadBatteryPercentAsync();
                        }
                        catch
                        {
                        }
                    }

                    AddLog(
                        "INFO",
                        product.Name,
                        $"Reconnected: {connection}");

                    if (IsActiveProduct(product))
                    {
                        DeviceStatus.Text = connection;
                        DeviceDot.Fill =
                            new SolidColorBrush(
                                MediaColor.FromRgb(
                                    48,
                                    209,
                                    88));

                        BottomStatus.Text =
                            connection.StartsWith(
                                "Bluetooth",
                                StringComparison.Ordinal)
                                ? L(
                                    "Reconnected wirelessly after wake.",
                                    "Đã kết nối lại Bluetooth sau khi wake.")
                                : L(
                                    "Reconnected over USB.",
                                    "Đã kết nối lại qua USB.");

                        SetDeviceControlsEnabled(true);
                        _lastAppliedAutoProfile = -1;
                        PollAutoProfile(force: true);
                        SendAllRgb();
                        SendPowerTiming();
                        await UpdateMemoryUsageAsync();
                        await UpdatePanelInfoAsync();
                        await RestoreScreensaverAfterReconnectAsync();
                        UpdateTransportIndicators();
                        UpdateSleepButtonUi();
                        await CheckForUpdatesAsync(silent: true);
                    }
                }

                await UpdateProductOverviewAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AddLog(
                    "WARN",
                    "APP",
                    $"Parallel reconnect loop: {ex.Message}");
            }
        }
    }

    private static int ComboSeconds(
        object sender,
        int fallback)
    {
        // Read the ComboBox's current SelectedValue (Tag) instead of relying
        // on SelectionChangedEventArgs.AddedItems. AddedItems can be stale or
        // empty after WPF style/language refreshes, which made the timing
        // controls appear stuck on the first selected value.
        if (sender is System.Windows.Controls.ComboBox combo &&
            int.TryParse(combo.SelectedValue?.ToString(), out int seconds))
        {
            return Math.Max(0, seconds);
        }

        return fallback;
    }

    private void SendPowerTiming()
    {
        _serial.SetScreensaverDelay(_screensaverDelaySeconds);
        _serial.SetScreensaverSource(
            string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal));
        _serial.SetSleepTimeout(_sleepDelaySeconds);
        _serial.SetRgbIdleTimeout(_rgbIdleDelaySeconds);

        if (_activeProduct.Driver != DeviceDriverKind.PixelProCdc)
            _serial.SetDeepSleepTimeout(_deepSleepDelaySeconds);
    }

    private void ScreensaverSourceCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ScreensaverSourceCombo?.SelectedItem is not ComboBoxItem item)
            return;

        _screensaverSource =
            string.Equals(item.Tag?.ToString(), "PcMonitor", StringComparison.Ordinal)
                ? "PcMonitor"
                : "Media";

        if (IsPixelProActive)
            SetPixelHomePreviewMode(false);

        UpdateScreensaverSourceUi();

        if (_uiReady)
            SaveAppSettings();

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetScreensaverSource(
                string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal));
        }
    }

    private void UpdateScreensaverSourceUi()
    {
        if (ScreensaverSourceHint is null)
            return;

        bool pc =
            string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal);

        ScreensaverSourceHint.Text = pc
            ? L("Live PC telemetry", "Thông số PC trực tiếp")
            : L("Uploaded media", "GIF / ảnh đã tải lên");

        if (SendScreensaverButton is not null)
            SendScreensaverButton.IsEnabled =
                !pc &&
                _serial.IsConnected &&
                _screensaverAnimation is not null;
    }

    private void ScreensaverDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _screensaverDelaySeconds =
            ComboSeconds(sender, _screensaverDelaySeconds);

        if (_uiReady)
        {
            SaveAppSettings();
        }

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetScreensaverDelay(_screensaverDelaySeconds);
            BottomStatus.Text = L(
                $"Screensaver: {_screensaverDelaySeconds}s",
                $"Bảo vệ màn hình: {_screensaverDelaySeconds} giây");
        }
    }

    private void SleepDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _sleepDelaySeconds =
            ComboSeconds(sender, _sleepDelaySeconds);

        if (_uiReady)
        {
            SaveAppSettings();
        }

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetSleepTimeout(_sleepDelaySeconds);
            BottomStatus.Text = L(
                _sleepDelaySeconds == 0 ? "Sleep: Never" : $"Sleep: {_sleepDelaySeconds}s",
                _sleepDelaySeconds == 0 ? "Ngủ: Không bao giờ" : $"Ngủ: {_sleepDelaySeconds} giây");
        }
    }

    private void RgbIdleDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _rgbIdleDelaySeconds =
            ComboSeconds(sender, _rgbIdleDelaySeconds);

        if (_uiReady)
            SaveAppSettings();

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetRgbIdleTimeout(_rgbIdleDelaySeconds);
            BottomStatus.Text = L(
                _rgbIdleDelaySeconds == 0
                    ? "LED idle timeout: Never"
                    : $"LED idle timeout: {_rgbIdleDelaySeconds}s",
                _rgbIdleDelaySeconds == 0
                    ? "Tắt LED khi rảnh: Không bao giờ"
                    : $"Tắt LED khi rảnh: {_rgbIdleDelaySeconds} giây");
        }
    }

    private void DeepSleepDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_activeProduct.Driver == DeviceDriverKind.PixelProCdc)
            return;

        _deepSleepDelaySeconds =
            ComboSeconds(sender, _deepSleepDelaySeconds);

        if (_uiReady)
            SaveAppSettings();

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetDeepSleepTimeout(_deepSleepDelaySeconds);
            BottomStatus.Text = L(
                _deepSleepDelaySeconds == 0
                    ? "Deep sleep: Never"
                    : $"Deep sleep: {_deepSleepDelaySeconds}s",
                _deepSleepDelaySeconds == 0
                    ? "Ngủ sâu: Không bao giờ"
                    : $"Ngủ sâu: {_deepSleepDelaySeconds} giây");
        }
    }

    private async void SleepKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text =
                L("Connect LumiPad before using sleep.",
                  "Hãy kết nối LumiPad trước khi dùng chế độ ngủ.");
            return;
        }

        try
        {
            if (_keyboardSleeping)
            {
                await _serial.WakeKeyboardAsync();
                _keyboardSleeping = false;
                BottomStatus.Text =
                    L("Keyboard display and RGB are awake.",
                      "Màn hình và RGB của bàn phím đã bật lại.");
            }
            else
            {
                await _serial.SleepKeyboardAsync();
                _keyboardSleeping = true;
                BottomStatus.Text =
                    L("Keyboard display and RGB are sleeping. Press again to wake.",
                      "Màn hình và RGB đang ngủ. Nhấn lại để bật lên.");
            }

            UpdateSleepButtonUi();
        }
        catch (Exception ex)
        {
            BottomStatus.Text =
                L($"Sleep/wake failed: {ex.Message}",
                  $"Ngủ/đánh thức thất bại: {ex.Message}");
        }
    }


    private sealed record LatestReleaseInfo(
        string Tag,
        string AppVersion,
        string FirmwareVersion,
        string ManifestUrl);

    private string CurrentAppVersion()
    {
        Version? version =
            System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetName()
                .Version;

        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private string? CurrentFirmwareVersion()
    {
        string hello = _serial.FirmwareHello ?? "";

        foreach (string part in hello.Split('|'))
        {
            if (part.StartsWith(
                    "FW=",
                    StringComparison.OrdinalIgnoreCase))
            {
                string value = part[3..].Trim();
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : value;
            }
        }

        return null;
    }

    private static Version ParseVersionLoose(string value)
    {
        string clean =
            (value ?? "")
                .Trim()
                .TrimStart('v', 'V');

        string numeric =
            new string(
                clean.TakeWhile(
                    c => char.IsDigit(c) || c == '.')
                     .ToArray());

        if (Version.TryParse(numeric, out Version? version))
            return version;

        return new Version(0, 0, 0);
    }

    private static bool IsNewerVersion(
        string latest,
        string current) =>
        ParseVersionLoose(latest) >
        ParseVersionLoose(current);

    private static string VersionFromReleaseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "";

        int start =
            tag.IndexOfAny(
                ['0', '1', '2', '3', '4',
                 '5', '6', '7', '8', '9']);

        if (start < 0)
            return tag.TrimStart('v', 'V');

        string candidate = tag[start..];

        string numeric =
            new string(
                candidate.TakeWhile(
                    ch => char.IsDigit(ch) || ch == '.')
                         .ToArray());

        return string.IsNullOrWhiteSpace(numeric)
            ? tag.TrimStart('v', 'V')
            : numeric;
    }

    private static async Task<(string Tag, string Version, string ManifestUrl)>
        ReadReleaseVersionAsync(
            string api,
            string manifestName,
            string versionProperty)
    {
        string separator =
            api.Contains('?') ? "&" : "?";

        string uncachedApi =
            $"{api}{separator}_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        using var response =
            await UpdateHttp.GetAsync(uncachedApi);

        response.EnsureSuccessStatusCode();

        using JsonDocument release =
            JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

        string tag =
            release.RootElement
                .GetProperty("tag_name")
                .GetString() ?? "";

        // The GitHub release tag is authoritative. This lets old/new app
        // releases work even when the optional manifest/CDN is unavailable.
        string version =
            VersionFromReleaseTag(tag);

        string manifestUrl = "";

        if (release.RootElement.TryGetProperty(
                "assets",
                out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (!string.Equals(
                        asset.GetProperty("name").GetString(),
                        manifestName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                manifestUrl =
                    asset.GetProperty(
                        "browser_download_url").GetString() ?? "";
                break;
            }
        }

        // Manifest is optional metadata only. Never discard a valid release
        // tag merely because GitHub's asset CDN cannot serve the manifest.
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            try
            {
                using var manifestResponse =
                    await UpdateHttp.GetAsync(manifestUrl);

                if (manifestResponse.IsSuccessStatusCode)
                {
                    using JsonDocument manifest =
                        JsonDocument.Parse(
                            await manifestResponse.Content.ReadAsStringAsync());

                    JsonElement value;

                    if (manifest.RootElement.TryGetProperty(
                            versionProperty,
                            out value) ||
                        manifest.RootElement.TryGetProperty(
                            "version",
                            out value))
                    {
                        string manifestVersion =
                            value.GetString() ?? "";

                        if (ParseVersionLoose(manifestVersion) >
                            new Version(0, 0, 0))
                        {
                            version = manifestVersion;
                        }
                    }
                }
            }
            catch
            {
                // Tag-derived version remains valid.
            }
        }

        return (tag, version, manifestUrl);
    }

    private async Task<LatestReleaseInfo> GetLatestReleaseInfoAsync()
    {
        // Independent release channels: a missing firmware release must not block app updates.
        string tag = "", appVersion = "", firmwareVersion = "", manifestUrl = "";
        try
        {
            (tag, appVersion, manifestUrl) = await ReadReleaseVersionAsync(
                UpdateReleaseApi, "release-manifest.json", "appVersion");
        }
        catch (Exception ex)
        {
            AddLog("WARN", "UPDATE", $"App release check failed: {ex.Message}");
        }
        try
        {
            bool pixel = string.Equals(_activeProduct.Id, ProductCatalog.PixelPro.Id,
                StringComparison.OrdinalIgnoreCase);
            var firmware = await ReadReleaseVersionAsync(
                pixel ? PixelProFirmwareReleaseApi : RynorFirmwareReleaseApi,
                pixel ? "firmware-manifest.json" : "release-manifest.json",
                pixel ? "version" : "firmwareVersion");
            firmwareVersion = firmware.Version;
        }
        catch (Exception ex)
        {
            AddLog("WARN", "UPDATE", $"Firmware release check failed: {ex.Message}");
        }
        return new LatestReleaseInfo(tag, appVersion, firmwareVersion, manifestUrl);
    }

    private void RefreshUpdateUi()
    {
        if (AppUpdateVersionText is null ||
            FirmwareUpdateVersionText is null)
        {
            return;
        }

        string currentApp = CurrentAppVersion();
        string? currentFirmware =
            _serial.IsConnected
                ? CurrentFirmwareVersion()
                : null;

        AppUpdateVersionText.Text =
            $"Current v{currentApp} · Latest " +
            (string.IsNullOrWhiteSpace(_latestAppVersion)
                ? "--"
                : $"v{_latestAppVersion}");

        FirmwareUpdateVersionText.Text =
            "Current " +
            (currentFirmware is null
                ? (_serial.IsConnected ? "legacy / unknown" : "--")
                : $"v{currentFirmware}") +
            " · Latest " +
            (string.IsNullOrWhiteSpace(_latestFirmwareVersion)
                ? "--"
                : $"v{_latestFirmwareVersion}");

        if (AppUpdateStateText is not null)
        {
            AppUpdateStateText.Text =
                _appUpdateAvailable
                    ? L("Update available", "Có bản mới")
                    : L("Up to date", "Đã mới nhất");
        }

        if (FirmwareUpdateStateText is not null)
        {
            if (!_serial.IsConnected)
            {
                FirmwareUpdateStateText.Text =
                    L(
                        "Connect keyboard to read firmware version",
                        "Kết nối bàn phím để đọc phiên bản firmware");
            }
            else if (_firmwareUpdateAvailable)
            {
                bool pixelBootstrapRequired =
                    string.Equals(
                        _activeProduct.Id,
                        ProductCatalog.PixelPro.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    _serial is PixelProCdcLink;

                FirmwareUpdateStateText.Text =
                    pixelBootstrapRequired
                        ? L(
                            "ROM BOOT flash required; LumiPad will install native USB automatically",
                            "Cần nạp qua ROM BOOT; LumiPad sẽ tự cài native USB")
                        : _serial.IsUsbConnected
                            ? L("Update available", "Có bản mới")
                            : L(
                                "Connect by USB to update firmware",
                                "Cắm USB để cập nhật firmware");
            }
            else
            {
                FirmwareUpdateStateText.Text =
                    L("Up to date", "Đã mới nhất");
            }
        }

        int count =
            (_appUpdateAvailable ? 1 : 0) +
            (_firmwareUpdateAvailable ? 1 : 0);

        if (UpdateStatusText is not null)
        {
            UpdateStatusText.Text =
                count switch
                {
                    0 => L(
                        "Everything is up to date.",
                        "Tất cả đã là bản mới nhất."),
                    1 => L(
                        "1 update available.",
                        "Có 1 bản cập nhật mới."),
                    _ => L(
                        "2 updates available.",
                        "Có 2 bản cập nhật mới.")
                };
        }

        SetDeviceControlsEnabled(
            _serial.IsConnected);
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_checkingUpdates || _updateBusy)
            return;

        _checkingUpdates = true;

        try
        {
            if (!silent && UpdateStatusText is not null)
            {
                UpdateStatusText.Text =
                    L(
                        "Checking for updates…",
                        "Đang kiểm tra cập nhật…");
            }

            LatestReleaseInfo info =
                await GetLatestReleaseInfoAsync();

            _latestReleaseTag = info.Tag;
            _latestAppVersion = info.AppVersion;
            _latestFirmwareVersion =
                info.FirmwareVersion;

            string currentApp =
                CurrentAppVersion();
            string? currentFirmware =
                CurrentFirmwareVersion();

            _appUpdateAvailable =
                IsNewerVersion(
                    _latestAppVersion,
                    currentApp);

            bool pixelRecovery =
                string.Equals(
                    _activeProduct.Id,
                    ProductCatalog.PixelPro.Id,
                    StringComparison.OrdinalIgnoreCase);

            _firmwareUpdateAvailable =
                !string.IsNullOrWhiteSpace(_latestFirmwareVersion) &&
                (
                    pixelRecovery
                        ? (!_serial.IsConnected ||
                           currentFirmware is null ||
                           IsNewerVersion(
                               _latestFirmwareVersion,
                               currentFirmware))
                        : (_serial.IsConnected &&
                           (currentFirmware is null ||
                            IsNewerVersion(
                                _latestFirmwareVersion,
                                currentFirmware)))
                );

            RefreshUpdateUi();

            AddLog(
                "INFO",
                "UPDATE",
                $"Check complete: app {currentApp}->{_latestAppVersion}, " +
                $"firmware {currentFirmware ?? "legacy"}->{_latestFirmwareVersion}");
        }
        catch (Exception ex)
        {
            if (UpdateStatusText is not null)
            {
                UpdateStatusText.Text =
                    L(
                        $"Update check failed: {ex.Message}",
                        $"Kiểm tra cập nhật lỗi: {ex.Message}");
            }

            AddLog(
                "WARN",
                "UPDATE",
                $"Update check failed: {ex.Message}");
        }
        finally
        {
            _checkingUpdates = false;

            if (CheckUpdatesButton is not null)
                CheckUpdatesButton.IsEnabled =
                    !_updateBusy;
        }
    }

    private async void CheckUpdates_Click(
        object sender,
        RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(silent: false);
    }

    private async Task<(string Tag, string Url)> FindLatestAssetAsync(
        string assetName,
        string? releaseApi = null)
    {
        using var response =
            await UpdateHttp.GetAsync(
                string.IsNullOrWhiteSpace(releaseApi)
                    ? UpdateReleaseApi
                    : releaseApi);
        response.EnsureSuccessStatusCode();

        using JsonDocument json =
            JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

        string tag =
            json.RootElement.TryGetProperty(
                "tag_name",
                out JsonElement tagElement)
                ? tagElement.GetString() ?? ""
                : "";

        if (!json.RootElement.TryGetProperty(
                "assets",
                out JsonElement assets))
        {
            throw new InvalidOperationException(
                "Latest release has no assets.");
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name =
                asset.GetProperty("name").GetString() ?? "";

            if (!string.Equals(
                    name,
                    assetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string url =
                asset.GetProperty(
                    "browser_download_url").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(url))
                break;

            return (tag, url);
        }

        throw new InvalidOperationException(
            $"Release asset not found: {assetName}");
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination)
    {
        using var response =
            await UpdateHttp.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using IO.Stream input =
            await response.Content.ReadAsStreamAsync();
        await using IO.FileStream output =
            new(
                destination,
                IO.FileMode.Create,
                IO.FileAccess.Write,
                IO.FileShare.None);

        await input.CopyToAsync(output);
    }

    private static string? FindUf2Drive(
        ISet<string>? exclude = null)
    {
        foreach (IO.DriveInfo drive in IO.DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                string root = drive.RootDirectory.FullName;
                if (exclude is not null &&
                    exclude.Contains(root))
                {
                    continue;
                }

                if (IO.File.Exists(
                        IO.Path.Combine(root, "INFO_UF2.TXT")))
                {
                    return root;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static HashSet<string> CurrentUf2Drives()
    {
        var result =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (IO.DriveInfo drive in IO.DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady &&
                    IO.File.Exists(
                        IO.Path.Combine(
                            drive.RootDirectory.FullName,
                            "INFO_UF2.TXT")))
                {
                    result.Add(
                        drive.RootDirectory.FullName);
                }
            }
            catch
            {
            }
        }

        return result;
    }

    private static HashSet<string> CurrentSerialPorts() =>
        new(
            System.IO.Ports.SerialPort.GetPortNames(),
            StringComparer.OrdinalIgnoreCase);

    private static string? FindPixelProBootPort(
        ISet<string> before)
    {
        string[] ports =
            System.IO.Ports.SerialPort.GetPortNames();

        var live =
            ports.ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        // ESP32-S2 ROM USB-OTG download mode enumerates as Espressif
        // VID 303A / PID 0002. Detect that identity first because Windows is
        // allowed to reuse the exact same COM number after the reboot.
        try
        {
            using var searcher =
                new System.Management.ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            var espressifPorts =
                new List<string>();

            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string name =
                    Convert.ToString(
                        obj["Name"]) ?? "";

                string pnp =
                    Convert.ToString(
                        obj["PNPDeviceID"]) ?? "";

                var match =
                    System.Text.RegularExpressions.Regex.Match(
                        name,
                        @"\((COM\d+)\)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success)
                    continue;

                string port =
                    match.Groups[1].Value;

                if (!live.Contains(port) ||
                    !pnp.Contains(
                        "VID_303A",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                espressifPorts.Add(port);

                if (pnp.Contains(
                        "PID_0002",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return port;
                }
            }

            string? freshEspressif =
                espressifPorts.FirstOrDefault(
                    port => !before.Contains(port));

            if (!string.IsNullOrWhiteSpace(freshEspressif))
                return freshEspressif;

            if (espressifPorts.Distinct(
                    StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                return espressifPorts[0];
            }
        }
        catch
        {
        }

        string? fresh =
            ports.FirstOrDefault(
                port => !before.Contains(port));

        if (!string.IsNullOrWhiteSpace(fresh))
            return fresh;

        return ports.Length == 1
            ? ports[0]
            : null;
    }

    private static async Task<string?> WaitForPixelProBootPortAsync(
        ISet<string> portsBefore,
        int attempts = 48,
        int delayMs = 250)
    {
        for (int i = 0; i < attempts; i++)
        {
            await Task.Delay(delayMs);

            string? port =
                FindPixelProBootPort(
                    portsBefore);

            if (!string.IsNullOrWhiteSpace(port))
            {
                // Windows can expose the new COM name slightly before usbser
                // finishes opening it. Give the ROM port a short settle time
                // so esptool can use it on the same update attempt.
                await Task.Delay(650);
                return port;
            }
        }

        return null;
    }

    private async Task<string> EnsureEspToolAsync(
        string toolRoot)
    {
        string? existing =
            IO.Directory.Exists(toolRoot)
                ? IO.Directory
                    .EnumerateFiles(
                        toolRoot,
                        "esptool.exe",
                        IO.SearchOption.AllDirectories)
                    .FirstOrDefault()
                : null;

        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        IO.Directory.CreateDirectory(toolRoot);

        string zipPath =
            IO.Path.Combine(
                toolRoot,
                "esptool-windows-amd64.zip");

        UpdateStatusText.Text =
            L(
                "Downloading Espressif flashing engine for the one-time bootstrap…",
                "Đang tải bộ nạp chính thức của Espressif cho lần bootstrap duy nhất…");

        await DownloadFileAsync(
            "https://github.com/espressif/esptool/releases/download/v5.4.0/esptool-v5.4.0-windows-amd64.zip",
            zipPath);

        string extractPath =
            IO.Path.Combine(
                toolRoot,
                "esptool-v5.4.0");

        if (IO.Directory.Exists(extractPath))
            IO.Directory.Delete(extractPath, true);

        IO.Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(
            zipPath,
            extractPath,
            true);

        try
        {
            IO.File.Delete(zipPath);
        }
        catch
        {
        }

        string? exe =
            IO.Directory
                .EnumerateFiles(
                    extractPath,
                    "esptool.exe",
                    IO.SearchOption.AllDirectories)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException(
                "Espressif esptool.exe was not found after extraction.");
        }

        return exe;
    }

    private async Task BootstrapPixelProFirmwareAsync()
    {
        var confirm = System.Windows.MessageBox.Show(
            L(
                "Install or repair native USB firmware on PIXEL PRO? LumiPad will download the latest PIXEL PRO native USB image and the official Espressif flashing engine. Put the board into ROM BOOT mode when prompted.",
                "Cài hoặc sửa firmware native USB cho PIXEL PRO? LumiPad sẽ tự tải bản native USB mới nhất và bộ nạp chính thức của Espressif. Đưa mạch vào ROM BOOT khi app yêu cầu."),
            "PIXEL PRO · Install native USB",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        SetDeviceControlsEnabled(true);

        string workRoot =
            IO.Path.Combine(
                IO.Path.GetTempPath(),
                "LumiPad-PixelPro-NativeUSB-" +
                Guid.NewGuid().ToString("N"));
        string mergedPath =
            IO.Path.Combine(
                workRoot,
                PixelProNativeFirmwareAsset);

        try
        {
            IO.Directory.CreateDirectory(workRoot);

            UpdateStatusText.Text =
                L(
                    "Downloading latest PIXEL PRO native USB firmware…",
                    "Đang tải firmware native USB mới nhất cho PIXEL PRO…");

            var firmware =
                await FindLatestAssetAsync(
                    PixelProNativeFirmwareAsset,
                    PixelProFirmwareReleaseApi);

            await DownloadFileAsync(
                firmware.Url,
                mergedPath);

            string localTools =
                IO.Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "LumiPad",
                    "Tools",
                    "esptool-5.4.0");

            string esptool =
                await EnsureEspToolAsync(localTools);

            HashSet<string> portsBefore =
                CurrentSerialPorts();

            _autoReconnectEnabled = false;

            bool automaticBootStarted = false;

            if (_serial is PixelProCdcLink pixelLink)
            {
                try
                {
                    automaticBootStarted =
                        await pixelLink.TryEnterRomBootloaderAsync();
                }
                catch (OperationCanceledException ex)
                {
                    // On Windows the normal CDC COM disappears during the
                    // intentional USB re-enumeration. SerialPort can surface
                    // that expected transition as "The operation was canceled".
                    // Do not abort the update: discover the ROM COM and keep
                    // flashing in this same button press.
                    automaticBootStarted = true;

                    AddLog(
                        "INFO",
                        "UPDATE",
                        "PIXEL PRO CDC changed to ROM BOOT while the old COM " +
                        $"operation was being canceled; continuing. {ex.Message}");
                }
                catch (IO.IOException ex)
                {
                    automaticBootStarted = true;

                    AddLog(
                        "INFO",
                        "UPDATE",
                        "PIXEL PRO CDC disappeared during ROM BOOT transition; " +
                        $"continuing boot-port discovery. {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    // A closing SerialPort may also throw InvalidOperationException
                    // after the ROM BOOT command has already taken effect.
                    AddLog(
                        "WARN",
                        "UPDATE",
                        "Automatic ROM BOOT serial transition did not finish " +
                        $"cleanly; checking for the new COM before falling back. {ex.Message}");
                }
            }

            // The application CDC handle is no longer useful once ROM BOOT
            // starts. Disconnecting here also prevents background reconnect
            // work from racing the bootloader COM discovery.
            try
            {
                _serial.Disconnect();
            }
            catch
            {
            }

            UpdateStatusText.Text =
                automaticBootStarted
                    ? L(
                        "PIXEL PRO switched USB mode. Waiting for the ROM BOOT COM…",
                        "PIXEL PRO đã chuyển chế độ USB. Đang chờ COM ROM BOOT…")
                    : L(
                        "Checking for PIXEL PRO ROM BOOT COM…",
                        "Đang kiểm tra COM ROM BOOT của PIXEL PRO…");

            // Always look for the new ROM COM before asking the user to retry.
            // This is the key one-click path when Windows changes COM numbers
            // and reports the old CDC operation as canceled.
            string? bootPort =
                await WaitForPixelProBootPortAsync(
                    portsBefore,
                    attempts: 32);

            if (string.IsNullOrWhiteSpace(bootPort))
            {
                var bootPrompt = System.Windows.MessageBox.Show(
                    L(
                        "Automatic ROM BOOT did not appear. Put PIXEL PRO into ROM BOOT now:\n\n1. Hold BOOT.\n2. Press RESET once.\n3. Release RESET.\n4. Release BOOT.\n5. Click OK here.\n\nLumiPad will continue this same update attempt.",
                        "ROM BOOT tự động chưa xuất hiện. Đưa PIXEL PRO vào ROM BOOT ngay:\n\n1. Giữ BOOT.\n2. Nhấn RESET một lần.\n3. Thả RESET.\n4. Thả BOOT.\n5. Bấm OK ở đây.\n\nLumiPad sẽ tiếp tục ngay trong lần cập nhật này."),
                    "PIXEL PRO · BOOT fallback",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (bootPrompt != MessageBoxResult.OK)
                    return;

                bootPort =
                    await WaitForPixelProBootPortAsync(
                        portsBefore,
                        attempts: 48);
            }

            if (string.IsNullOrWhiteSpace(bootPort))
            {
                throw new InvalidOperationException(
                    L(
                        "ESP32-S2 ROM BOOT device was not found. Repeat BOOT + RESET and retry.",
                        "Không tìm thấy thiết bị ROM BOOT ESP32-S2. Làm lại BOOT + RESET rồi thử lại."));
            }

            UpdateStatusText.Text =
                L(
                    $"Flashing PIXEL PRO on {bootPort}…",
                    $"Đang nạp PIXEL PRO trên {bootPort}…");

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = esptool,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            startInfo.ArgumentList.Add("--chip");
            startInfo.ArgumentList.Add("esp32s2");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(bootPort);
            startInfo.ArgumentList.Add("--baud");
            startInfo.ArgumentList.Add("460800");
            startInfo.ArgumentList.Add("--before");
            startInfo.ArgumentList.Add("no-reset");
            startInfo.ArgumentList.Add("--after");
            startInfo.ArgumentList.Add("hard-reset");
            startInfo.ArgumentList.Add("write-flash");
            startInfo.ArgumentList.Add("0x0");
            startInfo.ArgumentList.Add(mergedPath);

            using Process process =
                Process.Start(startInfo) ??
                throw new InvalidOperationException(
                    "Could not start Espressif esptool.");

            Task<string> stdoutTask =
                process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask =
                process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            string esptoolOutput =
                stdout + Environment.NewLine + stderr;

            bool flashVerified =
                esptoolOutput.Contains(
                    "Hash of data verified",
                    StringComparison.OrdinalIgnoreCase);

            bool postResetPortGone =
                flashVerified &&
                esptoolOutput.Contains(
                    "Cannot configure port",
                    StringComparison.OrdinalIgnoreCase) &&
                esptoolOutput.Contains(
                    "device which does not exist",
                    StringComparison.OrdinalIgnoreCase);

            AddLog(
                process.ExitCode == 0 || postResetPortGone
                    ? "INFO"
                    : "ERROR",
                "ESPTOOL",
                esptoolOutput);

            if (process.ExitCode != 0 && !postResetPortGone)
            {
                throw new InvalidOperationException(
                    L(
                        $"Espressif flashing failed (exit {process.ExitCode}). Open Diagnostics for details.",
                        $"Nạp bằng Espressif lỗi (mã {process.ExitCode}). Mở Diagnostics để xem chi tiết."));
            }

            if (postResetPortGone)
            {
                AddLog(
                    "INFO",
                    "UPDATE",
                    "PIXEL PRO flash verified; boot COM disappeared during watchdog reset, treating flash as successful.");
            }

            UpdateStatusText.Text =
                L(
                    $"PIXEL PRO native USB {firmware.Tag} installed. Reconnecting…",
                    $"Đã nạp native USB PIXEL PRO {firmware.Tag}. Đang kết nối lại…");

            _autoReconnectEnabled = true;

            string? connection = null;
            for (int i = 0; i < 12 && connection is null; i++)
            {
                await Task.Delay(500);
                connection =
                    await _serial.ConnectUsbAsync();
            }

            if (connection is null)
            {
                System.Windows.MessageBox.Show(
                    L(
                        "native USB flash completed and verified. If PIXEL PRO has not reconnected yet, press RESET once. LumiPad will keep trying the native USB CDC interface automatically.",
                        "Đã nạp và xác minh native USB xong. Nếu PIXEL PRO chưa kết nối lại, nhấn RESET một lần. LumiPad sẽ tự tiếp tục dò native USB vendor HID."),
                    "PIXEL PRO native USB install",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                DeviceStatus.Text = connection;
                DeviceDot.Fill =
                    new SolidColorBrush(
                        MediaColor.FromRgb(48, 209, 88));

                System.Windows.MessageBox.Show(
                    L(
                        "native USB installed and LumiPad connected through the dedicated PIXEL PRO CDC interface.",
                        "native USB đã cài và LumiPad đã kết nối qua giao diện vendor HID riêng của PIXEL PRO."),
                    "PIXEL PRO",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            await CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            _autoReconnectEnabled = true;
            UpdateStatusText.Text =
                L(
                    $"PIXEL PRO native USB install failed: {ex.Message}",
                    $"Cài native USB PIXEL PRO lỗi: {ex.Message}");
            AddLog(
                "ERROR",
                "UPDATE",
                $"PIXEL PRO native USB install failed: {ex}");
        }
        finally
        {
            try
            {
                if (IO.Directory.Exists(workRoot))
                    IO.Directory.Delete(workRoot, true);
            }
            catch
            {
            }

            _updateBusy = false;
            SetDeviceControlsEnabled(
                _serial.IsConnected);
        }
    }

    private async Task UpdatePixelProFirmwareAsync()
    {
        if (_serial is not PixelProCdcLink)
        {
            throw new InvalidOperationException(
                "PIXEL PRO native USB vendor HID driver is not active.");
        }

        // PIXEL PRO native USB phase 1 is recovered through the ESP32-S2 ROM BOOT
        // loader. The merged native USB image is written at 0x0.
        await BootstrapPixelProFirmwareAsync();
    }

    private async void FirmwareUpdate_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updateBusy)
            return;

        if (string.Equals(
                _activeProduct.Id,
                ProductCatalog.PixelPro.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            await UpdatePixelProFirmwareAsync();
            return;
        }

        if (!_serial.IsConnected ||
            !_serial.IsUsbConnected)
        {
            System.Windows.MessageBox.Show(
                L(
                    "Connect RYNOR ONE by USB first. The app will enter UF2 bootloader and flash the latest firmware automatically.",
                    "Hãy cắm RYNOR ONE bằng USB trước. App sẽ tự vào UF2 bootloader và tự nạp firmware mới nhất."),
                "LumiPad Firmware Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            L(
                "Download and install the latest RYNOR ONE firmware now?",
                "Tải và tự nạp firmware RYNOR ONE mới nhất ngay bây giờ?"),
            "LumiPad Firmware Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        SetDeviceControlsEnabled(true);

        string tempFile =
            IO.Path.Combine(
                IO.Path.GetTempPath(),
                $"rynor-one-{Guid.NewGuid():N}.uf2");

        try
        {
            UpdateStatusText.Text =
                L(
                    "Downloading latest firmware…",
                    "Đang tải firmware mới nhất…");

            var asset =
                await FindLatestAssetAsync(
                    "firmware.uf2", RynorFirmwareReleaseApi);
            await DownloadFileAsync(
                asset.Url,
                tempFile);

            HashSet<string> before =
                CurrentUf2Drives();

            UpdateStatusText.Text =
                L(
                    "Entering UF2 bootloader…",
                    "Đang vào UF2 bootloader…");

            _autoReconnectEnabled = false;
            await _serial.EnterDfuAsync();
            await Task.Delay(250);
            _serial.Disconnect();

            string? uf2Root = null;
            for (int i = 0; i < 40 && uf2Root is null; i++)
            {
                await Task.Delay(250);
                uf2Root = FindUf2Drive(before);
            }

            uf2Root ??= FindUf2Drive();

            if (string.IsNullOrWhiteSpace(uf2Root))
            {
                throw new InvalidOperationException(
                    L(
                        "UF2 drive did not appear. Check the USB cable and retry.",
                        "Không thấy ổ UF2. Kiểm tra cáp USB rồi thử lại."));
            }

            UpdateStatusText.Text =
                L(
                    $"Flashing {asset.Tag}…",
                    $"Đang nạp {asset.Tag}…");

            string target =
                IO.Path.Combine(
                    uf2Root,
                    "firmware.uf2");

            IO.File.Copy(
                tempFile,
                target,
                true);

            await Task.Delay(1800);

            _autoReconnectEnabled = true;
            UpdateStatusText.Text =
                L(
                    "Firmware installed. Reconnecting…",
                    "Đã nạp firmware. Đang kết nối lại…");

            await DetectAsync();

            UpdateStatusText.Text =
                L(
                    $"Firmware update complete · {asset.Tag}",
                    $"Cập nhật firmware hoàn tất · {asset.Tag}");
            await CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            _autoReconnectEnabled = true;
            UpdateStatusText.Text =
                L(
                    $"Firmware update failed: {ex.Message}",
                    $"Cập nhật firmware lỗi: {ex.Message}");
            AddLog(
                "ERROR",
                "UPDATE",
                $"Firmware update failed: {ex}");
        }
        finally
        {
            try
            {
                if (IO.File.Exists(tempFile))
                    IO.File.Delete(tempFile);
            }
            catch
            {
            }

            _updateBusy = false;
            SetDeviceControlsEnabled(
                _serial.IsConnected);
        }
    }

    private async void AppUpdate_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updateBusy)
            return;

        var confirm = System.Windows.MessageBox.Show(
            L(
                "Download the latest LumiPad app, replace this version, then reopen it automatically?",
                "Tải LumiPad mới nhất, thay bản hiện tại rồi tự mở lại app?"),
            "LumiPad App Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        SetDeviceControlsEnabled(
            _serial.IsConnected);

        string updateRoot =
            IO.Path.Combine(
                IO.Path.GetTempPath(),
                "LumiPadUpdate-" +
                Guid.NewGuid().ToString("N"));
        string zipPath =
            IO.Path.Combine(
                updateRoot,
                "app.zip");
        string stagePath =
            IO.Path.Combine(
                updateRoot,
                "stage");

        try
        {
            IO.Directory.CreateDirectory(updateRoot);

            UpdateStatusText.Text =
                L(
                    "Downloading latest app…",
                    "Đang tải app mới nhất…");

            var asset =
                await FindLatestAssetAsync(
                    "LumiPad-Windows-x64.zip");

            await DownloadFileAsync(
                asset.Url,
                zipPath);

            IO.Directory.CreateDirectory(stagePath);
            ZipFile.ExtractToDirectory(
                zipPath,
                stagePath,
                true);

            string currentExe =
                Environment.ProcessPath ??
                throw new InvalidOperationException(
                    "Current executable path is unavailable.");
            string targetDir =
                IO.Path.GetDirectoryName(currentExe) ??
                throw new InvalidOperationException(
                    "Current app directory is unavailable.");
            string exeName =
                IO.Path.GetFileName(currentExe);

            string stagedExe =
                IO.Directory
                    .EnumerateFiles(
                        stagePath,
                        exeName,
                        IO.SearchOption.AllDirectories)
                    .FirstOrDefault()
                ?? IO.Directory
                    .EnumerateFiles(
                        stagePath,
                        "*.exe",
                        IO.SearchOption.AllDirectories)
                    .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Downloaded app package has no executable.");

            string sourceDir =
                IO.Path.GetDirectoryName(stagedExe)!;

            string scriptPath =
                IO.Path.Combine(
                    updateRoot,
                    "install-update.ps1");

            string script =
$@"$ErrorActionPreference = 'Stop'
$pidToWait = {Environment.ProcessId}
$source = '{sourceDir.Replace("'", "''")}'
$target = '{targetDir.Replace("'", "''")}'
$exe = '{exeName.Replace("'", "''")}'
try {{
    Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
    Start-Process -FilePath (Join-Path $target $exe)
}} finally {{
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath '{updateRoot.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue
}}";

            IO.File.WriteAllText(
                scriptPath,
                script,
                new UTF8Encoding(false));

            UpdateStatusText.Text =
                L(
                    $"Installing {asset.Tag}. LumiPad will reopen automatically…",
                    $"Đang cài {asset.Tag}. LumiPad sẽ tự mở lại…");

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    WindowStyle =
                        ProcessWindowStyle.Hidden
                });

            // Perform the same graceful device-link cleanup as a normal Exit.
            // Abrupt process shutdown used to let Windows tear down CDC line
            // states unpredictably and could leave PIXEL PRO in ROM BOOT.
            ExitApplication();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text =
                L(
                    $"App update failed: {ex.Message}",
                    $"Cập nhật app lỗi: {ex.Message}");
            AddLog(
                "ERROR",
                "UPDATE",
                $"App update failed: {ex}");

            _updateBusy = false;
            SetDeviceControlsEnabled(
                _serial.IsConnected);
        }
    }

    private async void RestartKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text = L("Connect LumiPad before restarting the keyboard.", "Hãy kết nối LumiPad trước khi khởi động lại bàn phím.");
            return;
        }

        BottomStatus.Text = L("Restarting keyboard…", "Đang khởi động lại bàn phím…");

        try
        {
            await _serial.RestartKeyboardAsync();
            await Task.Delay(150);
            _serial.Disconnect();

            DeviceStatus.Text = L("Restarting…", "Đang khởi động lại…");
            DeviceDot.Fill =
                new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
            BottomStatus.Text =
                L("Keyboard is restarting. LumiPad will reconnect automatically.",
                  "Bàn phím đang khởi động lại. LumiPad sẽ tự kết nối lại.");
        }
        catch (Exception ex)
        {
            BottomStatus.Text = L($"Restart failed: {ex.Message}", $"Khởi động lại thất bại: {ex.Message}");
        }
    }

    private async void KeyboardDfu_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text = L("Connect LumiPad before entering DFU.", "Hãy kết nối LumiPad trước khi vào DFU.");
            return;
        }

        var result = System.Windows.MessageBox.Show(
            L("Put the keyboard into DFU/bootloader mode for firmware flashing?", "Đưa bàn phím vào chế độ DFU/bootloader để nạp firmware?"),
            "LumiPad DFU",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        _autoReconnectEnabled = false;
        BottomStatus.Text = L("Entering keyboard DFU…", "Đang đưa bàn phím vào DFU…");

        try
        {
            await _serial.EnterDfuAsync();
            await Task.Delay(150);
            _serial.Disconnect();

            DeviceStatus.Text = "DFU / Bootloader";
            DeviceDot.Fill =
                new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
            BottomStatus.Text =
                L("Keyboard is in DFU. Flash firmware, then press Connect when it boots normally.",
                  "Bàn phím đang ở DFU. Nạp firmware xong rồi bấm Kết nối khi máy khởi động lại.");
        }
        catch (Exception ex)
        {
            BottomStatus.Text = L($"DFU failed: {ex.Message}", $"DFU thất bại: {ex.Message}");
        }
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.PreviousAsync();

    private async void PlayButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.TogglePlayPauseAsync();

    private async void NextButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.NextAsync();

    private async void ShuffleButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await _nowPlaying.ToggleShuffleAsync();

    private async void RepeatButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await _nowPlaying.CycleRepeatModeAsync();

    private void TrackSlider_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_currentNowPlaying is not { CanSeek: true } data ||
            data.Duration <= TimeSpan.Zero ||
            sender is not Slider slider)
        {
            return;
        }

        _mediaSeekDragging = true;
        SetSliderValueFromPointer(slider, e);
        UpdateSeekPreview(slider.Value);
    }

    private async void TrackSlider_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_mediaSeekDragging ||
            _currentNowPlaying is not { CanSeek: true } data ||
            data.Duration <= TimeSpan.Zero ||
            sender is not Slider slider)
        {
            _mediaSeekDragging = false;
            return;
        }

        SetSliderValueFromPointer(slider, e);
        double fraction = Math.Clamp(slider.Value, 0, 1);
        _mediaSeekDragging = false;

        await _nowPlaying.SeekAsync(
            TimeSpan.FromTicks(
                (long)Math.Round(data.Duration.Ticks * fraction)));
    }

    private void TrackSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaSeekDragging)
            UpdateSeekPreview(e.NewValue);
    }

    private static void SetSliderValueFromPointer(
        Slider slider,
        MouseButtonEventArgs e)
    {
        if (slider.ActualWidth <= 1)
            return;

        double fraction =
            Math.Clamp(
                e.GetPosition(slider).X / slider.ActualWidth,
                0,
                1);
        slider.Value =
            slider.Minimum +
            (slider.Maximum - slider.Minimum) * fraction;
    }

    private void UpdateSeekPreview(double fraction)
    {
        if (_currentNowPlaying is not { } data ||
            data.Duration <= TimeSpan.Zero)
        {
            return;
        }

        fraction = Math.Clamp(fraction, 0, 1);
        TimeSpan preview =
            TimeSpan.FromTicks(
                (long)Math.Round(data.Duration.Ticks * fraction));
        ElapsedText.Text = FormatTime(preview);
        DurationText.Text =
            $"-{FormatTime(data.Duration - preview)}";
    }

    private void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _syncingMediaUi)
            return;

        double percent = Math.Clamp(e.NewValue, 0, 100);
        VolumeText.Text = $"{Math.Round(percent):0}%";

        if (SystemVolumeService.TrySetVolume(percent))
        {
            if (_volumeMuted && percent > 0)
            {
                SystemVolumeService.TrySetMute(false);
                _volumeMuted = false;
            }

            MuteButton.Content =
                percent <= 0.5 || _volumeMuted ? "🔇" : "🔊";
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SystemVolumeService.TrySetMute(!_volumeMuted))
            return;

        _volumeMuted = !_volumeMuted;
        _syncingMediaUi = true;
        try
        {
            SyncSystemVolumeUi(force: true);
        }
        finally
        {
            _syncingMediaUi = false;
        }
    }

    private System.Windows.Controls.ComboBox[] PcMetricCombos() =>
    [
        PcMetric1Combo,
        PcMetric2Combo,
        PcMetric3Combo,
        PcMetric4Combo,
        PcMetric5Combo,
        PcMetric6Combo
    ];

    private void InitializePcMetricSelectors()
    {
        _syncingPcMetricUi = true;
        try
        {
            foreach (System.Windows.Controls.ComboBox combo in PcMetricCombos())
            {
                combo.Items.Clear();

                foreach ((int id, string name) in PcMonitorMetricChoices)
                {
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = name,
                        Tag = id.ToString()
                    });
                }
            }

            ApplyPcMetricSelections();
        }
        finally
        {
            _syncingPcMetricUi = false;
        }
    }

    private void ApplyPcMetricSelections()
    {
        if (PcMetric1Combo is null)
            return;

        _syncingPcMetricUi = true;
        try
        {
            System.Windows.Controls.ComboBox[] combos = PcMetricCombos();

            for (int i = 0; i < combos.Length; i++)
            {
                combos[i].SelectedValue =
                    Math.Clamp(_pcMonitorMetricSlots[i], 0, 11).ToString();
            }
        }
        finally
        {
            _syncingPcMetricUi = false;
        }
    }

    private async void PcMetricSlot_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingPcMetricUi || !_uiReady ||
            sender is not System.Windows.Controls.ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int metricId))
        {
            return;
        }

        System.Windows.Controls.ComboBox[] combos = PcMetricCombos();
        int slot = Array.IndexOf(combos, combo);

        if (slot < 0)
            return;

        _pcMonitorMetricSlots[slot] =
            Math.Clamp(metricId, 0, 11);
        SaveAppSettings();

        if (_serial.IsConnected && _serial.SupportsPcMonitor)
        {
            await _serial.SendPcMonitorConfigAsync(
                _pcMonitorConfigName,
                _pcMonitorMetricSlots);
        }
    }

    private async Task PollPcMonitorAsync(bool force = false)
    {
        if ((!_pcMonitorEnabled && !force) || _pcMonitorPolling)
            return;

        _pcMonitorPolling = true;
        try
        {
            PcMonitorSnapshot snapshot =
                await Task.Run(() =>
                    _pcMonitorService.ReadSnapshot(_pcMonitorGpuId));
            _lastPcMonitorSnapshot = snapshot;
            RefreshPcGpuSelector(snapshot);
            ApplyPcMonitorUi(snapshot);
            UpdatePcMonitorConfigSummary(snapshot);

            if (_pcMonitorEnabled &&
                _serial.IsConnected &&
                _serial.SupportsPcMonitor)
            {
                bool configSent =
                    await _serial.SendPcMonitorConfigAsync(
                        _pcMonitorConfigName,
                        _pcMonitorMetricSlots);
                bool telemetrySent =
                    await _serial.SendPcMonitorAsync(
                        snapshot,
                        _pcMonitorMetricSlots);

                if (configSent && telemetrySent)
                {
                    PcMonitorLinkText.Text =
                        _serial.IsBluetoothConnected
                            ? L(
                                "Live · Bluetooth telemetry",
                                "Trực tiếp · dữ liệu Bluetooth")
                            : L(
                                "Live · USB telemetry",
                                "Trực tiếp · dữ liệu USB");
                }
                else
                {
                    PcMonitorLinkText.Text =
                        _serial.IsBluetoothConnected
                            ? L(
                                "Bluetooth telemetry send failed",
                                "Gửi dữ liệu Bluetooth thất bại")
                            : L(
                                "USB telemetry send failed",
                                "Gửi dữ liệu USB thất bại");
                }
            }
            else if (_pcMonitorEnabled && _serial.IsConnected)
            {
                PcMonitorLinkText.Text =
                    L("Firmware does not support PC Monitor yet.",
                      "Firmware chưa hỗ trợ PC Monitor.");
            }
            else if (_pcMonitorEnabled)
            {
                PcMonitorLinkText.Text =
                    L("PC sensors live · RYNOR ONE is offline",
                      "Cảm biến PC đang chạy · RYNOR ONE chưa kết nối");
            }
            else
            {
                PcMonitorLinkText.Text =
                    L("PC Monitor streaming is off", "Đã tắt truyền PC Monitor");
            }
        }
        catch (Exception ex)
        {
            PcMonitorLinkText.Text =
                L($"PC sensors unavailable: {ex.Message}",
                  $"Không đọc được cảm biến PC: {ex.Message}");
            AddLog("WARN", "PCMON", ex.Message);
        }
        finally
        {
            _pcMonitorPolling = false;
        }
    }

    private static string FormatNetworkRate(double mbps)
    {
        if (!double.IsFinite(mbps) || mbps < 0)
            return "--";

        if (mbps < 0.001)
            return "0 Kbps";

        if (mbps < 1)
            return $"{mbps * 1000d:0} Kbps";

        return $"{mbps:0.0} Mbps";
    }

    private void ApplyPcMonitorUi(PcMonitorSnapshot snapshot)
    {
        PcCpuLoadText.Text = $"{snapshot.CpuLoad:0}%";
        PcCpuTempText.Text = snapshot.CpuTemperature.HasValue
            ? $"{snapshot.CpuTemperature.Value:0} °C" : "-- °C";
        PcCpuClockText.Text = snapshot.CpuClockMHz.HasValue
            ? $"{snapshot.CpuClockMHz.Value:0} MHz" : "-- MHz";

        PcGpuLoadText.Text = snapshot.GpuLoad.HasValue
            ? $"{snapshot.GpuLoad.Value:0}%"
            : "--%";
        PcGpuNameText.Text = snapshot.GpuName;
        PcGpuActiveText.Text =
            string.Equals(_pcMonitorGpuId, "auto", StringComparison.OrdinalIgnoreCase)
                ? $"Auto active: {snapshot.GpuName}"
                : $"Pinned: {snapshot.GpuName}";
        PcGpuTempText.Text = snapshot.GpuTemperature.HasValue
            ? $"{snapshot.GpuTemperature.Value:0} °C" : "-- °C";
        PcGpuClockText.Text = snapshot.GpuClockMHz.HasValue
            ? $"{snapshot.GpuClockMHz.Value:0} MHz" : "-- MHz";

        PcRamLoadText.Text = $"{snapshot.MemoryLoad:0}%";
        PcRamDetailText.Text =
            $"{snapshot.MemoryUsedGb:0.0} / {snapshot.MemoryTotalGb:0.0} GB";
        PcNetDownText.Text = FormatNetworkRate(snapshot.NetworkDownloadMbps);
        PcNetUpText.Text = FormatNetworkRate(snapshot.NetworkUploadMbps);
        PcFpsText.Text = snapshot.Fps.HasValue
            ? $"FPS {snapshot.Fps.Value}" : "FPS --";
    }

    private void RefreshPcGpuSelector(PcMonitorSnapshot snapshot)
    {
        if (PcGpuCombo is null)
            return;

        string desired = _pcMonitorGpuId;
        string[] currentIds = PcGpuCombo.Items
            .OfType<ComboBoxItem>()
            .Select(i => i.Tag?.ToString() ?? "")
            .ToArray();

        string[] nextIds =
            new[] { "auto" }
                .Concat(snapshot.AvailableGpus.Select(g => g.Id))
                .ToArray();

        if (currentIds.SequenceEqual(nextIds, StringComparer.OrdinalIgnoreCase))
            return;

        _syncingPcMonitorUi = true;
        try
        {
            PcGpuCombo.Items.Clear();
            PcGpuCombo.Items.Add(new ComboBoxItem
            {
                Content = "Auto · active GPU",
                Tag = "auto"
            });

            foreach (PcGpuInfo gpu in snapshot.AvailableGpus)
            {
                PcGpuCombo.Items.Add(new ComboBoxItem
                {
                    Content = gpu.Load.HasValue
                        ? $"{gpu.Name} · {gpu.Load.Value:0}%"
                        : $"{gpu.Name} · --%",
                    Tag = gpu.Id
                });
            }

            PcGpuCombo.SelectedValue =
                nextIds.Contains(desired, StringComparer.OrdinalIgnoreCase)
                    ? desired
                    : "auto";

            if (PcGpuCombo.SelectedValue?.ToString() is string selected)
                _pcMonitorGpuId = selected;
        }
        finally
        {
            _syncingPcMonitorUi = false;
        }
    }

    private async void PcGpuCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingPcMonitorUi || !_uiReady)
            return;

        if (PcGpuCombo?.SelectedItem is not ComboBoxItem item)
            return;

        _pcMonitorGpuId =
            string.IsNullOrWhiteSpace(item.Tag?.ToString())
                ? "auto"
                : item.Tag!.ToString()!;

        SaveAppSettings();
        await PollPcMonitorAsync(force: true);
    }

    private async void PcMonitorConfigNameText_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!_uiReady || PcMonitorConfigNameText is null)
            return;

        string next = PcMonitorConfigNameText.Text.Trim();
        _pcMonitorConfigName =
            string.IsNullOrWhiteSpace(next)
                ? "MY PC"
                : next;

        SaveAppSettings();
        UpdatePcMonitorConfigSummary(_lastPcMonitorSnapshot);

        if (_serial.IsConnected && _serial.SupportsPcMonitor)
            await _serial.SendPcMonitorConfigAsync(
                _pcMonitorConfigName,
                _pcMonitorMetricSlots);
    }

    private void UpdatePcMonitorConfigSummary(PcMonitorSnapshot? snapshot)
    {
        if (PcMonitorConfigSummaryText is null)
            return;

        string gpu =
            string.Equals(_pcMonitorGpuId, "auto", StringComparison.OrdinalIgnoreCase)
                ? snapshot is null
                    ? "Auto GPU"
                    : $"Auto · {snapshot.GpuName}"
                : snapshot?.GpuName ?? "Selected GPU";

        PcMonitorConfigSummaryText.Text =
            $"{_pcMonitorConfigName} · {gpu}";
    }

    private async void PcMonitorEnabled_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        _pcMonitorEnabled =
            PcMonitorEnabledCheckBox?.IsChecked == true;

        if (_pcMonitorEnabled)
        {
            _pcMonitorTimer.Start();
            await PollPcMonitorAsync(force: true);
        }
        else
        {
            _pcMonitorTimer.Stop();

            if (_serial.IsConnected &&
                _serial.SupportsPcMonitor)
            {
                await _serial.ClearPcMonitorAsync();
            }

            PcMonitorLinkText.Text =
                L("PC Monitor streaming is off", "Đã tắt truyền PC Monitor");
        }

        SaveAppSettings();
    }

    private void PcMonitorIntervalCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PcMonitorIntervalCombo?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int interval) ||
            interval is not (500 or 1000 or 2000))
        {
            return;
        }

        _pcMonitorIntervalMs = interval;
        _pcMonitorTimer.Interval =
            TimeSpan.FromMilliseconds(_pcMonitorIntervalMs);

        if (_uiReady)
            SaveAppSettings();
    }

    private static string ProfileName(int index) =>
        index switch
        {
            0 => "OFFICE",
            1 => "MEDIA",
            2 => "FUSION 360",
            3 => "CUSTOM 4",
            4 => "CUSTOM 5",
            _ => $"PROFILE {index + 1}"
        };

    private string PixelProfileName(int index)
    {
        _pixelProfileCatalog.Normalize();
        index = Math.Clamp(index, 0, _pixelProfileCatalog.Count - 1);
        return _pixelProfileCatalog.Names[index];
    }

    private bool IsPixelProActive =>
        _activeProduct.Driver == DeviceDriverKind.PixelProCdc;

    private void RefreshAutoProfileDefaultSelectors()
    {
        if (AutoProfileDefaultCombo is null)
            return;

        _syncingAutoProfileUi = true;
        try
        {
            AutoProfileDefaultCombo.Items.Clear();

            if (IsPixelProActive)
            {
                _pixelProfileCatalog.Normalize();

                for (int i = 0; i < _pixelProfileCatalog.Count; i++)
                {
                    AutoProfileDefaultCombo.Items.Add(new ComboBoxItem
                    {
                        Content = $"{i + 1:00} · {PixelProfileName(i)}",
                        Tag = i.ToString()
                    });
                }

                AutoProfileDefaultCombo.SelectedValue =
                    Math.Clamp(
                        _autoProfileSettings.DefaultPixelProfile,
                        0,
                        _pixelProfileCatalog.Count - 1)
                    .ToString();

                if (AutoProfileDefaultLayerCombo is not null)
                {
                    AutoProfileDefaultLayerCombo.Visibility =
                        Visibility.Visible;
                    AutoProfileDefaultLayerCombo.SelectedValue =
                        Math.Clamp(
                            _autoProfileSettings.DefaultLayer,
                            0,
                            3)
                        .ToString();
                }
            }
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    AutoProfileDefaultCombo.Items.Add(new ComboBoxItem
                    {
                        Content = ProfileName(i),
                        Tag = i.ToString()
                    });
                }

                AutoProfileDefaultCombo.SelectedValue =
                    Math.Clamp(
                        _autoProfileSettings.DefaultProfile,
                        0,
                        4)
                    .ToString();

                if (AutoProfileDefaultLayerCombo is not null)
                    AutoProfileDefaultLayerCombo.Visibility =
                        Visibility.Collapsed;
            }
        }
        finally
        {
            _syncingAutoProfileUi = false;
        }
    }

    private void ApplyAutoProfileUiState()
    {
        _autoProfileSettings.EnsureNormalized();

        if (AutoProfileEnabledCheckBox is not null)
            AutoProfileEnabledCheckBox.IsChecked = _autoProfileSettings.Enabled;

        RefreshAutoProfileDefaultSelectors();
        RefreshAutoProfileMappingsUi();
        RefreshRunningAppsUi();
    }

    private void AutoProfileEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        _autoProfileSettings.Enabled =
            AutoProfileEnabledCheckBox.IsChecked == true;
        AutoProfileService.Save(_autoProfileSettings);
        _lastAppliedAutoProfile = -1;
        _lastAppliedAutoLayer = -1;
        _lastForegroundAppPath = null;
        PollAutoProfile(force: true);
    }

    private void AutoProfileDefaultCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingAutoProfileUi ||
            AutoProfileDefaultCombo?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int profile))
        {
            return;
        }

        if (IsPixelProActive)
        {
            _autoProfileSettings.DefaultPixelProfile =
                Math.Clamp(
                    profile,
                    0,
                    Math.Max(0, _pixelProfileCatalog.Count - 1));
        }
        else
        {
            _autoProfileSettings.DefaultProfile =
                Math.Clamp(profile, 0, 4);
        }

        if (_uiReady)
        {
            AutoProfileService.Save(_autoProfileSettings);
            _lastAppliedAutoProfile = -1;
            _lastAppliedAutoLayer = -1;
            PollAutoProfile(force: true);
        }
    }

    private void AutoProfileDefaultLayerCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingAutoProfileUi ||
            AutoProfileDefaultLayerCombo?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int layer))
        {
            return;
        }

        _autoProfileSettings.DefaultLayer =
            Math.Clamp(layer, 0, 3);

        if (_uiReady)
        {
            AutoProfileService.Save(_autoProfileSettings);
            _lastAppliedAutoProfile = -1;
            _lastAppliedAutoLayer = -1;
            PollAutoProfile(force: true);
        }
    }

    private void SelectAutoProfileApplication_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("Select Application", "Chọn ứng dụng"),
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            AddAutoProfileMapping(dialog.FileName);
    }

    private async void RefreshRunningApps_Click(
        object sender,
        RoutedEventArgs e) =>
        await RefreshRunningAppsAsync();

    private async Task RefreshRunningAppsAsync()
    {
        try
        {
            _runningApps =
                await Task.Run(AutoProfileService.ScanRunningApplications);
            RefreshRunningAppsUi();
        }
        catch (Exception ex)
        {
            AddLog("WARN", "AUTO", $"Running app scan failed: {ex.Message}");
        }
    }

    private void AddAutoProfileMapping(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        string path;
        try
        {
            path = System.IO.Path.GetFullPath(executablePath);
        }
        catch
        {
            path = executablePath;
        }

        var existing = _autoProfileSettings.Mappings.FirstOrDefault(
            m => AutoProfileService.PathsEqual(m.ExecutablePath, path));

        if (existing is null)
        {
            if (_autoProfileSettings.Mappings.Count >= 10)
            {
                AutoProfileStatusText.Text =
                    L(
                        "Maximum 10 application profiles.",
                        "Tối đa 10 profile ứng dụng.");
                return;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(path);

            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    name = info.FileDescription.Trim();
            }
            catch
            {
            }

            _autoProfileSettings.Mappings.Add(new AutoProfileMapping
            {
                Name =
                    string.IsNullOrWhiteSpace(name)
                        ? "Application"
                        : name,
                ExecutablePath = path,
                ProfileIndex =
                    Math.Clamp(_autoProfileSettings.DefaultProfile, 0, 4),
                PixelProfileIndex =
                    Math.Clamp(
                        _autoProfileSettings.DefaultPixelProfile,
                        0,
                        Math.Max(0, _pixelProfileCatalog.Count - 1)),
                LayerIndex =
                    Math.Clamp(_autoProfileSettings.DefaultLayer, 0, 3)
            });

            AutoProfileService.Save(_autoProfileSettings);
            AddLog("INFO", "AUTO", $"Added app mapping: {path}");
        }

        RefreshAutoProfileMappingsUi();
        RefreshRunningAppsUi();
        _lastAppliedAutoProfile = -1;
        _lastAppliedAutoLayer = -1;
        PollAutoProfile(force: true);
    }

    private System.Windows.Controls.ComboBox CreateProfileSelector(
        int selectedProfile)
    {
        var combo = new System.Windows.Controls.ComboBox
        {
            Width = 160,
            SelectedValuePath = "Tag",
            VerticalAlignment = VerticalAlignment.Center
        };

        for (int i = 0; i < 5; i++)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = ProfileName(i),
                Tag = i.ToString()
            });
        }

        combo.SelectedValue =
            Math.Clamp(selectedProfile, 0, 4).ToString();
        return combo;
    }

    private System.Windows.Controls.ComboBox CreatePixelProfileSelector(
        int selectedProfile)
    {
        _pixelProfileCatalog.Normalize();

        var combo = new System.Windows.Controls.ComboBox
        {
            Width = 154,
            SelectedValuePath = "Tag",
            VerticalAlignment = VerticalAlignment.Center
        };

        for (int i = 0; i < _pixelProfileCatalog.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{i + 1:00} · {PixelProfileName(i)}",
                Tag = i.ToString()
            });
        }

        combo.SelectedValue =
            Math.Clamp(
                selectedProfile,
                0,
                _pixelProfileCatalog.Count - 1)
            .ToString();

        return combo;
    }

    private static System.Windows.Controls.ComboBox CreateLayerSelector(
        int selectedLayer)
    {
        var combo = new System.Windows.Controls.ComboBox
        {
            Width = 72,
            SelectedValuePath = "Tag",
            VerticalAlignment = VerticalAlignment.Center
        };

        for (int layer = 0; layer < 4; layer++)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"L{layer}",
                Tag = layer.ToString()
            });
        }

        combo.SelectedValue =
            Math.Clamp(selectedLayer, 0, 3).ToString();

        return combo;
    }

    private void RefreshAutoProfileMappingsUi()
    {
        if (AutoProfileMappingsPanel is null)
            return;

        AutoProfileMappingsPanel.Children.Clear();

        if (AutoProfileMappingCountText is not null)
        {
            AutoProfileMappingCountText.Text =
                $"{_autoProfileSettings.Mappings.Count} / 10 linked apps";
        }

        if (_autoProfileSettings.Mappings.Count == 0)
        {
            AutoProfileMappingsPanel.Children.Add(new TextBlock
            {
                Text = IsPixelProActive
                    ? L(
                        "Add an app, then choose a PIXEL PRO keymap profile and layer.",
                        "Thêm ứng dụng rồi chọn profile keymap và layer của PIXEL PRO.")
                    : L(
                        "Add an application from the list on the right, then choose one of your existing profiles here.",
                        "Thêm ứng dụng từ danh sách bên phải, sau đó chọn một profile có sẵn tại đây."),
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 4, 0)
            });
            return;
        }

        foreach (AutoProfileMapping mapping in
                 _autoProfileSettings.Mappings.ToArray())
        {
            bool pixel = IsPixelProActive;

            var row = new Border
            {
                Background =
                    TryFindResource("Card2") as System.Windows.Media.Brush,
                BorderBrush =
                    TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(48)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(pixel ? 164 : 178)
            });

            if (pixel)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(82)
                });
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            FrameworkElement icon =
                CreateApplicationIcon(
                    mapping.ExecutablePath,
                    mapping.Name,
                    40);
            grid.Children.Add(icon);

            var text = new StackPanel
            {
                Margin = new Thickness(4, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(new TextBlock
            {
                Text = mapping.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15
            });
            text.Children.Add(new TextBlock
            {
                Text = mapping.ExecutablePath,
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 430,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            System.Windows.Controls.ComboBox profile =
                pixel
                    ? CreatePixelProfileSelector(mapping.PixelProfileIndex)
                    : CreateProfileSelector(mapping.ProfileIndex);

            profile.Tag = mapping;
            profile.Margin = new Thickness(0, 0, 8, 0);
            profile.SelectionChanged += (_, _) =>
            {
                if (profile.Tag is not AutoProfileMapping current ||
                    profile.SelectedItem is not ComboBoxItem selected ||
                    !int.TryParse(
                        selected.Tag?.ToString(),
                        out int index))
                {
                    return;
                }

                if (pixel)
                {
                    current.PixelProfileIndex =
                        Math.Clamp(
                            index,
                            0,
                            Math.Max(0, _pixelProfileCatalog.Count - 1));
                }
                else
                {
                    current.ProfileIndex =
                        Math.Clamp(index, 0, 4);
                }

                AutoProfileService.Save(_autoProfileSettings);
                _lastAppliedAutoProfile = -1;
                _lastAppliedAutoLayer = -1;
                PollAutoProfile(force: true);
            };
            Grid.SetColumn(profile, 2);
            grid.Children.Add(profile);

            int removeColumn = 3;

            if (pixel)
            {
                System.Windows.Controls.ComboBox layer =
                    CreateLayerSelector(mapping.LayerIndex);
                layer.Tag = mapping;
                layer.Margin = new Thickness(0, 0, 8, 0);
                layer.SelectionChanged += (_, _) =>
                {
                    if (layer.Tag is not AutoProfileMapping current ||
                        layer.SelectedItem is not ComboBoxItem selected ||
                        !int.TryParse(
                            selected.Tag?.ToString(),
                            out int index))
                    {
                        return;
                    }

                    current.LayerIndex =
                        Math.Clamp(index, 0, 3);

                    AutoProfileService.Save(_autoProfileSettings);
                    _lastAppliedAutoProfile = -1;
                    _lastAppliedAutoLayer = -1;
                    PollAutoProfile(force: true);
                };

                Grid.SetColumn(layer, 3);
                grid.Children.Add(layer);
                removeColumn = 4;
            }

            var remove = new System.Windows.Controls.Button
            {
                Content = "×",
                Width = 38,
                Height = 38,
                Padding = new Thickness(0),
                Tag = mapping,
                ToolTip = L("Delete", "Xóa"),
                Margin = new Thickness(0)
            };
            remove.Click += (_, _) =>
            {
                if (remove.Tag is not AutoProfileMapping current)
                    return;

                _autoProfileSettings.Mappings.Remove(current);
                AutoProfileService.Save(_autoProfileSettings);
                RefreshAutoProfileMappingsUi();
                RefreshRunningAppsUi();
                _lastAppliedAutoProfile = -1;
                _lastAppliedAutoLayer = -1;
                PollAutoProfile(force: true);
            };
            Grid.SetColumn(remove, removeColumn);
            grid.Children.Add(remove);

            row.Child = grid;
            AutoProfileMappingsPanel.Children.Add(row);
        }
    }

    private void RefreshRunningAppsUi()
    {
        if (RunningAppsPanel is null)
            return;

        RunningAppsPanel.Children.Clear();

        foreach (RunningAppInfo app in _runningApps)
        {
            bool added = _autoProfileSettings.Mappings.Any(
                m => AutoProfileService.PathsEqual(
                    m.ExecutablePath,
                    app.ExecutablePath));

            var row = new Border
            {
                Background =
                    TryFindResource("Card2") as System.Windows.Media.Brush,
                BorderBrush =
                    TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11),
                Margin = new Thickness(0, 0, 0, 9)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(46)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            FrameworkElement icon =
                CreateApplicationIcon(
                    app.ExecutablePath,
                    app.Name,
                    38);
            grid.Children.Add(icon);

            var text = new StackPanel
            {
                Margin = new Thickness(4, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(new TextBlock
            {
                Text = app.Name,
                FontWeight = FontWeights.SemiBold
            });
            text.Children.Add(new TextBlock
            {
                Text = app.ExecutablePath,
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 330,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var add = new System.Windows.Controls.Button
            {
                Content = added ? L("Added", "Đã thêm") : L("Add", "Thêm"),
                IsEnabled = !added,
                Tag = app.ExecutablePath,
                MinWidth = 68,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            add.Click += (_, _) =>
            {
                if (add.Tag is string path)
                    AddAutoProfileMapping(path);
            };
            Grid.SetColumn(add, 2);
            grid.Children.Add(add);

            row.Child = grid;
            RunningAppsPanel.Children.Add(row);
        }

        if (_runningApps.Count == 0)
        {
            RunningAppsPanel.Children.Add(new TextBlock
            {
                Text = L(
                    "No foreground-capable apps found.",
                    "Không tìm thấy ứng dụng có cửa sổ đang chạy."),
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush
            });
        }
    }

    private FrameworkElement CreateApplicationIcon(
        string executablePath,
        string fallbackName,
        double size)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Max(8, size * 0.24)),
            Background =
                TryFindResource("ControlBg") as System.Windows.Media.Brush,
            BorderBrush =
                TryFindResource("Line") as System.Windows.Media.Brush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };

        ImageSource? source = GetApplicationIcon(executablePath);
        if (source is not null)
        {
            border.Child = new System.Windows.Controls.Image
            {
                Source = source,
                Width = Math.Max(20, size - 8),
                Height = Math.Max(20, size - 8),
                Stretch = Stretch.Uniform,
                HorizontalAlignment =
                    System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return border;
        }

        string initial =
            string.IsNullOrWhiteSpace(fallbackName)
                ? "•"
                : fallbackName.Trim()[0].ToString().ToUpperInvariant();

        border.Child = new TextBlock
        {
            Text = initial,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        return border;
    }

    private ImageSource? GetApplicationIcon(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        if (_applicationIconCache.TryGetValue(
                executablePath,
                out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? result = null;

        try
        {
            using Drawing.Icon? icon =
                Drawing.Icon.ExtractAssociatedIcon(executablePath);

            if (icon is not null)
            {
                BitmapSource source =
                    Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                result = source;
            }
        }
        catch
        {
        }

        _applicationIconCache[executablePath] = result;
        return result;
    }

    private void PollAutoProfile(bool force = false)
    {
        if (!_uiReady || AutoProfileStatusText is null)
            return;

        if (!_autoProfileSettings.Enabled)
        {
            AutoProfileStatusText.Text =
                L("Auto Profile is disabled", "Auto Profile đang tắt");
            return;
        }

        RunningAppInfo? app =
            AutoProfileService.GetForegroundApplication();

        if (app is not null &&
            AutoProfileService.PathsEqual(
                app.ExecutablePath,
                Environment.ProcessPath))
        {
            AutoProfileStatusText.Text =
                L(
                    "LumiPad is active · keeping current profile",
                    "LumiPad đang được chọn · giữ nguyên profile");
            return;
        }

        AutoProfileMapping? mapping = app is null
            ? null
            : _autoProfileSettings.Mappings.FirstOrDefault(
                m => AutoProfileService.PathsEqual(
                    m.ExecutablePath,
                    app.ExecutablePath));

        bool pixel = IsPixelProActive;

        int targetProfile = pixel
            ? mapping?.PixelProfileIndex ??
              Math.Clamp(
                  _autoProfileSettings.DefaultPixelProfile,
                  0,
                  Math.Max(0, _pixelProfileCatalog.Count - 1))
            : mapping?.ProfileIndex ??
              Math.Clamp(
                  _autoProfileSettings.DefaultProfile,
                  0,
                  4);

        targetProfile = pixel
            ? Math.Clamp(
                targetProfile,
                0,
                Math.Max(0, _pixelProfileCatalog.Count - 1))
            : Math.Clamp(targetProfile, 0, 4);

        int targetLayer = pixel
            ? Math.Clamp(
                mapping?.LayerIndex ??
                _autoProfileSettings.DefaultLayer,
                0,
                3)
            : 0;

        string appName =
            app?.Name ?? L("Desktop", "Màn hình chính");

        string profileName =
            pixel
                ? PixelProfileName(targetProfile)
                : ProfileName(targetProfile);

        AutoProfileStatusText.Text =
            pixel
                ? mapping is null
                    ? $"{appName} → Default · {profileName} · L{targetLayer}"
                    : $"{appName} → {profileName} · L{targetLayer}"
                : mapping is null
                    ? $"{appName} → Default · {profileName}"
                    : $"{appName} → {profileName}";

        string? foregroundPath = app?.ExecutablePath;

        bool changed =
            force ||
            targetProfile != _lastAppliedAutoProfile ||
            targetLayer != _lastAppliedAutoLayer ||
            !AutoProfileService.PathsEqual(
                foregroundPath,
                _lastForegroundAppPath);

        if (!changed || !_serial.IsConnected)
            return;

        if (pixel && _serial is PixelProCdcLink pixelLink)
        {
            pixelLink.SetProfileLayer(targetProfile, targetLayer);
        }
        else
        {
            _serial.SetActiveProfile(targetProfile);
        }

        _lastAppliedAutoProfile = targetProfile;
        _lastAppliedAutoLayer = targetLayer;
        _lastForegroundAppPath = foregroundPath;

        AddLog(
            "INFO",
            "AUTO",
            pixel
                ? $"PIXEL PRO profile {targetProfile + 1} ({profileName}), layer {targetLayer} for {appName}"
                : $"Profile {targetProfile + 1} ({profileName}) for {appName}");
    }

    private async Task PollLumiActionAsync()
    {
        if (!_uiReady ||
            _actionScripts.Count == 0)
        {
            return;
        }

        foreach (ProductDefinition product in ProductCatalog.All)
        {
            IDeviceLink link =
                LinkFor(product);

            if (!link.IsConnected ||
                !link.SupportsActions)
            {
                continue;
            }

            uint afterSeq =
                _actionEventSeqByProduct.TryGetValue(
                    product.Id,
                    out uint storedSeq)
                    ? storedSeq
                    : 0;

            var actionEvent =
                await link.ReadActionEventAsync(
                    afterSeq);

            if (actionEvent is null)
                continue;

            _actionEventSeqByProduct[product.Id] =
                actionEvent.Value.Seq;

            ActionScriptDefinition? script =
                _actionScripts.FirstOrDefault(
                    s =>
                        s.ActionId ==
                        actionEvent.Value.ActionId);

            if (script is null)
            {
                AddLog(
                    "WARN",
                    product.Name,
                    $"No script assigned to Lumi Action {actionEvent.Value.ActionId}");
                continue;
            }

            if (!_runningActionIds.Add(script.ActionId))
            {
                AddLog(
                    "WARN",
                    product.Name,
                    $"Lumi Action {script.ActionId} ignored because it is already running");
                continue;
            }

            try
            {
                if (SelectedActionScript?.Id == script.Id &&
                    ActionScriptStatusText is not null)
                {
                    ActionScriptStatusText.Text =
                        L(
                            "Triggered from keyboard…",
                            "Đã kích hoạt từ bàn phím…");
                }

                AddLog(
                    "INFO",
                    product.Name,
                    $"Run #{script.ActionId:00} {script.Name} from key position {actionEvent.Value.Position}");

                await ActionScriptEngine.ExecuteAsync(
                    script,
                    step =>
                    {
                        if (SelectedActionScript?.Id != script.Id ||
                            ActionScriptStatusText is null)
                        {
                            return;
                        }

                        Dispatcher.Invoke(() =>
                            ActionScriptStatusText.Text =
                                step);
                    });

                if (SelectedActionScript?.Id == script.Id &&
                    ActionScriptStatusText is not null)
                {
                    ActionScriptStatusText.Text =
                        L(
                            "Completed",
                            "Hoàn tất");
                }
            }
            catch (Exception ex)
            {
                AddLog(
                    "ERROR",
                    product.Name,
                    $"Lumi Action {script.ActionId} failed: {ex.Message}");

                if (SelectedActionScript?.Id == script.Id &&
                    ActionScriptStatusText is not null)
                {
                    ActionScriptStatusText.Text =
                        L(
                            $"Failed: {ex.Message}",
                            $"Lỗi: {ex.Message}");
                }
            }
            finally
            {
                _runningActionIds.Remove(
                    script.ActionId);
            }
        }
    }

    private ActionScriptDefinition? SelectedActionScript =>
        ActionScriptsList?.SelectedItem as ActionScriptDefinition;

    private void RefreshActionScriptsUi(string? selectId = null)
    {
        if (ActionScriptsList is null)
            return;

        string? desired =
            selectId ??
            (ActionScriptsList.SelectedItem as ActionScriptDefinition)?.Id;

        _loadingActionScriptUi = true;
        try
        {
            ActionScriptsList.ItemsSource = null;
            ActionScriptsList.ItemsSource = _actionScripts;

            ActionScriptDefinition? selected =
                _actionScripts.FirstOrDefault(s => s.Id == desired) ??
                _actionScripts.FirstOrDefault();

            ActionScriptsList.SelectedItem = selected;
            LoadSelectedActionScriptUi(selected);
        }
        finally
        {
            _loadingActionScriptUi = false;
        }

        RefreshPixelMacroActionCombo();
        RefreshPixelMainMenuActionChoices();

        if (string.Equals(
                _pixelCurrentCategory,
                "Action",
                StringComparison.OrdinalIgnoreCase))
        {
            RebuildPixelPalette();
        }

        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
    }

    private void LoadSelectedActionScriptUi(ActionScriptDefinition? script)
    {
        if (ActionScriptNameText is null ||
            ActionStepsList is null ||
            ActionEditorTitle is null)
        {
            return;
        }

        _loadingActionScriptUi = true;
        try
        {
            ActionScriptNameText.Text = script?.Name ?? "";
            ActionEditorTitle.Text =
                script?.Name ??
                L(
                    "Select or create a script",
                    "Chọn hoặc tạo một script");

            ActionStepsList.ItemsSource = null;
            ActionStepsList.ItemsSource = script?.Steps;

            if (ActionScriptIdText is not null)
            {
                ActionScriptIdText.Text =
                    script is null || script.ActionId <= 0
                        ? "Lumi Action —"
                        : $"Lumi Action {script.ActionId} · Device Config → Lumi Action → Action {script.ActionId}";
            }

            ActionScriptStatusText.Text =
                script is null
                    ? L("Ready", "Sẵn sàng")
                    : $"{script.Steps.Count} step(s)";
        }
        finally
        {
            _loadingActionScriptUi = false;
        }
    }

    private void ActionScriptsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingActionScriptUi)
            return;

        LoadSelectedActionScriptUi(SelectedActionScript);
    }

    private void NewActionScript_Click(object sender, RoutedEventArgs e)
    {
        int actionId =
            ActionScriptStore.NextAvailableActionId(_actionScripts);

        if (actionId == 0)
        {
            ActionScriptStatusText.Text =
                L("Maximum 32 Lumi Actions.", "Tối đa 32 Lumi Action.");
            return;
        }

        var script = new ActionScriptDefinition
        {
            ActionId = actionId,
            Name = $"Script {_actionScripts.Count + 1}"
        };

        _actionScripts.Add(script);
        ActionScriptStore.Save(_actionScripts);
        RefreshActionScriptsUi(script.Id);
        ActionScriptNameText.Focus();
        ActionScriptNameText.SelectAll();
    }

    private void DeleteActionScript_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        _actionScripts.Remove(script);
        ActionScriptStore.Save(_actionScripts);
        RefreshActionScriptsUi();
    }

    private void ActionScriptNameText_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_loadingActionScriptUi)
            return;

        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        script.Name = string.IsNullOrWhiteSpace(ActionScriptNameText.Text)
            ? "Untitled Script"
            : ActionScriptNameText.Text.Trim();

        ActionEditorTitle.Text = script.Name;
    }

    private void AddActionStep_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
        {
            NewActionScript_Click(sender, e);
            script = SelectedActionScript;
        }

        if (script is null ||
            ActionStepTypeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string type = item.Tag?.ToString() ?? "Delay";
        string value = ActionStepValueText.Text.Trim();

        script.Steps.Add(new ActionScriptStep
        {
            Type = type,
            Value = value
        });

        ActionStepValueText.Clear();
        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex = script.Steps.Count - 1;
    }

    private void RemoveActionStep_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null ||
            ActionStepsList.SelectedIndex < 0 ||
            ActionStepsList.SelectedIndex >= script.Steps.Count)
        {
            return;
        }

        int index = ActionStepsList.SelectedIndex;
        script.Steps.RemoveAt(index);
        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex =
            Math.Min(index, script.Steps.Count - 1);
    }

    private void MoveActionStepUp_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        int index = ActionStepsList.SelectedIndex;

        if (script is null || index <= 0 || index >= script.Steps.Count)
            return;

        (script.Steps[index - 1], script.Steps[index]) =
            (script.Steps[index], script.Steps[index - 1]);

        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex = index - 1;
    }

    private void MoveActionStepDown_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        int index = ActionStepsList.SelectedIndex;

        if (script is null ||
            index < 0 ||
            index >= script.Steps.Count - 1)
        {
            return;
        }

        (script.Steps[index + 1], script.Steps[index]) =
            (script.Steps[index], script.Steps[index + 1]);

        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex = index + 1;
    }

    private void AssignActionKey_Click(
        object sender,
        RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null || script.ActionId <= 0)
        {
            ActionScriptStatusText.Text =
                L(
                    "Create or select an Action first.",
                    "Hãy tạo hoặc chọn một Action trước.");
            return;
        }

        if (_actionKeymapWindow is null ||
            !_actionKeymapWindow.IsLoaded)
        {
            _actionKeymapWindow =
                new ActionKeymapWindow(
                    script.ActionId,
                    script.Name,
                    _language)
                {
                    Owner = this
                };

            _actionKeymapWindow.Closed += (_, _) =>
                _actionKeymapWindow = null;
            _actionKeymapWindow.Show();
        }
        else
        {
            _actionKeymapWindow.SetAction(
                script.ActionId,
                script.Name,
                _language);
            _actionKeymapWindow.Activate();
        }

        ActionScriptStatusText.Text =
            L(
                $"Assign Lumi Action {script.ActionId} to a key in Device Config.",
                $"Gán Lumi Action {script.ActionId} vào phím trong Device Config.");
    }

    private void SaveActionScript_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        script.Name = string.IsNullOrWhiteSpace(ActionScriptNameText.Text)
            ? "Untitled Script"
            : ActionScriptNameText.Text.Trim();

        ActionScriptStore.Save(_actionScripts);
        RefreshActionScriptsUi(script.Id);
        ActionScriptStatusText.Text =
            L("Saved", "Đã lưu");
    }

    private async void RunActionScript_Click(
        object sender,
        RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        try
        {
            ActionScriptStatusText.Text =
                L("Running…", "Đang chạy…");

            await ActionScriptEngine.ExecuteAsync(
                script,
                step => Dispatcher.Invoke(() =>
                    ActionScriptStatusText.Text = step));

            ActionScriptStatusText.Text =
                L("Completed", "Hoàn tất");
        }
        catch (Exception ex)
        {
            ActionScriptStatusText.Text =
                L($"Failed: {ex.Message}", $"Lỗi: {ex.Message}");
            AddLog("ERROR", "SCRIPT", ex.Message);
        }
    }

    private void RgbProfileCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (IsPixelProActive)
            return;

        if (e.AddedItems.Count == 0 ||
            e.AddedItems[0] is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int index))
        {
            return;
        }

        _rgbProfileIndex = Math.Clamp(index, 0, _rgbProfiles.Length - 1);
        var profile = _rgbProfiles[_rgbProfileIndex];

        _rgbEffect = profile.Effect;
        _r = profile.R;
        _g = profile.G;
        _b = profile.B;

        UpdateRgbReadout();
    }

    private void RgbSaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (IsPixelProActive)
        {
            int sourceProfile =
                Math.Clamp(
                    _pixelSelectedProfile,
                    0,
                    _pixelRgbProfiles.Length - 1);

            int targetProfile =
                sourceProfile;

            if (PixelRgbSaveProfileCombo?.SelectedItem is ComboBoxItem targetItem &&
                targetItem.Tag is int selectedTarget)
            {
                targetProfile =
                    Math.Clamp(
                        selectedTarget,
                        0,
                        _pixelProfileCatalog.Count - 1);
            }

            _pixelRgbProfiles[targetProfile] =
                _pixelRgbProfiles[sourceProfile]
                    .ToArray();

            _pixelRgbEffects[targetProfile] =
                _pixelRgbEffects[sourceProfile];

            SaveAppSettings();

            if (_serial is PixelProCdcLink pixel &&
                pixel.IsConnected)
            {
                pixel.SetPixelRgbProfile(
                    targetProfile,
                    _pixelRgbProfiles[targetProfile]);

                pixel.SetPixelRgbEffect(
                    targetProfile,
                    _pixelRgbEffects[targetProfile]);

                pixel.SetPixelRgbSpeed(
                    _pixelRgbSpeed);
            }

            string targetName =
                targetProfile <
                    _pixelProfileCatalog.Names.Length
                    ? _pixelProfileCatalog.Names[targetProfile]
                    : $"Profile {targetProfile + 1}";

            BottomStatus.Text =
                L(
                    $"PIXEL RGB copied to Profile {targetProfile + 1:00} · {targetName}.",
                    $"Đã copy RGB PIXEL vào Profile {targetProfile + 1:00} · {targetName}.");

            return;
        }

        int index = Math.Clamp(_rgbProfileIndex, 0, _rgbProfiles.Length - 1);
        _rgbProfiles[index] = new RgbProfileSetting
        {
            Effect = Math.Clamp(_rgbEffect, 0, 4),
            R = _r,
            G = _g,
            B = _b
        };

        SaveAppSettings();

        if (_serial.IsConnected)
        {
            _serial.SetRgbProfile(
                index,
                _rgbProfiles[index].Effect,
                _rgbProfiles[index].R,
                _rgbProfiles[index].G,
                _rgbProfiles[index].B);
        }

        BottomStatus.Text = L(
            $"RGB profile {index + 1} saved.",
            $"Đã lưu RGB cho profile {index + 1}.");
    }

    private void LedEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        _rgbEnabled = LedEnabled.IsChecked == true;
        SaveAppSettings();
        _serial.SetEnabled(_rgbEnabled);

        if (IsPixelProActive)
            RenderPixelRgbPreview();
    }

    private void BrightnessSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null)
            return;

        int value = (int)Math.Round(e.NewValue);
        BrightnessText.Text = $"{value}%";

        if (_uiReady)
        {
            _rgbBrightness = value;
            SaveAppSettings();
            _serial.SetBrightness(value);

            if (IsPixelProActive)
                RenderPixelRgbPreview();
        }
    }

    private void BuildColorWheel()
    {
        const int width = 220;
        const int height = 185;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            double saturation = 1.0 - y / (double)(height - 1);

            for (int x = 0; x < width; x++)
            {
                double hue = x * 360.0 / (width - 1);
                (byte r, byte g, byte b) = HsvToRgb(hue, saturation, 1.0);

                int p = y * stride + x * 4;
                pixels[p] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96,
            PixelFormats.Bgra32,
            null, pixels, stride);

        bitmap.Freeze();
        ColorWheelImage.Source = bitmap;
        UpdateRgbReadout();
    }

    private void ColorWheelImage_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(ColorWheelImage);

        double width = Math.Max(1.0, ColorWheelImage.ActualWidth);
        double height = Math.Max(1.0, ColorWheelImage.ActualHeight);

        double hue = Math.Clamp(pos.X / width, 0.0, 1.0) * 360.0;
        double saturation = 1.0 - Math.Clamp(pos.Y / height, 0.0, 1.0);

        (_r, _g, _b) = HsvToRgb(hue, saturation, 1.0);
        ApplySelectedRgbColor(true);
    }

    private static (byte r, byte g, byte b) HsvToRgb(
        double hue,
        double saturation,
        double value)
    {
        double c = value * saturation;
        double x = c * (1.0 - Math.Abs((hue / 60.0) % 2.0 - 1.0));
        double m = value - c;

        double r1, g1, b1;

        if (hue < 60)       (r1, g1, b1) = (c, x, 0);
        else if (hue < 120) (r1, g1, b1) = (x, c, 0);
        else if (hue < 180) (r1, g1, b1) = (0, c, x);
        else if (hue < 240) (r1, g1, b1) = (0, x, c);
        else if (hue < 300) (r1, g1, b1) = (x, 0, c);
        else                (r1, g1, b1) = (c, 0, x);

        return (
            (byte)Math.Round((r1 + m) * 255.0),
            (byte)Math.Round((g1 + m) * 255.0),
            (byte)Math.Round((b1 + m) * 255.0));
    }

    private void UpdateRgbReadout()
    {
        if (ColorPreview is not null)
            ColorPreview.Background =
                new SolidColorBrush(MediaColor.FromRgb(_r, _g, _b));

        if (RgbHexText is not null)
            RgbHexText.Text = $"#{_r:X2}{_g:X2}{_b:X2}";

        if (RgbRText is not null) RgbRText.Text = _r.ToString();
        if (RgbGText is not null) RgbGText.Text = _g.ToString();
        if (RgbBText is not null) RgbBText.Text = _b.ToString();
    }

    private void UpdatePixelRgbUi()
    {
        if (!IsPixelProActive ||
            PixelRgbK1 is null)
        {
            return;
        }

        int profile =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                _pixelRgbProfiles.Length - 1);

        if (PixelRgbProfileText is not null)
        {
            string name =
                profile <
                    _pixelProfileCatalog.Names.Length
                    ? _pixelProfileCatalog.Names[profile]
                    : $"Profile {profile + 1}";

            PixelRgbProfileText.Text =
                $"Keymap Profile {profile + 1:00} · {name}";
        }

        System.Windows.Controls.Button[] buttons =
        [
            PixelRgbK1,
            PixelRgbK2,
            PixelRgbK3,
            PixelRgbK4,
            PixelRgbK5,
            PixelRgbK6,
            PixelRgbK7,
            PixelRgbK8
        ];

        for (int key = 0; key < 8; key++)
        {
            PixelRgbColor color =
                _pixelRgbProfiles[profile][key];

            buttons[key].Background =
                new SolidColorBrush(
                    MediaColor.FromRgb(
                        color.R,
                        color.G,
                        color.B));

            buttons[key].Foreground =
                new SolidColorBrush(
                    (color.R * 299 +
                     color.G * 587 +
                     color.B * 114) >
                    150000
                        ? MediaColor.FromRgb(20, 20, 20)
                        : MediaColor.FromRgb(245, 245, 245));

            bool selected =
                _pixelRgbSelectedKey == key;

            buttons[key].BorderBrush =
                new SolidColorBrush(
                    selected
                        ? MediaColor.FromRgb(255, 159, 10)
                        : MediaColor.FromRgb(92, 92, 96));

            buttons[key].BorderThickness =
                new Thickness(
                    selected
                        ? 3
                        : 1);
        }

        if (PixelRgbAllButton is not null)
        {
            bool allSelected =
                _pixelRgbSelectedKey < 0;

            PixelRgbAllButton.BorderBrush =
                new SolidColorBrush(
                    allSelected
                        ? MediaColor.FromRgb(255, 159, 10)
                        : MediaColor.FromRgb(92, 92, 96));

            PixelRgbAllButton.BorderThickness =
                new Thickness(
                    allSelected
                        ? 3
                        : 1);
        }

        int effect =
            _pixelRgbEffects[
                Math.Clamp(
                    profile,
                    0,
                    _pixelRgbEffects.Length - 1)];

        bool dynamic =
            effect != 3;

        if (PixelRgbDynamicPresets is not null)
        {
            PixelRgbDynamicPresets.Visibility =
                dynamic
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        if (PixelRgbStaticModeButton is not null)
        {
            PixelRgbStaticModeButton.BorderBrush =
                TryFindResource(
                    dynamic
                        ? "Line"
                        : "Accent")
                as System.Windows.Media.Brush;

            PixelRgbStaticModeButton.BorderThickness =
                new Thickness(
                    dynamic ? 1 : 2);
        }

        if (PixelRgbDynamicModeButton is not null)
        {
            PixelRgbDynamicModeButton.BorderBrush =
                TryFindResource(
                    dynamic
                        ? "Accent"
                        : "Line")
                as System.Windows.Media.Brush;

            PixelRgbDynamicModeButton.BorderThickness =
                new Thickness(
                    dynamic ? 2 : 1);
        }

        if (PixelRgbDynamicPresets is not null)
        {
            foreach (System.Windows.Controls.Button preset in
                     PixelRgbDynamicPresets.Children
                         .OfType<System.Windows.Controls.Button>())
            {
                bool selectedEffect =
                    dynamic &&
                    int.TryParse(
                        preset.Tag?.ToString(),
                        out int presetEffect) &&
                    presetEffect == effect;

                preset.BorderBrush =
                    TryFindResource(
                        selectedEffect
                            ? "Accent"
                            : "Line")
                    as System.Windows.Media.Brush;

                preset.BorderThickness =
                    new Thickness(
                        selectedEffect ? 2 : 1);
            }
        }

        ResetPixelRgbPreview();
    }

    private void ResetPixelRgbPreview()
    {
        _pixelRgbPreviewClock.Restart();
        RenderPixelRgbPreview();
    }

    private int PixelRgbPreviewFrameIntervalMs()
    {
        int speed =
            Math.Clamp(
                _pixelRgbSpeed,
                10,
                100);

        return 180 +
            (speed - 10) *
            (24 - 180) /
            90;
    }

    private static byte PixelTriangle8(
        long value)
    {
        int phase =
            (int)(
                value &
                0xFF);

        return phase < 128
            ? (byte)(
                phase *
                2)
            : (byte)(
                (255 -
                 phase) *
                2);
    }

    private static PixelRgbColor PixelScaleColor(
        PixelRgbColor color,
        int scale)
    {
        scale =
            Math.Clamp(
                scale,
                0,
                255);

        return new PixelRgbColor(
            (byte)(
                color.R *
                scale /
                255),
            (byte)(
                color.G *
                scale /
                255),
            (byte)(
                color.B *
                scale /
                255));
    }

    private static PixelRgbColor PixelMixColor(
        PixelRgbColor first,
        PixelRgbColor second,
        int mix)
    {
        mix =
            Math.Clamp(
                mix,
                0,
                255);

        int inverse =
            255 -
            mix;

        return new PixelRgbColor(
            (byte)(
                (first.R *
                     inverse +
                 second.R *
                     mix) /
                255),
            (byte)(
                (first.G *
                     inverse +
                 second.G *
                     mix) /
                255),
            (byte)(
                (first.B *
                     inverse +
                 second.B *
                     mix) /
                255));
    }

    private static PixelRgbColor PixelHsvColor(
        double hue,
        double saturation = 1.0,
        double value = 1.0)
    {
        hue %= 360.0;

        if (hue < 0)
            hue += 360.0;

        (byte r, byte g, byte b) =
            HsvToRgb(
                hue,
                Math.Clamp(
                    saturation,
                    0.0,
                    1.0),
                Math.Clamp(
                    value,
                    0.0,
                    1.0));

        return new PixelRgbColor(
            r,
            g,
            b);
    }

    private PixelRgbColor[] BuildPixelRgbPreviewColors(
        int profile,
        int effect,
        long step)
    {
        PixelRgbColor[] baseColors =
            _pixelRgbProfiles[profile];

        var colors =
            new PixelRgbColor[8];

        if (effect == 3)
        {
            for (int key = 0;
                 key < 8;
                 key++)
            {
                colors[key] =
                    baseColors[key];
            }

            return colors;
        }

        if (effect == 0)
        {
            for (int key = 0;
                 key < 8;
                 key++)
            {
                double hue =
                    ((step *
                          512L +
                      key *
                          (65535L /
                           8L)) %
                     65536L) *
                    360.0 /
                    65535.0;

                colors[key] =
                    PixelHsvColor(
                        hue);
            }

            return colors;
        }

        if (effect == 1)
        {
            int phase =
                (int)(
                    step %
                    14L);

            int position =
                phase < 8
                    ? phase
                    : 14 -
                      phase;

            for (int key = 0;
                 key < 8;
                 key++)
            {
                int distance =
                    Math.Abs(
                        key -
                        position);

                int level =
                    distance == 0
                        ? 255
                        : distance == 1
                            ? 72
                            : 12;

                colors[key] =
                    new PixelRgbColor(
                        (byte)(
                            190 *
                            level /
                            255),
                        (byte)(
                            40 *
                            level /
                            255),
                        (byte)(
                            255 *
                            level /
                            255));
            }

            return colors;
        }

        if (effect == 2)
        {
            bool on =
                (step &
                 1L) == 0;

            PixelRgbColor color =
                on
                    ? new PixelRgbColor(
                        255,
                        90,
                        0)
                    : new PixelRgbColor(
                        0,
                        0,
                        0);

            for (int key = 0;
                 key < 8;
                 key++)
            {
                colors[key] =
                    color;
            }

            return colors;
        }

        if (effect == 4)
        {
            int mix =
                (int)(
                    step &
                    0xFFL);

            for (int key = 0;
                 key < 8;
                 key++)
            {
                int next =
                    (key + 1) %
                    8;

                colors[key] =
                    PixelMixColor(
                        baseColors[key],
                        baseColors[next],
                        mix);
            }

            return colors;
        }

        if (effect == 5)
        {
            int head =
                (int)(
                    step %
                    8L);

            for (int key = 0;
                 key < 8;
                 key++)
            {
                int distance =
                    (head +
                     8 -
                     key) %
                    8;

                int level =
                    distance == 0
                        ? 255
                        : distance == 1
                            ? 110
                            : distance == 2
                                ? 42
                                : 6;

                colors[key] =
                    PixelScaleColor(
                        baseColors[key],
                        level);
            }

            return colors;
        }

        if (effect == 6)
        {
            int level =
                24 +
                PixelTriangle8(
                    step *
                    3L) *
                231 /
                255;

            for (int key = 0;
                 key < 8;
                 key++)
            {
                colors[key] =
                    PixelScaleColor(
                        baseColors[key],
                        level);
            }

            return colors;
        }

        if (effect == 7)
        {
            double hue =
                ((step *
                      420L) %
                 65536L) *
                360.0 /
                65535.0;

            PixelRgbColor color =
                PixelHsvColor(
                    hue);

            for (int key = 0;
                 key < 8;
                 key++)
            {
                colors[key] =
                    color;
            }

            return colors;
        }

        if (effect == 8)
        {
            int drop =
                (int)(
                    (step *
                         5L +
                     (step >>
                      2) *
                         3L) %
                    8L);

            int second =
                (drop + 3) %
                8;

            for (int key = 0;
                 key < 8;
                 key++)
            {
                int level =
                    key == drop
                        ? 255
                        : key == second
                            ? 150
                            : 12 +
                              (int)(
                                  (key *
                                       17L +
                                   step *
                                       11L) %
                                  24L);

                colors[key] =
                    new PixelRgbColor(
                        0,
                        (byte)(
                            level *
                            3 /
                            5),
                        (byte)level);
            }

            return colors;
        }

        // Effect 9: Wave.
        for (int key = 0;
             key < 8;
             key++)
        {
            int level =
                18 +
                PixelTriangle8(
                    step *
                        4L +
                    key *
                        28L) *
                237 /
                255;

            colors[key] =
                PixelScaleColor(
                    baseColors[key],
                    level);
        }

        return colors;
    }

    private void RenderPixelRgbPreview()
    {
        if (!_uiReady ||
            !IsPixelProActive ||
            !IsVisible ||
            PixelRgbK1 is null)
        {
            return;
        }

        int profile =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                _pixelRgbProfiles.Length - 1);

        int effect =
            Math.Clamp(
                _pixelRgbEffects[profile],
                0,
                9);

        int frameInterval =
            Math.Max(
                1,
                PixelRgbPreviewFrameIntervalMs());

        long step =
            _pixelRgbPreviewClock.IsRunning
                ? _pixelRgbPreviewClock.ElapsedMilliseconds /
                  frameInterval
                : 0;

        PixelRgbColor[] colors =
            BuildPixelRgbPreviewColors(
                profile,
                effect,
                step);

        System.Windows.Controls.Button[] buttons =
        [
            PixelRgbK1,
            PixelRgbK2,
            PixelRgbK3,
            PixelRgbK4,
            PixelRgbK5,
            PixelRgbK6,
            PixelRgbK7,
            PixelRgbK8
        ];

        int masterScale =
            _rgbEnabled
                ? Math.Clamp(
                    _rgbBrightness,
                    0,
                    100) *
                  255 /
                  100
                : 0;

        for (int key = 0;
             key < 8;
             key++)
        {
            PixelRgbColor display =
                PixelScaleColor(
                    colors[key],
                    masterScale);

            MediaColor media =
                MediaColor.FromRgb(
                    display.R,
                    display.G,
                    display.B);

            buttons[key].Background =
                new SolidColorBrush(
                    media);

            int luminance =
                display.R *
                    299 +
                display.G *
                    587 +
                display.B *
                    114;

            buttons[key].Foreground =
                new SolidColorBrush(
                    luminance >
                        150000
                        ? MediaColor.FromRgb(
                            20,
                            20,
                            20)
                        : MediaColor.FromRgb(
                            245,
                            245,
                            245));
        }
    }

    private void PixelRgbKey_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsPixelProActive ||
            sender is not System.Windows.Controls.Button button ||
            !int.TryParse(
                button.Tag?.ToString(),
                out int key))
        {
            return;
        }

        _pixelRgbSelectedKey =
            Math.Clamp(
                key,
                -1,
                7);

        int profile =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                _pixelRgbProfiles.Length - 1);

        PixelRgbColor selected =
            _pixelRgbProfiles[profile][
                _pixelRgbSelectedKey >= 0
                    ? _pixelRgbSelectedKey
                    : 0];

        _r = selected.R;
        _g = selected.G;
        _b = selected.B;

        UpdateRgbReadout();
        UpdatePixelRgbUi();
    }

    private void ApplySelectedRgbColor(bool send)
    {
        UpdateRgbReadout();

        if (!send ||
            !_uiReady)
        {
            return;
        }

        if (IsPixelProActive)
        {
            int profile =
                Math.Clamp(
                    _pixelSelectedProfile,
                    0,
                    _pixelRgbProfiles.Length - 1);

            PixelRgbColor color =
                new(
                    _r,
                    _g,
                    _b);

            _pixelRgbEffects[profile] = 3;

            if (_serial is PixelProCdcLink modePixel &&
                modePixel.IsConnected)
            {
                modePixel.SetPixelRgbEffect(
                    profile,
                    3);
            }

            if (_pixelRgbSelectedKey < 0)
            {
                for (int key = 0; key < 8; key++)
                    _pixelRgbProfiles[profile][key] = color;

                if (_serial is PixelProCdcLink pixel &&
                    pixel.IsConnected)
                {
                    pixel.SetPixelRgbAll(
                        profile,
                        color);
                }
            }
            else
            {
                int key =
                    Math.Clamp(
                        _pixelRgbSelectedKey,
                        0,
                        7);

                _pixelRgbProfiles[profile][key] =
                    color;

                if (_serial is PixelProCdcLink pixel &&
                    pixel.IsConnected)
                {
                    pixel.SetPixelRgbKey(
                        profile,
                        key,
                        color);
                }
            }

            SaveAppSettings();
            UpdatePixelRgbUi();
            return;
        }

        _rgbAuto = false;
        _rgbEffect = 3;
        SaveAppSettings();
        _serial.SetSolid(_r, _g, _b);
    }

    private void RgbSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not string hex ||
            System.Windows.Media.ColorConverter.ConvertFromString(hex)
                is not MediaColor color)
            return;

        _r = color.R;
        _g = color.G;
        _b = color.B;
        ApplySelectedRgbColor(true);
    }

    private void PixelRgbMode_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsPixelProActive ||
            sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        string mode =
            button.Tag?.ToString() ??
            "Static";

        int profile =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                _pixelRgbEffects.Length - 1);

        int effect =
            string.Equals(
                mode,
                "Dynamic",
                StringComparison.OrdinalIgnoreCase)
                ? (_pixelRgbEffects[profile] == 3
                    ? 0
                    : _pixelRgbEffects[profile])
                : 3;

        _pixelRgbEffects[profile] =
            effect;

        SaveAppSettings();

        if (_serial is PixelProCdcLink pixel &&
            pixel.IsConnected)
        {
            pixel.SetPixelRgbEffect(
                profile,
                effect);

            pixel.SetPixelRgbSpeed(
                _pixelRgbSpeed);
        }

        UpdatePixelRgbUi();
    }

    private void PixelRgbPreset_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsPixelProActive ||
            sender is not System.Windows.Controls.Button button ||
            !int.TryParse(
                button.Tag?.ToString(),
                out int effect))
        {
            return;
        }

        effect =
            Math.Clamp(
                effect,
                0,
                9);

        if (effect == 3)
            effect = 0;

        int profile =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                _pixelRgbEffects.Length - 1);

        _pixelRgbEffects[profile] =
            effect;

        SaveAppSettings();

        if (_serial is PixelProCdcLink pixel &&
            pixel.IsConnected)
        {
            pixel.SetPixelRgbEffect(
                profile,
                effect);

            pixel.SetPixelRgbSpeed(
                _pixelRgbSpeed);
        }

        UpdatePixelRgbUi();
    }

    private void RgbMode_Click(object sender, RoutedEventArgs e)
    {
        string mode = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "Static";

        RgbStaticPresets.Visibility =
            mode == "Static" ? Visibility.Visible : Visibility.Collapsed;
        RgbDynamicPresets.Visibility =
            mode == "Dynamic" ? Visibility.Visible : Visibility.Collapsed;
        RgbReactivePresets.Visibility =
            mode == "Reactive" ? Visibility.Visible : Visibility.Collapsed;

        if (!_uiReady)
            return;

        if (mode == "Static")
        {
            _rgbAuto = false;
            _rgbEffect = 3;
            _serial.SetSolid(_r, _g, _b);
        }
        else if (mode == "Reactive")
        {
            _rgbAuto = false;
            _rgbEffect = 4;
            _serial.SetEffect(4);
        }

        SaveAppSettings();
    }

    private void RgbPreset_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "";

        if (tag == "AUTO")
        {
            _rgbAuto = true;
            SaveAppSettings();
            _serial.SetAutoLayer();
            return;
        }

        if (!int.TryParse(tag, out int effect))
            return;

        _rgbAuto = false;
        _rgbEffect = effect;

        SaveAppSettings();

        if (effect == 3)
            _serial.SetSolid(_r, _g, _b);
        else
            _serial.SetEffect(effect);
    }

    private void RgbSpeedSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        int value = (int)Math.Round(e.NewValue);

        if (RgbSpeedText is not null)
            RgbSpeedText.Text = $"{value}%";

        if (_uiReady)
        {
            if (IsPixelProActive)
            {
                _pixelRgbSpeed = value;
                SaveAppSettings();

                if (_serial is PixelProCdcLink pixel &&
                    pixel.IsConnected)
                {
                    pixel.SetPixelRgbSpeed(
                        value);
                }

                ResetPixelRgbPreview();
            }
            else
            {
                _rgbSpeed = value;
                SaveAppSettings();
                _serial.SetSpeed(value);
            }
        }
    }

    private void SendAllRgb()
    {
        if (!_serial.IsConnected)
            return;

        _rgbEnabled =
            LedEnabled.IsChecked == true;

        _rgbBrightness =
            (int)Math.Round(
                BrightnessSlider.Value);

        if (IsPixelProActive &&
            _serial is PixelProCdcLink pixel)
        {
            int profile =
                Math.Clamp(
                    _pixelSelectedProfile,
                    0,
                    _pixelRgbProfiles.Length - 1);

            pixel.SetPixelRgbProfile(
                profile,
                _pixelRgbProfiles[profile]);

            pixel.SetPixelRgbEffect(
                profile,
                _pixelRgbEffects[profile]);

            pixel.SetPixelRgbSpeed(
                _pixelRgbSpeed);

            pixel.SetEnabled(
                _rgbEnabled);

            pixel.SetBrightness(
                _rgbBrightness);

            return;
        }

        _rgbSpeed =
            (int)Math.Round(
                RgbSpeedSlider.Value);

        // RYNOR ONE: preserve the known-working pre-redesign protocol.
        for (int i = 0; i < _rgbProfiles.Length; i++)
        {
            var profile = _rgbProfiles[i];
            _serial.SetRgbProfile(i, profile.Effect, profile.R, profile.G, profile.B);
        }

        _serial.SetEnabled(_rgbEnabled);
        _serial.SetBrightness(_rgbBrightness);
        _serial.SetSpeed(_rgbSpeed);

        if (_rgbAuto)
            _serial.SetAutoLayer();
        else if (_rgbEffect == 3)
            _serial.SetSolid(_r, _g, _b);
        else
            _serial.SetEffect(_rgbEffect);
    }

    private void SetScreensaverUploadState(
        string state,
        MediaColor color)
    {
        ScreensaverUploadState.Text = state;
        ScreensaverUploadDot.Fill = new SolidColorBrush(color);
    }

    private async void ChooseScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("Choose LumiPad screensaver", "Chọn bảo vệ màn hình LumiPad"),
            Filter = "GIF / Image|*.gif;*.png;*.jpg;*.jpeg;*.bmp|GIF|*.gif|Image|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        if (IsPixelProActive)
            SetPixelHomePreviewMode(false);

        _screensaverMediaPath = dialog.FileName;

        if (IsPixelProActive)
            _pixelScreensaverMediaPath = dialog.FileName;
        else
            _rynorScreensaverMediaPath = dialog.FileName;

        // PIXEL PRO has its own Center/no-upscale policy. Do not mutate the
        // shared RYNOR scale preference when choosing PIXEL media.
        _screensaverSource = "Media";
        if (ScreensaverSourceCombo is not null)
            SelectComboTag(ScreensaverSourceCombo, "Media");
        UpdateScreensaverSourceUi();
        SaveAppSettings();

        if (_serial.IsConnected)
            await _serial.SetScreensaverSourceAsync(false);

        await PrepareScreensaverMediaAsync();
    }

    private ScreensaverScaleMode SelectedScreensaverScaleMode()
    {
        if (IsPixelProActive)
            return _pixelMediaScaleMode;

        if (ScreensaverScaleCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ScreensaverScaleMode>(
                item.Tag?.ToString(),
                true,
                out var mode))
        {
            return mode;
        }

        return ScreensaverScaleMode.Fill;
    }

    private async void ScreensaverScaleCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady)
            return;

        if (IsPixelProActive)
            return;

        _screensaverScaleMode = SelectedScreensaverScaleMode();
        SaveAppSettings();

        if (string.IsNullOrWhiteSpace(_screensaverMediaPath))
            return;

        await PrepareScreensaverMediaAsync();
    }

    private async void PixelMediaSetting_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            !IsPixelProActive)
        {
            return;
        }

        SetPixelHomePreviewMode(false);

        if (PixelGifFpsCombo?.SelectedValue is string fpsText &&
            int.TryParse(fpsText, out int fps))
        {
            _pixelGifMaxFps =
                fps is 20 or 25 or 30 or 40 or 50 or 60
                    ? fps
                    : PixelProScreensaverMediaService.DefaultGifMaxFps;
        }

        _pixelGifMaxDurationSeconds =
            PixelProScreensaverMediaService.DefaultGifDurationSeconds;

        if (PixelMediaScaleCombo?.SelectedValue is string scaleText &&
            Enum.TryParse<ScreensaverScaleMode>(
                scaleText,
                true,
                out ScreensaverScaleMode parsedScale))
        {
            _pixelMediaScaleMode =
                PixelProScreensaverMediaService.NormalizePixelScale(
                    parsedScale);
        }

        SaveAppSettings();

        if (!string.IsNullOrWhiteSpace(_screensaverMediaPath))
        {
            await PrepareScreensaverMediaAsync();
        }
    }

    private async Task PrepareScreensaverMediaAsync()
    {
        if (string.IsNullOrWhiteSpace(_screensaverMediaPath))
            return;

        if (IsPixelProActive)
            SetPixelHomePreviewMode(false);

        SendScreensaverButton.IsEnabled = false;
        ScreensaverSendProgress.Value = 0;
        ScreensaverSendStatus.Text = L("Preparing local media…", "Đang xử lý media trên máy…");

        try
        {
            var scaleMode = SelectedScreensaverScaleMode();

            bool pixel = IsPixelProActive;

            _screensaverAnimation =
                pixel
                    ? await PixelProScreensaverMediaService.LoadAsync(
                        _screensaverMediaPath,
                        _pixelMediaScaleMode,
                        _pixelGifMaxFps,
                        _pixelGifMaxDurationSeconds,
                        _pixelImageJpegQuality)
                    : await ScreensaverMediaService.LoadAsync(
                        _screensaverMediaPath,
                        scaleMode);

            _screensaverPreviewTimer.Interval =
                TimeSpan.FromMilliseconds(
                    pixel
                        ? PixelProScreensaverMediaService.MinFrameIntervalMs
                        : ScreensaverMediaService.MinFrameIntervalMs);

            ScreensaverFileName.Text = _screensaverAnimation.FileName;

            if (_screensaverAnimation.PixelFormat ==
                ScreensaverPixelFormat.Rgb565)
            {
                if (pixel)
                {
                    var jpegInfo =
                        PixelProScreensaverMediaService.GetEncodedJpegInfo(
                            _screensaverAnimation);

                    ScreensaverMediaInfo.Text =
                        L(
                            $"PIXEL image · JPEG · {jpegInfo.Width}×{jpegInfo.Height} · {jpegInfo.StoredBytes / 1024.0:0.#} KB · {_pixelMediaScaleMode}",
                            $"Ảnh PIXEL · JPEG · {jpegInfo.Width}×{jpegInfo.Height} · {jpegInfo.StoredBytes / 1024.0:0.#} KB · {_pixelMediaScaleMode}");
                }
                else
                {
                    // RYNOR ONE keeps the original media description/behavior.
                    ScreensaverMediaInfo.Text =
                        L(
                            $"Static image · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} · RGB565 high quality · {scaleMode}",
                            $"Ảnh tĩnh · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} · RGB565 chất lượng cao · {scaleMode}");
                }

                ScreensaverPreviewImage.Source =
                    CreateRgb565Bitmap(
                        _screensaverAnimation.Frames[0],
                        _screensaverAnimation.Width,
                        _screensaverAnimation.Height);
            }
            else
            {
                var pixelPackedInfo =
                    pixel
                        ? PixelProScreensaverMediaService.GetPackedAnimationInfo(
                            _screensaverAnimation)
                        : default;

                string pixelGifSummary = "";
                if (pixel)
                {
                    double storedKb =
                        pixelPackedInfo.StoredBytes / 1024.0;

                    pixelGifSummary =
                        L(
                            $"PIXEL GIF · {storedKb:0.#} KB · {pixelPackedInfo.StorageWidth}×{pixelPackedInfo.StorageHeight} · {pixelPackedInfo.Fps} FPS · {pixelPackedInfo.ColorMode} · {pixelPackedInfo.ScaleMode}",
                            $"GIF PIXEL · {storedKb:0.#} KB · {pixelPackedInfo.StorageWidth}×{pixelPackedInfo.StorageHeight} · {pixelPackedInfo.Fps} FPS · {pixelPackedInfo.ColorMode} · {pixelPackedInfo.ScaleMode}");
                }

                ScreensaverMediaInfo.Text =
                    pixel
                        ? pixelGifSummary
                        : L(
                            $"{_screensaverAnimation.Frames.Count} stored GIF frames · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} -> 320×172 integer 2× · max {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}",
                            $"{_screensaverAnimation.Frames.Count} khung GIF lưu · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} -> 320×172 phóng nguyên 2× · tối đa {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}");

                ScreensaverPreviewImage.Source =
                    CreateRgb332Bitmap(
                        _screensaverAnimation.Frames[0],
                        _screensaverAnimation.Width,
                        _screensaverAnimation.Height);
            }

            ScreensaverPreviewImage.Visibility = Visibility.Visible;
            ScreensaverPreviewHint.Visibility = Visibility.Collapsed;

            if (PixelHomePreviewHint is not null)
                PixelHomePreviewHint.Visibility =
                    pixel
                        ? Visibility.Collapsed
                        : Visibility.Visible;

            _screensaverPreviewIndex = 0;
            _screensaverPreviewTimer.Stop();
            _screensaverPreviewClock.Reset();

            if (_screensaverAnimation.Frames.Count > 1)
            {
                _screensaverPreviewClock.Restart();
                _screensaverPreviewTimer.Start();
            }

            ScreensaverSendStatus.Text =
                pixel
                    ? L(
                        "Ready to upload.",
                        "Sẵn sàng tải lên.")
                    : L(
                        "Ready. Send once to store the lightweight loop in LumiPad flash.",
                        "Đã sẵn sàng. Gửi một lần để lưu vòng lặp nhẹ vào flash LumiPad.");
            SetScreensaverUploadState(
                L("Ready to upload", "Sẵn sàng tải lên"),
                MediaColor.FromRgb(255, 159, 10));
            SendScreensaverButton.IsEnabled =
                _serial.IsConnected &&
                !string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _screensaverAnimation = null;
            _screensaverPreviewTimer.Stop();
            SetScreensaverUploadState(
                L("Prepare failed", "Xử lý thất bại"),
                MediaColor.FromRgb(255, 69, 58));
            ScreensaverPreviewImage.Source = null;
            ScreensaverPreviewImage.Visibility = Visibility.Collapsed;
            ScreensaverPreviewHint.Visibility = Visibility.Visible;
            ScreensaverSendStatus.Text = L($"Cannot prepare file: {ex.Message}", $"Không thể xử lý tệp: {ex.Message}");
        }
    }

    private async void SendScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_screensaverAnimation is null)
            return;

        if (!_serial.IsConnected)
        {
            ScreensaverSendStatus.Text =
                L("Connect LumiPad first, then send the screensaver.",
                  "Hãy kết nối LumiPad trước rồi mới gửi bảo vệ màn hình.");
            return;
        }

        SendScreensaverButton.IsEnabled = false;
        ScreensaverSendProgress.Value = 0;
        SetScreensaverUploadState(
            L("Uploading…", "Đang tải lên…"),
            MediaColor.FromRgb(255, 159, 10));
        ScreensaverSendStatus.Text =
            IsPixelProActive
                ? L(
                    "Sending frames to PIXEL PRO over native USB…",
                    "Đang gửi frame tới PIXEL PRO qua USB native…")
                : L(
                    "Sending frames… Bluetooth can take a little while.",
                    "Đang gửi frame… Bluetooth có thể mất một lúc.");

        var progress = new Progress<int>(value =>
        {
            ScreensaverSendProgress.Value = value;
            ScreensaverSendStatus.Text = L($"Sending… {value}%", $"Đang gửi… {value}%");
        });

        try
        {
            bool verified = await _serial.SendScreensaverAnimationAsync(
                _screensaverAnimation,
                progress);

            ScreensaverSendProgress.Value = 100;

            if (verified)
            {
                _screensaverSource = "Media";
                if (ScreensaverSourceCombo is not null)
                    SelectComboTag(ScreensaverSourceCombo, "Media");
                UpdateScreensaverSourceUi();
                await _serial.SetScreensaverSourceAsync(false);

                SetScreensaverUploadState(
                    L("Uploaded & verified", "Đã tải lên và xác nhận"),
                    MediaColor.FromRgb(48, 209, 88));
                ScreensaverSendStatus.Text =
                    L("LumiPad confirmed the custom screensaver is ready.",
                      "LumiPad đã xác nhận bảo vệ màn hình tùy chỉnh sẵn sàng.");

                if (IsPixelProActive)
                    await UpdateMemoryUsageAsync();
                SaveAppSettings();
            }
            else
            {
                SetScreensaverUploadState(
                    L("Upload failed", "Tải lên thất bại"),
                    MediaColor.FromRgb(255, 69, 58));

                string? pixelReason =
                    (_serial as PixelProCdcLink)?.LastScreensaverError;

                ScreensaverSendStatus.Text =
                    !string.IsNullOrWhiteSpace(pixelReason)
                        ? pixelReason
                        : L(
                            "LumiPad did not confirm the upload. Check the diagnostic log and firmware version.",
                            "LumiPad chưa xác nhận dữ liệu. Hãy kiểm tra log chẩn đoán và phiên bản firmware.");
            }
        }
        catch (Exception ex)
        {
            AddLog("ERROR", "APP", $"Screensaver upload failed: {ex}");
            SetScreensaverUploadState(
                L("Upload failed", "Tải lên thất bại"),
                MediaColor.FromRgb(255, 69, 58));
            ScreensaverSendStatus.Text = L($"Send failed: {ex.Message}", $"Gửi thất bại: {ex.Message}");
        }
        finally
        {
            SendScreensaverButton.IsEnabled =
                _serial.IsConnected &&
                _screensaverAnimation is not null &&
                !string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal);
        }
    }

    private async void ShowScreensaverNow_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            ScreensaverSendStatus.Text =
                L("Connect LumiPad first.", "Hãy kết nối LumiPad trước.");
            return;
        }

        bool pcMonitor =
            string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal);

        await _serial.ShowScreensaverNowAsync(pcMonitor);

        ScreensaverSendStatus.Text =
            pcMonitor
                ? L("Showing PC Monitor screensaver now.",
                    "Đang bật PC Monitor làm bảo vệ màn hình.")
                : L("Showing the uploaded GIF / image now.",
                    "Đang hiển thị GIF / ảnh đã tải lên ngay.");
    }

    private void ClearScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        _screensaverAnimation = null;
        _screensaverMediaPath = null;

        if (IsPixelProActive)
            _pixelScreensaverMediaPath = null;
        else
            _rynorScreensaverMediaPath = null;

        SaveAppSettings();
        _screensaverPreviewTimer.Stop();
        _screensaverPreviewClock.Reset();
        _serial.ClearScreensaverAnimation();

        if (IsPixelProActive)
        {
            Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    await Task.Delay(300);
                    await UpdateMemoryUsageAsync();
                }));
        }

        ScreensaverPreviewImage.Source = null;
        ScreensaverPreviewImage.Visibility = Visibility.Collapsed;
        ScreensaverPreviewHint.Visibility = Visibility.Visible;

        if (PixelHomePreviewHint is not null)
            PixelHomePreviewHint.Visibility =
                IsPixelProActive
                    ? Visibility.Visible
                    : Visibility.Collapsed;

        ScreensaverFileName.Text = L("No file selected", "Chưa chọn tệp");
        ScreensaverMediaInfo.Text =
            IsPixelProActive
                ? L(
                    "PIXEL PRO keeps the original GIF file and scales it on-device to the 480×320 ILI9486.",
                    "PIXEL PRO giữ nguyên file GIF gốc và scale trực tiếp trên thiết bị ra ILI9486 480×320.")
                : L(
                    $"Converted to a lightweight loop for {_activeProduct.Name}.",
                    $"Tự chuyển thành vòng lặp nhẹ cho {_activeProduct.Name}.");
        ScreensaverSendProgress.Value = 0;
        SetScreensaverUploadState(
            L("Not uploaded", "Chưa tải lên"),
            MediaColor.FromRgb(99, 99, 102));
        ScreensaverSendStatus.Text =
            L("Custom screensaver cleared. No screensaver will be shown until another GIF or image is uploaded.",
              "Đã xóa bảo vệ màn hình. Sẽ không hiện screensaver cho tới khi tải GIF hoặc ảnh mới.");
        SendScreensaverButton.IsEnabled = false;
    }

    private async Task RestoreScreensaverAfterReconnectAsync()
    {
        if (string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal))
        {
            return;
        }

        if (_screensaverAnimation is null || !_serial.IsConnected)
            return;

        try
        {
            string? state = await _serial.GetScreensaverStateAsync();

            if (string.Equals(state, "READY", StringComparison.Ordinal))
            {
                ScreensaverSendProgress.Value = 100;
                SetScreensaverUploadState(
                    L("Stored on keyboard", "Đã lưu trên bàn phím"),
                    MediaColor.FromRgb(48, 209, 88));
                ScreensaverSendStatus.Text =
                    IsPixelProActive
                        ? L(
                            "The original GIF is already stored on PIXEL PRO.",
                            "GIF gốc đã được lưu sẵn trên PIXEL PRO.")
                        : L(
                            "Screensaver is already stored in keyboard flash.",
                            "Bảo vệ màn hình đã có sẵn trong flash của bàn phím.");
                AddLog(
                    "INFO",
                    "SAVER",
                    "Persisted screensaver already READY; reconnect restore skipped");
                return;
            }

            if (!string.Equals(state, "EMPTY", StringComparison.Ordinal) &&
                !string.Equals(state, "ERROR", StringComparison.Ordinal))
            {
                // An unrelated GATT response or temporary read failure must
                // never trigger a large automatic upload.
                AddLog(
                    "WARN",
                    "SAVER",
                    $"Saver state unavailable ({state ?? "unknown"}); automatic restore skipped");

                ScreensaverSendStatus.Text =
                    L("Could not verify keyboard screensaver; no restore was attempted.",
                      "Không xác minh được bảo vệ màn hình trên bàn phím; không tự tải lại.");
                return;
            }

            AddLog(
                "INFO",
                "SAVER",
                $"Keyboard reported {state}; restoring local screensaver");

            var progress = new Progress<int>(value =>
            {
                ScreensaverSendProgress.Value = value;
                ScreensaverSendStatus.Text =
                    L($"Restoring screensaver… {value}%",
                      $"Đang khôi phục bảo vệ màn hình… {value}%");
            });

            bool verified = await _serial.SendScreensaverAnimationAsync(
                _screensaverAnimation,
                progress);

            if (verified)
            {
                ScreensaverSendProgress.Value = 100;
                SetScreensaverUploadState(
                    L("Uploaded & verified", "Đã tải lên và xác nhận"),
                    MediaColor.FromRgb(48, 209, 88));
                ScreensaverSendStatus.Text =
                    IsPixelProActive
                        ? L(
                            "PIXEL PRO screensaver restored after reconnect.",
                            "Đã khôi phục bảo vệ màn hình PIXEL PRO sau khi kết nối lại.")
                        : L(
                            "Custom screensaver restored because keyboard flash was empty.",
                            "Đã khôi phục bảo vệ màn hình vì flash bàn phím đang trống.");
            }
        }
        catch (Exception ex)
        {
            AddLog(
                "WARN",
                "SAVER",
                $"Automatic screensaver state check failed: {ex.Message}");
            // Keep the keyboard connection alive even if state verification fails.
        }
    }

    private void OpenShopee_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://shopee.vn/lumi3d.hn",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AddLog("WARN", "SHOP", $"Open Shopee failed: {ex.Message}");
        }
    }

    private static PixelProKeyChoice[] CreatePixelKeyChoices()
    {
        var choices = new List<PixelProKeyChoice>();

        for (int i = 0; i < 26; i++)
        {
            choices.Add(
                new(
                    ((char)('A' + i)).ToString(),
                    PixelProKeyBindingType.Keyboard,
                    (ushort)(4 + i),
                    0,
                    "Basic"));
        }

        string[] digits =
            ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];

        for (int i = 0; i < digits.Length; i++)
        {
            choices.Add(
                new(
                    digits[i],
                    PixelProKeyBindingType.Keyboard,
                    (ushort)(30 + i),
                    0,
                    "Basic"));
        }

        choices.AddRange(
        [
            new("Enter", PixelProKeyBindingType.Keyboard, 40, 0, "Basic"),
            new("Esc", PixelProKeyBindingType.Keyboard, 41, 0, "Basic"),
            new("Backspace", PixelProKeyBindingType.Keyboard, 42, 0, "Basic"),
            new("Tab", PixelProKeyBindingType.Keyboard, 43, 0, "Basic"),
            new("Space", PixelProKeyBindingType.Keyboard, 44, 0, "Basic"),
            new("-", PixelProKeyBindingType.Keyboard, 45, 0, "Basic"),
            new("=", PixelProKeyBindingType.Keyboard, 46, 0, "Basic"),
            new("[", PixelProKeyBindingType.Keyboard, 47, 0, "Basic"),
            new("]", PixelProKeyBindingType.Keyboard, 48, 0, "Basic"),
            new("\\", PixelProKeyBindingType.Keyboard, 49, 0, "Basic"),
            new(";", PixelProKeyBindingType.Keyboard, 51, 0, "Basic"),
            new("Quote", PixelProKeyBindingType.Keyboard, 52, 0, "Basic"),
            new(",", PixelProKeyBindingType.Keyboard, 54, 0, "Basic"),
            new(".", PixelProKeyBindingType.Keyboard, 55, 0, "Basic"),
            new("/", PixelProKeyBindingType.Keyboard, 56, 0, "Basic"),
            new("Caps", PixelProKeyBindingType.Keyboard, 57, 0, "Basic"),
            new("F1", PixelProKeyBindingType.Keyboard, 58, 0, "Basic"),
            new("F2", PixelProKeyBindingType.Keyboard, 59, 0, "Basic"),
            new("F3", PixelProKeyBindingType.Keyboard, 60, 0, "Basic"),
            new("F4", PixelProKeyBindingType.Keyboard, 61, 0, "Basic"),
            new("F5", PixelProKeyBindingType.Keyboard, 62, 0, "Basic"),
            new("F6", PixelProKeyBindingType.Keyboard, 63, 0, "Basic"),
            new("F7", PixelProKeyBindingType.Keyboard, 64, 0, "Basic"),
            new("F8", PixelProKeyBindingType.Keyboard, 65, 0, "Basic"),
            new("F9", PixelProKeyBindingType.Keyboard, 66, 0, "Basic"),
            new("F10", PixelProKeyBindingType.Keyboard, 67, 0, "Basic"),
            new("F11", PixelProKeyBindingType.Keyboard, 68, 0, "Basic"),
            new("F12", PixelProKeyBindingType.Keyboard, 69, 0, "Basic"),
            new("PrtSc", PixelProKeyBindingType.Keyboard, 70, 0, "Basic"),
            new("Insert", PixelProKeyBindingType.Keyboard, 73, 0, "Basic"),
            new("Home", PixelProKeyBindingType.Keyboard, 74, 0, "Basic"),
            new("PgUp", PixelProKeyBindingType.Keyboard, 75, 0, "Basic"),
            new("Delete", PixelProKeyBindingType.Keyboard, 76, 0, "Basic"),
            new("End", PixelProKeyBindingType.Keyboard, 77, 0, "Basic"),
            new("PgDn", PixelProKeyBindingType.Keyboard, 78, 0, "Basic"),
            new("Right", PixelProKeyBindingType.Keyboard, 79, 0, "Basic"),
            new("Left", PixelProKeyBindingType.Keyboard, 80, 0, "Basic"),
            new("Down", PixelProKeyBindingType.Keyboard, 81, 0, "Basic"),
            new("Up", PixelProKeyBindingType.Keyboard, 82, 0, "Basic"),

            new("Play / Pause", PixelProKeyBindingType.Consumer, 0x00CD, 0, "Media"),
            new("Next Track", PixelProKeyBindingType.Consumer, 0x00B5, 0, "Media"),
            new("Previous", PixelProKeyBindingType.Consumer, 0x00B6, 0, "Media"),
            new("Stop", PixelProKeyBindingType.Consumer, 0x00B7, 0, "Media"),
            new("Mute", PixelProKeyBindingType.Consumer, 0x00E2, 0, "Media"),
            new("Volume +", PixelProKeyBindingType.Consumer, 0x00E9, 0, "Media"),
            new("Volume -", PixelProKeyBindingType.Consumer, 0x00EA, 0, "Media"),
            new("Brightness +", PixelProKeyBindingType.Consumer, 0x006F, 0, "Media"),
            new("Brightness -", PixelProKeyBindingType.Consumer, 0x0070, 0, "Media"),
            new("Browser Back", PixelProKeyBindingType.Consumer, 0x0224, 0, "Media"),
            new("Browser Forward", PixelProKeyBindingType.Consumer, 0x0225, 0, "Media"),
            new("Refresh", PixelProKeyBindingType.Consumer, 0x0227, 0, "Media"),
            new("Browser Home", PixelProKeyBindingType.Consumer, 0x0223, 0, "Media"),
            new("Calculator", PixelProKeyBindingType.Consumer, 0x0192, 0, "Media"),

            new("Ctrl", PixelProKeyBindingType.Keyboard, 0, 0x01, "Modifiers"),
            new("Shift", PixelProKeyBindingType.Keyboard, 0, 0x02, "Modifiers"),
            new("Alt", PixelProKeyBindingType.Keyboard, 0, 0x04, "Modifiers"),
            new("Win", PixelProKeyBindingType.Keyboard, 0, 0x08, "Modifiers"),

            new("Transparent", PixelProKeyBindingType.Transparent, 0, 0, "Special"),
            new("Disabled", PixelProKeyBindingType.Disabled, 0, 0, "Special")
        ]);

        for (byte layer = 0; layer < 4; layer++)
        {
            choices.Add(
                new(
                    $"MO({layer})",
                    PixelProKeyBindingType.Layer,
                    layer,
                    (byte)PixelProLayerAction.Momentary,
                    "Layers"));
            choices.Add(
                new(
                    $"TG({layer})",
                    PixelProKeyBindingType.Layer,
                    layer,
                    (byte)PixelProLayerAction.Toggle,
                    "Layers"));
            choices.Add(
                new(
                    $"TO({layer})",
                    PixelProKeyBindingType.Layer,
                    layer,
                    (byte)PixelProLayerAction.To,
                    "Layers"));
        }

        for (byte macro = 0; macro < 20; macro++)
        {
            choices.Add(
                new(
                    $"M{macro + 1}",
                    PixelProKeyBindingType.Macro,
                    macro,
                    0,
                    "Macro"));
        }

        return choices.ToArray();
    }

    private void BuildPixelProKeymapUi()
    {
        if (_pixelViaUiBuilt || PixelViaKeyboardGrid is null)
            return;

        _pixelViaUiBuilt = true;
        PixelViaKeyboardGrid.Children.Clear();
        _pixelViaKeys.Clear();

        for (int i = 0; i < 8; i++)
        {
            int index = i;

            var mainText = new TextBlock
            {
                Text = $"K{i + 1}",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            var subText = new TextBlock
            {
                Text = "—",
                FontSize = 11,
                Foreground = TryFindResource("Muted") as System.Windows.Media.Brush,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var content = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(mainText);
            content.Children.Add(subText);

            var button = new System.Windows.Controls.Button
            {
                Tag = index,
                Width = 126,
                Height = 72,
                Margin = new Thickness(5),
                Padding = new Thickness(8),
                Background = TryFindResource("Card2") as System.Windows.Media.Brush,
                BorderBrush = TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1),
                Content = content
            };

            button.Click += PixelViaKey_Click;
            PixelViaKeyboardGrid.Children.Add(button);

            _pixelViaKeys.Add(
                new PixelViaKeyVisual
                {
                    Index = index,
                    Button = button,
                    MainText = mainText,
                    SubText = subText
                });
        }

        RefreshPixelProfileCombo();
        RebuildPixelPalette();
        UpdatePixelLayerButtons();
        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
        BuildPixelMacroUi();
    }

    private void RefreshPixelProfileCombo()
    {
        if (PixelProfileCombo is null)
            return;

        _pixelProfileCatalog.Normalize();
        _pixelSelectedProfile =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                _pixelProfileCatalog.Count - 1);

        _pixelViaUpdating = true;
        try
        {
            PixelProfileCombo.Items.Clear();

            for (int i = 0; i < _pixelProfileCatalog.Count; i++)
            {
                PixelProfileCombo.Items.Add(
                    new ComboBoxItem
                    {
                        Content =
                            $"{i + 1:00} · {_pixelProfileCatalog.Names[i]}",
                        Tag = i
                    });
            }

            PixelProfileCombo.SelectedIndex =
                _pixelSelectedProfile;

            RefreshPixelRgbSaveProfileCombo(
                _pixelSelectedProfile);

            if (PixelProfileRenameTextBox is not null)
            {
                PixelProfileRenameTextBox.Visibility =
                    Visibility.Collapsed;
            }

            PixelProfileCombo.Visibility =
                Visibility.Visible;
            _pixelProfileRenameActive = false;
        }
        finally
        {
            _pixelViaUpdating = false;
        }
    }

    private void RefreshPixelRgbSaveProfileCombo(
        int selectedProfile)
    {
        if (PixelRgbSaveProfileCombo is null)
            return;

        int selected =
            Math.Clamp(
                selectedProfile,
                0,
                Math.Max(
                    0,
                    _pixelProfileCatalog.Count - 1));

        PixelRgbSaveProfileCombo.Items.Clear();

        for (int profile = 0;
             profile < _pixelProfileCatalog.Count;
             profile++)
        {
            PixelRgbSaveProfileCombo.Items.Add(
                new ComboBoxItem
                {
                    Content =
                        $"{profile + 1:00} · {_pixelProfileCatalog.Names[profile]}",
                    Tag =
                        profile
                });
        }

        PixelRgbSaveProfileCombo.SelectedIndex =
            selected;
    }

    private void SetPixelProfileDefaults(
        int profile)
    {
        profile =
            Math.Clamp(
                profile,
                0,
                19);

        PixelProKeyBinding[][] defaults =
            CreateDefaultPixelProfileLayers();

        for (int layer = 0; layer < 4; layer++)
        {
            _pixelProfileMaps[profile][layer] =
                defaults[layer].ToArray();

            _pixelLayerLoaded[profile, layer] =
                true;
        }

        _pixelRgbProfiles[profile] =
            CreateDefaultPixelRgbProfile(
                profile);

        _pixelRgbEffects[profile] = 3;

        _pixelModifierPositions.ResetProfile(
            profile);
    }

    private void ReindexAutoProfilesAfterPixelDelete(
        int removedProfile,
        int newCount)
    {
        int fallback =
            Math.Clamp(
                removedProfile,
                0,
                Math.Max(0, newCount - 1));

        if (_autoProfileSettings.DefaultPixelProfile ==
            removedProfile)
        {
            _autoProfileSettings.DefaultPixelProfile =
                fallback;
        }
        else if (_autoProfileSettings.DefaultPixelProfile >
                 removedProfile)
        {
            _autoProfileSettings.DefaultPixelProfile--;
        }

        foreach (AutoProfileMapping mapping in
                 _autoProfileSettings.Mappings)
        {
            if (mapping.PixelProfileIndex ==
                removedProfile)
            {
                mapping.PixelProfileIndex =
                    fallback;
            }
            else if (mapping.PixelProfileIndex >
                     removedProfile)
            {
                mapping.PixelProfileIndex--;
            }

            mapping.PixelProfileIndex =
                Math.Clamp(
                    mapping.PixelProfileIndex,
                    0,
                    Math.Max(0, newCount - 1));
        }

        AutoProfileService.Save(
            _autoProfileSettings);
    }

    private async void PixelAddProfile_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_pixelProfileCatalog.Count >= 20)
        {
            PixelKeymapStatusText.Text =
                L(
                    "Maximum 20 keymap profiles.",
                    "Tối đa 20 profile keymap.");
            return;
        }

        int created =
            _pixelProfileCatalog.Count;

        _pixelProfileCatalog.Count++;
        _pixelProfileCatalog.Names[created] =
            $"Profile {created + 1}";

        SetPixelProfileDefaults(
            created);

        PixelProProfileStore.Save(
            _pixelProfileCatalog);

        PixelProKeyEditorUiStore.Save(
            _pixelModifierPositions);

        SaveAppSettings();

        _pixelSelectedProfile =
            created;

        RefreshPixelProfileCombo();

        if (_serial is PixelProCdcLink pixel &&
            pixel.IsConnected)
        {
            for (int layer = 0; layer < 4; layer++)
            {
                await pixel.SetKeymapAsync(
                    created,
                    layer,
                    _pixelProfileMaps[created][layer]);
            }

            pixel.SetPixelRgbProfile(
                created,
                _pixelRgbProfiles[created]);

            pixel.SetPixelRgbEffect(
                created,
                _pixelRgbEffects[created]);

            pixel.SetPixelRgbSpeed(
                _pixelRgbSpeed);

            pixel.SetProfileLayer(
                created,
                _pixelSelectedLayer);
        }

        _pixelRgbSelectedKey = -1;
        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
        UpdatePixelRgbUi();
        RefreshAutoProfileMappingsUi();
        RefreshAutoProfileDefaultSelectors();

        PixelKeymapStatusText.Text =
            L(
                $"Profile {created + 1} created with defaults.",
                $"Đã tạo Profile {created + 1} với cấu hình mặc định.");
    }

    private async void PixelRemoveProfile_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_pixelProfileCatalog.Count <= 1)
        {
            PixelKeymapStatusText.Text =
                L(
                    "At least one keymap profile is required.",
                    "Cần giữ lại ít nhất một profile keymap.");
            return;
        }

        if (_serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected)
        {
            PixelKeymapStatusText.Text =
                L(
                    "Connect PIXEL PRO before deleting a profile so the remaining profiles can be shifted safely.",
                    "Hãy kết nối PIXEL PRO trước khi xoá profile để dồn các profile còn lại chính xác.");
            return;
        }

        int oldCount =
            _pixelProfileCatalog.Count;

        int removed =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                oldCount - 1);

        PixelKeymapStatusText.Text =
            L(
                $"Deleting Profile {removed + 1}…",
                $"Đang xoá Profile {removed + 1}…");

        // Load the source profiles before shifting them down.
        for (int profile = removed + 1;
             profile < oldCount;
             profile++)
        {
            for (int layer = 0;
                 layer < 4;
                 layer++)
            {
                if (!_pixelLayerLoaded[
                        profile,
                        layer])
                {
                    await LoadPixelLayerAsync(
                        profile,
                        layer);
                }
            }

            PixelRgbColor[]? colors =
                await pixel.GetPixelRgbProfileAsync(
                    profile);

            if (colors is { Length: 8 })
            {
                _pixelRgbProfiles[profile] =
                    colors.ToArray();
            }
        }

        // Remove exactly the selected profile. Everything after it keeps its
        // relative order and shifts up by one slot.
        for (int destination = removed;
             destination < oldCount - 1;
             destination++)
        {
            int source =
                destination + 1;

            _pixelProfileCatalog.Names[destination] =
                _pixelProfileCatalog.Names[source];

            for (int layer = 0;
                 layer < 4;
                 layer++)
            {
                _pixelProfileMaps[destination][layer] =
                    _pixelProfileMaps[source][layer]
                        .ToArray();

                _pixelLayerLoaded[
                    destination,
                    layer] =
                    true;

                await pixel.SetKeymapAsync(
                    destination,
                    layer,
                    _pixelProfileMaps[destination][layer]);
            }

            _pixelRgbProfiles[destination] =
                _pixelRgbProfiles[source]
                    .ToArray();

            _pixelRgbEffects[destination] =
                _pixelRgbEffects[source];

            pixel.SetPixelRgbProfile(
                destination,
                _pixelRgbProfiles[destination]);

            pixel.SetPixelRgbEffect(
                destination,
                _pixelRgbEffects[destination]);
        }

        int vacated =
            oldCount - 1;

        SetPixelProfileDefaults(
            vacated);

        _pixelProfileCatalog.Names[vacated] =
            $"Profile {vacated + 1}";

        for (int layer = 0;
             layer < 4;
             layer++)
        {
            await pixel.SetKeymapAsync(
                vacated,
                layer,
                _pixelProfileMaps[vacated][layer]);
        }

        pixel.SetPixelRgbProfile(
            vacated,
            _pixelRgbProfiles[vacated]);

        pixel.SetPixelRgbEffect(
            vacated,
            _pixelRgbEffects[vacated]);

        _pixelModifierPositions.RemoveProfileAndShift(
            removed,
            oldCount);

        _pixelProfileCatalog.Count =
            oldCount - 1;

        ReindexAutoProfilesAfterPixelDelete(
            removed,
            _pixelProfileCatalog.Count);

        PixelProProfileStore.Save(
            _pixelProfileCatalog);

        PixelProKeyEditorUiStore.Save(
            _pixelModifierPositions);

        SaveAppSettings();

        _pixelSelectedProfile =
            Math.Min(
                removed,
                _pixelProfileCatalog.Count - 1);

        _pixelRgbSelectedKey = -1;

        RefreshPixelProfileCombo();

        pixel.SetProfileLayer(
            _pixelSelectedProfile,
            _pixelSelectedLayer);

        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
        UpdatePixelRgbUi();
        RefreshAutoProfileMappingsUi();
        RefreshAutoProfileDefaultSelectors();

        PixelKeymapStatusText.Text =
            L(
                $"Deleted Profile {removed + 1}. Now editing Profile {_pixelSelectedProfile + 1}.",
                $"Đã xoá Profile {removed + 1}. Hiện đang chỉnh Profile {_pixelSelectedProfile + 1}.");
    }

    private void PixelRenameProfile_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (PixelProfileCombo is null ||
            PixelProfileRenameTextBox is null)
        {
            return;
        }

        _pixelProfileRenameActive =
            true;

        PixelProfileCombo.Visibility =
            Visibility.Collapsed;

        PixelProfileRenameTextBox.Text =
            _pixelProfileCatalog.Names[
                _pixelSelectedProfile];

        PixelProfileRenameTextBox.Visibility =
            Visibility.Visible;

        PixelProfileRenameTextBox.Focus();
        PixelProfileRenameTextBox.SelectAll();
    }

    private void PixelProfileRenameTextBox_KeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (!_pixelProfileRenameActive)
            return;

        if (e.Key == Key.Enter)
        {
            CommitPixelProfileRename();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelPixelProfileRename();
            e.Handled = true;
        }
    }

    private void PixelProfileRenameTextBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (_pixelProfileRenameActive)
            CommitPixelProfileRename();
    }

    private void CancelPixelProfileRename()
    {
        _pixelProfileRenameActive =
            false;

        if (PixelProfileRenameTextBox is not null)
        {
            PixelProfileRenameTextBox.Visibility =
                Visibility.Collapsed;
        }

        if (PixelProfileCombo is not null)
        {
            PixelProfileCombo.Visibility =
                Visibility.Visible;
            PixelProfileCombo.Focus();
        }
    }

    private void CommitPixelProfileRename()
    {
        if (!_pixelProfileRenameActive ||
            PixelProfileRenameTextBox is null)
        {
            return;
        }

        string name =
            PixelProfileRenameTextBox.Text
                .Trim();

        if (string.IsNullOrWhiteSpace(
                name))
        {
            name =
                $"Profile {_pixelSelectedProfile + 1}";
        }

        if (name.Length > 32)
            name = name[..32];

        _pixelProfileCatalog.Names[
            _pixelSelectedProfile] =
            name;

        PixelProProfileStore.Save(
            _pixelProfileCatalog);

        _pixelProfileRenameActive =
            false;

        RefreshPixelProfileCombo();
        RefreshPixelMainMenuUi();
        RefreshAutoProfileMappingsUi();
        RefreshAutoProfileDefaultSelectors();
        UpdatePixelRgbUi();

        PixelKeymapStatusText.Text =
            L(
                $"Profile {_pixelSelectedProfile + 1} renamed to {name}.",
                $"Đã đổi tên Profile {_pixelSelectedProfile + 1} thành {name}.");
    }

    private async void PixelProfileCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_pixelViaUpdating ||
            PixelProfileCombo?.SelectedItem is not ComboBoxItem item ||
            item.Tag is not int profile)
        {
            return;
        }

        _pixelSelectedProfile =
            Math.Clamp(
                profile,
                0,
                _pixelProfileCatalog.Count - 1);

        if (_serial is PixelProCdcLink pixel &&
            pixel.IsConnected)
        {
            for (int layer = 0;
                 layer < 4;
                 layer++)
            {
                if (!_pixelLayerLoaded[
                        _pixelSelectedProfile,
                        layer])
                {
                    await LoadPixelLayerAsync(
                        _pixelSelectedProfile,
                        layer);
                }
            }

            pixel.SetProfileLayer(
                _pixelSelectedProfile,
                _pixelSelectedLayer);

            PixelRgbColor[]? colors =
                await pixel.GetPixelRgbProfileAsync(
                    _pixelSelectedProfile);

            if (colors is { Length: 8 })
            {
                _pixelRgbProfiles[
                    _pixelSelectedProfile] =
                    colors.ToArray();
            }
        }

        _pixelRgbSelectedKey = -1;

        if (PixelRgbSaveProfileCombo is not null &&
            _pixelSelectedProfile <
                PixelRgbSaveProfileCombo.Items.Count)
        {
            PixelRgbSaveProfileCombo.SelectedIndex =
                _pixelSelectedProfile;
        }

        PixelRgbColor firstColor =
            _pixelRgbProfiles[
                _pixelSelectedProfile][0];

        _r = firstColor.R;
        _g = firstColor.G;
        _b = firstColor.B;

        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
        UpdateRgbReadout();
        UpdatePixelRgbUi();
        RefreshPixelMainMenuUi();
        SaveAppSettings();

        PixelKeymapStatusText.Text =
            L(
                $"Profile {_pixelSelectedProfile + 1} · Layer {_pixelSelectedLayer}",
                $"Profile {_pixelSelectedProfile + 1} · Layer {_pixelSelectedLayer}");
    }

    private void RebuildPixelPalette()
    {
        if (PixelKeyPalettePanel is null)
            return;

        PixelKeyPalettePanel.Children.Clear();

        IEnumerable<PixelProKeyChoice> items =
            string.Equals(
                _pixelCurrentCategory,
                "Action",
                StringComparison.OrdinalIgnoreCase)
                ? _actionScripts
                    .Where(x => x.ActionId is >= 1 and <= 32)
                    .OrderBy(x => x.ActionId)
                    .Select(x =>
                        new PixelProKeyChoice(
                            $"A{x.ActionId:00} · {x.Name}",
                            PixelProKeyBindingType.Action,
                            (ushort)x.ActionId,
                            0,
                            "Action"))
                : PixelKeyChoices.Where(
                    x => string.Equals(
                        x.Category,
                        _pixelCurrentCategory,
                        StringComparison.OrdinalIgnoreCase));

        foreach (PixelProKeyChoice choice in items)
        {
            var button = new System.Windows.Controls.Button
            {
                Tag = choice,
                Content = choice.Label,
                MinWidth = 74,
                Height = 38,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(3),
                Background = TryFindResource("Card2") as System.Windows.Media.Brush,
                BorderBrush = TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1)
            };

            button.Click += PixelPaletteButton_Click;
            PixelKeyPalettePanel.Children.Add(button);
        }

        if (PixelPaletteTitleText is not null)
            PixelPaletteTitleText.Text =
                _pixelCurrentCategory.ToUpperInvariant();
    }

    private void PixelViaKey_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not int index)
            return;

        _pixelSelectedKey = Math.Clamp(index, 0, 7);
        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
    }

    private async void PixelLayerButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not string layerText ||
            !int.TryParse(layerText, out int layer))
            return;

        layer = Math.Clamp(layer, 0, 3);
        _pixelSelectedLayer = layer;

        if (!_pixelLayerLoaded[_pixelSelectedProfile, layer] &&
            _serial is PixelProCdcLink pixel &&
            pixel.IsConnected)
        {
            await LoadPixelLayerAsync(_pixelSelectedProfile, layer);
        }

        if (_serial is PixelProCdcLink activePixel &&
            activePixel.IsConnected)
        {
            activePixel.SetProfileLayer(
                _pixelSelectedProfile,
                _pixelSelectedLayer);
        }

        UpdatePixelLayerButtons();
        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
    }

    private void PixelKeyCategoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PixelKeyCategoryList?.SelectedItem is
                System.Windows.Controls.ListBoxItem item &&
            item.Tag is string category)
        {
            _pixelCurrentCategory = category;
            RebuildPixelPalette();
        }
    }

    private async void PixelPaletteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not PixelProKeyChoice choice)
            return;

        PixelProKeyBinding binding;

        switch (choice.Type)
        {
            case PixelProKeyBindingType.Keyboard:
                byte modifiers = choice.Aux;
                if (choice.Code != 0)
                    modifiers |= CurrentPixelModifierMask();

                binding =
                    PixelProKeyBinding.Keyboard(
                        (byte)choice.Code,
                        modifiers);
                break;

            case PixelProKeyBindingType.Consumer:
                binding = PixelProKeyBinding.Consumer(choice.Code);
                break;

            case PixelProKeyBindingType.Layer:
                binding =
                    PixelProKeyBinding.Layer(
                        (byte)choice.Code,
                        (PixelProLayerAction)choice.Aux);
                break;

            case PixelProKeyBindingType.Macro:
                binding = PixelProKeyBinding.Macro((byte)choice.Code);
                break;

            case PixelProKeyBindingType.Action:
                binding = PixelProKeyBinding.Action((byte)choice.Code);
                break;

            case PixelProKeyBindingType.Transparent:
                binding = PixelProKeyBinding.Transparent();
                break;

            default:
                binding = PixelProKeyBinding.Disabled();
                break;
        }

        _pixelProfileMaps[_pixelSelectedProfile][_pixelSelectedLayer][_pixelSelectedKey] =
            binding;
        _pixelLayerLoaded[_pixelSelectedProfile, _pixelSelectedLayer] = true;

        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();

        await SavePixelLayerAsync(
            _pixelSelectedProfile,
            _pixelSelectedLayer,
            quiet: true);
    }

    private byte CurrentPixelModifierMask()
    {
        byte modifiers = 0;

        if (PixelSelectedCtrl?.IsChecked == true) modifiers |= 0x01;
        if (PixelSelectedShift?.IsChecked == true) modifiers |= 0x02;
        if (PixelSelectedAlt?.IsChecked == true) modifiers |= 0x04;
        if (PixelSelectedWin?.IsChecked == true) modifiers |= 0x08;

        return modifiers;
    }

    private async void PixelSelectedModifier_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_pixelViaUpdating)
            return;

        PixelProKeyBinding current =
            _pixelProfileMaps[_pixelSelectedProfile][_pixelSelectedLayer][_pixelSelectedKey];

        if (current.Type != PixelProKeyBindingType.Keyboard)
            return;

        byte modifiers = CurrentPixelModifierMask();

        _pixelProfileMaps[_pixelSelectedProfile][_pixelSelectedLayer][_pixelSelectedKey] =
            current.Code == 0 && modifiers == 0
                ? PixelProKeyBinding.Disabled()
                : PixelProKeyBinding.Keyboard(
                    (byte)current.Code,
                    modifiers);

        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();

        await SavePixelLayerAsync(
            _pixelSelectedProfile,
            _pixelSelectedLayer,
            quiet: true);
    }

    private void UpdatePixelLayerButtons()
    {
        System.Windows.Controls.Button[] buttons =
        [
            PixelLayer0Button,
            PixelLayer1Button,
            PixelLayer2Button,
            PixelLayer3Button
        ];

        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].BorderBrush =
                TryFindResource(
                    i == _pixelSelectedLayer ? "Accent" : "Line")
                    as System.Windows.Media.Brush;
            buttons[i].BorderThickness =
                new Thickness(i == _pixelSelectedLayer ? 2 : 1);
        }
    }

    private void UpdatePixelKeyVisuals()
    {
        if (!_pixelViaUiBuilt)
            return;

        for (int i = 0; i < _pixelViaKeys.Count; i++)
        {
            PixelViaKeyVisual visual = _pixelViaKeys[i];
            PixelProKeyBinding binding =
                _pixelProfileMaps[_pixelSelectedProfile][_pixelSelectedLayer][i];

            visual.MainText.Text = $"K{i + 1}";
            visual.SubText.Text = PixelBindingLabel(binding, i);

            bool selected = i == _pixelSelectedKey;
            visual.Button.BorderBrush =
                TryFindResource(selected ? "Accent" : "Line")
                    as System.Windows.Media.Brush;
            visual.Button.BorderThickness =
                new Thickness(selected ? 2 : 1);
            visual.Button.Opacity = 1.0;
        }
    }

    private void UpdatePixelSelectedEditor()
    {
        if (!_pixelViaUiBuilt)
            return;

        PixelProKeyBinding binding =
            _pixelProfileMaps[_pixelSelectedProfile][_pixelSelectedLayer][_pixelSelectedKey];

        if (PixelSelectedKeyTitle is not null)
        {
            PixelSelectedKeyTitle.Text =
                $"K{_pixelSelectedKey + 1} · P{_pixelSelectedProfile + 1} · L{_pixelSelectedLayer}";
        }

        if (PixelSelectedBindingText is not null)
            PixelSelectedBindingText.Text = PixelBindingLabel(binding, _pixelSelectedKey);

        _pixelViaUpdating = true;
        try
        {
            bool keyboard =
                binding.Type == PixelProKeyBindingType.Keyboard;

            PixelSelectedCtrl.IsEnabled = keyboard;
            PixelSelectedShift.IsEnabled = keyboard;
            PixelSelectedAlt.IsEnabled = keyboard;
            PixelSelectedWin.IsEnabled = keyboard;

            PixelSelectedCtrl.IsChecked =
                keyboard && (binding.Modifiers & 0x01) != 0;
            PixelSelectedShift.IsChecked =
                keyboard && (binding.Modifiers & 0x02) != 0;
            PixelSelectedAlt.IsChecked =
                keyboard && (binding.Modifiers & 0x04) != 0;
            PixelSelectedWin.IsChecked =
                keyboard && (binding.Modifiers & 0x08) != 0;
        }
        finally
        {
            _pixelViaUpdating = false;
        }

        ApplyPixelModifierOrder();
    }

    private void ApplyPixelModifierOrder()
    {
        if (PixelModifierOrderPanel is null)
            return;

        var cards =
            new Dictionary<
                string,
                System.Windows.Controls.Border>(
                StringComparer.Ordinal)
            {
                ["Ctrl"] = PixelModifierCtrlCard,
                ["Shift"] = PixelModifierShiftCard,
                ["Alt"] = PixelModifierAltCard,
                ["Win"] = PixelModifierWinCard
            };

        IReadOnlyList<string> order =
            _pixelModifierPositions.GetOrder(
                _pixelSelectedProfile,
                _pixelSelectedLayer,
                _pixelSelectedKey);

        PixelModifierOrderPanel.Children.Clear();

        for (int index = 0;
             index < order.Count;
             index++)
        {
            if (!cards.TryGetValue(
                    order[index],
                    out System.Windows.Controls.Border? card))
            {
                continue;
            }

            card.Margin =
                index ==
                    order.Count - 1
                    ? new Thickness(0)
                    : new Thickness(
                        0,
                        0,
                        5,
                        0);

            PixelModifierOrderPanel.Children.Add(
                card);
        }
    }

    private System.Windows.Controls.Border? FindPixelModifierCard(
        string? name)
    {
        if (string.IsNullOrWhiteSpace(
                name))
        {
            return null;
        }

        if (string.Equals(
                name,
                "Ctrl",
                StringComparison.Ordinal))
        {
            return PixelModifierCtrlCard;
        }

        if (string.Equals(
                name,
                "Shift",
                StringComparison.Ordinal))
        {
            return PixelModifierShiftCard;
        }

        if (string.Equals(
                name,
                "Alt",
                StringComparison.Ordinal))
        {
            return PixelModifierAltCard;
        }

        if (string.Equals(
                name,
                "Win",
                StringComparison.Ordinal))
        {
            return PixelModifierWinCard;
        }

        return null;
    }

    private void PixelModifierHandle_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBlock handle ||
            PixelModifierOrderPanel is null)
        {
            return;
        }

        _pixelModifierDragCard =
            FindPixelModifierCard(
                handle.Tag?.ToString());

        if (_pixelModifierDragCard is null)
            return;

        _pixelModifierOrderDragging =
            false;

        _pixelModifierDragStart =
            e.GetPosition(
                PixelModifierOrderPanel);

        handle.CaptureMouse();
        e.Handled = true;
    }

    private void PixelModifierHandle_PreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (_pixelModifierDragCard is null ||
            PixelModifierOrderPanel is null ||
            e.LeftButton !=
                MouseButtonState.Pressed)
        {
            return;
        }

        System.Windows.Point current =
            e.GetPosition(
                PixelModifierOrderPanel);

        if (!_pixelModifierOrderDragging)
        {
            if (Math.Abs(
                    current.X -
                    _pixelModifierDragStart.X) <
                4)
            {
                return;
            }

            _pixelModifierOrderDragging =
                true;
        }

        int currentIndex =
            PixelModifierOrderPanel.Children.IndexOf(
                _pixelModifierDragCard);

        if (currentIndex < 0)
            return;

        int targetIndex =
            PixelModifierOrderPanel.Children.Count - 1;

        for (int index = 0;
             index <
                 PixelModifierOrderPanel.Children.Count;
             index++)
        {
            if (PixelModifierOrderPanel.Children[index]
                is not FrameworkElement item)
            {
                continue;
            }

            System.Windows.Point center =
                item.TranslatePoint(
                    new System.Windows.Point(
                        item.ActualWidth / 2.0,
                        0),
                    PixelModifierOrderPanel);

            if (current.X < center.X)
            {
                targetIndex =
                    index;
                break;
            }
        }

        if (targetIndex !=
            currentIndex)
        {
            PixelModifierOrderPanel.Children.Remove(
                _pixelModifierDragCard);

            targetIndex =
                Math.Clamp(
                    targetIndex,
                    0,
                    PixelModifierOrderPanel.Children.Count);

            PixelModifierOrderPanel.Children.Insert(
                targetIndex,
                _pixelModifierDragCard);
        }

        e.Handled = true;
    }

    private void PixelModifierHandle_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is TextBlock handle)
        {
            handle.ReleaseMouseCapture();
        }

        if (_pixelModifierDragCard is null ||
            PixelModifierOrderPanel is null)
        {
            return;
        }

        if (_pixelModifierOrderDragging)
        {
            string[] order =
                PixelModifierOrderPanel.Children
                    .OfType<
                        System.Windows.Controls.Border>()
                    .Select(
                        card =>
                            card.Tag?.ToString() ??
                            "")
                    .Where(
                        value =>
                            !string.IsNullOrWhiteSpace(
                                value))
                    .ToArray();

            _pixelModifierPositions.SetOrder(
                _pixelSelectedProfile,
                _pixelSelectedLayer,
                _pixelSelectedKey,
                order);

            PixelProKeyEditorUiStore.Save(
                _pixelModifierPositions);

            UpdatePixelKeyVisuals();
            UpdatePixelSelectedEditor();
        }

        _pixelModifierDragCard =
            null;

        _pixelModifierOrderDragging =
            false;

        e.Handled = true;
    }

    private string ModifierPrefix(
        byte modifiers,
        int keyIndex)
    {
        var names =
            new List<string>();

        IReadOnlyList<string> order =
            _pixelModifierPositions.GetOrder(
                _pixelSelectedProfile,
                _pixelSelectedLayer,
                keyIndex);

        foreach (string name in order)
        {
            bool enabled =
                name switch
                {
                    "Ctrl" =>
                        (modifiers &
                         0x01) != 0,
                    "Shift" =>
                        (modifiers &
                         0x02) != 0,
                    "Alt" =>
                        (modifiers &
                         0x04) != 0,
                    "Win" =>
                        (modifiers &
                         0x08) != 0,
                    _ => false
                };

            if (enabled)
                names.Add(name);
        }

        return string.Join(
            "+",
            names);
    }

    private string PixelBindingLabel(
        PixelProKeyBinding binding,
        int keyIndex)
    {
        if (binding.Type == PixelProKeyBindingType.Disabled)
            return "Disabled";

        if (binding.Type == PixelProKeyBindingType.Transparent)
            return "Transparent";

        if (binding.Type == PixelProKeyBindingType.Macro)
            return $"M{binding.Code + 1}";

        if (binding.Type == PixelProKeyBindingType.Action)
        {
            ActionScriptDefinition? action =
                _actionScripts.FirstOrDefault(
                    x => x.ActionId == binding.Code);

            return action is null
                ? $"Action {binding.Code:00}"
                : $"A{binding.Code:00} · {action.Name}";
        }

        if (binding.Type == PixelProKeyBindingType.Layer)
        {
            string action =
                binding.Modifiers switch
                {
                    (byte)PixelProLayerAction.Momentary => "MO",
                    (byte)PixelProLayerAction.Toggle => "TG",
                    (byte)PixelProLayerAction.To => "TO",
                    _ => "L"
                };

            return $"{action}({binding.Code})";
        }

        PixelProKeyChoice? known =
            PixelKeyChoices.FirstOrDefault(
                x => x.Type == binding.Type &&
                     x.Code == binding.Code &&
                     (binding.Type != PixelProKeyBindingType.Layer ||
                      x.Aux == binding.Modifiers));

        string baseLabel =
            known?.Label ??
            (binding.Type ==
                PixelProKeyBindingType.Keyboard
                    ? $"KC_{binding.Code}"
                    : $"CC_{binding.Code}");

        if (binding.Type !=
            PixelProKeyBindingType.Keyboard)
        {
            return baseLabel;
        }

        string modifiers =
            ModifierPrefix(
                binding.Modifiers,
                keyIndex);

        if (binding.Code == 0)
        {
            return string.IsNullOrWhiteSpace(
                    modifiers)
                ? "Disabled"
                : modifiers;
        }

        return string.IsNullOrWhiteSpace(
                modifiers)
            ? baseLabel
            : $"{modifiers}+{baseLabel}";
    }

    private async Task<bool> LoadPixelLayerAsync(
        int profile,
        int layer)
    {
        if (_serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected ||
            profile < 0 || profile > 19 ||
            layer < 0 || layer > 3)
        {
            return false;
        }

        IReadOnlyList<PixelProKeyBinding>? map =
            await pixel.GetKeymapAsync(profile, layer);

        if (map is null || map.Count != 8)
            return false;

        for (int i = 0; i < 8; i++)
            _pixelProfileMaps[profile][layer][i] = map[i];

        _pixelLayerLoaded[profile, layer] = true;
        return true;
    }

    private async Task LoadPixelProKeymapAsync()
    {
        BuildPixelProKeymapUi();

        if (_activeProduct.Driver != DeviceDriverKind.PixelProCdc)
            return;

        if (_serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected)
        {
            PixelKeymapStatusText.Text =
                L(
                    "Connect PIXEL PRO to load the keymap.",
                    "Kết nối PIXEL PRO để tải keymap.");
            PixelKeymapSaveButton.IsEnabled = false;
            return;
        }

        PixelKeymapStatusText.Text =
            L(
                "Loading profile and layers…",
                "Đang tải profile và layer…");
        PixelKeymapSaveButton.IsEnabled = false;

        bool ok = true;

        for (int layer = 0; layer < 4; layer++)
            ok &= await LoadPixelLayerAsync(_pixelSelectedProfile, layer);

        if (!ok)
        {
            PixelKeymapStatusText.Text =
                L(
                    "20-profile keymap requires PIXEL PRO firmware 1.3.0 or newer.",
                    "Keymap 20 profile cần firmware PIXEL PRO 1.3.0 trở lên.");
            return;
        }

        pixel.SetProfileLayer(
            _pixelSelectedProfile,
            _pixelSelectedLayer);

        PixelKeymapSaveButton.IsEnabled = true;
        PixelKeymapStatusText.Text =
            L(
                "Live · click a key, then choose a keycode.",
                "Đang hoạt động · chọn phím rồi chọn keycode.");

        RefreshPixelProfileCombo();
        UpdatePixelLayerButtons();
        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
    }

    private async Task<bool> SavePixelLayerAsync(
        int profile,
        int layer,
        bool quiet = false)
    {
        if (_serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected ||
            profile < 0 || profile > 19 ||
            layer < 0 || layer > 3)
        {
            return false;
        }

        bool ok = await pixel.SetKeymapAsync(
            profile,
            layer,
            _pixelProfileMaps[profile][layer]);

        if (!quiet)
        {
            PixelKeymapStatusText.Text = ok
                ? L(
                    $"Profile {profile + 1} · Layer {layer} saved.",
                    $"Đã lưu Profile {profile + 1} · Layer {layer}.")
                : L(
                    $"Could not save Profile {profile + 1} · Layer {layer}.",
                    $"Không lưu được Profile {profile + 1} · Layer {layer}.");
        }

        return ok;
    }

    private async Task<bool> SaveCurrentPixelProfileAsync()
    {
        if (_serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected)
            return false;

        for (int layer = 0; layer < 4; layer++)
        {
            if (!await pixel.SetKeymapAsync(
                    _pixelSelectedProfile,
                    layer,
                    _pixelProfileMaps[_pixelSelectedProfile][layer]))
            {
                return false;
            }
        }

        return true;
    }

    private async void PixelKeymapSave_Click(
        object sender,
        RoutedEventArgs e)
    {
        PixelKeymapSaveButton.IsEnabled = false;
        PixelKeymapStatusText.Text =
            L(
                "Saving current profile…",
                "Đang lưu profile hiện tại…");

        bool saved = await SaveCurrentPixelProfileAsync();

        PixelKeymapSaveButton.IsEnabled =
            _serial.IsConnected;

        PixelKeymapStatusText.Text = saved
            ? L(
                "Profile saved to PIXEL PRO flash.",
                "Đã lưu profile vào flash PIXEL PRO.")
            : L(
                "Save failed. Open Diagnostics for details.",
                "Lưu thất bại. Mở Diagnostics để xem chi tiết.");
    }

    private async void PixelKeymapReset_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_serial is not PixelProCdcLink pixel ||
            !pixel.IsConnected)
        {
            PixelKeymapStatusText.Text =
                L(
                    "Connect PIXEL PRO before resetting the current profile.",
                    "Hãy kết nối PIXEL PRO trước khi reset profile hiện tại.");
            return;
        }

        int profile =
            Math.Clamp(
                _pixelSelectedProfile,
                0,
                _pixelProfileCatalog.Count - 1);

        PixelKeymapStatusText.Text =
            L(
                $"Resetting Profile {profile + 1} to defaults…",
                $"Đang đưa Profile {profile + 1} về mặc định…");

        SetPixelProfileDefaults(
            profile);

        _pixelProfileCatalog.Names[profile] =
            $"Profile {profile + 1}";

        _pixelSelectedLayer = 0;
        _pixelSelectedKey = 0;
        _pixelRgbSelectedKey = -1;

        bool ok = true;

        for (int layer = 0;
             layer < 4;
             layer++)
        {
            ok &=
                await pixel.SetKeymapAsync(
                    profile,
                    layer,
                    _pixelProfileMaps[profile][layer]);
        }

        pixel.SetPixelRgbProfile(
            profile,
            _pixelRgbProfiles[profile]);

        pixel.SetPixelRgbEffect(
            profile,
            _pixelRgbEffects[profile]);

        pixel.SetPixelRgbSpeed(
            _pixelRgbSpeed);

        pixel.SetProfileLayer(
            profile,
            0);

        PixelProProfileStore.Save(
            _pixelProfileCatalog);

        PixelProKeyEditorUiStore.Save(
            _pixelModifierPositions);

        SaveAppSettings();

        RefreshPixelProfileCombo();
        UpdatePixelLayerButtons();
        UpdatePixelKeyVisuals();
        UpdatePixelSelectedEditor();
        UpdatePixelRgbUi();
        RefreshAutoProfileMappingsUi();
        RefreshAutoProfileDefaultSelectors();

        PixelKeymapStatusText.Text =
            ok
                ? L(
                    $"Profile {profile + 1} restored to default.",
                    $"Profile {profile + 1} đã về mặc định.")
                : L(
                    $"Profile {profile + 1} reset was only partially saved.",
                    $"Profile {profile + 1} reset nhưng chưa lưu đủ xuống thiết bị.");
    }

    private async void PixelKeymapExport_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export PIXEL PRO keymap profile",
            Filter = "PIXEL PRO keymap (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"PIXEL-PRO-profile-{_pixelSelectedProfile + 1}.json"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var export = new PixelViaExport
        {
            Version = 2,
            ProfileIndex = _pixelSelectedProfile,
            ProfileName = _pixelProfileCatalog.NameAt(_pixelSelectedProfile),
            Layers = _pixelProfileMaps[_pixelSelectedProfile]
                .Select(layer => layer.ToArray())
                .ToArray()
        };

        string json = JsonSerializer.Serialize(
            export,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        await IO.File.WriteAllTextAsync(
            dialog.FileName,
            json,
            Encoding.UTF8);

        PixelKeymapStatusText.Text =
            L("Profile exported.", "Đã xuất profile.");
    }

    private async void PixelKeymapImport_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import PIXEL PRO keymap profile",
            Filter = "PIXEL PRO keymap (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            string json =
                await IO.File.ReadAllTextAsync(
                    dialog.FileName,
                    Encoding.UTF8);

            PixelViaExport? import =
                JsonSerializer.Deserialize<PixelViaExport>(json);

            if (import?.Layers is null ||
                import.Layers.Length != 4 ||
                import.Layers.Any(layer => layer is null || layer.Length != 8))
            {
                throw new System.IO.InvalidDataException(
                    "Invalid PIXEL PRO keymap file.");
            }

            for (int layer = 0; layer < 4; layer++)
            {
                for (int key = 0; key < 8; key++)
                {
                    _pixelProfileMaps[_pixelSelectedProfile][layer][key] =
                        import.Layers[layer][key];
                }

                _pixelLayerLoaded[_pixelSelectedProfile, layer] = true;
            }

            if (!string.IsNullOrWhiteSpace(import.ProfileName))
            {
                _pixelProfileCatalog.Names[_pixelSelectedProfile] =
                    import.ProfileName.Trim();
                PixelProProfileStore.Save(_pixelProfileCatalog);
                RefreshPixelProfileCombo();
            }

            UpdatePixelKeyVisuals();
            UpdatePixelSelectedEditor();

            bool applied =
                _serial.IsConnected &&
                await SaveCurrentPixelProfileAsync();

            PixelKeymapStatusText.Text =
                applied
                    ? L(
                        "Imported and applied to PIXEL PRO.",
                        "Đã nhập và áp dụng vào PIXEL PRO.")
                    : L(
                        "Imported locally. Connect PIXEL PRO and press Save.",
                        "Đã nhập. Kết nối PIXEL PRO rồi bấm Save.");
        }
        catch (Exception ex)
        {
            PixelKeymapStatusText.Text =
                L(
                    $"Import failed: {ex.Message}",
                    $"Nhập lỗi: {ex.Message}");
        }
    }

    private void UpdatePixelMatrixTest(
        int index,
        bool down,
        int layer)
    {
        if (!_pixelViaUiBuilt ||
            PixelMatrixTestCheckBox?.IsChecked != true ||
            index < 0 ||
            index >= _pixelViaKeys.Count)
            return;

        PixelViaKeyVisual visual = _pixelViaKeys[index];

        if (down)
        {
            visual.Button.Background =
                TryFindResource("Accent")
                    as System.Windows.Media.Brush;
            visual.Button.Opacity = 0.82;
            PixelKeymapStatusText.Text =
                $"K{index + 1} DOWN · P{_pixelSelectedProfile + 1} · L{layer}";
        }
        else
        {
            visual.Button.Background =
                TryFindResource("Card2")
                    as System.Windows.Media.Brush;
            visual.Button.Opacity = 1.0;
            UpdatePixelKeyVisuals();
        }
    }

    private void BuildPixelMacroUi()
    {
        if (PixelMacroList is null)
            return;

        PixelMacroList.ItemsSource = null;
        PixelMacroList.ItemsSource = _pixelMacros;
        PixelMacroStepTypeCombo.SelectedIndex = 0;
        RefreshPixelMacroActionCombo();

        _pixelSelectedMacroSlot =
            Math.Clamp(_pixelSelectedMacroSlot, 1, 20);
        PixelMacroList.SelectedIndex = _pixelSelectedMacroSlot - 1;
        RefreshPixelMacroEditor();
    }

    private PixelProMacroDefinition CurrentPixelMacro =>
        _pixelMacros[Math.Clamp(_pixelSelectedMacroSlot - 1, 0, 19)];

    private void RefreshPixelMacroActionCombo()
    {
        if (PixelMacroActionCombo is null)
            return;

        int? selectedId =
            PixelMacroActionCombo.SelectedItem is ComboBoxItem selectedItem &&
            selectedItem.Tag is int selected
                ? selected
                : null;

        PixelMacroActionCombo.Items.Clear();

        foreach (ActionScriptDefinition action in
                 _actionScripts
                     .Where(x => x.ActionId > 0)
                     .OrderBy(x => x.ActionId))
        {
            PixelMacroActionCombo.Items.Add(
                new ComboBoxItem
                {
                    Content = $"Action {action.ActionId:00} · {action.Name}",
                    Tag = action.ActionId
                });
        }

        ComboBoxItem? restore =
            PixelMacroActionCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x =>
                    selectedId.HasValue &&
                    x.Tag is int id &&
                    id == selectedId.Value);

        PixelMacroActionCombo.SelectedItem =
            restore ??
            PixelMacroActionCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault();
    }

    private void PixelMacroList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PixelMacroList?.SelectedItem is not PixelProMacroDefinition macro)
            return;

        _pixelSelectedMacroSlot = Math.Clamp(macro.Slot, 1, 20);
        RefreshPixelMacroEditor();
    }

    private void RefreshPixelMacroEditor()
    {
        if (PixelMacroTitleText is null ||
            PixelMacroNameTextBox is null ||
            PixelMacroStepsList is null)
        {
            return;
        }

        PixelProMacroDefinition macro = CurrentPixelMacro;

        _pixelViaUpdating = true;
        try
        {
            PixelMacroTitleText.Text = $"M{macro.Slot}";
            PixelMacroNameTextBox.Text = macro.Name;
            PixelMacroStepsList.ItemsSource = null;
            PixelMacroStepsList.ItemsSource = macro.Steps;
        }
        finally
        {
            _pixelViaUpdating = false;
        }
    }

    private void PixelMacroStepTypeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PixelMacroStepTypeCombo?.SelectedItem is not ComboBoxItem item)
            return;

        string tag = item.Tag?.ToString() ?? "";
        string type = tag.Split(':')[0];

        bool actionStep =
            type.Equals(
                "Action",
                StringComparison.OrdinalIgnoreCase);

        bool keyStep =
            type.Equals(
                "Key",
                StringComparison.OrdinalIgnoreCase);

        PixelMacroStepHintText.Text =
            type switch
            {
                "Action" =>
                    "Choose an existing Lumi Action.",
                "Key" =>
                    "Press one key. It adds ↓ / delay / key / delay / ↑ as five movable steps.",
                "Keys" =>
                    "Shortcut/chord, e.g. Ctrl+Shift+S.",
                "Text" =>
                    "Text to type.",
                "Run" =>
                    "App, file, folder or URL to open.",
                "Delay" =>
                    "Delay in milliseconds.",
                "Media" =>
                    "PLAY, PAUSE, NEXT, PREV, STOP, MUTE, VOLUP or VOLDOWN.",
                "MouseWheel" =>
                    "Wheel ticks, e.g. 1 or -1.",
                "MouseMove" =>
                    "Relative movement dx,dy, e.g. 20,-10.",
                "MouseClick" =>
                    "Mouse button click.",
                _ =>
                    "Enter the step value."
            };

        if (PixelMacroActionCombo is not null)
        {
            PixelMacroActionCombo.Visibility =
                actionStep
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        if (PixelMacroKeyInputPanel is not null)
        {
            PixelMacroKeyInputPanel.Visibility =
                keyStep
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        if (PixelMacroStepValueTextBox is not null)
        {
            PixelMacroStepValueTextBox.Visibility =
                !actionStep && !keyStep
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            string defaultValue =
                type switch
                {
                    "Delay" => "100",
                    "Media" => "PLAY",
                    "MouseWheel" => "1",
                    "MouseMove" => "0,0",
                    "MouseClick" when tag.Contains(':') =>
                        tag[(tag.IndexOf(':') + 1)..],
                    _ => ""
                };

            PixelMacroStepValueTextBox.Text = defaultValue;
        }

        if (PixelMacroAddStepButton is not null)
        {
            PixelMacroAddStepButton.Visibility =
                keyStep
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        if (actionStep)
            RefreshPixelMacroActionCombo();
    }

    private int InsertPixelMacroSteps(
        IEnumerable<ActionScriptStep> steps)
    {
        List<ActionScriptStep> incoming = steps.ToList();

        if (incoming.Count == 0)
            return -1;

        int selected =
            PixelMacroStepsList?.SelectedIndex ?? -1;

        int insertAt =
            selected >= 0
                ? selected + 1
                : CurrentPixelMacro.Steps.Count;

        insertAt =
            Math.Clamp(
                insertAt,
                0,
                CurrentPixelMacro.Steps.Count);

        foreach (ActionScriptStep step in incoming)
        {
            CurrentPixelMacro.Steps.Insert(
                insertAt++,
                step);
        }

        PixelProMacroStore.Save(_pixelMacros);
        RefreshPixelMacroEditor();

        int firstInserted =
            insertAt - incoming.Count;

        if (PixelMacroStepsList is not null)
        {
            PixelMacroStepsList.SelectedIndex =
                firstInserted;

            PixelMacroStepsList.ScrollIntoView(
                CurrentPixelMacro.Steps[firstInserted]);
        }

        return firstInserted;
    }

    private void PixelMacroAddStep_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (PixelMacroStepTypeCombo?.SelectedItem is not ComboBoxItem item)
            return;

        string tag = item.Tag?.ToString() ?? "";
        string type = tag.Split(':')[0];

        if (type.Equals("Key", StringComparison.OrdinalIgnoreCase))
        {
            PixelMacroStatusText.Text =
                L(
                    "Click the Key box and press a key.",
                    "Bấm ô Key rồi nhấn một phím.");
            return;
        }

        string value =
            PixelMacroStepValueTextBox?.Text?.Trim() ?? "";

        if (type.Equals(
                "Action",
                StringComparison.OrdinalIgnoreCase))
        {
            if (PixelMacroActionCombo?.SelectedItem is not ComboBoxItem actionItem ||
                actionItem.Tag is not int actionId)
            {
                PixelMacroStatusText.Text =
                    L(
                        "Create/select an Action first.",
                        "Hãy tạo/chọn Action trước.");
                return;
            }

            value = actionId.ToString();
        }

        if (tag.StartsWith(
                "MouseClick:",
                StringComparison.OrdinalIgnoreCase))
        {
            value =
                tag[(tag.IndexOf(':') + 1)..];
        }

        if (type.Equals(
                "Delay",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(
                    value,
                    out int delay))
            {
                delay = 100;
            }

            value =
                Math.Clamp(
                    delay,
                    0,
                    600_000)
                .ToString();
        }

        InsertPixelMacroSteps(
        [
            new ActionScriptStep
            {
                Type = type,
                Value = value
            }
        ]);
    }

    private void PixelMacroQuickStep_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not string tag)
            return;

        int sep = tag.IndexOf('|');
        string type = sep >= 0 ? tag[..sep] : tag;
        string value = sep >= 0 ? tag[(sep + 1)..] : "";

        InsertPixelMacroSteps(
        [
            new ActionScriptStep
            {
                Type = type,
                Value = value
            }
        ]);
    }

    private void PixelMacroNameTextBox_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (_pixelViaUpdating)
            return;

        SaveCurrentPixelMacroName();
    }

    private void SaveCurrentPixelMacroName()
    {
        PixelProMacroDefinition macro = CurrentPixelMacro;
        string name =
            string.IsNullOrWhiteSpace(PixelMacroNameTextBox?.Text)
                ? $"Macro {macro.Slot}"
                : PixelMacroNameTextBox.Text.Trim();

        if (name.Length > 40)
            name = name[..40];

        macro.Name = name;
        PixelProMacroStore.Save(_pixelMacros);

        if (PixelMacroNameTextBox is not null)
            PixelMacroNameTextBox.Text = name;

        PixelMacroList.Items.Refresh();
    }

    private void PixelMacroStepValue_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        PixelProMacroStore.Save(_pixelMacros);
    }

    private void PixelMacroKeyCaptureBox_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        string? token = PixelMacroKeyToken(e.Key);
        if (string.IsNullOrWhiteSpace(token))
        {
            PixelMacroStatusText.Text =
                L(
                    $"Unsupported key: {e.Key}",
                    $"Phím chưa hỗ trợ: {e.Key}");
            return;
        }

        int delayMs = 100;
        if (PixelMacroDefaultDelayTextBox is not null &&
            int.TryParse(
                PixelMacroDefaultDelayTextBox.Text.Trim(),
                out int parsed))
        {
            delayMs = Math.Clamp(parsed, 0, 600_000);
        }

        if (PixelMacroDefaultDelayTextBox is not null)
            PixelMacroDefaultDelayTextBox.Text = delayMs.ToString();

        int firstInserted =
            InsertPixelMacroSteps(
            [
                new ActionScriptStep
                {
                    Type = "KeyDown",
                    Value = token
                },
                new ActionScriptStep
                {
                    Type = "Delay",
                    Value = delayMs.ToString()
                },
                new ActionScriptStep
                {
                    Type = "KeyLabel",
                    Value = token
                },
                new ActionScriptStep
                {
                    Type = "Delay",
                    Value = delayMs.ToString()
                },
                new ActionScriptStep
                {
                    Type = "KeyUp",
                    Value = token
                }
            ]);

        if (PixelMacroStepsList is not null &&
            firstInserted >= 0)
        {
            PixelMacroStepsList.SelectedIndex =
                firstInserted + 2;
        }

        if (PixelMacroKeyCaptureBox is not null)
            PixelMacroKeyCaptureBox.Text =
                $"↓ · {delayMs} ms · {token} · {delayMs} ms · ↑";

        PixelMacroStatusText.Text =
            L(
                $"Added ↓ / {delayMs} ms / {token} / {delayMs} ms / ↑.",
                $"Đã thêm ↓ / {delayMs} ms / {token} / {delayMs} ms / ↑.");
    }

    private static string? PixelMacroKeyToken(Key key)
    {
        int keyValue = (int)key;

        if (keyValue >= (int)Key.A &&
            keyValue <= (int)Key.Z)
        {
            return key.ToString();
        }

        if (keyValue >= (int)Key.D0 &&
            keyValue <= (int)Key.D9)
        {
            return (keyValue - (int)Key.D0).ToString();
        }

        if (keyValue >= (int)Key.F1 &&
            keyValue <= (int)Key.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            Key.Return => "ENTER",
            Key.Tab => "TAB",
            Key.Escape => "ESC",
            Key.Space => "SPACE",
            Key.Back => "BACKSPACE",
            Key.Delete => "DELETE",
            Key.Home => "HOME",
            Key.End => "END",
            Key.PageUp => "PGUP",
            Key.PageDown => "PGDN",
            Key.Left => "LEFT",
            Key.Right => "RIGHT",
            Key.Up => "UP",
            Key.Down => "DOWN",
            Key.LeftCtrl or Key.RightCtrl => "CTRL",
            Key.LeftShift or Key.RightShift => "SHIFT",
            Key.LeftAlt or Key.RightAlt => "ALT",
            Key.LWin or Key.RWin => "WIN",
            Key.OemMinus => "MINUS",
            Key.OemPlus => "PLUS",
            Key.OemComma => "COMMA",
            Key.OemPeriod => "PERIOD",
            Key.OemQuestion => "SLASH",
            Key.OemSemicolon => "SEMICOLON",
            Key.OemQuotes => "QUOTE",
            Key.OemOpenBrackets => "LBRACKET",
            Key.OemCloseBrackets => "RBRACKET",
            Key.OemPipe => "BACKSLASH",
            Key.OemTilde => "TILDE",
            _ => null
        };
    }

    private void PixelMacroClearAll_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (CurrentPixelMacro.Steps.Count == 0)
            return;

        CurrentPixelMacro.Steps.Clear();
        PixelProMacroStore.Save(_pixelMacros);
        RefreshPixelMacroEditor();

        PixelMacroStatusText.Text =
            L(
                "All macro steps cleared.",
                "Đã xoá toàn bộ step của macro.");
    }

    private static T? PixelMacroFindAncestor<T>(
        DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;

        while (current is not null)
        {
            if (current is T match)
                return match;

            current =
                VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void PixelMacroStepsList_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _pixelMacroDragIndex = -1;
        _pixelMacroDragStartPoint =
            e.GetPosition(PixelMacroStepsList);

        if (e.OriginalSource is not DependencyObject source)
            return;

        if (PixelMacroFindAncestor<System.Windows.Controls.TextBox>(source) is not null)
            return;

        ListBoxItem? item =
            PixelMacroFindAncestor<ListBoxItem>(source);

        if (item is null ||
            PixelMacroStepsList is null)
        {
            return;
        }

        _pixelMacroDragIndex =
            PixelMacroStepsList
                .ItemContainerGenerator
                .IndexFromContainer(item);
    }

    private void PixelMacroStepsList_PreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _pixelMacroDragIndex < 0 ||
            PixelMacroStepsList is null)
        {
            return;
        }

        System.Windows.Point current =
            e.GetPosition(PixelMacroStepsList);

        if (Math.Abs(
                current.X - _pixelMacroDragStartPoint.X) <
                SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(
                current.Y - _pixelMacroDragStartPoint.Y) <
                SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        int sourceIndex = _pixelMacroDragIndex;
        _pixelMacroDragIndex = -1;

        var data =
            new System.Windows.DataObject(
                "PIXEL_MACRO_STEP_INDEX",
                sourceIndex);

        System.Windows.DragDrop.DoDragDrop(
            PixelMacroStepsList,
            data,
            System.Windows.DragDropEffects.Move);
    }

    private void PixelMacroStepsList_DragOver(
        object sender,
        System.Windows.DragEventArgs e)
    {
        e.Effects =
            e.Data.GetDataPresent(
                "PIXEL_MACRO_STEP_INDEX")
                ? System.Windows.DragDropEffects.Move
                : System.Windows.DragDropEffects.None;

        e.Handled = true;
    }

    private void PixelMacroStepsList_Drop(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (PixelMacroStepsList is null ||
            !e.Data.GetDataPresent(
                "PIXEL_MACRO_STEP_INDEX"))
        {
            return;
        }

        object? raw =
            e.Data.GetData(
                "PIXEL_MACRO_STEP_INDEX");

        if (raw is not int sourceIndex ||
            sourceIndex < 0 ||
            sourceIndex >= CurrentPixelMacro.Steps.Count)
        {
            return;
        }

        int targetIndex =
            CurrentPixelMacro.Steps.Count;

        if (e.OriginalSource is DependencyObject source)
        {
            ListBoxItem? targetItem =
                PixelMacroFindAncestor<ListBoxItem>(
                    source);

            if (targetItem is not null)
            {
                targetIndex =
                    PixelMacroStepsList
                        .ItemContainerGenerator
                        .IndexFromContainer(
                            targetItem);

                System.Windows.Point inside =
                    e.GetPosition(targetItem);

                if (inside.Y >
                    targetItem.ActualHeight / 2.0)
                {
                    targetIndex++;
                }
            }
        }

        ActionScriptStep moved =
            CurrentPixelMacro.Steps[sourceIndex];

        CurrentPixelMacro.Steps.RemoveAt(
            sourceIndex);

        if (sourceIndex < targetIndex)
            targetIndex--;

        targetIndex =
            Math.Clamp(
                targetIndex,
                0,
                CurrentPixelMacro.Steps.Count);

        CurrentPixelMacro.Steps.Insert(
            targetIndex,
            moved);

        PixelProMacroStore.Save(_pixelMacros);
        RefreshPixelMacroEditor();

        PixelMacroStepsList.SelectedIndex =
            targetIndex;

        PixelMacroStepsList.ScrollIntoView(
            moved);

        e.Handled = true;
    }

    private void PixelMacroSave_Click(
        object sender,
        RoutedEventArgs e)
    {
        PixelProMacroDefinition macro = CurrentPixelMacro;

        SaveCurrentPixelMacroName();
        BuildPixelMacroUi();

        PixelMacroList.SelectedIndex = macro.Slot - 1;
        PixelMacroStatusText.Text =
            L(
                $"M{macro.Slot} saved.",
                $"Đã lưu M{macro.Slot}.");
    }

    private void PixelMacroStepDelete_Click(
        object sender,
        RoutedEventArgs e)
    {
        int index = PixelMacroStepsList?.SelectedIndex ?? -1;

        if (index < 0 || index >= CurrentPixelMacro.Steps.Count)
            return;

        CurrentPixelMacro.Steps.RemoveAt(index);
        PixelProMacroStore.Save(_pixelMacros);
        RefreshPixelMacroEditor();
    }

    private void PixelMacroStepUp_Click(
        object sender,
        RoutedEventArgs e)
    {
        int index = PixelMacroStepsList?.SelectedIndex ?? -1;

        if (index <= 0 || index >= CurrentPixelMacro.Steps.Count)
            return;

        (CurrentPixelMacro.Steps[index - 1],
         CurrentPixelMacro.Steps[index]) =
            (CurrentPixelMacro.Steps[index],
             CurrentPixelMacro.Steps[index - 1]);

        PixelProMacroStore.Save(_pixelMacros);
        RefreshPixelMacroEditor();
        PixelMacroStepsList.SelectedIndex = index - 1;
    }

    private void PixelMacroStepDown_Click(
        object sender,
        RoutedEventArgs e)
    {
        int index = PixelMacroStepsList?.SelectedIndex ?? -1;

        if (index < 0 || index >= CurrentPixelMacro.Steps.Count - 1)
            return;

        (CurrentPixelMacro.Steps[index + 1],
         CurrentPixelMacro.Steps[index]) =
            (CurrentPixelMacro.Steps[index],
             CurrentPixelMacro.Steps[index + 1]);

        PixelProMacroStore.Save(_pixelMacros);
        RefreshPixelMacroEditor();
        PixelMacroStepsList.SelectedIndex = index + 1;
    }

    private async void PixelMacroTest_Click(
        object sender,
        RoutedEventArgs e) =>
        await ExecutePixelMacroAsync(_pixelSelectedMacroSlot);

    private async Task ExecutePixelActionAsync(
        int actionId,
        int sourceKey = 0,
        int profile = 0,
        int layer = 0)
    {
        if (actionId is < 1 or > 32)
            return;

        ActionScriptDefinition? script =
            _actionScripts.FirstOrDefault(
                x => x.ActionId == actionId);

        if (script is null)
        {
            AddLog(
                "WARN",
                "ACTION",
                $"PIXEL PRO Action {actionId} has no script");
            return;
        }

        if (!_runningActionIds.Add(actionId))
        {
            AddLog(
                "WARN",
                "ACTION",
                $"PIXEL PRO Action {actionId} ignored because it is already running");
            return;
        }

        try
        {
            AddLog(
                "INFO",
                "ACTION",
                $"Run PIXEL PRO Action {actionId:00} from key {sourceKey}, profile {profile + 1}, layer {layer}");

            if (SelectedActionScript?.Id == script.Id &&
                ActionScriptStatusText is not null)
            {
                ActionScriptStatusText.Text =
                    L(
                        $"Triggered by PIXEL PRO K{sourceKey}…",
                        $"Được kích hoạt bởi PIXEL PRO K{sourceKey}…");
            }

            await ActionScriptEngine.ExecuteAsync(
                script,
                step =>
                {
                    if (SelectedActionScript?.Id != script.Id ||
                        ActionScriptStatusText is null)
                    {
                        return;
                    }

                    Dispatcher.Invoke(() =>
                        ActionScriptStatusText.Text = step);
                });

            if (SelectedActionScript?.Id == script.Id &&
                ActionScriptStatusText is not null)
            {
                ActionScriptStatusText.Text =
                    L("Completed", "Hoàn tất");
            }
        }
        catch (Exception ex)
        {
            AddLog(
                "ERROR",
                "ACTION",
                $"PIXEL PRO Action {actionId} failed: {ex.Message}");

            if (SelectedActionScript?.Id == script.Id &&
                ActionScriptStatusText is not null)
            {
                ActionScriptStatusText.Text =
                    L(
                        $"Failed: {ex.Message}",
                        $"Lỗi: {ex.Message}");
            }
        }
        finally
        {
            _runningActionIds.Remove(actionId);
        }
    }

    private async Task ExecutePixelMacroAsync(
        int slot,
        int sourceKey = 0,
        int profile = 0,
        int layer = 0)
    {
        slot = Math.Clamp(slot, 1, 20);

        if (!_runningPixelMacroSlots.Add(slot))
        {
            AddLog(
                "WARN",
                "MACRO",
                $"M{slot} ignored because it is already running");
            return;
        }

        PixelProMacroDefinition macro = _pixelMacros[slot - 1];

        try
        {
            if (PixelMacroStatusText is not null &&
                _pixelSelectedMacroSlot == slot)
            {
                PixelMacroStatusText.Text =
                    L(
                        $"Running M{slot}…",
                        $"Đang chạy M{slot}…");
            }

            AddLog(
                "INFO",
                "MACRO",
                $"Run M{slot} from key {sourceKey}, profile {profile + 1}, layer {layer}");

            foreach (ActionScriptStep step in macro.Steps.ToArray())
            {
                if (step.Type.Equals(
                        "Action",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(step.Value, out int actionId))
                        continue;

                    ActionScriptDefinition? action =
                        _actionScripts.FirstOrDefault(
                            x => x.ActionId == actionId);

                    if (action is null)
                    {
                        AddLog(
                            "WARN",
                            "MACRO",
                            $"M{slot}: Lumi Action {actionId} not found");
                        continue;
                    }

                    await ActionScriptEngine.ExecuteAsync(
                        action,
                        progress: text =>
                        {
                            if (PixelMacroStatusText is not null &&
                                _pixelSelectedMacroSlot == slot)
                            {
                                Dispatcher.Invoke(() =>
                                    PixelMacroStatusText.Text =
                                        $"M{slot} · {text}");
                            }
                        });
                }
                else
                {
                    var single = new ActionScriptDefinition
                    {
                        Name = $"M{slot}",
                        Steps =
                        [
                            new ActionScriptStep
                            {
                                Type = step.Type,
                                Value = step.Value
                            }
                        ]
                    };

                    await ActionScriptEngine.ExecuteAsync(single);
                }
            }

            if (PixelMacroStatusText is not null &&
                _pixelSelectedMacroSlot == slot)
            {
                PixelMacroStatusText.Text =
                    L(
                        $"M{slot} completed.",
                        $"M{slot} hoàn tất.");
            }
        }
        catch (Exception ex)
        {
            AddLog(
                "ERROR",
                "MACRO",
                $"M{slot} failed: {ex.Message}");

            if (PixelMacroStatusText is not null &&
                _pixelSelectedMacroSlot == slot)
            {
                PixelMacroStatusText.Text =
                    L(
                        $"M{slot} failed: {ex.Message}",
                        $"M{slot} lỗi: {ex.Message}");
            }
        }
        finally
        {
            _runningPixelMacroSlots.Remove(slot);
        }
    }

    private string CurrentConfiguratorName() =>
        _activeProduct.Driver switch
        {
            DeviceDriverKind.RynorSerial => "ZMK Keymap",
            DeviceDriverKind.PixelProCdc => "Keymap",
            DeviceDriverKind.Esp32Companion => "Device Config",
            _ => "Device Config"
        };

    private string CurrentConfiguratorUrl() =>
        _activeProduct.Driver switch
        {
            DeviceDriverKind.RynorSerial => "https://zmk.studio/",
            _ => ""
        };

    private void UpdateDeviceConfiguratorUi()
    {
        bool pixel =
            _activeProduct.Driver == DeviceDriverKind.PixelProCdc;

        ConfiguratorTab.Header =
            pixel ? "Keymap" : CurrentConfiguratorName();

        if (RynorConfiguratorPanel is not null)
            RynorConfiguratorPanel.Visibility =
                pixel ? Visibility.Collapsed : Visibility.Visible;

        if (PixelProKeymapPanel is not null)
            PixelProKeymapPanel.Visibility =
                pixel ? Visibility.Visible : Visibility.Collapsed;

        if (PixelMacroTab is not null)
            PixelMacroTab.Visibility =
                pixel ? Visibility.Visible : Visibility.Collapsed;

        if (ConnectBluetoothButton is not null)
            ConnectBluetoothButton.Visibility =
                pixel ? Visibility.Collapsed : Visibility.Visible;

        if (DeepSleepSettingsPanel is not null)
            DeepSleepSettingsPanel.Visibility =
                pixel ? Visibility.Collapsed : Visibility.Visible;

        if (RynorHardwareInfoPanel is not null)
        {
            RynorHardwareInfoPanel.Visibility =
                pixel
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        if (PixelHardwareInfoPanel is not null)
        {
            PixelHardwareInfoPanel.Visibility =
                pixel
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        if (pixel && _connectionPreference == "bluetooth")
            _connectionPreference = "auto";

        if (!pixel && DeviceConfiguratorTitle is not null)
            DeviceConfiguratorTitle.Text = CurrentConfiguratorName();

        if (pixel && PixelKeymapSaveButton is not null)
            PixelKeymapSaveButton.IsEnabled = _serial.IsConnected;

        if (AutoProfileDefaultCombo is not null)
            RefreshAutoProfileDefaultSelectors();

        if (AutoProfileMappingsPanel is not null)
            RefreshAutoProfileMappingsUi();
    }

    private async void MainTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || !_uiReady)
            return;

        ApplyLanguage();
        Dispatcher.BeginInvoke(new Action(ApplyLanguage));
        SetDeviceControlsEnabled(_serial.IsConnected);
        UpdateDeviceConfiguratorUi();

        if (ConfiguratorTab.IsSelected)
        {
            if (_activeProduct.Driver == DeviceDriverKind.PixelProCdc)
                await LoadPixelProKeymapAsync();
            else
                await EnsureDeviceConfiguratorAsync();
        }
    }

    private async Task EnsureDeviceConfiguratorAsync(bool force = false)
    {
        string url = CurrentConfiguratorUrl();
        string name = CurrentConfiguratorName();

        UpdateDeviceConfiguratorUi();

        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!force &&
            _configuratorInitialized &&
            string.Equals(
                _loadedConfiguratorUrl,
                url,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            ConfiguratorStatus.Text = L(
                $"Loading {url} …",
                $"Đang tải {url} …");

            await ConfiguratorWebView.EnsureCoreWebView2Async();
            ConfiguratorWebView.Source = new Uri(url);
            _loadedConfiguratorUrl = url;
            _configuratorInitialized = true;
            await Task.Delay(750);
            ConfiguratorStatus.Text = $"{name} · {url}";
        }
        catch (Exception ex)
        {
            ConfiguratorStatus.Text =
                $"WebView2 unavailable: {ex.Message}";
        }
    }

    private async void ReloadConfigurator_Click(object sender, RoutedEventArgs e)
    {
        _configuratorInitialized = false;
        await EnsureDeviceConfiguratorAsync(force: true);

        if (ConfiguratorWebView.CoreWebView2 is not null)
            ConfiguratorWebView.Reload();
    }

    private async void OpenConfiguratorExternal_Click(object sender, RoutedEventArgs e)
    {
        string url = CurrentConfiguratorUrl();
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AddLog(
                "WARN",
                "CONFIG",
                $"Open {CurrentConfiguratorName()} failed: {ex.Message}");
        }
    }
}
