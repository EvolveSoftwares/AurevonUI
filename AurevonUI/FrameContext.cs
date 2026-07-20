using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using SkiaSharp;

namespace AurevonUI;

public sealed class FrameContext
{

    public float Time { get; internal set; }

    public float Delta { get; internal set; }

    public int Width { get; internal set; }

    public int Height { get; internal set; }

    public SKCanvas Canvas { get; internal set; } = null!;

    public RenderOptions RenderOptions { get; internal set; } = null!;

    internal void Begin(float Time, float Delta, int Width, int Height, SKCanvas Canvas, RenderOptions RenderOptions)
    {
        this.Time = Time;
        this.Delta = Delta;
        this.Width = Width;
        this.Height = Height;
        this.Canvas = Canvas;
        this.RenderOptions = RenderOptions;
    }

    private static readonly Dictionary<(string Family, bool Bold, bool Italic), SKTypeface> _typefaces = new();
    private static readonly Dictionary<string, SKTypeface> _custom_fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SKPaint _text_paint = new() { IsAntialias = true };
    private readonly SKFont _text_font = new();
    [ThreadStatic] private static SKFont? _measure_font;

    public static IReadOnlyCollection<string> RegisteredFontNames => _custom_fonts.Keys;

    public static List<string> LoadEmbeddedFonts()
    {
        var Loaded = new List<string>();
        var Asm = Assembly.GetEntryAssembly();
        if (Asm is null) return Loaded;

        foreach (var Res in Asm.GetManifestResourceNames())
        {
            if (!Res.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) &&
                !Res.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                continue;

            using var S = Asm.GetManifestResourceStream(Res);
            if (S is null) continue;

            var Tf = RegisterFont(S, null);
            if (Tf is null) continue;

            string FileName = Res;
            int Ext = FileName.LastIndexOf('.');
            if (Ext >= 0) FileName = FileName.Substring(0, Ext);
            int Dot = FileName.LastIndexOf('.');
            if (Dot >= 0) FileName = FileName.Substring(Dot + 1);
            if (FileName.Length > 0)
                _custom_fonts[FileName] = Tf;

            Loaded.Add(Tf.FamilyName);
            if (!string.Equals(FileName, Tf.FamilyName, StringComparison.OrdinalIgnoreCase))
                Loaded.Add(FileName);
        }
        return Loaded;
    }

    public static SKTypeface? LoadEmbeddedFont(string ResourceName, string? RegisterAs = null)
    {
        var Asm = Assembly.GetEntryAssembly();
        if (Asm is null) return null;
        foreach (var Res in Asm.GetManifestResourceNames())
        {
            if (Res.EndsWith(ResourceName, StringComparison.OrdinalIgnoreCase))
            {
                using var S = Asm.GetManifestResourceStream(Res);
                if (S is not null) return RegisterFont(S, RegisterAs);
            }
        }
        return null;
    }

    public static SKTypeface? RegisterFont(Stream TtfOrOtf, string? Family = null)
    {
        using var Ms = new MemoryStream();
        TtfOrOtf.CopyTo(Ms);

        var Data = SKData.CreateCopy(Ms.ToArray());
        var Tf = SKTypeface.FromData(Data);
        if (Tf is null)
            return null;
        _custom_fonts[Family ?? Tf.FamilyName] = Tf;
        return Tf;
    }

    public static SKTypeface ResolveFont(string FamilyList, bool Bold = false, bool Italic = false)
    {
        string? First = null;
        foreach (var Raw in FamilyList.Split(','))
        {
            var Fam = Raw.Trim().Trim('\'', '"', ' ');
            if (Fam.Length == 0) continue;
            First ??= Fam;

            if (_custom_fonts.TryGetValue(Fam, out var Custom))
                return Custom;

            var Tf = SKTypeface.FromFamilyName(Fam, StyleOf(Bold, Italic));
            if (Tf is not null && FamilyMatches(Fam, Tf.FamilyName))
                return Tf;
        }
        return SystemFont(First ?? "Segoe UI", Bold, Italic);
    }

    private static SKFontStyle StyleOf(bool Bold, bool Italic) => new(
        Bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal,
        (int)SKFontStyleWidth.Normal,
        Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private static bool FamilyMatches(string Requested, string Actual)
    {
        static string N(string S)
        {
            var Sb = new StringBuilder(S.Length);
            foreach (var Ch in S)
                if (char.IsLetterOrDigit(Ch)) Sb.Append(char.ToLowerInvariant(Ch));
            return Sb.ToString();
        }
        var R = N(Requested);
        var A = N(Actual);
        return A.Length > 0 && (A.StartsWith(R, StringComparison.Ordinal) || R.StartsWith(A, StringComparison.Ordinal));
    }

    public static SKTypeface SystemFont(string Family, bool Bold = false, bool Italic = false)
    {

        if (_custom_fonts.TryGetValue(Family, out var Reg))
            return Reg;

        var Key = (Family, Bold, Italic);
        if (!_typefaces.TryGetValue(Key, out var Tf) || Tf is null)
        {
            var Style = new SKFontStyle(
                Bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal,
                (int)SKFontStyleWidth.Normal,
                Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            Tf = SKTypeface.FromFamilyName(Family, Style) ?? SKTypeface.Default;
            _typefaces[Key] = Tf;
        }
        return Tf;
    }

    public void DrawText(string Text, float X, float Y, float Size, SKColor Color,
        string Family = "Segoe UI", bool Bold = false, bool Italic = false,
        SKTextAlign Align = SKTextAlign.Left)
    {
        _text_font.Typeface = ResolveFont(Family, Bold, Italic);
        _text_font.Size = Size;
        _text_paint.Color = Color;
        _text_paint.IsAntialias = RenderOptions?.Antialiasing ?? true;
        Canvas.DrawText(Text, X, Y, Align, _text_font, _text_paint);
    }

    public static float MeasureText(string Text, float Size, string Family = "Segoe UI", bool Bold = false)
    {
        var Font = _measure_font ??= new SKFont();
        Font.Typeface = ResolveFont(Family, Bold);
        Font.Size = Size;
        return Font.MeasureText(Text);
    }
}
