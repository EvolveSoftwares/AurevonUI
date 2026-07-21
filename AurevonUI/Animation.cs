using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AurevonUI
{
    public enum Ease
    {
        Linear,
        QuadIn, QuadOut, QuadInOut,
        CubicIn, CubicOut, CubicInOut,
        SineInOut,
        ExpoOut,
        BackOut,
        ElasticOut,
        BounceOut,
    }

    public static class Animation
    {
        public const float Tau = MathF.PI * 2f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp01(float V) => V < 0f ? 0f : (V > 1f ? 1f : V);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float A, float B, float T) => A + (B - A) * T;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 Lerp(Vector2 A, Vector2 B, float T) => A + (B - A) * T;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Lerp(Vector3 A, Vector3 B, float T) => A + (B - A) * T;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 Lerp(Vector4 A, Vector4 B, float T) => A + (B - A) * T;

        public static float Pulse(float Time, float Speed = 1f, float Phase = 0f)
            => 0.5f + 0.5f * MathF.Sin(Time * Speed + Phase);

        public static float PingPong(float Time, float Period = 1f)
        {
            if (Period <= 0f) return 0f;
            float X = (Time % Period) / Period;
            return X < 0.5f ? X * 2f : 2f - X * 2f;
        }

        public static float Wave(float Time, float Speed, float From, float To, float Phase = 0f)
            => Lerp(From, To, 0.5f + 0.5f * MathF.Sin(Time * Speed + Phase));

        private static readonly Regex _num_re = new(
            @"[-+]?(\d*\.\d+|\d+\.?\d*)([eE][-+]?\d+)?", RegexOptions.Compiled);

        private sealed class MorphPlan
        {
            public string? From;
            public string? To;
            public bool Compatible;
            public float[] FromNums = Array.Empty<float>();
            public float[] ToNums = Array.Empty<float>();
            public int[] Starts = Array.Empty<int>();
            public int[] Lengths = Array.Empty<int>();
        }

        [ThreadStatic] private static MorphPlan? _morph_plan;
        [ThreadStatic] private static StringBuilder? _morph_sb;

        public static string MorphPathData(string? From, string? To, float T)
        {
            if (string.IsNullOrEmpty(From)) return To ?? "";
            if (string.IsNullOrEmpty(To)) return From!;

            var Plan = _morph_plan;
            if (Plan is null || !ReferenceEquals(Plan.From, From) || !ReferenceEquals(Plan.To, To))
                _morph_plan = Plan = BuildMorphPlan(From!, To!);

            if (!Plan.Compatible)
                return T < 0.5f ? From! : To!;

            var Sb = _morph_sb ??= new StringBuilder(To!.Length + 16);
            Sb.Clear();
            int Last = 0;
            for (int I = 0; I < Plan.Starts.Length; I++)
            {
                int Start = Plan.Starts[I];
                Sb.Append(To, Last, Start - Last);
                Sb.Append(Lerp(Plan.FromNums[I], Plan.ToNums[I], T).ToString("0.####", CultureInfo.InvariantCulture));
                Last = Start + Plan.Lengths[I];
            }
            Sb.Append(To, Last, To!.Length - Last);
            return Sb.ToString();
        }

        private static MorphPlan BuildMorphPlan(string From, string To)
        {
            var Plan = new MorphPlan { From = From, To = To };

            var Fm = _num_re.Matches(From);
            var Tm = _num_re.Matches(To);
            if (Fm.Count != Tm.Count || Skeleton(From, Fm) != Skeleton(To, Tm))
                return Plan;

            int N = Tm.Count;
            Plan.FromNums = new float[N];
            Plan.ToNums = new float[N];
            Plan.Starts = new int[N];
            Plan.Lengths = new int[N];
            for (int I = 0; I < N; I++)
            {
                var M = Tm[I];
                Plan.FromNums[I] = ParseNum(Fm[I].Value);
                Plan.ToNums[I] = ParseNum(M.Value);
                Plan.Starts[I] = M.Index;
                Plan.Lengths[I] = M.Length;
            }
            Plan.Compatible = true;
            return Plan;
        }

        private static string Skeleton(string S, MatchCollection Matches)
        {
            var Sb = new StringBuilder(S.Length);
            int Last = 0;
            foreach (Match M in Matches)
            {
                Sb.Append(S, Last, M.Index - Last);
                Sb.Append('#');
                Last = M.Index + M.Length;
            }
            Sb.Append(S, Last, S.Length - Last);
            return Sb.ToString();
        }

        private static float ParseNum(string S) =>
            float.TryParse(S, NumberStyles.Float, CultureInfo.InvariantCulture, out var V) ? V : 0f;

        public delegate float EasingFunc(float T);

        public static readonly EasingFunc[] EasingFunctions =
        {
            T => T,
            T => T * T,
            T => 1f - (1f - T) * (1f - T),
            T => T < 0.5f ? 2f*T*T : 1f - MathF.Pow(-2f*T+2f, 2f)/2f,
            T => T * T * T,
            T => 1f - MathF.Pow(1f - T, 3f),
            T => T < 0.5f ? 4f*T*T*T : 1f - MathF.Pow(-2f*T+2f, 3f)/2f,
            T => -(MathF.Cos(MathF.PI * T) - 1f) / 2f,
            T => T >= 1f ? 1f : 1f - MathF.Pow(2f, -10f * T),
            T => {
                const float C1 = 1.70158f;
                const float C3 = C1 + 1f;
                float P = T - 1f;
                return 1f + C3 * P * P * P + C1 * P * P;
            },
            T => {
                if (T <= 0f) return 0f;
                if (T >= 1f) return 1f;
                const float C4 = Tau / 3f;
                return MathF.Pow(2f, -10f * T) * MathF.Sin((T * 10f - 0.75f) * C4) + 1f;
            },
            T => {
                const float N1 = 7.5625f;
                const float D1 = 2.75f;
                if (T < 1f / D1) return N1 * T * T;
                if (T < 2f / D1) { T -= 1.5f / D1; return N1 * T * T + 0.75f; }
                if (T < 2.5f / D1) { T -= 2.25f / D1; return N1 * T * T + 0.9375f; }
                T -= 2.625f / D1;
                return N1 * T * T + 0.984375f;
            },
        };

        public static float Apply(Ease Ease, float T)
        {
            T = Clamp01(T);
            return EasingFunctions[(int)Ease](T);
        }

        public struct Tween
        {
            public float Duration;
            public Ease Ease;
            public bool Loop;
            public bool PingPong;

            private float _elapsed;
            private float _value;
            private readonly EasingFunc _ease_func;

            public Tween(float Duration, Ease Ease = Ease.CubicInOut, bool Loop = false, bool PingPong = false)
            {
                this.Duration = MathF.Max(Duration, 0.0001f);
                this.Ease = Ease;
                this.Loop = Loop;
                this.PingPong = PingPong;
                _elapsed = 0f;
                _value = 0f;
                _ease_func = EasingFunctions[(int)Ease];
            }

            public readonly bool IsDone => !Loop && _elapsed >= Duration;

            public void Reset() => _elapsed = 0f;

            public float Update(float Delta)
            {
                Step(Delta);
                return _value;
            }

            private void Step(float StepDt)
            {
                _elapsed += StepDt;
                if (Duration <= 0f)
                {
                    _value = 1f;
                    return;
                }

                float P = _elapsed / Duration;

                if (Loop)
                {
                    P %= PingPong ? 2f : 1f;
                    if (PingPong && P > 1f) P = 2f - P;
                }
                else
                {
                    if (P > 1f) P = 1f;
                    if (PingPong) P = 2f * (P < 0.5f ? P : 1f - P);
                }

                _value = _ease_func(P);
            }

            public float Update(float Delta, float From, float To) => Lerp(From, To, Update(Delta));
        }

        public struct Spring
        {
            public float Value;
            public float Target;
            public float Stiffness;
            public float Damping;

            private float _velocity;

            public Spring(float Value, float Stiffness = 150f, float Damping = -1f)
            {
                this.Value = Value;
                Target = Value;
                this.Stiffness = Stiffness;

                this.Damping = Damping > 0f ? Damping : 2f * MathF.Sqrt(Stiffness);
                _velocity = 0f;
            }

            public void SnapTo(float Val)
            {
                Value = Val;
                Target = Val;
                _velocity = 0f;
            }

            public float Update(float Delta)
            {
                const float FixedDt = 1f / 60f;
                float Remaining = Delta;
                while (Remaining > 0f)
                {
                    float Dt = MathF.Min(Remaining, FixedDt);
                    float Force = Stiffness * (Target - Value) - Damping * _velocity;
                    _velocity += Force * Dt;
                    Value += _velocity * Dt;
                    Remaining -= Dt;
                }
                return Value;
            }
        }
    }
}