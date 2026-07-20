using System.Globalization;
using System.Numerics;

namespace AurevonUI.Svg;

internal readonly struct Matrix2D
{
    public readonly float A, B, C, D, E, F;

    public static readonly Matrix2D Identity = new(1, 0, 0, 1, 0, 0);

    public Matrix2D(float AVal, float BVal, float CVal, float DVal, float EVal, float FVal)
    {
        A = AVal; B = BVal; C = CVal; D = DVal; E = EVal; F = FVal;
    }

    public Vector2 Transform(Vector2 P)
        => new(A * P.X + C * P.Y + E, B * P.X + D * P.Y + F);

    public Matrix2D Multiply(in Matrix2D Other) => new(
        A * Other.A + C * Other.B,
        B * Other.A + D * Other.B,
        A * Other.C + C * Other.D,
        B * Other.C + D * Other.D,
        A * Other.E + C * Other.F + E,
        B * Other.E + D * Other.F + F);

    public bool IsAxisAligned => MathF.Abs(B) < 1e-6f && MathF.Abs(C) < 1e-6f && A > 0f && D > 0f;

    public float AverageScale
    {
        get
        {
            float Sx = MathF.Sqrt(A * A + B * B);
            float Sy = MathF.Sqrt(C * C + D * D);
            return (Sx + Sy) * 0.5f;
        }
    }

    public bool TryInvert(out Matrix2D Inv)
    {
        float Det = A * D - B * C;
        if (MathF.Abs(Det) < 1e-12f)
        {
            Inv = Identity;
            return false;
        }
        float InvDet = 1f / Det;
        Inv = new Matrix2D(
            D * InvDet, -B * InvDet,
            -C * InvDet, A * InvDet,
            (C * F - D * E) * InvDet,
            (B * E - A * F) * InvDet);
        return true;
    }

    public static Matrix2D Translate(float X, float Y) => new(1, 0, 0, 1, X, Y);
    public static Matrix2D Scale(float X, float Y) => new(X, 0, 0, Y, 0, 0);

    public static Matrix2D Rotate(float Degrees)
    {
        float R = Degrees * MathF.PI / 180f;
        float CosR = MathF.Cos(R), SinR = MathF.Sin(R);
        return new Matrix2D(CosR, SinR, -SinR, CosR, 0, 0);
    }

    public static Matrix2D Parse(string? TransformStr)
    {
        var Result = Identity;
        if (string.IsNullOrWhiteSpace(TransformStr))
            return Result;

        int I = 0;
        while (I < TransformStr.Length)
        {
            while (I < TransformStr.Length && !char.IsLetter(TransformStr[I])) I++;
            if (I >= TransformStr.Length) break;

            int NameStart = I;
            while (I < TransformStr.Length && char.IsLetter(TransformStr[I])) I++;
            string Name = TransformStr[NameStart..I];

            int Lp = TransformStr.IndexOf('(', I);
            if (Lp < 0) break;
            int Rp = TransformStr.IndexOf(')', Lp);
            if (Rp < 0) break;

            var Args = ParseFloats(TransformStr.AsSpan(Lp + 1, Rp - Lp - 1));
            I = Rp + 1;

            Matrix2D M = Name switch
            {
                "matrix" when Args.Length >= 6 => new Matrix2D(Args[0], Args[1], Args[2], Args[3], Args[4], Args[5]),
                "translate" when Args.Length >= 2 => Translate(Args[0], Args[1]),
                "translate" when Args.Length == 1 => Translate(Args[0], 0),
                "scale" when Args.Length >= 2 => Scale(Args[0], Args[1]),
                "scale" when Args.Length == 1 => Scale(Args[0], Args[0]),
                "rotate" when Args.Length == 1 => Rotate(Args[0]),
                "rotate" when Args.Length >= 3 => Translate(Args[1], Args[2]).Multiply(Rotate(Args[0])).Multiply(Translate(-Args[1], -Args[2])),
                "skewX" when Args.Length == 1 => new Matrix2D(1, 0, MathF.Tan(Args[0] * MathF.PI / 180f), 1, 0, 0),
                "skewY" when Args.Length == 1 => new Matrix2D(1, MathF.Tan(Args[0] * MathF.PI / 180f), 0, 1, 0, 0),
                _ => Identity,
            };
            Result = Result.Multiply(M);
        }
        return Result;
    }

    internal static float[] ParseFloats(ReadOnlySpan<char> S)
    {
        var List = new List<float>(6);
        int I = 0;
        while (I < S.Length)
        {
            while (I < S.Length && (S[I] == ' ' || S[I] == ',' || S[I] == '\t' || S[I] == '\n' || S[I] == '\r')) I++;
            int Start = I;
            while (I < S.Length && S[I] != ' ' && S[I] != ',' && S[I] != '\t' && S[I] != '\n' && S[I] != '\r') I++;
            if (I > Start && float.TryParse(S[Start..I], NumberStyles.Float, CultureInfo.InvariantCulture, out var V))
                List.Add(V);
        }
        return List.ToArray();
    }
}
