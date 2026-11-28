using OpenTK.Mathematics;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    internal static Vector2[] BuildPreciseFloorPositions(IReadOnlyList<Floor> floors,
        ReadOnlySpan<PlanetTrackFloor> track, float spacing, CancellationToken token)
    {
        Vector2[] remainders = new Vector2[floors.Count];
        double x = floors[0].position.X;
        double y = floors[0].position.Y;
        for (int i = 0; i < floors.Count; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();

            Floor floor = floors[i];
            remainders[i] = StoreFloorPosition(floor, x, y);
            if (i + 1 < floors.Count)
            {
                (double sin, double cos) = Math.SinCos(MathHelper.DegreesToRadians((double)floor.angle));
                double distance = spacing * ((double)track[i + 1].Radius + track[i].HoldDistance);
                x += distance * cos;
                y += distance * sin;
            }
        }
        return remainders;
    }

    private static Vector2 StoreFloorPosition(Floor floor, double x, double y)
    {
        floor.position = new System.Numerics.Vector2((float)x, (float)y);
        return new Vector2((float)(x - floor.position.X), (float)(y - floor.position.Y));
    }

    internal static void ApplyTrackPlacement(
        IReadOnlyList<Floor> floors, FloorTrackState[] preview, FloorTrackState[] runtime,
        Span<Vector2> remainders = default)
    {
        var view = new FloorTrackStateTable(preview);
        ApplyTrackPlacement(floors, view, ReferenceEquals(preview, runtime) ? view : new FloorTrackStateTable(runtime), remainders);
    }

    private static void ApplyTrackPlacement(IReadOnlyList<Floor> floors, FloorTrackStateTable preview,
        FloorTrackStateTable runtime, Span<Vector2> remainders)
    {
        for (int i = 0; i < floors.Count; i++)
        {
            var state = runtime[i];
            Vector2 offset = state.Transform.Offset;
            Floor floor = floors[i];
            if (remainders.IsEmpty)
            {
                floor.position += new System.Numerics.Vector2(offset.X, offset.Y);
            }
            else if (offset != Vector2.Zero)
            {
                remainders[i] = StoreFloorPosition(floor, (double)floor.position.X + remainders[i].X + offset.X,
                    (double)floor.position.Y + remainders[i].Y + offset.Y);
            }
            if (!ReferenceEquals(preview, runtime))
            {
                if (offset != Vector2.Zero)
                {
                    var editorState = preview[i];
                    preview[i] = editorState with { Transform = RelativeToMesh(editorState.Transform, offset) };
                }
                preview.CompactCompletedPage(i);
            }
            if (offset != Vector2.Zero)
                runtime[i] = state with { Transform = RelativeToMesh(state.Transform, offset) };
            runtime.CompactCompletedPage(i);
        }
    }

    private static FloorRenderTransform RelativeToMesh(FloorRenderTransform transform, Vector2 offset) =>
        transform with { Offset = transform.Offset - offset };

    private static void ApplyHoldIcons(IReadOnlyList<Floor> floors, ReadOnlySpan<PlanetTrackFloor> track,
        FloorTrackStateTable states)
    {
        for (int i = 0; i + 1 < floors.Count; i++)
        {
            if (!track[i].HasHold)
            {
                continue;
            }

            FloorIcon holdIcon = track[i].HoldLength == 0 ? FloorIcon.HoldArrowShort : FloorIcon.HoldArrowLong;
            if (states[i].Icon is null && floors[i].icon is FloorIcon.None or FloorIcon.Vfx)
            {
                states[i] = states[i] with { Icon = holdIcon };
            }

            if (states[i + 1].Icon is null && floors[i + 1].icon is FloorIcon.None or FloorIcon.Vfx)
            {
                states[i + 1] = states[i + 1] with { Icon = holdIcon, ReverseIcon = true };
            }
        }
    }

    private (Vector2 Center, float Rotation) PlanetAnchor(int index) => PlanetAnchor(index, _playTime);

    private (Vector2 Center, float Rotation) PlanetAnchor(int index, double time)
    {
        var position = _floors[index].position;
        Vector2 anchor = new(position.X, position.Y);
        var motion = _runtimeTrackActive && _trackRuntime is { } runtime
            ? runtime.PlanetMotionAt(index, time)
            : default;
        return (anchor + motion.Offset, motion.Rotation);
    }

    public bool HiddenFloorJudgment(int floor) =>
        (uint)floor < (uint)_trackStates.Count && _trackStates[floor].HideJudgment;

    internal void SetRuntimeTrackMode(bool active)
    {
        if (_runtimeTrackActive != active)
        {
            _runtimeTrackActive = active;
            _floorVisualTime = double.NaN;
            _residentTime = FloorTimeRange.Empty;
            if (_cameraTimeline is not null)
                _cameraSnap = true;
        }
    }

    private bool _editorTilesOnly;

    internal void SetEditorTilesOnly(bool enabled)
    {
        if (enabled)
            SetRuntimeTrackMode(false);
        if (_editorTilesOnly == enabled)
            return;
        _editorTilesOnly = enabled;
        _floorVisualTime = double.NaN;
    }

    private void UpdateTrackVisuals()
    {
        RuntimeTrackTimeline? runtime = _runtimeTrackActive ? _trackRuntime : null;
        runtime?.Advance(_playTime);
        var states = runtime?.States ?? _trackStates;
        float? volume = null;
        if (_activeFloorChunks is { } chunks)
        {
            foreach (ResidentFloorChunk chunk in chunks)
            {
                UpdateFloorVisuals(chunk.Renderer, chunk.Floors, runtime, states, ref volume);
            }
        }
        else if (_officialRenderer is { HasGeometry: true } renderer)
        {
            UpdateFloorVisuals(renderer, _residentFloors, runtime, states, ref volume);
        }
        if (_editorAngleTrack is { HasGeometry: true } angleTrack)
            for (int i = 0; i < angleTrack.FloorCount; i++)
                UpdateFloorVisual(angleTrack, i, _editorAngleAnchor + i, null, states, ref volume, anglePreview: true);
        _floorVisualTime = _playTime;
    }

    private void UpdateFloorVisuals(OfficialFloorRenderer renderer, int[]? indices, RuntimeTrackTimeline? runtime,
        IReadOnlyList<FloorTrackState> states, ref float? volume)
    {
        renderer.SetMaterials(states, runtime?.MaterialRevision ?? 0, _styleOverride, indices);
        int[]? selection = renderer.LevelSelection;
        int count = selection?.Length ?? renderer.FloorCount;
        for (int local = 0; local < count; local++)
        {
            int i = selection?[local] ?? local;
            int floor = indices?[i] ?? i;
            UpdateFloorVisual(renderer, i, floor, runtime, states, ref volume);
        }
    }

    private void UpdateFloorVisual(OfficialFloorRenderer renderer, int local, int floor, RuntimeTrackTimeline? runtime,
        IReadOnlyList<FloorTrackState> states, ref float? volume, bool anglePreview = false)
    {
        FloorTrackState state = states[floor];
        FloorRenderTransform transform = state.Transform;
        float opacity = state.Hidden ? 0f : state.Opacity;
        if (runtime is not null)
            (transform, opacity) = runtime.TransformAt(floor, _playTime, _hidePassedFloors);

        Vector4? color = _floorRenderColors?[floor];
        if (color is null && volume is null && state.Mode == TrackColorMode.Volume)
            volume = ReadTrackVolume();
        float trackVolume = volume.GetValueOrDefault();

        if (_floorRenderTransforms is { } transforms)
            transform = transforms[floor];
        if (_floorPositionRemainders is { } remainders)
            transform = transform with { Offset = transform.Offset + remainders[floor] };
        if (_editorTilesOnly)
        {
            opacity = 1f;
            Vector2 scale = transform.Scale;
            transform = transform with { Scale = new(scale.X == 0f ? 1f : scale.X, scale.Y == 0f ? 1f : scale.Y) };
        }
        if (_editorAngleAnchor >= 0)
        {
            if (floor > _editorAngleAnchor)
                transform = transform with { Offset = transform.Offset + _editorAngleOffset };
            // The two connecting meshes are replaced; subsequent tiles keep their meshes and shift together.
            if (!anglePreview && (uint)(floor - _editorAngleAnchor) <= 1)
                opacity = 0f;
        }

        // The static chart shows unlit tiles. Track glow intensity controls the playback effect.
        var appearance = runtime is not null
            ? runtime.AppearanceAt(floor, _playTime, opacity, trackVolume, color)
            : new FloorAppearance(color ?? state.EvaluateColor(floor, (float)_playTime, trackVolume), opacity);
        if (_editorTilesOnly)
            appearance = appearance with { Color = appearance.Color with { W = 1f } };
        renderer.SetFloorVisual(local, transform, appearance);
    }

    private Thrust.Audio.AudioSampleWindow? _trackAudioWindow;
    private bool _trackAudioFailed;

    private float ReadTrackVolume()
    {
        if (_trackAudioFailed || _audioMusic is not { SampleRate: > 0, Channels: > 0 } music)
            return 0f;

        double musicTime = _playTime + (_offlineSubSample ? 0.0 : AudioClockShift);
        long start = (long)Math.Clamp(Math.Max(0, musicTime - .03) * music.SampleRate, 0, music.FrameCount);
        long end = (long)Math.Clamp(Math.Max(0, musicTime) * music.SampleRate, start, music.FrameCount);
        if (start == end) return 0f;

        try
        {
            _trackAudioWindow ??= new(music, Math.Max(8192, (int)(music.SampleRate * .03 + 1)));
            var samples = _trackAudioWindow.Read(start, (int)(end - start));
            if (samples.IsEmpty) return 0f;
            double power = 0;
            foreach (short sample in samples)
            {
                double value = sample / 32768.0;
                power += value * value;
            }
            return (float)Math.Sqrt(power / samples.Length);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _trackAudioWindow?.Dispose();
            _trackAudioWindow = null;
            _trackAudioFailed = true;
            Console.Error.WriteLine($"[Audio] Track volume: {error.Message}");
            return 0f;
        }
    }
}
