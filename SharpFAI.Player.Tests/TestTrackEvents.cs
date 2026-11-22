using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Tests;

public class TestTrackEvents
{
    [Test]
    public void PlaybackKeepsCoreTwirlSemanticsWithoutBindingEditorTwirlObjects()
    {
        Level chart = Chart(
            """{"floor":1,"eventType":"Twirl"}""",
            """{"floor":1,"eventType":"Twirl"}""",
            """{"floor":2,"eventType":"Twirl"}""",
            """{"floor":2,"eventType":"PositionTrack","positionOffset":[1,0],"justThisTile":true}""");

        var events = chart.DeserializeEvents();
        Assert.That(events[0], Is.TypeOf<SharpFAI.Events.Twirl>());
        Assert.That(events[2].Floor, Is.EqualTo(2));
        Assert.That(PlaybackEventBinder.BindForPlayback(chart).Single(), Is.InstanceOf<PositionTrack>());

        var timing = FloorTimingCalculator.Calculate(chart);
        Assert.That(timing[1].IsCCW, Is.False);
        Assert.That(timing[2].IsCCW, Is.True);

        var floors = chart.CreateFloors(default, usePositionTrack: false, calculateLegacyEntryTimes: false);
        Assert.That(floors[1].isCW, Is.False);
        Assert.That(floors[1].icon, Is.EqualTo(FloorIcon.Swirl));
        Assert.That(floors[1].events, Has.Count.EqualTo(2));
    }

    [Test]
    public void EditorPhasesAlsoApplyToCameraAndDecorationEvents()
    {
        Level level = Chart(
            """{"floor":0,"eventType":"Bookmark"}""",
            """{"floor":1,"eventType":"MoveCamera","duration":2,"active":"Disabled"}""",
            """{"floor":1,"eventType":"MoveTrack","scale":[null,50],"disabled":{"scale":true}}""");
        level.decorations!.Add(JObject.Parse("""{"floor":0,"eventType":"AddDecoration","image":"example.png"}"""));

        PlaybackEvent[] events = PlaybackEventBinder.Bind(level, includeDecorations: true);
        Assert.That(events[0], Is.InstanceOf<PreviewEvent>());
        Assert.That(events[0].Source, Is.InstanceOf<SharpFAI.Events.Bookmark>());
        Assert.That(events[1], Is.InstanceOf<RuntimeEvent>());
        Assert.That(events[1].Source, Is.InstanceOf<SharpFAI.Events.MoveCamera>());
        Assert.That(events[1].IsActive, Is.False);
        Assert.That(events[2], Is.InstanceOf<MoveTrack>());
        MoveTrack move = (MoveTrack)events[2];
        Assert.That(move.Scale?.X, Is.Null);
        Assert.That(move.Scale?.Y, Is.EqualTo(50f));
        Assert.That(move.Enabled("scale", move.Scale), Is.Null);
        Assert.That(events[3], Is.InstanceOf<PreviewEvent>());
        Assert.That(events[3].EventType, Is.EqualTo(EventType.AddDecoration));
    }

    [Test]
    public void PreviewEventsResolveLocallyAndDropEditorOnlyOffsetsInPlayback()
    {
        Level level = Chart(
            """{"floor":1,"eventType":"PositionTrack","positionOffset":[1,0],"editorOnly":true,"justThisTile":true}""",
            """{"floor":1,"eventType":"TileDimensions","width":150,"length":50}""",
            """{"floor":1,"eventType":"ColorTrack","trackStyle":"Gems","justThisTile":true}""",
            """{"floor":2,"eventType":"ChangeTrack","trackStyle":"Neon","trackDisappearAnimation":"Fade","beatsBehind":2}""",
            """{"floor":2,"eventType":"Hide","hideTileIcon":true}""",
            """{"floor":2,"eventType":"SetFloorIcon","icon":"Rabbit"}""",
            """{"floor":2,"eventType":"ColorTrack","active":false,"trackStyle":"Basic"}""",
            """{"floor":3,"eventType":"AnimateTrack","trackAnimation":"Grow","beatsAhead":1}""");
        Floor[] floors = Floors(5);
        FloorTrackState[] preview = FloorTrackStateResolver.Resolve(level, floors);
        FloorTrackState[] playback = FloorTrackStateResolver.Resolve(level, floors, includeEditorOnly: false);

        Assert.That(preview[1].Transform.Offset.X, Is.EqualTo(Floor.length * 2));
        Assert.That(playback[1].Transform.Offset, Is.EqualTo(Vector2.Zero));
        Assert.That(preview[2].Transform.Offset, Is.EqualTo(Vector2.Zero));
        Assert.That(preview[1].Style, Is.EqualTo(TrackStyleInfo.Gems));
        Assert.That(preview[2].Style, Is.EqualTo(TrackStyleInfo.Neon));
        Assert.That(preview[3].Dimensions, Is.EqualTo(new Vector2(1.5f, .5f)));
        Assert.That(preview[2].Icon, Is.EqualTo(FloorIcon.Rabbit));
        Assert.That(preview[3].HideIcon, Is.True);
        Assert.That(preview[3].Appear, Is.EqualTo(TrackAnimation.Grow));
        Assert.That(preview[3].Disappear, Is.EqualTo(TrackAnimation.Fade));
        Assert.That(preview[3].BeatsBehind, Is.EqualTo(2));
    }

    [Test]
    public void RuntimeEventsRespectOffsetsGapsAndRewind()
    {
        Level level = Chart(
            """{"floor":1,"eventType":"MoveTrack","startTile":[0,"Start"],"endTile":[-1,"End"],"gapLength":1,"angleOffset":180,"duration":2,"positionOffset":[1,0],"scale":[null,50],"rotationOffset":90,"opacity":20,"ease":"Linear"}""",
            """{"floor":1,"eventType":"RecolorTrack","startTile":[-1,"ThisTile"],"endTile":[-1,"ThisTile"],"duration":2,"trackColor":"ff0000","trackStyle":"Minimal","ease":"Linear"}""");
        RuntimeTrackTimeline timeline = Runtime(level, 5);

        timeline.Advance(.75);
        Assert.That(timeline.TransformAt(0, .75, false).Transform.Offset, Is.EqualTo(Vector2.Zero));
        Assert.That(timeline.States[0].Style, Is.EqualTo(TrackStyleInfo.Minimal));
        Assert.That(timeline.ColorAt(0, .75), Is.EqualTo(new Vector4(1f, .75f, .75f, 1f)));
        timeline.Advance(1.5);
        (FloorRenderTransform moved, float opacity) = timeline.TransformAt(0, 1.5, false);
        Assert.That(moved.Offset.X, Is.EqualTo(Floor.length));
        Assert.That(moved.Scale, Is.EqualTo(new Vector2(1f, .75f)));
        Assert.That(moved.Rotation, Is.EqualTo(MathF.PI / 4f).Within(1e-6f));
        Assert.That(opacity, Is.EqualTo(.6f).Within(1e-6f));
        Assert.That(timeline.TransformAt(1, 1.5, false).Transform.Offset, Is.EqualTo(Vector2.Zero));
        Assert.That(timeline.TransformAt(2, 1.5, false).Transform, Is.EqualTo(moved));

        timeline.Advance(0);
        Assert.That(timeline.States[0].Style, Is.EqualTo(TrackStyleInfo.Standard));
        Assert.That(timeline.ColorAt(0, 0), Is.EqualTo(Vector4.One));
        Assert.That(timeline.TransformAt(0, 0, false).Transform.Offset, Is.EqualTo(Vector2.Zero));
        timeline.Advance(1.5);
        Assert.That(timeline.TransformAt(0, 1.5, false).Transform, Is.EqualTo(moved));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void MoveTrackUsesPlacedOriginAndOnlyAttachedPlanetsFollow(bool stick)
    {
        Level level = Chart(
            $$"""{"floor":1,"eventType":"PositionTrack","positionOffset":[2,3],"scale":150,"rotation":30,"stickToFloors":{{(stick ? "true" : "false")}},"justThisTile":true}""",
            """{"floor":1,"eventType":"MoveTrack","positionOffset":[-2,null],"scale":[null,50],"opacity":160,"rotationOffset":90,"angleOffset":-180,"duration":2}""",
            """{"floor":2,"eventType":"MoveTrack","startTile":[-1,"ThisTile"],"endTile":[-1,"ThisTile"],"positionOffset":[1,null],"angleOffset":-90,"duration":2}""");
        Floor[] floors = Floors(5);
        PlaybackEvent[] events = PlaybackEventBinder.Bind(level);
        FloorTrackState[] states = FloorTrackStateResolver.Resolve(level, floors, events, false);
        LevelRenderer.ApplyTrackPlacement(floors, states, states);
        RuntimeTrackTimeline timeline = new(events, floors, states, [0, .5, 1, 1.5, 2], 1);
        float spacing = Floor.length * 2f;

        timeline.Advance(.5);
        var (transform, opacity) = timeline.TransformAt(1, .5, false);
        Assert.That(floors[1].position.X, Is.EqualTo(3f * spacing));
        Assert.That(transform.Offset, Is.EqualTo(new Vector2(-spacing, 0)));
        Assert.That(transform.Scale, Is.EqualTo(new Vector2(1.5f, 1f)));
        Assert.That(transform.Rotation, Is.EqualTo(MathHelper.DegreesToRadians(60f)).Within(1e-6f));
        Assert.That(opacity, Is.EqualTo(1.3f).Within(1e-6f));
        Assert.That(timeline.PlanetMotionAt(1, .5), Is.EqualTo(stick ? (transform.Offset, transform.Rotation) : default));

        timeline.Advance(.75);
        Assert.That(timeline.TransformAt(1, .75, false).Transform.Offset.X, Is.EqualTo(-2f * spacing));
        timeline.Advance(1.75);
        var end = timeline.TransformAt(1, 1.75, false);
        Assert.That(end.Transform.Offset, Is.EqualTo(new Vector2(spacing, 0)));
        Assert.That(end.Transform.Scale, Is.EqualTo(new Vector2(1.5f, .5f)));
        Assert.That(end.Opacity, Is.EqualTo(1.6f));

        timeline.Advance(-.1);
        Assert.That(timeline.TransformAt(1, -.1, false).Transform.Offset, Is.EqualTo(Vector2.Zero));
    }

    [Test]
    public void TrackAnimationsAndTaggedFloorRepeatsUsePlaybackClock()
    {
        Level level = Chart(
            """{"floor":0,"eventType":"AnimateTrack","trackAnimation":"Fade","beatsAhead":0,"trackDisappearAnimation":"Shrink","beatsBehind":0}""",
            """{"floor":1,"eventType":"MoveTrack","positionOffset":[1,0],"eventTag":"move"}""",
            """{"floor":1,"eventType":"RepeatEvents","tag":"move","repeatType":"Floor","floorCount":2,"gapLength":1,"executeOnCurrentFloor":true}""");
        RuntimeTrackTimeline timeline = Runtime(level, 5);

        timeline.Advance(.625);
        Assert.That(timeline.TransformAt(1, .625, false).Opacity, Is.EqualTo(.5f).Within(1e-6f));
        Assert.That(timeline.TransformAt(0, .625, false).Transform.Scale.X, Is.EqualTo(.5f).Within(1e-6f));
        Assert.That(timeline.TransformAt(2, .625, false).Transform.Offset, Is.EqualTo(Vector2.Zero));
        timeline.Advance(1.5);
        Assert.That(timeline.TransformAt(3, 1.5, false).Transform.Offset.X, Is.EqualTo(Floor.length * 2));
        Assert.That(timeline.TransformAt(4, 1.5, false).Transform.Offset, Is.EqualTo(Vector2.Zero));
    }

    [Test]
    public void AnimationWindowsUseChartStartAndKeepSignedExitDelay()
    {
        Level level = Chart(
            """{"floor":1,"eventType":"AnimateTrack","trackAnimation":"Fade","beatsAhead":2,"trackDisappearAnimation":"Fade","beatsBehind":-0.5,"justThisTile":true}""");
        RuntimeTrackTimeline timeline = Runtime(level, 5, start: -.1);

        Assert.That(timeline.TransformAt(1, 0, false).Opacity, Is.EqualTo(.4f).Within(1e-6f));
        Assert.That(timeline.TransformAt(1, .775, false).Opacity, Is.EqualTo(.5f).Within(1e-6f));
        Assert.That(timeline.TransformAt(1, .9, false).Opacity, Is.Zero);
        Assert.That(timeline.TransformAt(2, .9, false).Opacity, Is.EqualTo(1));
    }

    [TestCase("Extend")]
    [TestCase("Fade")]
    public void OpeningFloorsAreVisibleDuringPositiveAudioLeadIn(string animation)
    {
        Level level = Chart($$"""{"floor":0,"eventType":"AnimateTrack","trackAnimation":"{{animation}}","beatsAhead":1}""");
        RuntimeTrackTimeline timeline = Runtime(level, 5, start: 3);

        float Visibility(int floor, double time)
        {
            (FloorRenderTransform transform, float opacity) = timeline.TransformAt(floor, time, false);
            return transform.Scale.X * opacity;
        }

        Assert.That(Visibility(0, 0), Is.EqualTo(1));
        Assert.That(Visibility(1, 0), Is.EqualTo(1));
        Assert.That(Visibility(2, 0), Is.Zero);
        Assert.That(Visibility(2, 3.625), Is.InRange(.1f, .9f));
        Assert.That(Visibility(2, 3.75), Is.EqualTo(1));
    }

    [Test]
    public void TrackAnimationUsesReferenceBpmForOffsetsAndLocalBpmForFadeDuration()
    {
        Level level = Chart(new JObject
        {
            ["bpm"] = 120, ["pitch"] = 100, ["version"] = 15,
            ["trackAnimation"] = "Fade", ["beatsAhead"] = 1,
        },
            """{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":480}""",
            """{"floor":2,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":960,"angleOffset":90}""",
            """{"floor":2,"eventType":"AnimateTrack","trackAnimation":"Fade","beatsAhead":1}""",
            """{"floor":3,"eventType":"AnimateTrack","trackAnimation":"None","beatsAhead":99,"disabled":{"trackAnimation":true},"trackDisappearAnimation":"Fade","beatsBehind":2}""");
        List<Floor> floors = level.CreateFloors();
        LevelRenderer.PlaybackTimeline playback = LevelRenderer.BuildTimeline(level, floors);
        PlaybackEvent[] events = PlaybackEventBinder.Bind(level);
        FloorTrackState[] states = FloorTrackStateResolver.Resolve(level, floors, events, false, playback.FloorBpms);
        RuntimeTrackTimeline timeline = new(events, floors, states,
            playback.EntryTimes, playback.Pitch, playback.FloorBpms);

        double firstFadeMidpoint = playback.EntryTimes[1] - 60.0 / 120 + 15.0 / 480;
        double laterFadeMidpoint = playback.EntryTimes[3] - 60.0 / 640 + 15.0 / 960;
        double exitFadeMidpoint = playback.EntryTimes[4] + 2 * 60.0 / 640 + 15.0 / 960;
        Assert.That(timeline.TransformAt(1, firstFadeMidpoint, false).Opacity, Is.EqualTo(.5f).Within(1e-6f));
        Assert.That(timeline.TransformAt(3, laterFadeMidpoint, false).Opacity, Is.EqualTo(.5f).Within(1e-6f));
        Assert.That(timeline.TransformAt(3, exitFadeMidpoint, false).Opacity, Is.EqualTo(.5f).Within(1e-6f));
    }

    [Test]
    public void RecolorMaterialsAreBakedOnceAndColorOnlyEventsShareGeometry()
    {
        Level level = Chart(
            """{"floor":1,"eventType":"RecolorTrack","startTile":[0,"Start"],"endTile":[0,"Start"],"trackStyle":"Gems"}""",
            """{"floor":2,"eventType":"RecolorTrack","startTile":[0,"Start"],"endTile":[1,"Start"],"trackColor":"ff2200"}""",
            """{"floor":3,"eventType":"RecolorTrack","startTile":[0,"Start"],"endTile":[0,"Start"],"trackStyle":"Minimal"}""");
        Floor[] floors = Floors(5);
        FloorTrackState[] preview = FloorTrackStateResolver.Resolve(level, floors);
        RuntimeTrackTimeline timeline = Runtime(level, 5);
        HashSet<FloorMaterial>?[] options = timeline.PrepareMaterials(CancellationToken.None);
        using FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel(floors, preview, 16, materialOptions: options);

        Assert.That(geometry.GetVariants(0).ToArray().Select(item => item.Material.Style), Is.EquivalentTo(
            new[] { TrackStyleInfo.Standard, TrackStyleInfo.Gems, TrackStyleInfo.Minimal }));
        Assert.That(geometry.GetVariants(1).Length, Is.EqualTo(1));

        timeline.Advance(.5);
        int revision = timeline.MaterialRevision;
        Assert.That(timeline.States[0].Style, Is.EqualTo(TrackStyleInfo.Gems));
        timeline.Advance(1);
        Assert.That(timeline.MaterialRevision, Is.EqualTo(revision));
        timeline.Advance(1.5);
        Assert.That(timeline.States[0].Style, Is.EqualTo(TrackStyleInfo.Minimal));
        timeline.Advance(0);
        Assert.That(timeline.States[0].Style, Is.EqualTo(TrackStyleInfo.Standard));
    }

    private static Level Chart(params string[] actions) =>
        Chart(new JObject { ["trackColor"] = "ffffff", ["bpm"] = 120, ["version"] = 15 }, actions);

    private static Level Chart(JObject settings, params string[] actions) => new(new Dictionary<string, object>
    {
        ["angleData"] = new JArray(0, 0, 0, 0),
        ["settings"] = settings,
        ["actions"] = new JArray(actions.Select(JObject.Parse)),
        ["decorations"] = new JArray(),
    });

    private static Floor[] Floors(int count) => Enumerable.Range(0, count)
        .Select(i => new Floor(0, 180, new System.Numerics.Vector2(i * Floor.length * 2, 0))
            { index = i, bpm = 120 }).ToArray();

    private static RuntimeTrackTimeline Runtime(Level level, int count, double start = 0)
    {
        Floor[] floors = Floors(count);
        PlaybackEvent[] events = PlaybackEventBinder.Bind(level);
        return new RuntimeTrackTimeline(events, floors, FloorTrackStateResolver.Resolve(level, floors, events, false),
            Enumerable.Range(0, count).Select(i => start + i * .5).ToArray(), 1);
    }
}
