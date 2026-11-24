using System.Reflection;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Play;
using SharpFAI.Serialization;
using SharpFAI.Util;
using Thrust.Scene;

namespace SharpFAI.Player.Tests;

public class TestCameraMotion
{
    [TestCase(0, 5.0 / 4.9)]
    [TestCase(.5, 5.0 / 4.95)]
    [TestCase(1, 1)]
    [TestCase(2, 1)]
    [TestCase(-.1, 1)]
    public void LandingPulseInterpolatesOrthographicSizeForOneSecond(double elapsed, double expected) =>
        Assert.That(CameraMotion.PulseScale(elapsed), Is.EqualTo(expected).Within(1e-6));

    [TestCase(60, 1)]
    [TestCase(120, .5)]
    [TestCase(240, .25)]
    public void FollowTakesTwoBeatsWithoutDistanceAcceleration(double bpm, double elapsed)
    {
        Vector2 result = CameraMotion.Follow(Vector2.Zero, new Vector2(2000, 0), Vector2.Zero, elapsed, bpm);
        Assert.That(result.X, Is.EqualTo(1000).Within(1e-4));
    }

    [Test]
    public void HoldOffsetIsInsideTheFollowInterpolation()
    {
        Vector2 result = CameraMotion.Follow(Vector2.Zero, new Vector2(100, 0), new Vector2(40, 20), .5, 120);
        Assert.That(result, Is.EqualTo(new Vector2(70, 10)));
        Assert.That(CameraMotion.ReleaseHold(new Vector2(80, 40), .15), Is.EqualTo(new Vector2(10, 5)));
        Assert.That(CameraMotion.ReleaseHold(new Vector2(80, 40), .3), Is.EqualTo(Vector2.Zero));
    }

    [Test]
    public void FollowLatchesLandingTargetAndPauseDoesNotAdvanceIt()
    {
        using var renderer = new LevelRenderer();
        var floors = Get<List<Floor>>(renderer, "_floors");
        floors.Add(new Floor(0, 0, new System.Numerics.Vector2(500, 0)) { bpm = 120 });
        Set(renderer, "_entryTimes", new double[] { 0 });
        Set(renderer, "_cameraIndex", 0);
        Set(renderer, "_cameraTo", new Vector2(100, 0));
        Set(renderer, "_playTime", .5);
        Update(renderer, 0, new Vector2(500, 0));
        var camera = Get<Camera2D>(renderer, "_camera");
        Assert.That(camera.Position.X, Is.EqualTo(50).Within(1e-4));
        Assert.That(Get<Vector2>(renderer, "_cameraTo").X, Is.EqualTo(100));
        Update(renderer, 0, new Vector2(500, 0));
        Assert.That(camera.Position.X, Is.EqualTo(50).Within(1e-4));
    }

    [Test]
    public void ManualPulseRecordsAcceptedHitsAndDoesNotRetriggerForTheSameHoldFloor()
    {
        using var renderer = new LevelRenderer();
        Hit(renderer, new HitRecord(3, HitMargin.Perfect, 0, 2.25, Accepted: true));
        Hit(renderer, new HitRecord(4, HitMargin.Miss, 0, 2.5));
        Hit(renderer, new HitRecord(3, HitMargin.Perfect, 0, 2.75, Accepted: true));
        Assert.That(Get<double>(renderer, "_cameraPulseTime"), Is.EqualTo(2.25));
        Hit(renderer, new HitRecord(4, HitMargin.Perfect, 0, 3.1, Accepted: true));
        Assert.That(Get<double>(renderer, "_cameraPulseTime"), Is.EqualTo(3.1));
    }

    [Test]
    public void ZeroZoomDisablesRenderingAndNonzeroZoomRestoresIt()
    {
        using var renderer = new LevelRenderer();
        Get<List<Floor>>(renderer, "_floors").Add(new Floor(0, 0, System.Numerics.Vector2.Zero) { bpm = 120 });
        Set(renderer, "_entryTimes", new double[] { 0 });
        Set(renderer, "_settings", new LevelPlaybackSettings { CameraZoom = 0 });
        Update(renderer, 0, Vector2.Zero);
        Assert.That(Get<bool>(renderer, "_cameraRenderDisabled"), Is.True);
        renderer.Render();
        Assert.That(renderer.DrawCalls, Is.Zero);
        Set(renderer, "_settings", new LevelPlaybackSettings { CameraZoom = 100 });
        Update(renderer, 0, Vector2.Zero);
        Assert.That(Get<bool>(renderer, "_cameraRenderDisabled"), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FreshPlayUsesAuthoredZoomButResumingBeforeTheFirstTickKeepsUserZoom(bool manual)
    {
        using var renderer = CreatePlayback();
        renderer.Zoom(2, Vector2.Zero);

        if (manual)
            renderer.SetManualPlay(true);
        else
            renderer.Play();
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.6f).Within(1e-6f));

        renderer.Pause();
        renderer.Zoom(1, Vector2.Zero);
        float pausedZoom = renderer.Camera.Zoom;
        renderer.Play();
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(pausedZoom));
    }

    [Test]
    public void RestartAndReplayAfterTheEndUseAuthoredZoom()
    {
        using var renderer = CreatePlayback();
        renderer.Play();
        renderer.Zoom(2, Vector2.Zero);
        renderer.Restart();
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.6f).Within(1e-6f));

        renderer.Seek(renderer.Duration);
        renderer.Zoom(1, Vector2.Zero);
        renderer.Play();
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.6f).Within(1e-6f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StartingFromSelectedFloorUsesAuthoredZoom(bool manual)
    {
        using var renderer = CreatePlayback();
        renderer.Play();
        renderer.Pause();
        renderer.SelectFloor(1);
        renderer.Zoom(2, Vector2.Zero);

        if (manual)
            renderer.SetManualPlay(true);
        else
            renderer.Play();

        Assert.That(renderer.CurrentIndex, Is.EqualTo(1));
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.6f).Within(1e-6f));
    }

    [Test]
    public void LaterCameraEventAnimatesFromAuthoredZoomAndStillAppliesWhenStartingAfterIt()
    {
        using var renderer = CreatePlayback("""{"floor":2,"eventType":"MoveCamera","duration":1,"zoom":300}""");
        renderer.Zoom(2, Vector2.Zero);
        renderer.Play();
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.6f).Within(1e-6f));

        double target = Get<double[]>(renderer, "_entryTimes")[2] + .25;
        for (double time = .25; time < target; time += .25)
            renderer.Tick(time);
        renderer.Tick(target);
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.4f).Within(1e-6f));
        renderer.Pause();
        renderer.SelectFloor(4);
        renderer.Zoom(2, Vector2.Zero);
        renderer.Play();
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.3f).Within(1e-6f));
    }

    [Test]
    public void CameraRotationKeepsTheAuthoredSignAtStartAndAfterMoveCamera()
    {
        using var renderer = CreatePlayback("""{"floor":1,"eventType":"MoveCamera","duration":0,"rotation":-30}""");
        renderer.Play();
        Assert.That(renderer.Camera.Rotation, Is.EqualTo(15));
        renderer.Pause();
        renderer.SelectFloor(2);
        Assert.That(renderer.Camera.Rotation, Is.EqualTo(-30));
    }

    [Test]
    public void AutomaticAndOfflinePlaybackTriggerConditionalCameraAndSceneOnTheLandingFrame()
    {
        using var renderer = CreatePlayback(
            """{"floor":1,"eventType":"SetConditionalEvents","perfectTag":"hit"}""",
            """{"floor":1,"eventType":"MoveCamera","eventTag":"hit","zoom":300}""",
            """{"floor":1,"eventType":"ScalePlanets","eventTag":"hit","targetPlanet":"All","scale":200}""",
            """{"floor":1,"eventType":"Flash","eventTag":"hit","plane":"Foreground","startOpacity":90,"endOpacity":90}""",
            """{"floor":1,"eventType":"MoveTrack","eventTag":"hit","positionOffset":[2,0]}""",
            """{"floor":1,"eventType":"RepeatEvents","tag":"hit","repetitions":2,"interval":1}""",
            """{"floor":2,"eventType":"SetConditionalEvents","perfectTag":"NONE"}""");
        var times = Get<double[]>(renderer, "_entryTimes");
        var scene = Get<SceneEffectTimeline>(renderer, "_sceneEffects");
        var backdrop = Get<BackdropTimeline>(renderer, "_backdrop");
        var track = Get<RuntimeTrackTimeline>(renderer, "_trackRuntime");
        var camera = Get<CameraTimeline>(renderer, "_cameraTimeline");
        Assert.That(camera.Triggers.Authored, Has.Length.EqualTo(1));

        renderer.Play();
        renderer.Tick(times[1] - .01);
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.6f).Within(1e-6f));
        float untriggeredFollowX = camera.Evaluate(times[1] + .25, 150).Position.X;
        renderer.Tick(times[1]);
        Assert.Multiple(() =>
        {
            Assert.That(renderer.Camera.Zoom, Is.EqualTo(.3f).Within(1e-6f));
            Assert.That(scene.PlanetScale(0, times[1]), Is.EqualTo(2f));
            Assert.That(backdrop.FlashAt(times[1], foreground: true).W, Is.EqualTo(.9f));
            Assert.That(track.TransformAt(1, times[1], false).Transform.Offset.X, Is.EqualTo(2 * renderer.TileSpacing));
            Assert.That(camera.Evaluate(times[1] + .25, 150).Position.X - untriggeredFollowX,
                Is.EqualTo(renderer.TileSpacing / 2).Within(.001f));
        });

        renderer.Restart();
        Assert.That(camera.Triggers.Active, Is.Empty);
        renderer.SeekForOfflineRender(times[3]);
        Assert.That(camera.Triggers.Active, Has.Count.EqualTo(1));
        Assert.That(backdrop.FlashAt(times[3], foreground: true).W, Is.EqualTo(.9f));
        renderer.SeekForOfflineRender(times[1] - .01);
        Assert.That(camera.Triggers.Active, Is.Empty);
        Assert.That(backdrop.FlashAt(times[1], foreground: true).W, Is.Zero);
    }

    [Test]
    public void ManualPlaybackTriggersOnlyTheMatchingJudgmentAndCheckpoint()
    {
        using var renderer = CreatePlayback(
            """{"floor":0,"eventType":"SetConditionalEvents","perfectTag":"hit","hitTag":"near","lossTag":"loss","onCheckpointTag":"retry"}""",
            """{"floor":0,"eventType":"MoveCamera","eventTag":"hit","zoom":300}""",
            """{"floor":0,"eventType":"MoveCamera","eventTag":"near","angleOffset":90,"rotation":45}""",
            """{"floor":0,"eventType":"MoveCamera","eventTag":"loss","angleOffset":90,"rotation":60}""",
            """{"floor":0,"eventType":"MoveCamera","eventTag":"retry","angleOffset":90,"rotation":90}""");
        var times = Get<double[]>(renderer, "_entryTimes");
        var camera = Get<CameraTimeline>(renderer, "_cameraTimeline");

        renderer.SetManualPlay(true);
        Assert.That(camera.Triggers.Active, Is.Empty);
        Hit(renderer, new HitRecord(1, HitMargin.EarlyPerfect, 0, .5, Accepted: true));
        Assert.That(camera.Evaluate(.74, 150).Rotation, Is.EqualTo(15));
        Assert.That(camera.Evaluate(.75, 150).Rotation, Is.EqualTo(45));
        Assert.That(camera.Evaluate(.75, 150).Zoom, Is.Null);
        Hit(renderer, new HitRecord(1, HitMargin.Miss, 0, 1, Failed: true));
        Assert.That(camera.Evaluate(1, 150).Rotation, Is.EqualTo(60));

        renderer.Pause();
        renderer.SelectFloor(2);
        Assert.That(camera.Triggers.Active, Has.Count.EqualTo(1));
        Assert.That(camera.Evaluate(times[2], 150).Rotation, Is.EqualTo(90));
    }

    [TestCase(-30f)]
    [TestCase(30f)]
    public void RotatedCameraScreenConversionsMatchTheRenderMatrix(float rotation)
    {
        var camera = new Camera2D { Position = new Vector2(17, 11), Zoom = 2f, Rotation = rotation };
        var world = camera.Position + new Vector2(30, 0);
        var clip = Vector4.TransformRow(new Vector4(world.X, world.Y, 0f, 1f), camera.ViewProjection);
        Vector2 expected = new((1f + clip.X) * camera.ViewportWidth / 2f,
            (1f - clip.Y) * camera.ViewportHeight / 2f);
        var screen = camera.WorldToScreen(world);
        Assert.That((screen - expected).Length, Is.LessThan(.001f));
        Assert.That((camera.ScreenToWorld(expected) - world).Length, Is.LessThan(.001f));
    }

    [TestCase(-30f)]
    [TestCase(30f)]
    public void RotatedCameraZoomAndDragPreserveTheScreenAnchor(float rotation)
    {
        var camera = new Camera2D { Position = new Vector2(17, 11), Zoom = 2f, Rotation = rotation };
        Vector2 anchor = new(920, 275);
        Vector2 world = camera.ScreenToWorld(anchor);
        camera.ZoomAt(anchor, 1.5f);
        Assert.That((camera.WorldToScreen(world) - anchor).Length, Is.LessThan(.001f));

        Vector2 drag = new(-51, 29);
        camera.PanPixels(drag);
        Assert.That((camera.WorldToScreen(world) - anchor - drag).Length, Is.LessThan(.001f));
    }

    [Test]
    public void LeavingCameraEventPreviewDoesNotOverwriteTheFreeEditCamera()
    {
        using var renderer = CreatePlayback("""{"floor":1,"eventType":"MoveCamera","duration":0,"zoom":300,"rotation":45}""");
        renderer.SelectFloor(2);
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(.3f).Within(1e-6f));

        renderer.Pan(Vector2.Zero);
        renderer.Camera.Zoom = 1.5f;
        renderer.Camera.Rotation = 0;
        renderer.Tick(.1);
        Assert.That(renderer.Camera.Zoom, Is.EqualTo(1.5f));
        Assert.That(renderer.Camera.Rotation, Is.Zero);
    }

    private static LevelRenderer CreatePlayback(params string[] actions)
    {
        Level level = new(new Dictionary<string, object>
        {
            ["angleData"] = new JArray(0, 0, 0, 0, 0),
            ["settings"] = new JObject
            {
                ["version"] = 15, ["bpm"] = 120, ["pitch"] = 100, ["offset"] = 0,
                ["relativeTo"] = "Player", ["zoom"] = 150, ["rotation"] = 15,
            },
            ["actions"] = new JArray(actions.Select(JObject.Parse)),
        });
        List<Floor> floors = level.CreateFloors();
        PlaybackEvent[] events = PlaybackEventBinder.BindForPlayback(level);
        var timeline = LevelRenderer.BuildTimeline(level, floors, events);
        var settings = LevelPlaybackSettings.Read(level);
        var renderer = new LevelRenderer();
        renderer.Resize(1280, 900);
        Set(renderer, "_floors", floors);
        Set(renderer, "_settings", settings);
        Set(renderer, "_manualTrack", new ManualTrack(floors, timeline, events));
        var camera = CameraTimeline.Build(events, floors, timeline, settings, Floor.length * 2f);
        if (ImageDecorationTimeline.Build(events, floors, timeline.EntryTimes, timeline.FloorBpms,
                null, [], CancellationToken.None) is { } triggers)
        {
            var backdrop = BackdropTimeline.Build(settings, events, floors, timeline.EntryTimes, timeline.FloorBpms, null, []);
            var scene = SceneEffectTimeline.Build(events, floors, timeline.EntryTimes, timeline.FloorBpms);
            var states = FloorTrackStateResolver.Resolve(level, floors, events, false, timeline.FloorBpms);
            var track = new RuntimeTrackTimeline(events, floors, states, timeline.EntryTimes, timeline.Pitch, timeline.FloorBpms);
            camera = LevelRenderer.BuildCameraTimeline(events, floors, timeline, settings, Floor.length * 2f, states);
            triggers.BindTimedEffects(floors, timeline.FloorBpms, null, null, camera, backdrop, scene, track);
            Set(renderer, "_imageDecorations", triggers);
            Set(renderer, "_backdrop", backdrop);
            if (scene is not null)
                Set(renderer, "_sceneEffects", scene);
            Set(renderer, "_trackRuntime", track);
            Set(renderer, "_trackStates", states);
        }
        typeof(LevelRenderer).GetField("_cameraTimeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(renderer, camera);
        typeof(LevelRenderer).GetMethod("ApplyTimelineData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, [timeline]);
        return renderer;
    }

    private static void Update(LevelRenderer renderer, int index, Vector2 here) =>
        typeof(LevelRenderer).GetMethod("UpdatePlaybackCamera", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, [index, here, null]);

    private static void Hit(LevelRenderer renderer, HitRecord hit) =>
        typeof(LevelRenderer).GetMethod("OnPlaybackCameraHit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(renderer, [hit]);

    private static T Get<T>(LevelRenderer renderer, string field) =>
        (T)typeof(LevelRenderer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;

    private static void Set(LevelRenderer renderer, string field, object value) =>
        typeof(LevelRenderer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(renderer, value);
}
