namespace LumiPad.App;

public enum DeviceDriverKind
{
    RynorSerial,
    PixelProCdc,
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
    public static ProductDefinition RynorOne { get; } =
        new(
            "rynor-one",
            "RYNOR ONE",
            "12-key wireless macro controller",
            "RY-01",
            true,
            DeviceDriverKind.RynorSerial);


    public static ProductDefinition PixelPro { get; } =
        new(
            "pixel-pro",
            "PIXEL PRO",
            "ESP32-S2 USB HID + CDC macro control pad",
            "PP-01",
            false,
            DeviceDriverKind.PixelProCdc,
            0x303A,
            0x80C2,
            RawUsagePage: 0xFF00,
            RawUsageId: 0x0001,
            RawReportId: 0);

    public static IReadOnlyList<ProductDefinition> All { get; } =
    [
        RynorOne,
        PixelPro
    ];
}
