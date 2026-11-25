using OpenTK.Mathematics;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    internal readonly record struct EditorInsertedFloor(Vector2 Center, double Incoming, double Outgoing, bool Midspin);

    private OfficialFloorRenderer? _editorInsertedTrack;
    private OfficialFloorMeshBaker.BakeCache? _editorInsertedCache;
    private int _editorInsertedVersion = -1;
    private bool _editorInsertedTilesOnly;
    internal int EditorInsertedFloorCount => _editorInsertedTrack?.FloorCount ?? 0;

    internal bool EditorInsertedTrackNeedsUpdate(int version, bool tilesOnly) =>
        version != _editorInsertedVersion || tilesOnly != _editorInsertedTilesOnly;

    internal void SetEditorInsertedTrack(int version, int anchor, IReadOnlyList<EditorInsertedFloor> inserted, bool tilesOnly)
    {
        if (inserted.Count == 0 || (uint)anchor >= (uint)_trackStates.Length)
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
        _editorInsertedTilesOnly = tilesOnly;
    }

    internal void ClearEditorInsertedTrack()
    {
        if (_editorInsertedVersion < 0) return;
        _editorInsertedTrack?.ClearGeometry();
        _editorInsertedCache = null;
        _editorInsertedVersion = -1;
    }
}
