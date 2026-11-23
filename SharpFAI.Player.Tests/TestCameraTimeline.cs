using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Tests;

public class TestCameraTimeline
{
    [Test]
    public void ShakeScreenUsesAngleOffsetAndCanBeSampledAfterSeek()
    {
        var actions = new PlaybackEvent[]
        {
            new SceneEffectEvent
            {
                EventType = EventType.ShakeScreen, Floor = 1, AngleOffset = 180, Duration = 4,
                Strength = 25, Intensity = 80, FadeOut = true,
            },
            new SceneEffectEvent
            {
                EventType = EventType.ShakeScreen, Floor = 2, Duration = 1,
                Strength = 0, Intensity = 80,
            },
        };
        Floor[] floors = [new Floor(0, 180, default), new Floor(0, 180, default), new Floor(0, 180, default)];
        using var timeline = SceneEffectTimeline.Build(actions, floors, [0, 1, 2], [120, 120, 120])!;
        Vector2 active = timeline.ShakeAt(1.75);
        Assert.Multiple(() =>
        {
            Assert.That(timeline.ShakeAt(1.49), Is.EqualTo(Vector2.Zero));
            Assert.That(active.Length, Is.InRange(.001f, 25f));
            Assert.That(timeline.ShakeAt(2.1), Is.EqualTo(Vector2.Zero));
            Assert.That(timeline.ShakeAt(3), Is.EqualTo(Vector2.Zero));
            Assert.That(timeline.ShakeAt(1.75), Is.EqualTo(active));
        });
    }

    [Test]
    public void PositionRotationAndZoomUseLocalBeatTimeAndSurviveRewind()
    {
        (CameraTimeline camera, LevelRenderer.PlaybackTimeline time, float spacing) = Build(
            """{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":240}""",
            """{"floor":1,"eventType":"MoveCamera","duration":2,"angleOffset":90,"relativeTo":"Player","position":[4,null],"rotation":60,"zoom":200,"ease":"InQuad"}""");
        Vector2 player = new(100, 200);
        double start = time.EntryTimes[1] + .125;

        CameraTimeline.Sample halfway = camera.Evaluate(start + .25, player, 100);
        Assert.That(halfway.Position, Is.EqualTo(player + new Vector2(1.75f, 2) * spacing));
        Assert.That(halfway.Rotation, Is.EqualTo(15).Within(1e-5));
        Assert.That(halfway.Zoom, Is.EqualTo(125).Within(1e-5));
        Assert.That(camera.Evaluate(start + .5, player, 100).Zoom, Is.EqualTo(200));
        CameraTimeline.Sample before = camera.Evaluate(start - .01, player, 100);
        Assert.That(before.Position, Is.EqualTo(player + new Vector2(1, 2) * spacing));
        Assert.That(before.Zoom, Is.Null);
        Assert.That(camera.Evaluate(start + .25, player, 100), Is.EqualTo(halfway));
    }

    [Test]
    public void RelativeModesAndInterruptedChannelsResolveInEventOrder()
    {
        (CameraTimeline camera, LevelRenderer.PlaybackTimeline time, float spacing) = Build(
            """{"floor":1,"eventType":"MoveCamera","duration":8,"relativeTo":"Global","position":[5,6],"rotation":20}""",
            """{"floor":2,"eventType":"MoveCamera","duration":0,"relativeTo":"LastPosition","position":[1,2],"rotation":10}""",
            """{"floor":3,"eventType":"MoveCamera","duration":0,"relativeTo":"LastPositionNoRotation","position":[2,3],"rotation":5}""",
            """{"floor":4,"eventType":"MoveCamera","duration":0,"relativeTo":"Tile","position":[2,null],"disabled":{"rotation":true}}""");

        Vector2 player = new(1000, -1000);
        CameraTimeline.Sample relative = camera.Evaluate(time.EntryTimes[2], player, 100);
        Assert.That(relative.Position, Is.EqualTo(new Vector2(6, 8) * spacing));
        Assert.That(relative.Rotation, Is.EqualTo(30));
        CameraTimeline.Sample noRotation = camera.Evaluate(time.EntryTimes[3], player, 100);
        Assert.That(noRotation.Position, Is.EqualTo(new Vector2(8, 11) * spacing));
        Assert.That(noRotation.Rotation, Is.EqualTo(5));
        CameraTimeline.Sample tile = camera.Evaluate(time.EntryTimes[4], player, 100);
        Assert.That(tile.Position.X, Is.EqualTo(6 * spacing).Within(1e-4));
        Assert.That(tile.Position.Y, Is.Zero.Within(1e-4));
        Assert.That(tile.Rotation, Is.EqualTo(5));
    }

    [Test]
    public void FloorRepeatUsesTheRepeatedTileAsItsCameraAnchor()
    {
        (CameraTimeline camera, LevelRenderer.PlaybackTimeline time, float spacing) = Build(
            """{"floor":1,"eventType":"MoveCamera","duration":0,"relativeTo":"Tile","position":[1,0],"eventTag":"cam"}""",
            """{"floor":1,"eventType":"RepeatEvents","repeatType":"Floor","floorCount":1,"gapLength":2,"executeOnCurrentFloor":true,"tag":"cam"}""");

        Assert.That(camera.Evaluate(time.EntryTimes[1], Vector2.Zero, 100).Position.X,
            Is.EqualTo(2 * spacing).Within(1e-4));
        Assert.That(camera.Evaluate(time.EntryTimes[3], Vector2.Zero, 100).Position.X,
            Is.EqualTo(4 * spacing).Within(1e-4));
    }

    [Test]
    public void SwitchingModesRebasesInParentSpaceAndFreezesTheFollowCenterAtTheEvent()
    {
        Vector2 PlanetAt(double _) => new(50, -30);
        Vector2 FollowAt(double t, CameraTimeline.FollowStart? start) => start is { } entry
            ? entry.Center + new Vector2((float)(t - entry.Time) * 10, 0)
            : new Vector2(20 + (float)t * 10, -10);
        (CameraTimeline camera, LevelRenderer.PlaybackTimeline time, float spacing) = BuildWithFollow(FollowAt, PlanetAt, 100,
            """{"floor":0,"eventType":"MoveCamera","duration":8,"relativeTo":"Player","position":[3,4]}""",
            """{"floor":1,"eventType":"MoveCamera","angleOffset":90,"duration":1,"relativeTo":"Global","position":[5,6]}""",
            """{"floor":3,"eventType":"MoveCamera","duration":2,"relativeTo":"Player","position":[0,0]}""",
            """{"floor":4,"eventType":"MoveCamera","duration":1,"relativeTo":"Tile","position":[2,null]}""");

        double exit = time.EntryTimes[1] + .25;
        double entry = time.EntryTimes[3];
        double nextExit = time.EntryTimes[4];
        Vector2 frozen = FollowAt(exit, null) + new Vector2(3, 4) * spacing;
        Vector2 global = new Vector2(5, 6) * spacing;
        Vector2 middle = Vector2.Lerp(frozen, global, .5f);
        Assert.Multiple(() =>
        {
            Assert.That(camera.Evaluate(nextExit, 100).Position, Is.EqualTo(PlanetAt(entry) + new Vector2(5, 0)));
            Assert.That(camera.Evaluate(exit + .25, 100).Position, Is.EqualTo(middle));
            Assert.That(camera.Evaluate(exit, 100).Position, Is.EqualTo(frozen));
            Assert.That(camera.Evaluate(entry, 100).Position, Is.EqualTo(global));
            Assert.That(camera.Evaluate(exit + .25, 100).Position, Is.EqualTo(middle));
        });
    }

    [Test]
    public void ZeroZoomDisablesAtCompletionAndCompletesWhenInterrupted()
    {
        (CameraTimeline camera, LevelRenderer.PlaybackTimeline time, _) = Build(
            """{"floor":1,"eventType":"MoveCamera","duration":4,"zoom":0}""",
            """{"floor":2,"eventType":"MoveCamera","duration":1,"zoom":250,"disabled":{"zoom":true}}""",
            """{"floor":3,"eventType":"MoveCamera","duration":2,"zoom":200}""",
            """{"floor":5,"eventType":"MoveCamera","duration":0,"zoom":0}""");

        Assert.Multiple(() =>
        {
            Assert.That(camera.Evaluate(time.EntryTimes[0], 100).Zoom, Is.Null);
            Assert.That(camera.Evaluate(time.EntryTimes[2], 100).Zoom, Is.EqualTo(75));
            Assert.That(camera.Evaluate(time.EntryTimes[3], 100).CameraDisabled, Is.True);
            Assert.That(camera.Evaluate(time.EntryTimes[3] + .5, 100).Zoom, Is.EqualTo(100));
            Assert.That(camera.Evaluate(time.EntryTimes[5], 100).CameraDisabled, Is.True);
            Assert.That(camera.Evaluate(time.EntryTimes[2], 100).CameraDisabled, Is.False);
        });
    }

    [Test]
    public void InitialZeroZoomIsDistinctFromAnAbsentSetting()
    {
        const string action = """{"floor":1,"eventType":"MoveCamera","duration":1,"zoom":200}""";
        var zero = BuildWithFollow(null, null, 0, action);
        var absent = BuildWithFollow(null, null, null, action);
        Assert.Multiple(() =>
        {
            Assert.That(zero.Camera.Evaluate(zero.Timeline.EntryTimes[0], 100).CameraDisabled, Is.True);
            Assert.That(zero.Camera.Evaluate(zero.Timeline.EntryTimes[1] + .25, 100).Zoom, Is.EqualTo(100));
            Assert.That(absent.Camera.Evaluate(absent.Timeline.EntryTimes[0], 100).Zoom, Is.Null);
            Assert.That(absent.Camera.Evaluate(absent.Timeline.EntryTimes[1] + .25, 100).Zoom, Is.EqualTo(150));
        });
    }

    private static (CameraTimeline Camera, LevelRenderer.PlaybackTimeline Timeline, float Spacing) Build(params string[] actions) =>
        BuildWithFollow(null, null, 100, actions);

    private static (CameraTimeline Camera, LevelRenderer.PlaybackTimeline Timeline, float Spacing) BuildWithFollow(
        CameraTimeline.FollowSampler? followAt, Func<double, Vector2>? playerAt, float? zoom, params string[] actions)
    {
        Level level = new(new Dictionary<string, object>
        {
            ["angleData"] = new JArray(0, 0, 0, 0, 0, 0),
            ["settings"] = new JObject
            {
                ["version"] = 15, ["bpm"] = 120, ["pitch"] = 100, ["offset"] = 0, ["countdownTicks"] = 1,
                ["separateCountdownTime"] = false,
                ["relativeTo"] = "Player", ["position"] = new JArray(1, 2),
            },
            ["actions"] = new JArray(actions.Select(JObject.Parse)),
        });
        if (zoom.HasValue)
            level.PutSetting("zoom", zoom.Value);
        List<Floor> floors = level.CreateFloors();
        PlaybackEvent[] events = PlaybackEventBinder.BindForPlayback(level);
        LevelRenderer.PlaybackTimeline timeline = LevelRenderer.BuildTimeline(level, floors, events);
        float spacing = Floor.length * 2f;
        return (CameraTimeline.Build(events, floors, timeline, LevelPlaybackSettings.Read(level), spacing, followAt, playerAt)!,
            timeline, spacing);
    }
}
