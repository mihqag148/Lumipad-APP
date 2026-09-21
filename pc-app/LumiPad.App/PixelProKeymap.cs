namespace LumiPad.App;

public enum PixelProKeyBindingType : byte
{
    Disabled = 0,
    Keyboard = 1,
    Consumer = 2,
    Layer = 3,
    Macro = 4,
    Transparent = 5
}

public enum PixelProLayerAction : byte
{
    Momentary = 1,
    Toggle = 2,
    To = 3
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

    public static PixelProKeyBinding Layer(byte layer, PixelProLayerAction action) =>
        new(PixelProKeyBindingType.Layer, layer, (byte)action);

    public static PixelProKeyBinding Macro(byte index) =>
        new(PixelProKeyBindingType.Macro, index, 0);

    public static PixelProKeyBinding Transparent() =>
        new(PixelProKeyBindingType.Transparent, 0, 0);

    public static PixelProKeyBinding Disabled() =>
        new(PixelProKeyBindingType.Disabled, 0, 0);
}

public sealed record PixelProKeyChoice(
    string Label,
    PixelProKeyBindingType Type,
    ushort Code,
    byte Aux = 0,
    string Category = "Basic");
