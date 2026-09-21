using System.IO;
using System.Text.Json;

namespace LumiPad.App;

public sealed class PixelProMacroDefinition
{
    public int Slot { get; set; }
    public string Name { get; set; } = "";
    public List<ActionScriptStep> Steps { get; set; } = [];

    public override string ToString() =>
        $"M{Slot}  {(string.IsNullOrWhiteSpace(Name) ? $"Macro {Slot}" : Name)}";
}

public static class PixelProMacroStore
{
    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "pixel_macros.json");

    public static List<PixelProMacroDefinition> Load()
    {
        List<PixelProMacroDefinition> result;

        try
        {
            result =
                File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<PixelProMacroDefinition>>(
                        File.ReadAllText(FilePath)) ?? []
                    : [];
        }
        catch
        {
            result = [];
        }

        var bySlot = result
            .Where(x => x.Slot is >= 1 and <= 20)
            .GroupBy(x => x.Slot)
            .ToDictionary(g => g.Key, g => g.First());

        var normalized = new List<PixelProMacroDefinition>(20);

        for (int slot = 1; slot <= 20; slot++)
        {
            if (!bySlot.TryGetValue(slot, out PixelProMacroDefinition? macro))
            {
                macro = new PixelProMacroDefinition
                {
                    Slot = slot,
                    Name = $"Macro {slot}"
                };
            }

            macro.Slot = slot;
            macro.Name =
                string.IsNullOrWhiteSpace(macro.Name)
                    ? $"Macro {slot}"
                    : macro.Name.Trim();
            macro.Steps ??= [];
            normalized.Add(macro);
        }

        return normalized;
    }

    public static void Save(IEnumerable<PixelProMacroDefinition> macros)
    {
        try
        {
            string? folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(
                    macros.OrderBy(x => x.Slot).Take(20),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
