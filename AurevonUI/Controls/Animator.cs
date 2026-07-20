using System.Collections.Concurrent;
using System.Collections.Generic;
using static AurevonUI.Animation;

namespace AurevonUI;

public static class Animator
{
    private sealed class Anim
    {
        public AnimKey Key;
        public float Duration, StartTime;
        public Track? Track;
        public System.Action? OnComplete;
    }

    private static readonly ConcurrentDictionary<AnimKey, Anim> _anims_dict = new();
    private static Anim[] _finished_buffer = new Anim[16];
    private static int _finished_count;
    private static float _current_time;

    public static void Timeline(float Duration, params Step[] Steps)
        => Run(Duration, Ease.CubicOut, 0f, true, null, Steps);

    public static void Timeline(float Duration, Ease Ease, params Step[] Steps)
        => Run(Duration, Ease, 0f, true, null, Steps);

    public static void Timeline(float Duration, Ease Ease, float Delay, params Step[] Steps)
        => Run(Duration, Ease, Delay, true, null, Steps);

    public static void Timeline(float Duration, Ease Ease, float Delay, bool LazyStop, params Step[] Steps)
        => Run(Duration, Ease, Delay, LazyStop, null, Steps);

    public static void Timeline(float Duration, Ease Ease, float Delay, bool LazyStop, System.Action? OnComplete, params Step[] Steps)
        => Run(Duration, Ease, Delay, LazyStop, OnComplete, Steps);

    private static void Run(float Duration, Ease Ease, float Delay, bool LazyStop, System.Action? OnComplete, Step[] Steps)
    {
        if (Steps is null || Steps.Length == 0)
            return;

        var Tracks = Track.Build(Steps, Animation.EasingFunctions[(int)Ease]);
        if (Tracks.Length == 0)
            return;

        float Dur = System.MathF.Max(Duration, 0f);
        float Del = System.MathF.Max(Delay, 0f);

        for (int I = 0; I < Tracks.Length; I++)
        {
            var Tr = Tracks[I];

            if (LazyStop && _anims_dict.ContainsKey(Tr.Key))
                Tr.StartFromCurrent();

            var NewAnim = new Anim
            {
                Key = Tr.Key,
                Duration = Dur,
                StartTime = _current_time + Del,
                Track = Tr,
                OnComplete = I == 0 ? OnComplete : null,
            };
            _anims_dict[Tr.Key] = NewAnim;

            Tr.SampleAt(0f);
        }
    }

    public static void StopAll() => _anims_dict.Clear();

    public static void Delay(float Delay, System.Action Action)
    {

        var Key = new AnimKey(new object(), "__delay");
        var NewAnim = new Anim
        {
            Key = Key,
            Duration = 0f,
            StartTime = _current_time + System.MathF.Max(Delay, 0f),
            Track = null!,
            OnComplete = Action,
        };
        _anims_dict[Key] = NewAnim;
    }

    internal static void Tick(float Time, float Dt)
    {
        _current_time = Time;
        _finished_count = 0;
        foreach (var Kvp in _anims_dict)
        {
            Anim A = Kvp.Value;
            float E = Time - A.StartTime;
            if (E < 0f) continue;

            float K = A.Duration <= 0f ? 1f : System.Math.Clamp(E / A.Duration, 0f, 1f);
            A.Track?.SampleAt(K);

            if (K >= 1f)
            {
                if (_finished_count == _finished_buffer.Length)
                    System.Array.Resize(ref _finished_buffer, _finished_buffer.Length * 2);
                _finished_buffer[_finished_count++] = A;
            }
        }

        for (int I = 0; I < _finished_count; I++)
        {
            Anim A = _finished_buffer[I];
            _finished_buffer[I] = null!;

            ((ICollection<KeyValuePair<AnimKey, Anim>>)_anims_dict)
                .Remove(new KeyValuePair<AnimKey, Anim>(A.Key, A));
            A.OnComplete?.Invoke();
        }
    }
}
