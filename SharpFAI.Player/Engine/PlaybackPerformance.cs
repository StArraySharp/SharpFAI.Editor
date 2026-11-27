using System.Diagnostics;

namespace SharpFAI.Player.Engine;

internal sealed class PlaybackPerformance
{
    public const int Capacity = 2048;
    private readonly Sample[] _samples = new Sample[Capacity];
    private readonly float[] _sorted = new float[Capacity];
    private int _next;
    private long _lastPresent;
    private double _time;
    private int _floor;
    private bool _drewScene;
    private Stages _stages;

    [ThreadStatic] private static PlaybackPerformance? _recording;

    internal enum Stage { TextureUpload, DecodeWait, Particles, Filters }
    internal readonly record struct Sample(float Interval, double Time, int Floor, Stages Stages);
    internal struct Stages
    {
        public float TextureUpload, DecodeWait, Particles, Filters;
    }

    public int Count { get; private set; }
    public int RecordedFrames { get; private set; }
    public Sample this[int index] => _samples[(_next - Count + index + Capacity) % Capacity];

    public void Clear()
    {
        _next = Count = RecordedFrames = 0;
        _lastPresent = 0;
    }

    public WindowFrame BeginFrame(bool capture)
    {
        if (!capture)
        {
            _lastPresent = 0;
            return default;
        }
        _recording = this;
        _drewScene = false;
        _stages = default;
        return new WindowFrame(this);
    }

    internal static void MarkScene(double time, int floor)
    {
        if (_recording is not { } recording) return;
        recording._time = time;
        recording._floor = floor;
        recording._drewScene = true;
    }

    internal void Present(long timestamp)
    {
        if (!_drewScene)
        {
            _lastPresent = 0;
            return;
        }
        if (_lastPresent != 0)
        {
            float interval = ElapsedMilliseconds(timestamp - _lastPresent);
            _samples[_next] = new(interval, _time, _floor, _stages);
            _next = (_next + 1) % Capacity;
            Count = Math.Min(Count + 1, Capacity);
            RecordedFrames++;
        }
        _lastPresent = timestamp;
    }

    public (float P95, float P99, float Max) Percentiles()
    {
        if (Count == 0) return default;
        for (int i = 0; i < Count; i++)
            _sorted[i] = this[i].Interval;
        Array.Sort(_sorted, 0, Count);
        return (_sorted[(int)Math.Ceiling(Count * .95) - 1], _sorted[(int)Math.Ceiling(Count * .99) - 1], _sorted[Count - 1]);
    }

    internal readonly struct WindowFrame(PlaybackPerformance? owner) : IDisposable
    {
        public void Present() { if (owner is not null) owner.Present(Stopwatch.GetTimestamp()); }
        public void Dispose() { if (owner is not null) _recording = null; }
    }

    internal static Measurement Measure(Stage stage) => _recording is { } active
        ? new(active, stage, Stopwatch.GetTimestamp()) : default;

    private static float ElapsedMilliseconds(long ticks) => (float)(ticks * (1000.0 / Stopwatch.Frequency));

    internal readonly struct Measurement(PlaybackPerformance? owner, Stage stage, long start) : IDisposable
    {
        public void Dispose()
        {
            if (owner is null) return;
            float milliseconds = ElapsedMilliseconds(Stopwatch.GetTimestamp() - start);
            switch (stage)
            {
                case Stage.TextureUpload: owner._stages.TextureUpload += milliseconds; break;
                case Stage.DecodeWait: owner._stages.DecodeWait += milliseconds; break;
                case Stage.Particles: owner._stages.Particles += milliseconds; break;
                case Stage.Filters: owner._stages.Filters += milliseconds; break;
            }
        }
    }
}
