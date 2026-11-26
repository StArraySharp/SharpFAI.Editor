using SharpFAI.ModApi;

namespace SharpFAI.Player.Engine;

public sealed partial class LevelRenderer
{
    internal void TickInspectionLoad()
    {
        PumpLoad();
        PumpEffectWarmup();
    }

    internal ChartFrameState InspectRenderedFrame()
    {
        var position = _camera.Position;
        if (_visualSettings.CameraShake && !_cameraFree) position += _cameraShake;
        var camera = new ChartCamera(position.X, position.Y, _camera.Rotation, _camera.Zoom * CameraPulseScale(),
            !_cameraRenderDisabled || _cameraFree);
        var filters = _backgroundPasses.Select(entry => InspectFilter("background", entry))
            .Concat(_scenePasses.Select(entry => InspectFilter("scene", entry))).ToArray();
        return new(_currentIndex, _playTime, camera, filters, DrawCalls, DrawnTriangles);
    }

    private static ChartFilter InspectFilter(string layer, Filters.FilterTimeline.Entry entry)
    {
        Dictionary<string, float[]> parameters = new(StringComparer.Ordinal);
        foreach (string name in entry.Filter.Parameters)
            if (entry.Filter.GetParameter(name) is { } value &&
                float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W))
                parameters[name] = [value.X, value.Y, value.Z, value.W];
        return new(layer, entry.Name, parameters);
    }
}
