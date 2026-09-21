namespace LumiPad.App;

public enum PixelProKeyBindingType : byte
{
    Disabled = 0,
    Keyboard = 1,
    Consumer = 2
}

public sealed record PixelProKeyBinding(
    PixelProKeyBindingType Type,
    ushort Code,
    byte Modifiers)
{
    public static PixelProKeyBinding Keyboard(byte usage, byte modifiers = 0) =>
        new(PixelProKeyBindingType.Keyboard, usage, modifiers);

    public static PixelProKeyBinding Consumer(ushort usage) =>
        new(PixelProKeyBindingType.Consumer, usage, 0);

    public static PixelProKeyBinding Disabled() =>
        new(PixelProKeyBindingType.Disabled, 0, 0);
}

public sealed record PixelProKeyChoice(
    string Label,
    PixelProKeyBindingType Type,
    ushort Code);
