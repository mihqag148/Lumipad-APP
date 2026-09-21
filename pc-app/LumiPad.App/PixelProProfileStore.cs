using System.IO;
using System.Text.Json;

namespace LumiPad.App;

public sealed class PixelProProfileCatalog
{
    public int Count { get; set; } = 1;
    public string[] Names { get; set; } =
        Enumerable.Range(1, 20).Select(i => $"Profile {i}").ToArray();

    public void Normalize()
    {
        Count = Math.Clamp(Count, 1, 20);

        string[] normalized =
            Enumerable.Range(1, 20)
                .Select(i => $"Profile {i}")
                .ToArray();

        if (Names is { Length: > 0 })
        {
            for (int i = 0; i < Math.Min(20, Names.Length); i++)
            {
                if (!string.IsNullOrWhiteSpace(Names[i]))
                    normalized[i] = Names[i].Trim();
            }
        }

        Names = normalized;
    }

    public string NameAt(int index)
    {
        Normalize();
        index = Math.Clamp(index, 0, Count - 1);
        return Names[index];
    }
}

public static class PixelProProfileStore
{
    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "pixel_profiles.json");

    public static PixelProProfileCatalog Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new PixelProProfileCatalog();

            PixelProProfileCatalog? catalog =
                JsonSerializer.Deserialize<PixelProProfileCatalog>(
                    File.ReadAllText(FilePath));

            catalog ??= new PixelProProfileCatalog();
            catalog.Normalize();
            return catalog;
        }
        catch
        {
            return new PixelProProfileCatalog();
        }
    }

    public static void Save(PixelProProfileCatalog catalog)
    {
        try
        {
            catalog.Normalize();
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
