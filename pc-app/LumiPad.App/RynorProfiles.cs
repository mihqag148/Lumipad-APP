namespace LumiPad.App;

internal static class RynorProfiles
{
    public const int Count = 10;
    public const int LastIndex = Count - 1;

    public static readonly string[] DefaultNames =
    [
        "OFFICE",
        "MEDIA",
        "BAMBU STUDIO",
        "FUSION 360",
        "CAPCUT",
        "DELTA FORCE",
        "WUWA",
        "PC MONITOR",
        "RESERVED",
        "CONNECTION"
    ];

    public static int Clamp(int index) =>
        Math.Clamp(index, 0, LastIndex);

    public static string DefaultName(int index)
    {
        index = Clamp(index);
        string name = DefaultNames[index];
        return string.IsNullOrWhiteSpace(name)
            ? $"PROFILE {index + 1}"
            : name;
    }
}
