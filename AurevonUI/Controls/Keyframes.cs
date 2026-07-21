using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using static AurevonUI.Animation;

namespace AurevonUI;

internal readonly struct AnimKey : IEquatable<AnimKey>
{
    public readonly object? Instance;
    public readonly string Member;

    public AnimKey(object? Instance, string Member)
    {
        this.Instance = Instance;
        this.Member = Member;
    }

    public bool Equals(AnimKey Other) => ReferenceEquals(Instance, Other.Instance) && Member == Other.Member;
    public override bool Equals(object? Obj) => Obj is AnimKey K && Equals(K);
    public override int GetHashCode() => unchecked(RuntimeHelpers.GetHashCode(Instance) * 397 ^ (Member?.GetHashCode() ?? 0));
}

public abstract class TrackValue
{
    internal abstract void Sample(object? From, object? To, float U);
    internal abstract object? ReadCurrent();
    internal abstract object? Target();
    internal abstract AnimKey Key { get; }
}

public class Value : TrackValue
{
    private readonly BoundMember _bound;
    private readonly float _target;

    public Value(Expression<Func<float>> Property, float Target)
    {
        _bound = Bindings.Bind(Property);
        _target = Target;
    }

    public Value(Expression<Func<float?>> Property, float Target)
    {
        _bound = Bindings.Bind(Property);
        _target = Target;
    }

    internal override void Sample(object? From, object? To, float U) => _bound.Set(Lerp((float)From!, (float)To!, U));
    internal override object? ReadCurrent() => _bound.Get() ?? (object)0f;
    internal override object? Target() => _target;
    internal override AnimKey Key => _bound.Key;
}

public sealed class Value<T> : TrackValue
{
    private readonly BoundMember _bound;
    private readonly T _target;

    public Value(Expression<Func<T>> Property, T Target)
    {
        _bound = Bindings.Bind(Property);
        _target = Target;
    }

    internal override void Sample(object? From, object? To, float U) => _bound.Set(_lerpFunc((T)From!, (T)To!, U));
    internal override object? ReadCurrent() => _bound.Get();
    internal override object? Target() => _target;
    internal override AnimKey Key => _bound.Key;

    private static readonly Func<T, T, float, T> _lerpFunc = GetLerpFunc();

    private static Func<T, T, float, T> GetLerpFunc()
    {
        var t = typeof(T);
        if (t == typeof(float))
            return (Func<T, T, float, T>)(object)new Func<float, float, float, float>((a, b, u) => Lerp(a, b, u));
        if (t == typeof(double))
            return (Func<T, T, float, T>)(object)new Func<double, double, float, double>((a, b, u) => a + (b - a) * u);
        if (t == typeof(double?))
            return (Func<T, T, float, T>)(object)new Func<double?, double?, float, double?>((a, b, u) => a is null || b is null ? (u < 0.5 ? a : b) : a.Value + (b.Value - a.Value) * u);
        if (t == typeof(int))
            return (Func<T, T, float, T>)(object)new Func<int, int, float, int>((a, b, u) => (int)MathF.Round(Lerp(a, b, u)));
        if (t == typeof(int?))
            return (Func<T, T, float, T>)(object)new Func<int?, int?, float, int?>((a, b, u) => a is null || b is null ? (u < 0.5 ? a : b) : (int)MathF.Round(Lerp(a.Value, b.Value, u)));
        if (t == typeof(Thickness))
            return (Func<T, T, float, T>)(object)new Func<Thickness, Thickness, float, Thickness>((a, b, u) => new Thickness(
                Lerp(a.Left, b.Left, u),
                Lerp(a.Top, b.Top, u),
                Lerp(a.Right, b.Right, u),
                Lerp(a.Bottom, b.Bottom, u)
            ));
        if (t == typeof(Color))
            return (Func<T, T, float, T>)(object)new Func<Color, Color, float, Color>((a, b, u) => new Color(
                (byte)Math.Clamp(MathF.Round(Lerp(a.R, b.R, u)), 0f, 255f),
                (byte)Math.Clamp(MathF.Round(Lerp(a.G, b.G, u)), 0f, 255f),
                (byte)Math.Clamp(MathF.Round(Lerp(a.B, b.B, u)), 0f, 255f),
                (byte)Math.Clamp(MathF.Round(Lerp(a.A, b.A, u)), 0f, 255f)
            ));
        if (t == typeof(Color?))
            return (Func<T, T, float, T>)(object)new Func<Color?, Color?, float, Color?>((a, b, u) => {
                if (a is null) return b;
                if (b is null) return a;
                var ac = a.Value;
                var bc = b.Value;
                return new Color(
                    (byte)Math.Clamp(MathF.Round(Lerp(ac.R, bc.R, u)), 0f, 255f),
                    (byte)Math.Clamp(MathF.Round(Lerp(ac.G, bc.G, u)), 0f, 255f),
                    (byte)Math.Clamp(MathF.Round(Lerp(ac.B, bc.B, u)), 0f, 255f),
                    (byte)Math.Clamp(MathF.Round(Lerp(ac.A, bc.A, u)), 0f, 255f)
                );
            });
        if (t == typeof(Vector2))
            return (Func<T, T, float, T>)(object)new Func<Vector2, Vector2, float, Vector2>((a, b, u) => Lerp(a, b, u));
        if (t == typeof(Vector3))
            return (Func<T, T, float, T>)(object)new Func<Vector3, Vector3, float, Vector3>((a, b, u) => Lerp(a, b, u));
        if (t == typeof(Vector4))
            return (Func<T, T, float, T>)(object)new Func<Vector4, Vector4, float, Vector4>((a, b, u) => Lerp(a, b, u));

        return (a, b, u) => u < 0.5f ? a : b;
    }
}

public static class Val
{
    public static Value<T> Create<T>(Expression<Func<T>> Property, T Target)
        => new Value<T>(Property, Target);
}

public sealed class PathValue : TrackValue
{
    private readonly BoundMember _bound;
    private readonly string _target;

    public PathValue(Expression<Func<string?>> Property, string Target)
    {
        _bound = Bindings.Bind(Property);
        _target = Target ?? "";
    }

    internal override void Sample(object? From, object? To, float U) => _bound.Set(MorphPathData((string)From!, (string)To!, U));
    internal override object? ReadCurrent() => _bound.Get() ?? "";
    internal override object? Target() => _target;
    internal override AnimKey Key => _bound.Key;
}

internal sealed class BoundMember
{
    public readonly Func<object?> Get;
    public readonly Action<object?> Set;
    public readonly AnimKey Key;

    public BoundMember(Func<object?> get, Action<object?> set, AnimKey key)
    {
        Get = get;
        Set = set;
        Key = key;
    }
}

internal static class Bindings
{
    private readonly struct AccessorKey : IEquatable<AccessorKey>
    {
        public readonly MemberInfo Leaf;
        public readonly MemberInfo? StructParent;

        public AccessorKey(MemberInfo leaf, MemberInfo? structParent)
        {
            Leaf = leaf;
            StructParent = structParent;
        }

        public bool Equals(AccessorKey Other) =>
            Leaf.Equals(Other.Leaf) && Equals(StructParent, Other.StructParent);
        public override bool Equals(object? Obj) => Obj is AccessorKey K && Equals(K);
        public override int GetHashCode() => HashCode.Combine(Leaf, StructParent);
    }

    private static readonly ConcurrentDictionary<AccessorKey, Func<object?, object?>> _getters = new();
    private static readonly ConcurrentDictionary<AccessorKey, Action<object?, object?>> _setters = new();

    public static BoundMember Bind(LambdaExpression Property)
    {
        var M = RequireMember(Property);

        if (M.Expression is MemberExpression StructExpr && StructExpr.Type.IsValueType)
        {
            var Owner = Evaluate(StructExpr.Expression);
            var CacheKey = new AccessorKey(M.Member, StructExpr.Member);
            var Get = _getters.GetOrAdd(CacheKey, _ => BuildStructGetter(StructExpr, M));
            var Set = _setters.GetOrAdd(CacheKey, _ => BuildStructSetter(StructExpr, M));
            var Key = MakeKey(Owner, StructExpr.Member.DeclaringType,
                StructExpr.Member.Name + "." + M.Member.Name);
            return new BoundMember(() => Get(Owner), V => Set(Owner, V), Key);
        }
        else
        {
            var Owner = Evaluate(M.Expression);
            var CacheKey = new AccessorKey(M.Member, null);
            var Get = _getters.GetOrAdd(CacheKey, _ => BuildGetter(M));
            var Set = _setters.GetOrAdd(CacheKey, _ => BuildSetter(M));
            var Key = MakeKey(Owner, M.Member.DeclaringType, M.Member.Name);
            return new BoundMember(() => Get(Owner), V => Set(Owner, V), Key);
        }
    }

    public static MemberExpression RequireMember(LambdaExpression Expr)
    {
        var Body = Expr.Body;
        if (Body is UnaryExpression U && (U.NodeType == ExpressionType.Convert || U.NodeType == ExpressionType.ConvertChecked))
            Body = U.Operand;
        if (Body is MemberExpression M && IsWritable(M.Member))
            return M;
        throw new ArgumentException(
            "Animated value must be a direct access to a writeable property or field, e.g. () => Logo.Opacity.");
    }

    private static bool IsWritable(MemberInfo Mi) =>
        Mi is PropertyInfo P ? P.CanWrite : Mi is FieldInfo F && !F.IsInitOnly;

    private static Type MemberType(MemberInfo Mi) =>
        Mi is PropertyInfo P ? P.PropertyType : ((FieldInfo)Mi).FieldType;

    private static AnimKey MakeKey(object? Owner, Type? DeclaringType, string MemberName) =>
        new AnimKey(Owner, (DeclaringType?.FullName ?? "") + "." + MemberName);

    private static Expression OwnerAccess(Expression Param, MemberExpression M) =>
        M.Expression is null
            ? Expression.MakeMemberAccess(null, M.Member) // static member
            : Expression.MakeMemberAccess(Expression.Convert(Param, M.Member.DeclaringType!), M.Member);

    private static Func<object?, object?> BuildGetter(MemberExpression M)
    {
        var O = Expression.Parameter(typeof(object), "o");
        var Body = Expression.Convert(OwnerAccess(O, M), typeof(object));
        return Expression.Lambda<Func<object?, object?>>(Body, O).Compile();
    }

    private static Action<object?, object?> BuildSetter(MemberExpression M)
    {
        var O = Expression.Parameter(typeof(object), "o");
        var V = Expression.Parameter(typeof(object), "v");
        var Assign = Expression.Assign(OwnerAccess(O, M), Expression.Convert(V, MemberType(M.Member)));
        return Expression.Lambda<Action<object?, object?>>(Assign, O, V).Compile();
    }

    private static Func<object?, object?> BuildStructGetter(MemberExpression StructExpr, MemberExpression Leaf)
    {
        var O = Expression.Parameter(typeof(object), "o");
        var LeafAccess = Expression.MakeMemberAccess(OwnerAccess(O, StructExpr), Leaf.Member);
        var Body = Expression.Convert(LeafAccess, typeof(object));
        return Expression.Lambda<Func<object?, object?>>(Body, O).Compile();
    }

    private static Action<object?, object?> BuildStructSetter(MemberExpression StructExpr, MemberExpression Leaf)
    {
        var O = Expression.Parameter(typeof(object), "o");
        var V = Expression.Parameter(typeof(object), "v");
        var StructProp = OwnerAccess(O, StructExpr);
        var Tmp = Expression.Variable(StructExpr.Type, "tmp");
        var Body = Expression.Block(
            new[] { Tmp },
            Expression.Assign(Tmp, StructProp),
            Expression.Assign(Expression.MakeMemberAccess(Tmp, Leaf.Member), Expression.Convert(V, MemberType(Leaf.Member))),
            Expression.Assign(StructProp, Tmp)
        );
        return Expression.Lambda<Action<object?, object?>>(Body, O, V).Compile();
    }

    private static object? Evaluate(Expression? Expr)
    {
        switch (Expr)
        {
            case null:
                return null;
            case ConstantExpression C:
                return C.Value;
            case MemberExpression M:
                var Owner = Evaluate(M.Expression);
                return M.Member switch
                {
                    FieldInfo F => F.GetValue(Owner),
                    PropertyInfo P => P.GetValue(Owner),
                    _ => Expression.Lambda(Expr).Compile().DynamicInvoke(),
                };
            default:
                return Expression.Lambda(Expr).Compile().DynamicInvoke();
        }
    }
}

public sealed class Step
{
    public double Timeline { get; }
    public TrackValue[] Values { get; }

    public Step(double Timeline, params TrackValue[] Values)
    {
        this.Timeline = Timeline;
        this.Values = Values ?? Array.Empty<TrackValue>();
    }
}

internal sealed class Track
{
    private readonly TrackValue _binding;
    private readonly float[] _pos;
    private readonly object?[] _val;
    private readonly EasingFunc _ease;

    public AnimKey Key => _binding.Key;

    private Track(TrackValue Binding, float[] Pos, object?[] Vals, EasingFunc Ease)
    {
        _binding = Binding; _pos = Pos; _val = Vals; _ease = Ease;
    }

    public static Track[] Build(Step[] Steps, EasingFunc Ease)
    {
        var Sorted = Steps.OrderBy(S => S.Timeline).ToArray();

        var Index = new Dictionary<AnimKey, int>();
        var Bindings = new List<TrackValue>();
        var Positions = new List<List<float>>();
        var Values = new List<List<object?>>();

        foreach (var S in Sorted)
        {
            float P = (float)Math.Clamp(S.Timeline, 0.0, 1.0);
            var Vs = S.Values;
            for (int I = 0; I < Vs.Length; I++)
            {
                var Tv = Vs[I];
                if (Tv is null)
                    continue;

                if (!Index.TryGetValue(Tv.Key, out int G))
                {
                    G = Bindings.Count;
                    Index[Tv.Key] = G;
                    Bindings.Add(Tv);
                    Positions.Add(new List<float>());
                    Values.Add(new List<object?>());
                }

                var Pos = Positions[G];
                var Val = Values[G];
                if (Pos.Count > 0 && Pos[Pos.Count - 1] == P)
                    Val[Val.Count - 1] = Tv.Target();
                else
                {
                    Pos.Add(P);
                    Val.Add(Tv.Target());
                }
            }
        }

        var Result = new Track[Bindings.Count];
        for (int G = 0; G < Bindings.Count; G++)
        {
            var Rep = Bindings[G];
            var Pos = Positions[G];
            var Val = Values[G];

            if (Pos[0] > 0f)
            {
                Pos.Insert(0, 0f);
                Val.Insert(0, Rep.ReadCurrent());
            }

            if (Pos[Pos.Count - 1] < 1f)
            {
                Pos.Add(1f);
                Val.Add(Val[Val.Count - 1]);
            }

            Result[G] = new Track(Rep, Pos.ToArray(), Val.ToArray(), Ease);
        }
        return Result;
    }

    public void StartFromCurrent() => _val[0] = _binding.ReadCurrent();

    public void SampleAt(float K)
    {
        if (_pos.Length < 2)
        {
            _binding.Sample(_val[0], _val[0], 1f);
            return;
        }

        float E = _ease(Clamp01(K));

        int J = 0;
        while (J < _pos.Length - 2 && E > _pos[J + 1])
            J++;

        float Span = _pos[J + 1] - _pos[J];
        float U = Span <= 1e-6f ? 1f : (E - _pos[J]) / Span;
        _binding.Sample(_val[J], _val[J + 1], U);
    }
}
