using OpenTK.Mathematics;
using SharpFAI.Framework;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

/// <summary>Draws decoration floors with the track pipeline while retaining decoration draw order.</summary>
internal sealed class ObjectFloorRenderer : IDisposable
{
    private const int MeshLimit = 128;
    private readonly record struct MeshKey(TrackStyleInfo Style, float Angle, bool Midspin, FloorIcon Icon, bool RedSwirl,
        (FloorTexture Icon, FloorTexture Outline)? IconTextures);
    private sealed class Entry(OfficialFloorRenderer renderer, Matrix4 bounds)
    {
        public OfficialFloorRenderer Renderer { get; } = renderer;
        public Matrix4 Bounds { get; } = bounds;
        public long LastUse { get; set; }
    }

    private readonly GlMeshUploader _uploader = new();
    private readonly OfficialFloorRenderer _pipeline;
    private readonly bool _ownsPipeline;
    private readonly Dictionary<MeshKey, Entry> _meshes = [];
    private readonly OfficialFloorMeshBaker.BakeCache _bakeCache = new(MeshLimit);
    private long _sequence;

    public ObjectFloorRenderer(OfficialFloorRenderer? pipeline)
    {
        _ownsPipeline = pipeline is null;
        _pipeline = pipeline ?? new OfficialFloorRenderer(_uploader, OfficialFloorRenderer.GetTextureCapacity());
    }

    public Matrix4 BoundsFor(ObjectFloorState state, double time, bool editor) => MeshFor(state, time, editor).Bounds;

    public int Draw(ObjectFloorState state, double time, bool editor, in Matrix4 clip, Vector2 viewport, Vector4 tint,
        float opacity, bool forceIconOutline, int mask, int maskMode)
    {
        var entry = MeshFor(state, time, editor);
        Matrix4 projection = clip;
        projection.Row2 = Vector4.Zero;
        projection.Row3.Z = 0f;
        var visual = new FloorDecorationDraw(tint, opacity, -MathHelper.DegreesToRadians(state.IconAngle),
            state.IconFlipped, mask, maskMode);
        entry.Renderer.DrawDecoration(projection, (int)viewport.X, (int)viewport.Y,
            forceIconOutline || state.IconOutlines, visual);
        return entry.Renderer.DrawCalls;
    }

    private Entry MeshFor(ObjectFloorState state, double time, bool editor)
    {
        FloorIcon icon = state.Icon switch
        {
            ObjectFloorIcon.Snail => FloorIcon.Snail,
            ObjectFloorIcon.DoubleSnail => FloorIcon.DoubleSnail,
            ObjectFloorIcon.Rabbit => FloorIcon.Rabbit,
            ObjectFloorIcon.DoubleRabbit => FloorIcon.DoubleRabbit,
            ObjectFloorIcon.Swirl => FloorIcon.Swirl,
            ObjectFloorIcon.Checkpoint => FloorIcon.Checkpoint,
            ObjectFloorIcon.HoldArrowShort => FloorIcon.HoldArrowShort,
            ObjectFloorIcon.HoldArrowLong => FloorIcon.HoldArrowLong,
            ObjectFloorIcon.MultiPlanetTwo => FloorIcon.MultiPlanetTwo,
            ObjectFloorIcon.MultiPlanetThreeMore => FloorIcon.MultiPlanetThreeMore,
            ObjectFloorIcon.Portal => FloorIcon.Portal,
            _ => FloorIcon.None,
        };
        var iconTextures = state.Icon is ObjectFloorIcon.HoldReleaseShort or ObjectFloorIcon.HoldReleaseLong
            ? FloorIconTextures.HoldRelease(state.Icon == ObjectFloorIcon.HoldReleaseLong)
            : FloorIconTextures.ResolveSpeedIcon(icon, state.GraySpeedIcon, state.SpeedIconBpm, time, editor);
        float angle = state.Style.Sprite ? 180f : state.Angle % 360f;
        var key = new MeshKey(state.Style, angle, state.Midspin && !state.Style.Sprite,
            icon, icon == FloorIcon.Swirl && state.RedSwirl, iconTextures);
        if (!_meshes.TryGetValue(key, out var entry))
        {
            var floor = new Floor(-180, 180 - key.Angle, System.Numerics.Vector2.Zero)
            {
                isMidspin = key.Midspin,
                swirlRed = key.RedSwirl,
            };
            var visual = new FloorTrackState(key.Style, Vector4.One, Vector4.One, TrackColorMode.Single, 0, 0, 0, -1, 0, 0)
            {
                Icon = key.Icon,
            };
            var renderer = new OfficialFloorRenderer(_uploader, _pipeline.TextureSlots, _pipeline);
            FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel([floor], [visual], _pipeline.TextureSlots,
                cache: _bakeCache, computeBounds: false, iconTextures: key.IconTextures);
            var bounds = Bounds(geometry.GetVariants(0)[0].Outline);
            try
            {
                renderer.SetGeometry(geometry, releaseCpuMesh: true);
            }
            catch
            {
                geometry.Dispose();
                renderer.Dispose();
                throw;
            }
            if (_meshes.Count == MeshLimit)
            {
                var oldest = _meshes.MinBy(pair => pair.Value.LastUse);
                _meshes.Remove(oldest.Key);
                oldest.Value.Renderer.Dispose();
            }
            _meshes.Add(key, entry = new Entry(renderer, bounds));
        }
        entry.LastUse = ++_sequence;
        return entry;
    }

    private static Matrix4 Bounds(ReadOnlySpan<System.Numerics.Vector2> outline)
    {
        if (outline.IsEmpty)
            return Matrix4.Identity;
        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var point in outline)
        {
            Vector2 xy = new(point.X, point.Y);
            min = Vector2.ComponentMin(min, xy);
            max = Vector2.ComponentMax(max, xy);
        }
        Vector2 size = max - min;
        Vector2 center = (min + max) * .5f;
        return Matrix4.CreateScale(size.X, size.Y, 1f) * Matrix4.CreateTranslation(center.X, center.Y, 0f);
    }

    public void Dispose()
    {
        foreach (var entry in _meshes.Values)
            entry.Renderer.Dispose();
        _meshes.Clear();
        if (_ownsPipeline)
            _pipeline.Dispose();
        _uploader.Dispose();
    }
}
