using OpenTK.Mathematics;
using SharpFAI.Serialization;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    private Vector2 LevelCameraTarget(Vector2 floor)
    {
        Vector2 offset = _settings.CameraOffset * TileSpacing;
        return _settings.CameraRelativeTo switch
        {
            EventEnums.CamMovementType.Global => offset,
            EventEnums.CamMovementType.Tile or EventEnums.CamMovementType.LastPosition
                or EventEnums.CamMovementType.LastPositionNoRotation =>
                _cameraOrigin + offset,
            _ => floor + offset,
        };
    }

    private float AuthoredZoom(float height, float zoomPercent) =>
        zoomPercent == 0f ? .5f
            : Math.Clamp(height / (1000f * (zoomPercent / 100f)), _camera.MinZoom, _camera.MaxZoom);

    internal float GetExportZoom() => _settings.CameraZoom is float zoom ? AuthoredZoom(_camera.ViewportHeight, zoom) : 0.5f;

    internal bool HasCameraEventZoom => _cameraEventZoomApplied;

    private double EasePlanet(int floor, double time)
    {
        if (!_runtimeTrackActive || time <= 0 || time >= 1)
        {
            return time;
        }

        var rotation = _sceneEffects?.PlanetRotation(floor);
        var ease = rotation?.Enabled("ease", rotation.Ease) ?? _settings.PlanetEase;
        int parts = rotation?.Enabled("easeParts", rotation.EaseParts) ?? _settings.PlanetEaseParts;
        if (parts <= 0 || ease == EventEnums.Ease.Linear)
            return time;
        bool mirrorParts = rotation?.Enabled("easePartBehavior", rotation.EasePartBehavior) is { } behavior
            ? behavior != "Repeat" : _settings.MirrorPlanetEase;
        double scaled = time * parts;
        int index = (int)scaled;
        float fraction = (float)(scaled - index);
        bool mirror = mirrorParts && index % 2 == 1;
        float eased = mirror ? 1f - TrackEase.Evaluate(ease, 1f - fraction)
            : TrackEase.Evaluate(ease, fraction);
        return (index + eased) / parts;
    }

    private float CameraPulseScale()
    {
        if (!_settings.PulseOnFloor || !_runtimeTrackActive || _cameraFree || _currentIndex <= 0
            || _currentIndex >= _entryTimes.Length)
        {
            return 1f;
        }

        double hitTime = ManualAttemptActive ? _cameraPulseTime : _entryTimes[_currentIndex];
        return CameraMotion.PulseScale((_playTime - hitTime) / _pitch);
    }
}
