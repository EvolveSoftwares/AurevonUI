using System.Globalization;
using System.Numerics;

namespace AurevonUI.Svg;

internal static class SvgPath
{
    public sealed class SubPath
    {
        public readonly List<Vector2> Points = new();
        public bool Closed;
    }

    public static List<SubPath> Flatten(string D, int Quality = 28)
    {
        var Subs = new List<SubPath>();
        if (string.IsNullOrWhiteSpace(D))
            return Subs;

        int I = 0;
        char Cmd = ' ';
        Vector2 Pos = default, Start = default;
        Vector2 LastCubicCtrl = default, LastQuadCtrl = default;
        char LastCmd = ' ';
        SubPath? Cur = null;

        void EnsureSub()
        {
            if (Cur is null)
            {
                Cur = new SubPath();
                Cur.Points.Add(Pos);
                Subs.Add(Cur);
            }
        }

        while (I < D.Length)
        {
            SkipSep(D, ref I);
            if (I >= D.Length) break;

            char Ch = D[I];
            if (char.IsLetter(Ch))
            {
                Cmd = Ch;
                I++;
                SkipSep(D, ref I);
            }

            bool Rel = char.IsLower(Cmd);
            switch (char.ToUpperInvariant(Cmd))
            {
                case 'M':
                {
                    var P = ReadPoint(D, ref I);
                    Pos = Rel ? Pos + P : P;
                    Start = Pos;
                    Cur = new SubPath();
                    Cur.Points.Add(Pos);
                    Subs.Add(Cur);
                    Cmd = Rel ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    var P = ReadPoint(D, ref I);
                    Pos = Rel ? Pos + P : P;
                    EnsureSub();
                    Cur!.Points.Add(Pos);
                    break;
                }
                case 'H':
                {
                    float X = ReadFloat(D, ref I);
                    Pos = new Vector2(Rel ? Pos.X + X : X, Pos.Y);
                    EnsureSub();
                    Cur!.Points.Add(Pos);
                    break;
                }
                case 'V':
                {
                    float Y = ReadFloat(D, ref I);
                    Pos = new Vector2(Pos.X, Rel ? Pos.Y + Y : Y);
                    EnsureSub();
                    Cur!.Points.Add(Pos);
                    break;
                }
                case 'C':
                {
                    var C1 = ReadPoint(D, ref I);
                    var C2 = ReadPoint(D, ref I);
                    var End = ReadPoint(D, ref I);
                    if (Rel) { C1 += Pos; C2 += Pos; End += Pos; }
                    EnsureSub();
                    FlattenCubic(Cur!.Points, Pos, C1, C2, End, Quality);
                    LastCubicCtrl = C2;
                    Pos = End;
                    break;
                }
                case 'S':
                {
                    var C2 = ReadPoint(D, ref I);
                    var End = ReadPoint(D, ref I);
                    if (Rel) { C2 += Pos; End += Pos; }
                    var C1 = (LastCmd is 'C' or 'c' or 'S' or 's') ? Pos * 2f - LastCubicCtrl : Pos;
                    EnsureSub();
                    FlattenCubic(Cur!.Points, Pos, C1, C2, End, Quality);
                    LastCubicCtrl = C2;
                    Pos = End;
                    break;
                }
                case 'Q':
                {
                    var C = ReadPoint(D, ref I);
                    var End = ReadPoint(D, ref I);
                    if (Rel) { C += Pos; End += Pos; }
                    EnsureSub();
                    FlattenQuad(Cur!.Points, Pos, C, End, Quality);
                    LastQuadCtrl = C;
                    Pos = End;
                    break;
                }
                case 'T':
                {
                    var End = ReadPoint(D, ref I);
                    if (Rel) End += Pos;
                    var C = (LastCmd is 'Q' or 'q' or 'T' or 't') ? Pos * 2f - LastQuadCtrl : Pos;
                    EnsureSub();
                    FlattenQuad(Cur!.Points, Pos, C, End, Quality);
                    LastQuadCtrl = C;
                    Pos = End;
                    break;
                }
                case 'A':
                {
                    float Rx = ReadFloat(D, ref I);
                    float Ry = ReadFloat(D, ref I);
                    float Rot = ReadFloat(D, ref I);
                    float LargeArc = ReadFlag(D, ref I);
                    float Sweep = ReadFlag(D, ref I);
                    var End = ReadPoint(D, ref I);
                    if (Rel) End += Pos;
                    EnsureSub();
                    FlattenArc(Cur!.Points, Pos, Rx, Ry, Rot, LargeArc > 0.5f, Sweep > 0.5f, End, Quality);
                    Pos = End;
                    break;
                }
                case 'Z':
                {
                    if (Cur is not null)
                    {
                        Cur.Closed = true;
                        Cur = null;
                    }
                    Pos = Start;
                    break;
                }
                default:
                    I++;
                    break;
            }
            LastCmd = Cmd;
        }

        Subs.RemoveAll(S => S.Points.Count < 2);
        return Subs;
    }

    private static void FlattenCubic(List<Vector2> OutPts, Vector2 P0, Vector2 C1, Vector2 C2, Vector2 P1, int Quality)
    {
        int N = SegmentsFor(P0, C1, C2, P1, Quality);
        for (int K = 1; K <= N; K++)
        {
            float T = K / (float)N;
            float U = 1f - T;
            var P = U * U * U * P0 + 3f * U * U * T * C1 + 3f * U * T * T * C2 + T * T * T * P1;
            OutPts.Add(P);
        }
    }

    private static void FlattenQuad(List<Vector2> OutPts, Vector2 P0, Vector2 C, Vector2 P1, int Quality)
    {
        int N = SegmentsFor(P0, C, C, P1, Quality);
        for (int K = 1; K <= N; K++)
        {
            float T = K / (float)N;
            float U = 1f - T;
            var P = U * U * P0 + 2f * U * T * C + T * T * P1;
            OutPts.Add(P);
        }
    }

    private static int SegmentsFor(Vector2 P0, Vector2 C1, Vector2 C2, Vector2 P1, int Quality)
    {
        float Len = (C1 - P0).Length() + (C2 - C1).Length() + (P1 - C2).Length();
        int N = (int)MathF.Ceiling(MathF.Sqrt(Len) * Quality / 8f);
        return Math.Clamp(N, 4, 128);
    }

    private static void FlattenArc(List<Vector2> OutPts, Vector2 P0, float Rx, float Ry, float RotDeg,
        bool LargeArc, bool Sweep, Vector2 P1, int Quality)
    {
        if (Rx <= 0 || Ry <= 0 || P0 == P1)
        {
            OutPts.Add(P1);
            return;
        }

        Rx = MathF.Abs(Rx);
        Ry = MathF.Abs(Ry);
        float Phi = RotDeg * MathF.PI / 180f;
        float CosPhi = MathF.Cos(Phi), SinPhi = MathF.Sin(Phi);

        float Dx = (P0.X - P1.X) / 2f, Dy = (P0.Y - P1.Y) / 2f;
        float X1p = CosPhi * Dx + SinPhi * Dy;
        float Y1p = -SinPhi * Dx + CosPhi * Dy;

        float Lambda = X1p * X1p / (Rx * Rx) + Y1p * Y1p / (Ry * Ry);
        if (Lambda > 1f)
        {
            float S = MathF.Sqrt(Lambda);
            Rx *= S;
            Ry *= S;
        }

        float Num = Rx * Rx * Ry * Ry - Rx * Rx * Y1p * Y1p - Ry * Ry * X1p * X1p;
        float Den = Rx * Rx * Y1p * Y1p + Ry * Ry * X1p * X1p;
        float Co = MathF.Sqrt(MathF.Max(0f, Num / Den));
        if (LargeArc == Sweep) Co = -Co;

        float Cxp = Co * Rx * Y1p / Ry;
        float Cyp = -Co * Ry * X1p / Rx;
        float Cx = CosPhi * Cxp - SinPhi * Cyp + (P0.X + P1.X) / 2f;
        float Cy = SinPhi * Cxp + CosPhi * Cyp + (P0.Y + P1.Y) / 2f;

        float Theta1 = AngleBetween(1, 0, (X1p - Cxp) / Rx, (Y1p - Cyp) / Ry);
        float DTheta = AngleBetween((X1p - Cxp) / Rx, (Y1p - Cyp) / Ry, (-X1p - Cxp) / Rx, (-Y1p - Cyp) / Ry);
        if (!Sweep && DTheta > 0) DTheta -= 2f * MathF.PI;
        if (Sweep && DTheta < 0) DTheta += 2f * MathF.PI;

        int N = Math.Clamp((int)MathF.Ceiling(MathF.Abs(DTheta) / (MathF.PI / 2f) * Quality), 4, 96);
        for (int K = 1; K <= N; K++)
        {
            float T = Theta1 + DTheta * K / N;
            float Ct = MathF.Cos(T), St = MathF.Sin(T);
            OutPts.Add(new Vector2(
                CosPhi * Rx * Ct - SinPhi * Ry * St + Cx,
                SinPhi * Rx * Ct + CosPhi * Ry * St + Cy));
        }
    }

    private static float AngleBetween(float Ux, float Uy, float Vx, float Vy)
    {
        float Dot = Ux * Vx + Uy * Vy;
        float Len = MathF.Sqrt((Ux * Ux + Uy * Uy) * (Vx * Vx + Vy * Vy));
        float Ang = MathF.Acos(Math.Clamp(Dot / Len, -1f, 1f));
        if (Ux * Vy - Uy * Vx < 0) Ang = -Ang;
        return Ang;
    }

    private static void SkipSep(string S, ref int I)
    {
        while (I < S.Length && (S[I] == ' ' || S[I] == ',' || S[I] == '\t' || S[I] == '\n' || S[I] == '\r')) I++;
    }

    private static float ReadFloat(string S, ref int I)
    {
        SkipSep(S, ref I);
        int Start = I;
        if (I < S.Length && (S[I] == '+' || S[I] == '-')) I++;
        bool Dot = false, Exp = false;
        while (I < S.Length)
        {
            char C = S[I];
            if (char.IsDigit(C)) { I++; continue; }
            if (C == '.' && !Dot && !Exp) { Dot = true; I++; continue; }
            if ((C == 'e' || C == 'E') && !Exp && I > Start) { Exp = true; I++; if (I < S.Length && (S[I] == '+' || S[I] == '-')) I++; continue; }
            break;
        }
        return I > Start && float.TryParse(S.AsSpan(Start, I - Start), NumberStyles.Float, CultureInfo.InvariantCulture, out var V) ? V : 0f;
    }

    private static float ReadFlag(string S, ref int I)
    {
        SkipSep(S, ref I);
        if (I < S.Length && (S[I] == '0' || S[I] == '1'))
            return S[I++] - '0';
        return ReadFloat(S, ref I);
    }

    private static Vector2 ReadPoint(string S, ref int I)
    {
        float X = ReadFloat(S, ref I);
        float Y = ReadFloat(S, ref I);
        return new Vector2(X, Y);
    }
}
