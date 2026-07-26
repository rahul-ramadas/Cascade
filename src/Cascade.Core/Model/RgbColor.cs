namespace Cascade.Core.Model;

/// <summary>An RGB color independent of any UI framework (Core stays UI-agnostic).</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public int ToRgbInt() => (R << 16) | (G << 8) | B;

    public static RgbColor FromRgbInt(int rgb) =>
        new((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    public string ToHex() => $"{R:x2}{G:x2}{B:x2}";

    public static bool TryParseHex(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;
        if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int v)) return false;
        color = FromRgbInt(v);
        return true;
    }
}
