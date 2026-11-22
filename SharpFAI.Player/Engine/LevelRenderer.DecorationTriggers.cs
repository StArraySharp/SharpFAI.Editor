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
    private Action<HitRecord>? _decorationJudgmentCallback;

    private void ResetDecorationTriggers()
    {
        _imageDecorations?.ResetTriggers();
        _decorationHitboxes = null;
        _lastDecorationConditionFloor = -1;
        _lastDecorationTriggerTime = double.NegativeInfinity;
    }

    private void TriggerDecorationJudgment(HitRecord hit)
    {
        int floor = hit.Accepted ? hit.Floor : PlaySession.CurrentFloor;
        _imageDecorations?.TriggerCondition(floor, hit.Margin, hit.Time, hit.Failed);
        if (hit.Accepted)
            _lastDecorationConditionFloor = floor;
    }

    private void UpdateDecorationTriggers(bool manual, int? autoFrom = null)
    {
        if (_imageDecorations is not { } timeline || _lastDecorationTriggerTime == _playTime)
            return;
        if (_playTime < _lastDecorationTriggerTime)
            ResetDecorationTriggers();
        _lastDecorationTriggerTime = _playTime;
        if (!manual && timeline.HasConditions)
        {
            int first = _lastDecorationConditionFloor + 1;
            if (autoFrom is { } start)
                first = Math.Max(first, start);
            for (int floor = first; floor <= _currentIndex; floor++)
                timeline.TriggerCondition(floor, HitMargin.Auto, _entryTimes[floor]);
            _lastDecorationConditionFloor = _currentIndex;
        }

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
                if (_decorationHitboxes[other].Active &&
                    DecorationHitbox.Overlaps(state.Vertices, _decorationHitboxes[other].Vertices))
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
        return frames;
    }
}
