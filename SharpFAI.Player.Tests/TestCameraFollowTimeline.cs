using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Tests;

public class TestCameraFollowTimeline
{
    [Test]
    public void FollowLatchesTargetsAndUsesTheNewBpmOnLanding()
    {
        Floor[] floors = [Tile(0), Tile(100), Tile(200)];
        var follow = new CameraFollowTimeline(floors, MakeTimeline([0, .5, 1], [120, 120, 240]),
            new LevelPlaybackSettings(), 25, (floor, time) => new Vector2(floor * 100 + (float)time * 20, 0));

        Assert.Multiple(() =>
        {
            Assert.That(follow.Follow(1.25, null).X, Is.EqualTo(137.5f));
            Assert.That(follow.Follow(.75, null).X, Is.EqualTo(27.5f));
            Assert.That(follow.PlayerAt(.75).X, Is.EqualTo(115f));
            Assert.That(follow.Follow(1, null).X, Is.EqualTo(55f));
            Assert.That(follow.Follow(1.25, null).X, Is.EqualTo(137.5f));
        });
    }

    [Test]
    public void RestartReplaysLandingsIncludingSimultaneousMidspinHandoffs()
    {
        var follow = new CameraFollowTimeline([Tile(0), Tile(100), Tile(200), Tile(300)],
            MakeTimeline([0, .5, 1, 1], [120, 120, 120, 120]), new LevelPlaybackSettings(), 25);
        var restart = new CameraTimeline.FollowStart(.75, new Vector2(20, 0));

        Assert.Multiple(() =>
        {
            Assert.That(follow.Follow(1.5, restart).X, Is.EqualTo(170f));
            Assert.That(follow.Follow(.75, restart).X, Is.EqualTo(20f));
            Assert.That(follow.Follow(1, restart).X, Is.EqualTo(40f));
            Assert.That(follow.PlayerAt(1).X, Is.EqualTo(300f));
            Assert.That(follow.Follow(1.5, null).X, Is.EqualTo(175f));
            Assert.That(follow.Follow(1.5, restart).X, Is.EqualTo(170f));
        });
    }

    [Test]
    public void HoldOffsetsUseTheEasedPivotInsideTheFollowInterpolation()
    {
        PlanetTrackFloor regular = new(1, 2, 0, 1, 2, -1, 0, false);
        PlanetTrackFloor hold = regular with { HoldLength = 1 };
        PlanetTrackFloor landing = regular with { Radius = 2 };
        var follow = new CameraFollowTimeline([Tile(0), Tile(100), Tile(250)],
            MakeTimeline([0, .5, 1.5], [120, 120, 120], [regular, hold, landing]),
            new LevelPlaybackSettings
            {
                PlanetEase = EventEnums.Ease.InQuad, PlanetEaseParts = 2, MirrorPlanetEase = true,
            }, 25);

        Assert.Multiple(() =>
        {
            // At 3/4 of the Hold, mirrored InQuad over two parts yields 7/8 of the 100-unit extension.
            Assert.That(follow.PlayerAt(1.25).X, Is.EqualTo(187.5f));
            Assert.That(follow.Follow(1.25, null).X, Is.EqualTo(140.625f));
            Assert.That(follow.Follow(1.5, null).X, Is.EqualTo(200f));
            Assert.That(follow.PlayerAt(1.5).X, Is.EqualTo(250f));
        });
    }

    [Test]
    public void ProductionCameraBuildFreezesTheSmoothedWorldPositionAndCanSeekBackward()
    {
        var level = new Level(new Dictionary<string, object>
        {
            ["angleData"] = new Newtonsoft.Json.Linq.JArray(0, 0, 0, 0),
            ["settings"] = Newtonsoft.Json.Linq.JObject.Parse("""
                {"version":15,"bpm":120,"pitch":100,"offset":0,"relativeTo":"Player","position":[0,0],
                 "countdownTicks":1,"separateCountdownTime":false}
                """),
            ["actions"] = new Newtonsoft.Json.Linq.JArray(Newtonsoft.Json.Linq.JObject.Parse("""
                {"floor":2,"eventType":"MoveCamera","relativeTo":"Global","position":[0,0],"duration":2}
                """)),
        });
        var floors = level.CreateFloors();
        var events = SharpFAI.Player.Engine.Events.PlaybackEventBinder.BindForPlayback(level);
        var timeline = LevelRenderer.BuildTimeline(level, floors, events);
        var settings = LevelPlaybackSettings.Read(level);
        float spacing = Floor.length * 2;
        var follow = new CameraFollowTimeline(floors, timeline, settings, spacing);
        var camera = LevelRenderer.BuildCameraTimeline(events, floors, timeline, settings, spacing)!;
        double time = timeline.EntryTimes[2];
        Vector2 frozen = follow.Follow(time, null);
        Assert.That(camera.Evaluate(time, 100).Position, Is.EqualTo(frozen));
        Assert.That(camera.Evaluate(time + .5, 100).Position.X, Is.EqualTo(frozen.X * .5f).Within(1e-4));
        Assert.That(camera.Evaluate(time, 100).Position, Is.EqualTo(frozen));
    }

    private static Floor Tile(float x) => new(0, 180, new System.Numerics.Vector2(x, 0));

    private static LevelRenderer.PlaybackTimeline MakeTimeline(double[] entries, double[] bpms,
        PlanetTrackFloor[]? track = null) => new()
    {
        EntryTimes = entries,
        FloorBpms = bpms,
        PlanetTrack = track ?? Enumerable.Repeat(new PlanetTrackFloor(1, 2, 0, 1, 2, -1, 0, false),
            entries.Length).ToArray(),
    };
}
