using System.Text.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;

namespace SharpFAI.Player;

internal sealed partial class PlayerWindow
{
    private ModUi? _modUi;
    private Guid _modDocumentId = Guid.NewGuid();
    private int _modLoadGeneration;
    internal Func<IModRuntime>? CreateMods { get; init; }

    private void InitializeMods()
    {
        // Interactive settings explicitly approve packages. Capture never executes them.
        if (CapturePath is not null || CreateMods?.Invoke() is not { } host) return;
        host.Culture = () => System.Globalization.CultureInfo.GetCultureInfo("zh-Hans");
        _modUi = new(host, ModContext, fullscreen =>
        {
            if (_windowMode.IsFullscreen != fullscreen) _windowMode.RequestToggle();
        }, ModTransport);
        _renderer.Mods = host;
        if (ExportRequest is null)
            _modUi.Initialize();
        else
        {
            _renderer.ModTarget = UiHost.Export;
            host.NoticeWritten = notice => Console.WriteLine($"[Mod {notice.ModId}] {notice.Message}");
            Task.Run(host.InitializeAsync).GetAwaiter().GetResult();
        }
    }

    private void TickMods()
    {
        if (_modUi is not { } ui || ExportRequest is not null) return;
        ui.Tick(new(new(ClientSize.X, ClientSize.Y), new(FramebufferSize.X, FramebufferSize.Y),
            ClientSize.X > 0 ? (float)FramebufferSize.X / ClientSize.X : 1, IsFocused, _windowMode.IsFullscreen), false);
        int generation = ui.Host.LoadGeneration;
        if (_modLoadGeneration == generation) return;
        _modLoadGeneration = generation;
        if (_renderer.LevelPath is { } path)
            _renderer.Load(path);
    }

    private CommandContext ModContext()
    {
        var level = _renderer.Level;
        var chart = level is null ? null : new ChartView(new(_modDocumentId, _modLoadGeneration),
            Path.GetFileName(_renderer.LevelPath), _renderer.FloorCount - 1, _renderer.SelectedFloor,
            floor => floor > 0 && floor <= level.angles.Count ? level.angles[floor - 1] : null,
            key => level.root["settings"]?[key] is { } value ? Read(value) : null,
            floor => level.actions.OfType<JObject>().Where(action => action.Value<int?>("floor") == floor).Select(Read).ToArray());
        var mode = _renderer.ManualPlayEnabled ? PlaybackMode.Manual
            : _renderer.IsPlaying ? PlaybackMode.Auto : PlaybackMode.Preview;
        return new(chart, new(mode, _renderer.PlayTime, _renderer.Duration, _renderer.CurrentIndex, _renderer.IsPlaying));

        static JsonElement Read(JToken source) => JsonSerializer.Deserialize<JsonElement>(source.ToString(Newtonsoft.Json.Formatting.None));
    }

    private bool ModTransport(TransportAction action, double time)
    {
        if (_renderer.Level is null || _renderer.IsLoading || ExportRequest is not null ||
            _renderer.ManualPlayEnabled || _settingsWindow.IsOpen)
            return false;
        switch (action)
        {
            case TransportAction.Play: _renderer.Play(); return true;
            case TransportAction.Pause: _renderer.Pause(); return true;
            case TransportAction.Seek when double.IsFinite(time):
                _renderer.Seek(Math.Clamp(time, 0, _renderer.Duration));
                return true;
            default: return false;
        }
    }
}
