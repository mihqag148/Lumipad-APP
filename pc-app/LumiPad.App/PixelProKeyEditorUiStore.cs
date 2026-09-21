using System.IO;
using System.Text.Json;

namespace LumiPad.App;

/// <summary>
/// PIXEL PRO key-editor UI preferences.
///
/// v1 stored the screen position of the whole Modifiers card. That was not
/// the intended behavior. The card is now fixed and this store keeps only the
/// per-key visual order of Ctrl / Shift / Alt / Win.
///
/// The legacy Positions member is intentionally retained for JSON
/// compatibility with older app settings, but it is no longer used.
/// </summary>
public sealed class PixelProModifierPositionCatalog
{
    public Dictionary<string, PixelProModifierPosition> Positions { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, string[]> Orders { get; set; } =
        new(StringComparer.Ordinal);

    private static readonly string[] DefaultOrder =
        ["Ctrl", "Shift", "Alt", "Win"];

    private static string Id(
        int profile,
        int layer,
        int key) =>
        $"{profile}:{layer}:{key}";

    public IReadOnlyList<string> GetOrder(
        int profile,
        int layer,
        int key)
    {
        string id =
            Id(
                profile,
                layer,
                key);

        if (!Orders.TryGetValue(
                id,
                out string[]? stored))
        {
            return DefaultOrder;
        }

        string[] normalized =
            stored
                .Where(
                    value =>
                        DefaultOrder.Contains(
                            value,
                            StringComparer.Ordinal))
                .Distinct(
                    StringComparer.Ordinal)
                .ToArray();

        if (normalized.Length !=
            DefaultOrder.Length)
        {
            return DefaultOrder;
        }

        return normalized;
    }

    public void SetOrder(
        int profile,
        int layer,
        int key,
        IEnumerable<string> order)
    {
        string[] normalized =
            order
                .Where(
                    value =>
                        DefaultOrder.Contains(
                            value,
                            StringComparer.Ordinal))
                .Distinct(
                    StringComparer.Ordinal)
                .ToArray();

        if (normalized.Length !=
            DefaultOrder.Length)
        {
            normalized =
                DefaultOrder.ToArray();
        }

        Orders[
            Id(
                profile,
                layer,
                key)] =
            normalized;
    }

    public void ResetProfile(
        int profile)
    {
        string prefix =
            $"{profile}:";

        foreach (string key in Orders.Keys
                     .Where(
                         key =>
                             key.StartsWith(
                                 prefix,
                                 StringComparison.Ordinal))
                     .ToArray())
        {
            Orders.Remove(key);
        }
    }

    public void RemoveProfileAndShift(
        int removedProfile,
        int oldCount)
    {
        var shifted =
            new Dictionary<string, string[]>(
                StringComparer.Ordinal);

        foreach (var pair in Orders)
        {
            string[] parts =
                pair.Key.Split(':');

            if (parts.Length != 3 ||
                !int.TryParse(
                    parts[0],
                    out int profile) ||
                !int.TryParse(
                    parts[1],
                    out int layer) ||
                !int.TryParse(
                    parts[2],
                    out int key))
            {
                shifted[pair.Key] =
                    pair.Value;
                continue;
            }

            if (profile ==
                removedProfile)
            {
                continue;
            }

            if (profile >
                    removedProfile &&
                profile <
                    oldCount)
            {
                profile--;
            }

            shifted[
                Id(
                    profile,
                    layer,
                    key)] =
                pair.Value;
        }

        Orders =
            shifted;
    }

    // Legacy v1 accessors kept only so old serialized data remains harmless.
    public PixelProModifierPosition Get(
        int profile,
        int layer,
        int key) =>
        new();

    public void Set(
        int profile,
        int layer,
        int key,
        double left,
        double top)
    {
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
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "pixel_key_editor_ui.json");

    public static PixelProModifierPositionCatalog Load()
    {
        try
        {
            if (!File.Exists(
                    FilePath))
            {
                return new PixelProModifierPositionCatalog();
            }

            return JsonSerializer.Deserialize<
                       PixelProModifierPositionCatalog>(
                       File.ReadAllText(
                           FilePath))
                   ?? new PixelProModifierPositionCatalog();
        }
        catch
        {
            return new PixelProModifierPositionCatalog();
        }
    }

    public static void Save(
        PixelProModifierPositionCatalog catalog)
    {
        try
        {
            string? folder =
                Path.GetDirectoryName(
                    FilePath);

            if (!string.IsNullOrWhiteSpace(
                    folder))
            {
                Directory.CreateDirectory(
                    folder);
            }

            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(
                    catalog,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
        }
        catch
        {
        }
    }
}
