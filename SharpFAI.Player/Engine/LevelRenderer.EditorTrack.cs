using OpenTK.Mathematics;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    internal readonly record struct EditorInsertedFloor(Vector2 Center, double Incoming, double Outgoing, bool Midspin);

    private OfficialFloorRenderer? _editorInsertedTrack;
    private OfficialFloorMeshBaker.BakeCache? _editorInsertedCache;
    private int _editorInsertedVersion = -1;
    private int _editorInsertedAnchor = -1;
    private bool _editorInsertedTilesOnly;
    private OfficialFloorRenderer? _editorAngleTrack;
    private OfficialFloorMeshBaker.BakeCache? _editorAngleCache;
    private int _editorAngleAnchor = -1;
    private double _editorAngle;
    private Vector2 _editorAngleOffset;
    private PositionTrackGuides? _positionTrackGuides;
    private bool _positionTrackGuidesDirty = true;
    internal bool ShowPositionTrackGuides { get; set; }
    internal int EditorInsertedFloorCount => _editorInsertedTrack?.FloorCount ?? 0;

    internal bool EditorInsertedTrackNeedsUpdate(int version, bool tilesOnly) =>
        version != _editorInsertedVersion || tilesOnly != _editorInsertedTilesOnly;

    internal void SetEditorInsertedTrack(int version, int anchor, IReadOnlyList<EditorInsertedFloor> inserted, bool tilesOnly)
    {
        if (inserted.Count == 0 || (uint)anchor >= (uint)_trackStates.Count)
        {
            ClearEditorInsertedTrack();
            return;
        }
        var floors = new Floor[inserted.Count];
        var states = new FloorTrackState[inserted.Count];
        var anchorState = _trackStates[anchor];
        var state = anchorState with { Icon = FloorIcon.None, ReverseIcon = false, Hidden = false };
        for (int i = 0; i < inserted.Count; i++)
        {
            var tile = inserted[i];
            int index = anchor + 1 + i;
            floors[i] = new Floor(tile.Outgoing, tile.Incoming - 180, new(tile.Center.X, tile.Center.Y))
            {
                index = index,
                renderOrder = -index,
                isMidspin = tile.Midspin,
                angle = (float)tile.Outgoing,
            };
            states[i] = state;
        }

        _officialRenderer ??= new OfficialFloorRenderer(_uploader, _floorTextureCapacity);
        _editorInsertedTrack ??= new OfficialFloorRenderer(_uploader, _floorTextureCapacity, _officialRenderer);
        _editorInsertedCache ??= new OfficialFloorMeshBaker.BakeCache(shapeCacheLimit: 64);
        var geometry = OfficialFloorMeshBaker.BakeLevel(floors, states, _floorTextureCapacity,
            levelDirectory: Path.GetDirectoryName(LevelPath), styleOverride: _styleOverride,
            cache: _editorInsertedCache, computeBounds: false);
        try
        {
            _editorInsertedTrack.SetGeometry(geometry, releaseCpuMesh: true);
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
        var transform = state.Transform with { Offset = Vector2.Zero };
        if (tilesOnly)
            transform = transform with { Scale = new(transform.Scale.X == 0f ? 1f : transform.Scale.X,
                transform.Scale.Y == 0f ? 1f : transform.Scale.Y) };
        float volume = state.Mode == TrackColorMode.Volume ? ReadTrackVolume() : 0f;
        for (int i = 0; i < floors.Length; i++)
        {
            Vector4 color = state.EvaluateColor(floors[i].index, (float)_playTime, volume);
            if (tilesOnly) color.W = 1f;
            _editorInsertedTrack.SetFloorVisual(i, transform,
                new FloorAppearance(color, tilesOnly ? 1f : state.Opacity));
        }
        _editorInsertedVersion = version;
        _editorInsertedAnchor = anchor;
        _editorInsertedTilesOnly = tilesOnly;
    }

    internal void ClearEditorInsertedTrack()
    {
        if (_editorInsertedVersion < 0) return;
        _editorInsertedTrack?.ClearGeometry();
        _editorInsertedCache = null;
        _editorInsertedVersion = -1;
        _editorInsertedAnchor = -1;
    }

    internal void SetEditorAnglePreview(int anchor, double angle)
    {
        if (anchor == _editorAngleAnchor && angle == _editorAngle) return;
        Floor selected = _floors[anchor];
        Floor[] boundary;
        FloorTrackState[] states;
        Vector2 offset = Vector2.Zero;
        if (anchor + 1 < _floors.Count)
        {
            int next = anchor + 1;
            boundary = [WithAngles(selected, angle, selected.exitAngle), WithAngles(_floors[next], _floors[next].entryAngle, angle - 180)];
            states = [_trackStates[anchor], _trackStates[next]];
            float distance = TileSpacing * (_planetTrack[next].Radius + _planetTrack[anchor].HoldDistance);
            offset = Direction(angle) * TileSpacing - Direction(selected.angle) * distance;
        }
        else
        {
            boundary = [WithAngles(selected, angle, selected.exitAngle)];
            states = [_trackStates[anchor]];
        }

        _officialRenderer ??= new OfficialFloorRenderer(_uploader, _floorTextureCapacity);
        _editorAngleTrack ??= new OfficialFloorRenderer(_uploader, _floorTextureCapacity, _officialRenderer);
        _editorAngleCache ??= new OfficialFloorMeshBaker.BakeCache(shapeCacheLimit: 64);
        FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel(boundary, states, _floorTextureCapacity,
            levelDirectory: Path.GetDirectoryName(LevelPath), styleOverride: _styleOverride,
            cache: _editorAngleCache, computeBounds: false);
        try
        {
            _editorAngleTrack.SetGeometry(geometry, releaseCpuMesh: true);
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
        _editorAngleAnchor = anchor;
        _editorAngle = angle;
        _editorAngleOffset = offset;
        _floorVisualTime = double.NaN;

        static Floor WithAngles(Floor floor, double outgoing, double incoming) => new(outgoing, incoming, floor.position)
        {
            index = floor.index,
            renderOrder = floor.renderOrder,
            angle = (float)outgoing,
            isMidspin = floor.isMidspin,
            icon = floor.icon,
            iconAngle = floor.iconAngle,
            iconFlipped = floor.iconFlipped,
            swirlRed = floor.swirlRed,
            textureRotation = floor.textureRotation,
            textureOffsetX = floor.textureOffsetX,
            textureOffsetY = floor.textureOffsetY,
        };

        static Vector2 Direction(double degrees)
        {
            (double sin, double cos) = Math.SinCos(degrees * Math.PI / 180);
            return new((float)cos, (float)sin);
        }
    }

    internal void ClearEditorAnglePreview()
    {
        if (_editorAngleAnchor < 0) return;
        _editorAngleAnchor = -1;
        _editorAngleOffset = Vector2.Zero;
        _editorAngleTrack?.ClearGeometry();
        _floorVisualTime = double.NaN;
    }

    private void InvalidatePositionTrackGuides()
    {
        _positionTrackGuides?.Clear();
        _positionTrackGuidesDirty = true;
    }

    private void DrawPositionTrackGuides(Matrix4 viewProjection)
    {
        if (!ShowPositionTrackGuides || _editorEvents is not { } events) return;
        if (_positionTrackGuidesDirty)
        {
            _positionTrackGuidesDirty = false;
            _positionTrackGuides ??= new PositionTrackGuides(_uploader);
            _positionTrackGuides.Update(_floors, _trackStates, _planetTrack, events, TileSpacing);
        }
        if (_positionTrackGuides is { HasGeometry: true } guides)
        {
            guides.Draw(viewProjection, _lastFrameTime);
            DrawCalls++;
        }
    }
}
