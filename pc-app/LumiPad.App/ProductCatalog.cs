namespace LumiPad.App;

public enum DeviceDriverKind
{
    LumiZmk,
    PixelProZmkHid,
    Esp32Companion
}

public sealed record ProductDefinition(
    string Id,
    string Name,
    string Subtitle,
    string ProductCode,
    bool SupportsBattery,
    DeviceDriverKind Driver,
    int? UsbVendorId = null,
    int? UsbProductId = null,
    ushort RawUsagePage = 0xFF60,
    ushort RawUsageId = 0x0061,
    byte RawReportId = 0);

public static class ProductCatalog
{
    public static ProductDefinition DialDesk { get; } =
        new(
            "dial-desk",
            "RYNOR ONE",
            "Wireless macro control desk",
            "DD-01",
            true,
            DeviceDriverKind.LumiZmk);


    public static ProductDefinition PixelPro { get; } =
        new(
            "pixel-pro",
            "PIXEL PRO",
            "USB ZMK macro control pad",
            "PP-01",
            false,
            DeviceDriverKind.PixelProZmkHid,
            0x1209,
            0x0001,
            RawUsagePage: 0xFF00,
            RawUsageId: 0x0001,
            RawReportId: 0);

    public static IReadOnlyList<ProductDefinition> All { get; } =
    [
        DialDesk,
        PixelPro
    ];
}
