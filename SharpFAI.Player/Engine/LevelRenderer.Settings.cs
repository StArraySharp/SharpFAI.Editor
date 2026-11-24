using OpenTK.Mathematics;
using SharpFAI.Serialization;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    private ApplicationSettings _visualSettings = ApplicationSettings.PlayerDefaults;
    private int _loadedDecorationTexturePercent = 100;
    private Vector4 _firePlanetTint = new(1f, 0f, 0f, 1f);
    private Vector4 _icePlanetTint = new(0f, 0f, 1f, 1f);

    internal void ApplyVisualSettings(ApplicationSettings settings)
    {
        var previous = _visualSettings;
        _visualSettings = settings;
        _firePlanetTint = FloorTrackStateResolver.ParseColor(settings.FirePlanetColor) ?? PlanetTint(1);
        _icePlanetTint = FloorTrackStateResolver.ParseColor(settings.IcePlanetColor) ?? PlanetTint(0);
        _trail.FireColorOverride = _firePlanetTint == PlanetTint(1) ? null : _firePlanetTint.Xyz;
        _trail.IceColorOverride = _icePlanetTint == PlanetTint(0) ? null : _icePlanetTint.Xyz;

        if (settings.PlanetTrailPercent != previous.PlanetTrailPercent)
        {
            _trail.ActiveLifetime = PlanetTrail.Lifetime * settings.PlanetTrailPercent / 100.0;
            ResetTrail();
        }

        bool filterPassesChanged = settings.CameraFilters != previous.CameraFilters ||
            settings.ScreenEffects != previous.ScreenEffects;
        if (filterPassesChanged || settings.VisibleDecorations != previous.VisibleDecorations ||
            settings.FirePlanetColor != previous.FirePlanetColor || settings.IcePlanetColor != previous.IcePlanetColor)
        {
            _filterBackend?.ResetCameraHistory();
            _colorHistory?.Reset();
        }
        if (filterPassesChanged && _level is not null)
            BeginShaderPrewarm();
    }

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
