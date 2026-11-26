using System.Numerics;
using System.Text.Json;
using SharpFAI.ModApi;
using SharpFAI.ModExamples.Shared;

namespace SharpFAI.ModExamples;

public sealed class BaseMod : ISharpFaiMod
{
    public ValueTask ActivateAsync(IModContext context, CancellationToken token)
    {
        context.GetRequiredService<IModServices>().Publish(context.Id + "/greeting",
            (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(ExampleInfo.Greeting())));
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ChartToolsMod : ISharpFaiMod
{
    private volatile bool _preview;
    private volatile float _amount = .5f;
    private volatile bool _depth, _history;
    private volatile float _audioGain = 1f;
    private int _checks;
    private IToolWindowRegistry _windows = null!;
    private ToolWindowId _analysisWindow;

    public async ValueTask ActivateAsync(IModContext context, CancellationToken token)
    {
        var greeting = await context.GetRequiredService<IModServices>().InvokeAsync(
            "com.sharpfai.example.base/greeting", default, token);
        string provider = greeting.GetString() ?? "";
        _windows = context.GetRequiredService<IToolWindowRegistry>();
        string id = context.Id;
        _analysisWindow = new ToolWindowId(id + "/analysis");
        _windows.Register(new(_analysisWindow, "window", UiHost.Editor, ToolWindowKind.Dockable,
            ToolWindowScope.Application, DockSlot.Right), () => new AnalysisWindow(this, context, provider));

        context.GetRequiredService<IChartPreprocessorRegistry>().Register(new(id + "/check"), ChartPreparationStage.ValidateSource,
            new(["settings"], ["bpm"]), (chart, _) =>
            {
                Interlocked.Increment(ref _checks);
                bool valid = chart.Chart.TryGetProperty("settings", out var settings) &&
                    settings.TryGetProperty("bpm", out var bpm) && bpm.TryGetDouble(out var value) && value > 0;
                return ValueTask.FromResult(new ChartPreparationResult(valid ? [] : [new("Invalid chart BPM.", 0)]));
            });
        var passes = context.GetRequiredService<IRenderPassRegistry>();
        string palette = id + "/warm-palette";
        passes.RegisterShader(new(palette, UiHost.Editor), RenderStage.PrepareFrame,
            new("vec4 modEffect(vec2 uv, vec4 color) { return vec4(1.0, 0.88, 0.55, 1.0); }", ProducesAuxiliary: true),
            enabled: _ => _preview);
        passes.RegisterShader(new(id + "/warm-color", UiHost.Editor), RenderStage.SceneAfterNative,
            new("vec4 modEffect(vec2 uv, vec4 color) { return color * mix(vec4(1.0), texture(_ModInput0, uv), _ModParameters.x); }",
                Inputs: [palette]), _ => new(_amount, 0, 0, 0), _ => _preview);
        passes.RegisterShader(new(id + "/depth", UiHost.Editor, Order: 10), RenderStage.SceneAfterNative,
            new("""
                vec4 modEffect(vec2 uv, vec4 color) {
                    float surface = 1.0 - step(0.999999, readDepth(uv));
                    return vec4(mix(color.rgb, color.rgb * vec3(0.55, 1.0, 1.0), surface * 0.55), color.a);
                }
                """) { UsesSceneDepth = true }, enabled: _ => _depth);
        passes.RegisterShader(new(id + "/history", UiHost.Editor, Order: 20), RenderStage.SceneAfterNative,
            new("""
                vec4 modEffect(vec2 uv, vec4 color) {
                    return mix(color, readHistory(uv), 0.48);
                }
                """) { UsesHistory = true }, enabled: _ => _history);
        context.GetService<IAudioProcessorRegistry>()?.RegisterRealtime(new(id + "/master"), AudioBus.Mix,
            () => new LiveGain(this));
        var custom = context.GetService<ICustomEventRegistry>();
        custom?.Register(new(id + "/warm-pulse"),
            value =>
            {
                double duration = value.Payload.TryGetProperty("duration", out var length) ? length.GetDouble() : 1;
                return new(value.Time, value.Time + Math.Clamp(duration, .1, 10), value.Payload);
            },
            (effect, frame) =>
            {
                float strength = effect.Payload.TryGetProperty("strength", out var amount) ? amount.GetSingle() : .7f;
                float remaining = (float)((effect.End - frame.Time) / (effect.End - effect.Start));
                float weight = Math.Clamp(strength, 0, 1) * remaining;
                return new(new Vector4(1, 1 - weight * .2f, 1 - weight * .5f, 1), Vector4.Zero);
            },
            new("pulse", 1, [
                new("duration", "duration", CustomFieldKind.Number, JsonSerializer.SerializeToElement(1f), .1f, 10),
                new("strength", "strength", CustomFieldKind.Number, JsonSerializer.SerializeToElement(.7f), 0, 1),
            ]));
        custom?.RegisterCompatibilityConverter(new(id + "/warm-pulse"), (evt, _) =>
        {
            double duration = evt.Payload.TryGetProperty("duration", out var length) ? length.GetDouble() : 1;
            double strength = evt.Payload.TryGetProperty("strength", out var amount) ? amount.GetDouble() : .7;
            var fields = JsonSerializer.SerializeToElement(new
            {
                duration = Math.Clamp(duration, .1, 10) * Math.Max(1, evt.Bpm) / 60,
                angleOffset = evt.AngleOffsetDegrees, plane = "Foreground", startColor = "ff9c40", endColor = "ff9c40",
                startOpacity = Math.Clamp(strength, 0, 1) * 25, endOpacity = 0, ease = "Linear",
            });
            return ValueTask.FromResult<CompatibilityConversion?>(new([new(evt.Floor, "Flash", fields)],
                Note: context.Text("compat-pulse")));
        });
        context.GetService<IExportJobRegistry>()?.Register(new(new(id + "/report", UiHost.Editor), "export-report", "json"),
            async (job, cancellation) =>
            {
                await File.WriteAllTextAsync(job.TemporaryPath,
                    JsonSerializer.Serialize(new { job.Chart.FileName, Tiles = job.Chart.LastFloor + 1 }), cancellation);
                job.Progress.Report(1);
            });
        context.GetRequiredService<IExportPostprocessorRegistry>().Register(new(id + "/verify-artifact", UiHost.Export),
            (file, _) =>
            {
                context.Log($"Exported {file.OutputFileName} ({new FileInfo(file.TemporaryPath).Length:N0} bytes).");
                return ValueTask.CompletedTask;
            });
        context.Log($"Activated with {provider}.");
    }

    [MenuItem("menu/open", Shortcut = "Ctrl+Shift+F9", SnapshotChart = false)]
    [MenuItem("Tools/menu/open", SnapshotChart = false)]
    private async ValueTask OpenAnalysis(CancellationToken token) => await _windows.OpenAsync(_analysisWindow, token);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class LiveGain(ChartToolsMod mod) : IRealtimeAudioProcessor
    {
        public void Reset(AudioFormat format, long firstFrame) { }
        public void Process(in RealtimeAudioBlock block, Span<float> samples)
        {
            float gain = mod._audioGain;
            for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        }
        public void Dispose() { }
    }

    private sealed class AnalysisWindow(ChartToolsMod mod, IModContext context, string provider) : IToolWindow
    {
        public IReadOnlyList<UiNode> Build(in ToolWindowContext view)
        {
            var chart = view.Command.Chart;
            string file = chart is null ? context.Text("no-chart") : chart.FileName ?? "Untitled";
            return
            [
                new UiText(provider, Muted: true),
                new UiText($"{context.Text("chart")}  {file}"),
                new UiText($"{context.Text("floors")}  {(chart?.LastFloor ?? -1) + 1:N0}"),
                new UiText($"{context.Text("position")}  {view.Command.Playback.Time:F3} s"),
                new UiToggle("warm", context.Text("preview-color"), mod._preview),
                new UiNumber("amount", context.Text("strength"), mod._amount, 0, 1),
                new UiToggle("depth", context.Text("depth"), mod._depth),
                new UiToggle("history", context.Text("history"), mod._history),
                new UiNumber("gain", context.Text("gain"), mod._audioGain, 0, 2),
                new UiText($"{context.Text("checks")}  {Volatile.Read(ref mod._checks)}"),
            ];
        }

        public ValueTask HandleAsync(UiAction action, CancellationToken token)
        {
            if (action.Value is { } value)
            {
                if (action.Id == "warm") mod._preview = value.GetBoolean();
                if (action.Id == "amount") mod._amount = value.GetSingle();
                if (action.Id == "depth") mod._depth = value.GetBoolean();
                if (action.Id == "history") mod._history = value.GetBoolean();
                if (action.Id == "gain") mod._audioGain = value.GetSingle();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
