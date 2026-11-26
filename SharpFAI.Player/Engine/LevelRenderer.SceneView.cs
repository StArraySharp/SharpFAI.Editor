using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    internal readonly record struct SceneTile(OfficialFloorMeshBaker.Shape Shape, Vector2d Center,
        Vector2 Scale, float Rotation);

    internal void CollectSceneTiles(FloorRegion region, int focus, int limit, List<int> result)
    {
        result.Clear();
        if (_visibleFloorIndex is { } index)
            index.SelectNearby(region, focus, limit, result);
        else
            for (int i = 0; i < _floors.Count && result.Count < limit; i++)
                result.Add(i);
    }

    internal bool TryGetSceneTile(int index, out SceneTile tile)
    {
        tile = default;
        if ((uint)index >= (uint)_floors.Count) return false;
        var state = _trackStates[index];
        var floor = _floors[index];
        var transform = state.Transform;
        Vector2 remainder = _floorPositionRemainders?[index] ?? Vector2.Zero;
        tile = new(OfficialFloorMeshBaker.Shape.Create(floor, state.Material),
            new Vector2d((double)floor.position.X + remainder.X + transform.Offset.X,
                (double)floor.position.Y + remainder.Y + transform.Offset.Y),
            transform.Scale, transform.Rotation);
        return true;
    }
}
