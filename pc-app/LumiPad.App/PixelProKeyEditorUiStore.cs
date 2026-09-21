using System.IO;
using System.Text.Json;

namespace LumiPad.App;

public sealed class PixelProModifierPositionCatalog
{
    public Dictionary<string, PixelProModifierPosition> Positions { get; set; } =
        new(StringComparer.Ordinal);

    public PixelProModifierPosition Get(
        int profile,
        int layer,
        int key)
    {
        string id = $"{profile}:{layer}:{key}";
        return Positions.TryGetValue(id, out PixelProModifierPosition? value)
            ? value
            : new PixelProModifierPosition();
    }

    public void Set(
        int profile,
        int layer,
        int key,
        double left,
        double top)
    {
        string id = $"{profile}:{layer}:{key}";
        Positions[id] =
            new PixelProModifierPosition
            {
                Left = Math.Max(0, left),
                Top = Math.Max(0, top)
            };
    }
}

public sealed class PixelProModifierPosition
{
    public double Left { get; set; }
    public double Top { get; set; }
}

public static class PixelProKeyEditorUiStore
{
    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "pixel_key_editor_ui.json");

    public static PixelProModifierPositionCatalog Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new PixelProModifierPositionCatalog();

            return JsonSerializer.Deserialize<PixelProModifierPositionCatalog>(
                       File.ReadAllText(FilePath))
                   ?? new PixelProModifierPositionCatalog();
        }
        catch
        {
            return new PixelProModifierPositionCatalog();
        }
    }

    public static void Save(PixelProModifierPositionCatalog catalog)
    {
        try
        {
            string? folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(
                    catalog,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
