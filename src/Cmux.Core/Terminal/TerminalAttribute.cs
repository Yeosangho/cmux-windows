namespace Cmux.Core.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strikethrough = 128,
}

public struct TerminalColor : IEquatable<TerminalColor>
{
    public byte R;
    public byte G;
    public byte B;
    public bool IsDefault;

    public TerminalColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
        IsDefault = false;
    }

    public static TerminalColor Default => new() { IsDefault = true };

    public static TerminalColor FromIndex(int index)
    {
        // 16-color palette tuned for dark backgrounds. VGA defaults
        // (0xAA series + 0x0000AA blue) are too saturated and the navy-blue
        // index 4 used by `ls --color` for directories disappears against the
        // editor background. Values mirror VS Code's "Dark+" terminal palette,
        // which is widely familiar and readable.
        if (index < 16)
        {
            return index switch
            {
                0 => new(0x00, 0x00, 0x00),  // black
                1 => new(0xCD, 0x31, 0x31),  // red
                2 => new(0x0D, 0xBC, 0x79),  // green
                3 => new(0xE5, 0xE5, 0x10),  // yellow
                4 => new(0x24, 0x72, 0xC8),  // blue — readable on dark bg (was #0000AA)
                5 => new(0xBC, 0x3F, 0xBC),  // magenta
                6 => new(0x11, 0xA8, 0xCD),  // cyan
                7 => new(0xE5, 0xE5, 0xE5),  // white / light gray
                8 => new(0x66, 0x66, 0x66),  // bright black (gray)
                9 => new(0xF1, 0x4C, 0x4C),  // bright red
                10 => new(0x23, 0xD1, 0x8B), // bright green
                11 => new(0xF5, 0xF5, 0x43), // bright yellow
                12 => new(0x3B, 0x8E, 0xEA), // bright blue
                13 => new(0xD6, 0x70, 0xD6), // bright magenta
                14 => new(0x29, 0xB8, 0xDB), // bright cyan
                15 => new(0xFF, 0xFF, 0xFF), // bright white
                _ => Default,
            };
        }

        if (index < 232)
        {
            // 216-color cube (6x6x6)
            int i = index - 16;
            int r = i / 36;
            int g = (i / 6) % 6;
            int b = i % 6;
            return new((byte)(r > 0 ? r * 40 + 55 : 0), (byte)(g > 0 ? g * 40 + 55 : 0), (byte)(b > 0 ? b * 40 + 55 : 0));
        }

        if (index < 256)
        {
            // 24-step grayscale
            byte v = (byte)(index - 232);
            v = (byte)(v * 10 + 8);
            return new(v, v, v);
        }

        return Default;
    }

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(r, g, b);

    public bool Equals(TerminalColor other) =>
        R == other.R && G == other.G && B == other.B && IsDefault == other.IsDefault;

    public override bool Equals(object? obj) => obj is TerminalColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(R, G, B, IsDefault);
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}

public struct TerminalAttribute
{
    public CellFlags Flags;
    public TerminalColor Foreground;
    public TerminalColor Background;

    public static TerminalAttribute Default => new()
    {
        Flags = CellFlags.None,
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
    };
}

public struct TerminalCell
{
    public char Character;
    public TerminalAttribute Attribute;
    public bool IsDirty;
    public int Width; // 1 for normal, 2 for wide chars

    public static TerminalCell Empty => new()
    {
        Character = ' ',
        Attribute = TerminalAttribute.Default,
        IsDirty = true,
        Width = 1,
    };
}
