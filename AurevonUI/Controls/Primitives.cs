using System;
using SkiaSharp;

namespace AurevonUI;

public readonly struct Color : IEquatable<Color>
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; }

    public Color(byte r, byte g, byte b, byte a = 255)
    {
        R = r; G = g; B = b; A = a;
    }

    public static readonly Color White = new(255, 255, 255);
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color Transparent = new(0, 0, 0, 0);

    public Color WithAlpha(byte a) => new(R, G, B, a);

    public float Opacity => A / 255f;

    public static bool TryParse(string? Text, out Color Result)
    {
        if (!string.IsNullOrWhiteSpace(Text) && SKColor.TryParse(Text, out var Sk))
        {
            Result = new Color(Sk.Red, Sk.Green, Sk.Blue, Sk.Alpha);
            return true;
        }
        Result = default;
        return false;
    }

    public static Color Parse(string Text) =>
        TryParse(Text, out var C) ? C : throw new FormatException($"Invalid color: '{Text}'.");

    public string ToHex() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public override string ToString() => ToHex();

    internal SKColor ToSK() => new(R, G, B, A);
    internal static Color FromSK(SKColor C) => new(C.Red, C.Green, C.Blue, C.Alpha);

    public bool Equals(Color O) => R == O.R && G == O.G && B == O.B && A == O.A;
    public override bool Equals(object? O) => O is Color C && Equals(C);
    public override int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;
    public static bool operator ==(Color X, Color Y) => X.Equals(Y);
    public static bool operator !=(Color X, Color Y) => !X.Equals(Y);
}

public sealed class Bitmap : IDisposable
{
    internal readonly SKImage Sk;

    internal Bitmap(SKImage sk) => Sk = sk;

    public int Width => Sk.Width;
    public int Height => Sk.Height;

    public static Bitmap? Decode(byte[] Data)
    {
        if (Data is null || Data.Length == 0) return null;
        var Img = SKImage.FromEncodedData(Data);
        return Img is null ? null : new Bitmap(Img);
    }

    public static Bitmap? FromFile(string Path)
    {
        try { return Decode(System.IO.File.ReadAllBytes(Path)); }
        catch { return null; }
    }

    public void Dispose() => Sk.Dispose();
}
