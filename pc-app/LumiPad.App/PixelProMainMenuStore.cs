using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumiPad.App;

public sealed class PixelProMainMenuSlot
{
    public int ActionId { get; set; }
    public string? IconPath { get; set; }
    public string? AppPath { get; set; }
    public bool AutoIcon { get; set; }
}

// Legacy v1/v2 shape. Kept only so existing pixel_main_menu.json files migrate.
public sealed class PixelProMainMenuPage
{
    public int Layer { get; set; }
    public PixelProMainMenuSlot[] Slots { get; set; } =
        Enumerable.Range(0, 12)
            .Select(_ => new PixelProMainMenuSlot())
            .ToArray();
}

public sealed class PixelProMainMenuProfile
{
    public string? BackgroundPath { get; set; }
    public int BlurPercent { get; set; }
    public int OpacityPercent { get; set; } = 100;
    public ScreensaverScaleMode ScaleMode { get; set; } =
        ScreensaverScaleMode.Fill;

    // PIXEL PRO has eight physical keys. The LCD mirrors them as 2 x 4 icons.
    public PixelProMainMenuSlot[] Slots { get; set; } =
        Enumerable.Range(0, PixelProMainMenuStore.SlotCount)
            .Select(_ => new PixelProMainMenuSlot())
            .ToArray();
}

public sealed class PixelProMainMenuConfig
{
    public int Version { get; set; } = 4;

    public PixelProMainMenuProfile[] Profiles { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BrightnessPercent { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OpacityPercent { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PixelProMainMenuPage[]? Pages { get; set; }
}

public static class PixelProMainMenuStore
{
    public const int ProfileCount = 20;
    public const int SlotCount = 8;

    private static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "pixel_main_menu.json");

    private static string RecoveryBackupPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "pixel_main_menu.recovery.json");

    public static PixelProMainMenuConfig Load()
    {
        try
        {
            PixelProMainMenuConfig primary =
                File.Exists(ConfigPath)
                    ? Normalize(
                        JsonSerializer.Deserialize<PixelProMainMenuConfig>(
                            File.ReadAllText(ConfigPath)) ??
                        new PixelProMainMenuConfig())
                    : Normalize(
                        new PixelProMainMenuConfig());

            // Never let a device-side empty state permanently erase the only
            // recoverable Main Menu copy on the PC. If the primary cache is
            // empty but a shadow backup still has real menu data, restore the
            // backup into the editor immediately.
            if (!HasRecoverableContent(primary))
            {
                PixelProMainMenuConfig backup =
                    LoadRecoveryBackup();

                if (HasRecoverableContent(backup))
                {
                    WriteConfig(
                        ConfigPath,
                        backup);

                    return backup;
                }
            }

            if (HasRecoverableContent(primary))
            {
                WriteConfig(
                    RecoveryBackupPath,
                    primary);
            }

            return primary;
        }
        catch
        {
            return Normalize(new PixelProMainMenuConfig());
        }
    }

    public static PixelProMainMenuConfig LoadRecoveryBackup()
    {
        try
        {
            if (!File.Exists(RecoveryBackupPath))
                return Normalize(new PixelProMainMenuConfig());

            return Normalize(
                JsonSerializer.Deserialize<PixelProMainMenuConfig>(
                    File.ReadAllText(RecoveryBackupPath)) ??
                new PixelProMainMenuConfig());
        }
        catch
        {
            return Normalize(new PixelProMainMenuConfig());
        }
    }

    public static bool HasRecoverableProfile(
        PixelProMainMenuProfile? profile)
    {
        if (profile is null)
            return false;

        if (!string.IsNullOrWhiteSpace(
                profile.BackgroundPath))
        {
            return true;
        }

        return (profile.Slots ?? [])
            .Any(
                slot =>
                    slot is not null &&
                    (slot.ActionId > 0 ||
                     !string.IsNullOrWhiteSpace(slot.IconPath) ||
                     !string.IsNullOrWhiteSpace(slot.AppPath)));
    }

    public static bool HasRecoverableContent(
        PixelProMainMenuConfig? config) =>
        config?.Profiles?.Any(
            HasRecoverableProfile) == true;

    public static void Save(PixelProMainMenuConfig config)
    {
        try
        {
            config = Normalize(config);

            WriteConfig(
                ConfigPath,
                config);

            // An empty device response is allowed to update the disposable
            // editor cache, but it must never overwrite the recovery copy.
            if (HasRecoverableContent(config))
            {
                WriteConfig(
                    RecoveryBackupPath,
                    config);
            }
        }
        catch
        {
        }
    }

    private static void WriteConfig(
        string path,
        PixelProMainMenuConfig config)
    {
        string? folder =
            Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                config,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    private static PixelProMainMenuSlot NormalizeSlot(
        PixelProMainMenuSlot? slot)
    {
        PixelProMainMenuSlot value =
            slot ?? new PixelProMainMenuSlot();

        value.ActionId =
            Math.Clamp(value.ActionId, 0, 32);

        if (string.IsNullOrWhiteSpace(value.IconPath))
            value.IconPath = null;

        if (string.IsNullOrWhiteSpace(value.AppPath))
            value.AppPath = null;

        if (value.IconPath is null)
            value.AutoIcon = false;

        return value;
    }

    private static PixelProMainMenuProfile NormalizeProfile(
        PixelProMainMenuProfile? profile)
    {
        PixelProMainMenuProfile value =
            profile ?? new PixelProMainMenuProfile();

        value.BlurPercent =
            Math.Clamp(value.BlurPercent, 0, 100);

        value.OpacityPercent =
            Math.Clamp(value.OpacityPercent, 0, 100);

        if (!Enum.IsDefined(value.ScaleMode) ||
            value.ScaleMode is not (
                ScreensaverScaleMode.Fill or
                ScreensaverScaleMode.Fit or
                ScreensaverScaleMode.Stretch))
        {
            value.ScaleMode =
                ScreensaverScaleMode.Fill;
        }

        if (string.IsNullOrWhiteSpace(value.BackgroundPath))
            value.BackgroundPath = null;

        PixelProMainMenuSlot[] slots =
            value.Slots ?? [];

        // v1.22 stored 12 visual slots. Preserve the first eight key-aligned
        // slots and discard the old third row when migrating to v1.23.
        value.Slots =
            Enumerable.Range(0, SlotCount)
                .Select(index =>
                    NormalizeSlot(
                        index < slots.Length
                            ? slots[index]
                            : null))
                .ToArray();

        return value;
    }

    private static PixelProMainMenuConfig Normalize(
        PixelProMainMenuConfig config)
    {
        PixelProMainMenuProfile[] source =
            config.Profiles ?? [];

        bool migrateLegacy =
            source.Length == 0 &&
            (config.Pages is { Length: > 0 } ||
             !string.IsNullOrWhiteSpace(config.BackgroundPath) ||
             config.OpacityPercent.HasValue);

        var profiles =
            Enumerable.Range(0, ProfileCount)
                .Select(index =>
                    index < source.Length
                        ? NormalizeProfile(source[index])
                        : NormalizeProfile(
                            new PixelProMainMenuProfile()))
                .ToArray();

        if (migrateLegacy)
        {
            string? background =
                string.IsNullOrWhiteSpace(config.BackgroundPath)
                    ? null
                    : config.BackgroundPath;

            int opacity =
                Math.Clamp(
                    config.OpacityPercent ?? 100,
                    0,
                    100);

            foreach (PixelProMainMenuProfile profile in profiles)
            {
                profile.BackgroundPath = background;
                profile.OpacityPercent = opacity;
            }

            PixelProMainMenuPage[] oldPages =
                config.Pages ?? [];

            for (int profile = 0;
                 profile < Math.Min(4, oldPages.Length);
                 profile++)
            {
                PixelProMainMenuSlot[] oldSlots =
                    oldPages[profile]?.Slots ?? [];

                profiles[profile].Slots =
                    Enumerable.Range(0, SlotCount)
                        .Select(slot =>
                            NormalizeSlot(
                                slot < oldSlots.Length
                                    ? oldSlots[slot]
                                    : null))
                        .ToArray();
            }
        }

        config.Version = 4;
        config.Profiles = profiles;

        config.BackgroundPath = null;
        config.BrightnessPercent = null;
        config.OpacityPercent = null;
        config.Pages = null;

        return config;
    }
}
