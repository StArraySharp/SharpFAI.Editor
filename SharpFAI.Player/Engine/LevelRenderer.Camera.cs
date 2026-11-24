using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    internal static CameraTimeline? BuildCameraTimeline(IReadOnlyList<PlaybackEvent> events,
        IReadOnlyList<Floor> floors, PlaybackTimeline timeline, LevelPlaybackSettings settings, float spacing,
        FloorTrackState[]? runtimeStates = null)
    {
        if (!events.OfType<MoveCameraEvent>().Any() || floors.Count == 0)
            return null;
        RuntimeTrackTimeline? motion = runtimeStates is not null
            ? new RuntimeTrackTimeline(events, floors, runtimeStates, timeline.EntryTimes, timeline.Pitch, timeline.FloorBpms)
            : null;
        Vector2 AnchorAt(int index, double time)
        {
            var position = floors[index].position;
            if (motion is null)
                return new Vector2(position.X, position.Y);
            motion.Advance(time);
            return new Vector2(position.X, position.Y) + motion.PlanetMotionAt(index, time).Offset;
        }
        var follow = new CameraFollowTimeline(floors, timeline, settings, spacing, AnchorAt);
        var camera = CameraTimeline.Build(events, floors, timeline, settings, spacing, follow.Follow, follow.PlayerAt);
        if (motion is not null)
            camera?.BindFollowTrack(motion.Triggers, follow);
        return camera;
    }
}
