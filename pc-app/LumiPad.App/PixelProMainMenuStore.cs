using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LumiPad.App;

public sealed class PixelProMainMenuSlot
{
    public int ActionId { get; set; }
    public string? IconPath { get; set; }
}

public sealed class PixelProMainMenuPage
{
    public int Layer { get; set; }
    public PixelProMainMenuSlot[] Slots { get; set; } =
        Enumerable.Range(0, 12)
            .Select(_ => new PixelProMainMenuSlot())
            .ToArray();
}

public sealed class PixelProMainMenuConfig
{
    public string? BackgroundPath { get; set; }
    public int BrightnessPercent { get; set; } = 70;
    public PixelProMainMenuPage[] Pages { get; set; } =
        Enumerable.Range(0, 4)
            .Select(i => new PixelProMainMenuPage { Layer = i })
            .ToArray();
}

public static class PixelProMainMenuStore
{
    private static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "pixel_main_menu.json");

    public static PixelProMainMenuConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return Normalize(new PixelProMainMenuConfig());

            return Normalize(
                JsonSerializer.Deserialize<PixelProMainMenuConfig>(
                    File.ReadAllText(ConfigPath)) ??
                new PixelProMainMenuConfig());
        }
        catch
        {
            return Normalize(new PixelProMainMenuConfig());
        }
    }

    public static void Save(PixelProMainMenuConfig config)
    {
        try
        {
            config = Normalize(config);

            string? folder = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(
                ConfigPath,
                JsonSerializer.Serialize(
                    config,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
        }
        catch
        {
        }
    }

    private static PixelProMainMenuConfig Normalize(
        PixelProMainMenuConfig config)
    {
        config.BrightnessPercent =
            Math.Clamp(config.BrightnessPercent, 20, 100);

        PixelProMainMenuPage[] source =
            config.Pages ?? [];

        config.Pages =
            Enumerable.Range(0, 4)
                .Select(page =>
                {
                    PixelProMainMenuPage value =
                        page < source.Length &&
                        source[page] is not null
                            ? source[page]
                            : new PixelProMainMenuPage();

                    value.Layer =
                        Math.Clamp(value.Layer, 0, 3);

                    PixelProMainMenuSlot[] slots =
                        value.Slots ?? [];

                    value.Slots =
                        Enumerable.Range(0, 12)
                            .Select(slot =>
                            {
                                PixelProMainMenuSlot item =
                                    slot < slots.Length &&
                                    slots[slot] is not null
                                        ? slots[slot]
                                        : new PixelProMainMenuSlot();

                                item.ActionId =
                                    Math.Clamp(item.ActionId, 0, 32);

                                if (string.IsNullOrWhiteSpace(item.IconPath))
                                    item.IconPath = null;

                                return item;
                            })
                            .ToArray();

                    return value;
                })
                .ToArray();

        return config;
    }
}
