using System.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly bool _effectBenchmark;
    private readonly List<(string Type, JObject Event, string Field)> _benchmarkTargets = [];
    private readonly List<(string Type, double Cpu, double Frame)> _benchmarkSamples = [];
    private LevelRenderer? _benchmarkRenderer;
    private object? _benchmarkLevel;
    private JObject? _benchmarkEvent;
    private string? _benchmarkField;
    private JToken? _benchmarkExpected;
    private long _benchmarkStarted;
    private int _benchmarkIteration;
    private double _benchmarkDeadline;

    private void VerifyEffectBenchmark()
    {
        double now = _playbackClock.Elapsed.TotalSeconds;
        if (_benchmarkDeadline == 0)
            _benchmarkDeadline = now + 240;
        if (_playback?.LoadError is { } error)
            throw new InvalidOperationException(error);
        if (now > _benchmarkDeadline)
            throw new TimeoutException("Live effect benchmark timed out.");
        if (!HasCurrentPlayback || !_playbackFrameAvailable || _playback is not { } renderer)
            return;

        if (_benchmarkRenderer is null)
        {
            _benchmarkRenderer = renderer;
            _benchmarkLevel = renderer.Level;
            _playbackView = PlaybackView.Preview;
            _playbackFloor = null;
            renderer.Seek(131);
            _floor = renderer.CurrentIndex;
            foreach (var (type, field) in new[]
            {
                ("MoveDecorations", "opacity"), ("MoveCamera", "zoom"),
                ("SetFilter", "intensity"), ("Flash", "startOpacity"),
            })
            {
                var item = _document.ActionsOfType(type)
                    .Where(item => EditorDocument.Floor(item) <= _floor &&
                        !string.Equals((string?)item["active"], "false", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(EditorDocument.Floor).FirstOrDefault();
                if (item is not null)
                    _benchmarkTargets.Add((type, item, field));
            }
            if (_benchmarkTargets.Count == 0)
                throw new InvalidOperationException("The chart has no supported effects before 2:11.");
        }
        else if (!ReferenceEquals(renderer, _benchmarkRenderer) || !ReferenceEquals(renderer.Level, _benchmarkLevel))
            throw new InvalidOperationException("Editing an effect reloaded the chart instead of patching it.");

        if (_benchmarkEvent is not null)
        {
            if (!JToken.DeepEquals(renderer.Level!.actions[_benchmarkEvent.Parent is JArray array
                    ? array.IndexOf(_benchmarkEvent) : -1]?[_benchmarkField!], _benchmarkExpected))
                throw new InvalidOperationException("The rendered revision does not contain the edited effect value.");
            string type = _benchmarkTargets[(_benchmarkIteration - 1) / 32].Type;
            if ((_benchmarkIteration - 1) % 32 == 0)
                Console.WriteLine($"Benchmark target {type}, floor={EditorDocument.Floor(_benchmarkEvent)}, " +
                    $"layers={renderer.LastEditorEffectLayers}, tag={_benchmarkEvent["tag"]}, " +
                    renderer.LastEditorEffectTimings);
            _benchmarkSamples.Add((type, renderer.LastEditorEffectMainThreadMilliseconds,
                Stopwatch.GetElapsedTime(_benchmarkStarted).TotalMilliseconds));
        }
        if (_benchmarkIteration == _benchmarkTargets.Count * 32)
        {
            foreach (var group in _benchmarkSamples.GroupBy(sample => sample.Type))
            {
                double[] cpu = group.Select(sample => sample.Cpu).Order().ToArray();
                double[] frames = group.Select(sample => sample.Frame).Order().ToArray();
                int p95 = (int)Math.Ceiling(cpu.Length * .95) - 1;
                Console.WriteLine($"Live effects at 131s: {group.Key}, n={cpu.Length}, " +
                    $"update p95={cpu[p95]:F3}ms, input-to-render p95={frames[p95]:F3}ms, " +
                    $"update max={cpu[^1]:F3}ms; no chart reload.");
            }
            _allowClose = true;
            Close();
            return;
        }
        var target = _benchmarkTargets[_benchmarkIteration / 32];
        _benchmarkEvent = target.Event;
        _benchmarkField = target.Field;
        _benchmarkExpected = new JValue(50 + _benchmarkIteration % 32);
        _benchmarkStarted = Stopwatch.GetTimestamp();
        _document.Set(target.Event, target.Field, _benchmarkExpected);
        _benchmarkIteration++;
    }
}
