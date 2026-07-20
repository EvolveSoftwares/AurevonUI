using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using SkiaSharp;

namespace AurevonUI.Svg;

internal static class SvgBounds
{
    private static readonly HashSet<string> NonVisual = new()
    {
        "defs", "clipPath", "mask", "filter", "linearGradient", "radialGradient",
        "pattern", "symbol", "style", "metadata", "title", "desc",
    };

    public static Dictionary<string, SKRect> Compute(XElement Root)
    {
        var Result = new Dictionary<string, SKRect>();
        var Active = new List<string>();
        Walk(Root, Matrix2D.Identity, Active, Result);
        return Result;
    }

    private static void Walk(XElement El, Matrix2D Parent, List<string> Active, Dictionary<string, SKRect> Result)
    {
        foreach (var Child in El.Elements())
        {
            string Name = Child.Name.LocalName;
            if (NonVisual.Contains(Name))
                continue;

            var M = Parent.Multiply(Matrix2D.Parse((string?)Child.Attribute("transform")));

            string? Id = (string?)Child.Attribute("id");
            bool Pushed = false;
            if (Id is not null)
            {
                Active.Add(Id);
                Pushed = true;
            }

            switch (Name)
            {
                case "rect":
                {
                    float X = Len(Child, "x"), Y = Len(Child, "y");
                    float W = Len(Child, "width"), H = Len(Child, "height");
                    if (W > 0 && H > 0)
                        AddRect(Active, Result, M, X, Y, W, H);
                    break;
                }
                case "circle":
                {
                    float Cx = Len(Child, "cx"), Cy = Len(Child, "cy"), R = Len(Child, "r");
                    if (R > 0)
                        AddRect(Active, Result, M, Cx - R, Cy - R, 2 * R, 2 * R);
                    break;
                }
                case "ellipse":
                {
                    float Cx = Len(Child, "cx"), Cy = Len(Child, "cy");
                    float Rx = Len(Child, "rx"), Ry = Len(Child, "ry");
                    if (Rx > 0 && Ry > 0)
                        AddRect(Active, Result, M, Cx - Rx, Cy - Ry, 2 * Rx, 2 * Ry);
                    break;
                }
                case "line":
                {
                    AddPoint(Active, Result, M.Transform(new Vector2(Len(Child, "x1"), Len(Child, "y1"))));
                    AddPoint(Active, Result, M.Transform(new Vector2(Len(Child, "x2"), Len(Child, "y2"))));
                    break;
                }
                case "polyline":
                case "polygon":
                {
                    var Nums = Matrix2D.ParseFloats((string?)Child.Attribute("points") ?? "");
                    for (int I = 0; I + 1 < Nums.Length; I += 2)
                        AddPoint(Active, Result, M.Transform(new Vector2(Nums[I], Nums[I + 1])));
                    break;
                }
                case "path":
                {
                    var D = (string?)Child.Attribute("d");
                    if (D is not null)
                    {

                        foreach (var Sub in SvgPath.Flatten(D, Quality: 6))
                            foreach (var P in Sub.Points)
                                AddPoint(Active, Result, M.Transform(P));
                    }
                    break;
                }
                case "image":
                {
                    float X = Len(Child, "x"), Y = Len(Child, "y");
                    float W = Len(Child, "width"), H = Len(Child, "height");
                    if (W > 0 && H > 0)
                        AddRect(Active, Result, M, X, Y, W, H);
                    break;
                }
                case "text":
                {
                    float X = ParseFloatUnit(Attr(Child, "x"), 0f);
                    float Y = ParseFloatUnit(Attr(Child, "y"), 0f);
                    float Size = ParseFloatUnit(Attr(Child, "font-size"), 16f);
                    if (Size <= 0f) Size = 16f;
                    string TextContent = Child.Value;
                    float W = TextContent.Length * Size * 0.6f;
                    if (W <= 0f) W = Size * 0.6f;
                    float H = Size;
                    AddRect(Active, Result, M, X, Y - H, W, H);
                    break;
                }
                default:
                    Walk(Child, M, Active, Result);
                    break;
            }

            if (Pushed)
                Active.RemoveAt(Active.Count - 1);
        }
    }

    private static void AddRect(List<string> Active, Dictionary<string, SKRect> Result, in Matrix2D M,
        float X, float Y, float W, float H)
    {
        AddPoint(Active, Result, M.Transform(new Vector2(X, Y)));
        AddPoint(Active, Result, M.Transform(new Vector2(X + W, Y)));
        AddPoint(Active, Result, M.Transform(new Vector2(X + W, Y + H)));
        AddPoint(Active, Result, M.Transform(new Vector2(X, Y + H)));
    }

    private static void AddPoint(List<string> Active, Dictionary<string, SKRect> Result, Vector2 P)
    {
        foreach (var Id in Active)
        {
            if (Result.TryGetValue(Id, out var R))
            {
                Result[Id] = new SKRect(
                    MathF.Min(R.Left, P.X), MathF.Min(R.Top, P.Y),
                    MathF.Max(R.Right, P.X), MathF.Max(R.Bottom, P.Y));
            }
            else
            {
                Result[Id] = new SKRect(P.X, P.Y, P.X, P.Y);
            }
        }
    }

    private static float Len(XElement El, string AttrName)
    {
        var A = (string?)El.Attribute(AttrName);
        if (A is null) return 0f;
        A = A.Trim();
        int End = A.Length;
        while (End > 0 && !char.IsDigit(A[End - 1]) && A[End - 1] != '.') End--;
        return float.TryParse(A.AsSpan(0, End), NumberStyles.Float, CultureInfo.InvariantCulture, out var V) ? V : 0f;
    }

    private static string? Attr(XElement El, string Name)
    {
        var A = (string?)El.Attribute(Name);
        if (A is not null)
            return A;
        foreach (var At in El.Attributes())
            if (At.Name.LocalName == Name)
                return At.Value;

        var Style = (string?)El.Attribute("style");
        if (Style is not null)
        {
            var Parts = Style.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var Part in Parts)
            {
                var Kv = Part.Split(new[] { ':' }, 2);
                if (Kv.Length == 2 && Kv[0].Trim().Equals(Name, StringComparison.OrdinalIgnoreCase))
                {
                    return Kv[1].Trim();
                }
            }
        }
        return null;
    }

    private static float ParseFloatUnit(string? val, float fallback = 0f)
    {
        if (val is null) return fallback;
        val = val.Trim();
        int End = val.Length;
        while (End > 0 && !char.IsDigit(val[End - 1]) && val[End - 1] != '.') End--;
        return float.TryParse(val.AsSpan(0, End), NumberStyles.Float, CultureInfo.InvariantCulture, out var V) ? V : fallback;
    }
}
