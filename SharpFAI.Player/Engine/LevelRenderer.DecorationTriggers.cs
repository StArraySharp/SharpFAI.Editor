using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Play;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    private sealed class HitboxFrame(DecorationHitbox definition, int[] targets)
    {
        public readonly DecorationHitbox Definition = definition;
        public readonly Vector2[] Vertices = new Vector2[definition.VertexCount];
        public readonly int[] Targets = targets;
        public Box2? Bounds;
        public bool NeedsBounds;
        public bool Active;
        public bool Touching;
        public double Fired = double.NegativeInfinity;

        public bool Register(bool touching, double time)
        {
            bool fire = touching && (Definition.Trigger switch
            {
                "PerTouch" or "1" => !Touching,
                "Repeat" or "2" => time > Fired && time - Fired >= Definition.RepeatInterval,
                _ => double.IsNegativeInfinity(Fired),
            });
            Touching = touching;
            if (fire)
                Fired = time;
            return fire;
        }
    }

    private HitboxFrame[]? _decorationHitboxes;
    private int _lastDecorationConditionFloor = -1;
    private double _lastDecorationTriggerTime = double.NegativeInfinity;

    private void ResetDecorationTriggers()
    {
        _imageDecorations?.ResetTriggers();
        _lastDecorationConditionFloor = -1;
        ResetDecorationContacts();
    }

    private void ResetDecorationContacts()
    {
        _decorationHitboxes = null;
        _lastDecorationTriggerTime = double.NegativeInfinity;
    }

    private void TriggerDecorationJudgment(HitRecord hit)
    {
        int floor = hit.Accepted ? hit.Floor : PlaySession.CurrentFloor;
        _imageDecorations?.TriggerCondition(floor, hit.Margin, hit.Time, hit.Failed);
        if (hit.Accepted)
            _lastDecorationConditionFloor = floor;
    }

    private void BindDecorationEffects(double[]? bpms)
    {
        if (_imageDecorations is { } triggers)
        {
            triggers.BindTimedEffects(_floors, bpms, _filterTimeline, _backgroundFilterTimeline,
                _cameraTimeline, _backdrop, _sceneEffects, _trackRuntime);
            return;
        }
        _filterTimeline?.SetAutomaticSchedule(null);
        _backgroundFilterTimeline?.SetAutomaticSchedule(null);
        _cameraTimeline?.Triggers.SetAutomatic(null);
        _cameraTimeline?.FollowTrackTriggers?.SetAutomatic(null);
        _backdrop?.Triggers.SetAutomatic(null);
        _sceneEffects?.Triggers.SetAutomatic(null);
        _trackRuntime?.Triggers.SetAutomatic(null);
    }

    private void UpdateAutomaticConditions(int floor)
    {
        if (_imageDecorations is not { HasConditions: true } timeline)
            return;
        if (floor < _lastDecorationConditionFloor || _playTime < _lastDecorationTriggerTime)
            ResetDecorationTriggers();
        timeline.TriggerAutomaticConditions(_lastDecorationConditionFloor + 1, floor, _entryTimes);
        _lastDecorationConditionFloor = floor;
    }

    private void UpdateDecorationTriggers(bool manual)
    {
        if (_imageDecorations is not { } timeline || _lastDecorationTriggerTime == _playTime)
            return;
        if (_playTime < _lastDecorationTriggerTime)
            ResetDecorationTriggers();
        _lastDecorationTriggerTime = _playTime;
        if (timeline.Hitboxes.Count == 0 || manual && (PlaySession.Failed || PlaySession.Countdown > 0))
            return;

        _decorationParentStates.Clear();
        _decorationHitboxes ??= GetHitboxFrames(timeline);
        float zoom = AuthoredZoom(_camera.ViewportHeight, _settings.CameraZoom ?? 100f);
        Matrix4? inverseView = null;
        for (int i = 0; i < timeline.Hitboxes.Count; i++)
        {
            var item = timeline.Hitboxes[i];
            var state = _decorationHitboxes[i];
            var pose = item.At(_playTime);
            // Hiding a sprite only disables its renderer in the original.
            state.Active = pose.Scale.X != 0f && pose.Scale.Y != 0f &&
                timeline.Images.TryGetValue(item.Initial.Image, out _);
            if (!state.Active)
                continue;
            var sprite = timeline.Images[item.Initial.Image];
            Vector2 pixels = new(sprite.Width, sprite.Height);
            var model = ImageDecorationRenderer.ModelFor(pixels, item, pose, _camera, TileSpacing,
                zoom, DecorationFloor, DecorationPlanet);
            if (ImageDecorationRenderer.IsScreenRelative(pose))
                model *= inverseView ??= Matrix4.Invert(_camera.ViewProjection);
            state.Definition.FillVertices(model, pixels, state.Vertices);
            if (state.NeedsBounds)
                state.Bounds = DecorationHitbox.BroadPhaseBounds(state.Vertices);
        }

        PlanetTrackFloor track = _planetTrack[_currentIndex];
        for (int i = 0; i < _decorationHitboxes.Length; i++)
        {
            var state = _decorationHitboxes[i];
            var definition = state.Definition;
            bool touching = state.Active && (definition.AgainstDecorations ? TouchesDecoration(i) :
                definition.CanTouchPivot && DecorationHitbox.TouchesPlanet(state.Vertices, _planetCenter) ||
                definition.CanTouchOrbit && (DecorationHitbox.TouchesPlanet(state.Vertices, _secondaryCenter) ||
                    track.Count == 3 && DecorationHitbox.TouchesPlanet(state.Vertices, _thirdCenter)));
            if (!state.Register(touching, _playTime / _pitch))
                continue;
            if (!definition.Kills)
                timeline.TriggerCollision(definition.EventTags, _playTime);
            else if (manual && PlaySession.FailByHitbox())
            {
                timeline.TriggerCondition(_currentIndex, HitMargin.Overload, _playTime, failed: true);
                Pause();
                break;
            }
        }

        bool TouchesDecoration(int source)
        {
            var state = _decorationHitboxes[source];
            foreach (int other in state.Targets)
            {
                var candidate = _decorationHitboxes[other];
                if (candidate.Active && DecorationHitbox.MayOverlap(state.Bounds, candidate.Bounds) &&
                    DecorationHitbox.Overlaps(state.Vertices, candidate.Vertices))
                    return true;
            }
            return false;
        }
    }

    private static HitboxFrame[] GetHitboxFrames(ImageDecorationTimeline timeline)
    {
        var tagged = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < timeline.Hitboxes.Count; i++)
            foreach (string tag in timeline.Hitboxes[i].Tags)
            {
                if (!tagged.TryGetValue(tag, out var indexes))
                    tagged.Add(tag, indexes = []);
                indexes.Add(i);
            }
        var frames = new HitboxFrame[timeline.Hitboxes.Count];
        HashSet<int> targets = [];
        for (int i = 0; i < frames.Length; i++)
        {
            var definition = timeline.Hitboxes[i].Hitbox!;
            targets.Clear();
            if (definition.AgainstDecorations)
                foreach (string tag in definition.TargetTags)
                    if (tagged.TryGetValue(tag, out var indexes))
                        targets.UnionWith(indexes);
            targets.Remove(i);
            frames[i] = new HitboxFrame(definition, targets.ToArray());
        }
        foreach (var frame in frames)
        {
            if (frame.Targets.Length == 0)
                continue;
            frame.NeedsBounds = true;
            foreach (int target in frame.Targets)
                frames[target].NeedsBounds = true;
        }
        return frames;
    }
}
