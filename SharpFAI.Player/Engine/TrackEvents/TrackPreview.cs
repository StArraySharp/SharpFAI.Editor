using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

internal static class TrackPreview
{
    public static bool Applies(PlaybackEvent item, bool includeEditorOnly) => item.IsActive && item switch
    {
        PositionTrack position => includeEditorOnly || position.Enabled("editorOnly", position.EditorOnly) != true,
        ColorTrack or AnimateTrack or TileDimensions or Hide or SetFloorIcon => true,
        _ => false,
    };

    public static bool ThisFloorOnly(PlaybackEvent item) => item switch
    {
        PositionTrack position => position.Enabled("justThisTile", position.JustThisTile) == true,
        ColorTrack color => color.Enabled("justThisTile", color.JustThisTile) == true,
        AnimateTrack animate => animate.Enabled("justThisTile", animate.JustThisTile) == true,
        TileDimensions dimensions => dimensions.Enabled("justThisTile", dimensions.JustThisTile) == true,
        Hide hide => hide.Enabled("justThisTile", hide.JustThisTile) == true,
        SetFloorIcon => true,
        _ => false,
    };

    public static FloorTrackState Apply(PlaybackEvent item, FloorTrackState state, in TrackPreviewContext context) =>
        item switch
        {
            PositionTrack position => ApplyPosition(position, state, context),
            ChangeTrack change => state.ApplyColorFields(change, change, change.Floor).ApplyAnimationFields(
                change, change.TrackAnimation, change.TrackDisappearAnimation, change.BeatsAhead, change.BeatsBehind),
            ColorTrack color => state.ApplyColorFields(color, color, color.Floor),
            AnimateTrack animate => ApplyAnimation(animate, state, context.Bpm),
            TileDimensions dimensions => state with
            {
                Dimensions = new Vector2(
                    MathF.Max(0.01f, dimensions.Enabled("width", dimensions.Width) ?? state.Dimensions.X * 100f),
                    MathF.Max(0.01f, dimensions.Enabled("length", dimensions.Length) ?? state.Dimensions.Y * 100f)) / 100f,
            },
            Hide hide => state with
            {
                HideIcon = hide.Enabled("hideTileIcon", hide.HideTileIcon) ?? state.HideIcon,
                HideJudgment = hide.Enabled("hideJudgment", hide.HideJudgment) ?? state.HideJudgment,
                Hidden = hide.Enabled("hideTrack", hide.HideTrack) ?? state.Hidden,
            },
            SetFloorIcon icon => ApplyIcon(icon, state),
            _ => state,
        };

    private static FloorTrackState ApplyAnimation(AnimateTrack item, FloorTrackState state, double bpm)
    {
        bool changesAppear = item.Enabled("trackAnimation", item.TrackAnimation) is not null;
        bool changesDisappear = item.Enabled("trackDisappearAnimation", item.TrackDisappearAnimation) is not null;
        double eventBpm = double.IsFinite(bpm) && bpm > 1e-6 ? bpm : state.LastAnimateTrackBpm;

        // The last AnimateTrack sets the BPM for beat offsets. An exit-only event uses the previous event's BPM.
        return state.ApplyAnimationFields(item, item.TrackAnimation, item.TrackDisappearAnimation,
            changesAppear ? item.BeatsAhead : null, changesDisappear ? item.BeatsBehind : null) with
        {
            AnimationReferenceBpm = changesAppear ? eventBpm : state.LastAnimateTrackBpm,
            LastAnimateTrackBpm = eventBpm,
        };
    }

    private static FloorTrackState ApplyPosition(PositionTrack item, FloorTrackState state, in TrackPreviewContext context)
    {
        FloorRenderTransform transform = state.Transform;
        if (item.Enabled("positionOffset", item.PositionOffset) is EventVector offset)
        {
            int relative = item.Enabled("relativeTo", item.RelativeTo)?.Resolve(item.Floor, context.Floors.Count - 1)
                ?? item.Floor;
            Vector2 delta = new Vector2(offset.X ?? 0f, offset.Y ?? 0f) * context.TileSpacing;
            if (relative != item.Floor)
            {
                delta += context.Position(relative) - context.Position(item.Floor, includeOffset: false)
                    - context.ContinuingOffset;
            }

            transform = transform with { Offset = transform.Offset + delta };
        }

        if (item.Enabled("scale", item.Scale) is EventVector scale)
        {
            transform = transform with
            {
                Scale = new Vector2(
                    scale.X is float x ? x / 100f : transform.Scale.X,
                    scale.Y is float y ? y / 100f : transform.Scale.Y),
            };
        }

        return state with
        {
            Transform = transform with
            {
                Rotation = item.Enabled("rotation", item.Rotation) is float rotation
                    ? MathHelper.DegreesToRadians(rotation) : transform.Rotation,
            },
            Opacity = item.Enabled("opacity", item.Opacity) is float opacity
                ? opacity / 100f : state.Opacity,
            StickToFloors = item.Enabled("stickToFloors", item.StickToFloors) ?? state.StickToFloors,
        };
    }

    private static FloorTrackState ApplyIcon(SetFloorIcon item, FloorTrackState state) =>
        (item.Enabled("floorIcon", item.FloorIcon) ?? item.Enabled("icon", item.Icon)) switch
        {
            "HoldReleaseShort" => state with { Icon = FloorIcon.HoldArrowShort, ReverseIcon = true },
            "HoldReleaseLong" => state with { Icon = FloorIcon.HoldArrowLong, ReverseIcon = true },
            { } text when Enum.TryParse(text, true, out FloorIcon icon) => state with { Icon = icon, ReverseIcon = false },
            _ => state,
        };
}

internal readonly record struct TrackPreviewContext(
    IReadOnlyList<Floor> Floors, IReadOnlyList<FloorTrackState> Resolved, int Current, float TileSpacing, double Bpm,
    Vector2 ContinuingOffset = default)
{
    public Vector2 Position(int floor, bool includeOffset = true)
    {
        var position = Floors[floor].position;
        Vector2 authored = new(position.X, position.Y);
        return includeOffset && floor < Current ? authored + Resolved[floor].Transform.Offset : authored;
    }
}
