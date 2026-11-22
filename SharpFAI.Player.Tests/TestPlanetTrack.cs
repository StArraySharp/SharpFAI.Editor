using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Tests;

public class TestPlanetTrack
{
    [Test]
    public void DistantFloorCentersKeepExactSpacingAndPlacementOffsets()
    {
        const float origin = 134217728f;
        Floor[] floors =
        [
            new(0, 180, new System.Numerics.Vector2(origin, -origin)),
            new(0, 180, default),
            new(0, 180, default),
        ];
        PlanetTrackFloor normal = new(1, 2, 0, 1, 2, -1, 0, false);
        Vector2[] remainders = LevelRenderer.BuildPreciseFloorPositions(floors,
            [normal, normal, normal with { Radius = 1.5f }], 150f, CancellationToken.None);

        FloorTrackState state = new(TrackStyleInfo.Standard, Vector4.One, Vector4.One,
            TrackColorMode.Single, 0, 1, 1, 0, 0, 0);
        FloorTrackState[] states =
            [state, state with { Transform = state.Transform with { Offset = new Vector2(3.25f, -2.5f) } }, state];
        LevelRenderer.ApplyTrackPlacement(floors, states, states, remainders);

        Assert.That((double)floors[1].position.X + remainders[1].X - origin, Is.EqualTo(153.25));
        Assert.That((double)floors[1].position.Y + remainders[1].Y + origin, Is.EqualTo(-2.5));
        Assert.That((double)floors[2].position.X + remainders[2].X - origin, Is.EqualTo(375));
    }

    [Test]
    public void DepartingPlanetTrailFadesAndExpiresWhileOtherPlanetsKeepRecording()
    {
        PlanetTrail trail = new();
        trail.RecordPlanet(2, new Vector2(0, 100), Vector4.One, 0);
        trail.RecordPlanet(2, new Vector2(40, 100), Vector4.One, .1);
        trail.RecordPlanet(0, Vector2.Zero, Vector4.One, .1);
        trail.RecordPlanet(0, new Vector2(40, 0), Vector4.One, .2);

        PlanetTrail.Sample[] transition = trail.Build(.2).ToArray();
        Assert.That(transition.Any(sample => sample.Center.Y == 100), Is.True);
        Assert.That(transition.Where(sample => sample.Center.Y == 100).Max(sample => sample.Tint.W), Is.LessThan(1));
        trail.RecordPlanet(0, new Vector2(80, 0), Vector4.One, 1.05);
        PlanetTrail.Sample[] later = trail.Build(1.05).ToArray();
        Assert.That(later, Is.Not.Empty);
        Assert.That(later.All(sample => sample.Center.Y == 0), Is.True);
    }

    [Test]
    public void HoldSpacingRadiusAndThreePlanetSweepShareThePlaybackTimeline()
    {
        Level level = Chart(
            """{"floor":1,"eventType":"Hold","duration":2,"distanceMultiplier":50,"landingAnimation":true}""",
            """{"floor":2,"eventType":"ScaleRadius","scale":150}""",
            """{"floor":2,"eventType":"MultiPlanet","planets":"ThreePlanets"}""",
            """{"floor":3,"eventType":"Hold","duration":5,"active":false}""",
            """{"floor":3,"eventType":"MultiPlanet","planets":"TwoPlanets","active":false}""",
            """{"floor":4,"eventType":"PlanetRadius","scale":100}""",
            """{"floor":4,"eventType":"MultiPlanet","planets":"TwoPlanets"}""");

        Level playback = PlaybackEventBinder.ForPlayback(level);
        List<Floor> floors = playback.CreateFloors();
        LevelRenderer.PlaybackTimeline timeline = LevelRenderer.BuildTimeline(playback, floors);
        float spacing = Floor.length * 2f;
        PlanetTrackFloor.ApplySpacing(floors, timeline.PlanetTrack, spacing);

        Assert.That(timeline.PlanetTrack[1].LandingAnimation, Is.True);
        Assert.That(timeline.PlanetTrack[2].Radius, Is.EqualTo(1.5f));
        Assert.That(timeline.PlanetTrack[4].Radius, Is.EqualTo(1f));
        Assert.That(timeline.PlanetTrack[3].HasHold, Is.False);
        Assert.That(timeline.PlanetTrack.Select(item => item.Count), Is.EqualTo(new[] { 2, 2, 3, 3, 2, 2 }));
        Assert.That(floors[2].position.X - floors[1].position.X, Is.EqualTo(4 * spacing).Within(.001));
        Assert.That(timeline.EntryTimes[2] - timeline.EntryTimes[1], Is.EqualTo(2.5).Within(1e-5));
        Assert.That(timeline.SweepDelta[2], Is.EqualTo(-120).Within(1e-4));
        Assert.That(timeline.SweepStart[2] + timeline.SweepDelta[2], Is.EqualTo(floors[2].angle).Within(1e-5));
        Assert.That(timeline.PlanetTrack[3].Pivot, Is.EqualTo(timeline.PlanetTrack[2].Orbit));
        FloorTrackState[] states = FloorTrackStateResolver.Resolve(playback, floors);
        using var guides = TrackGuideMesh.Build(floors, states, states, timeline, spacing, CancellationToken.None);
        Assert.That(guides?.IndexCount, Is.GreaterThan(300));
    }

    [Test]
    public void RelativePositionTrackKeepsLocalOffsetAndContinuesFromTheResolvedPosition()
    {
        Level level = Chart(
            """{"floor":1,"eventType":"PositionTrack","positionOffset":[1,0]}""",
            """{"floor":2,"eventType":"PositionTrack","positionOffset":[2,0],"justThisTile":true}""",
            """{"floor":2,"eventType":"PositionTrack","positionOffset":[0,0],"relativeTo":[1,"Start"]}""",
            """{"floor":3,"eventType":"PositionTrack","positionOffset":[0,1],"editorOnly":true,"justThisTile":true}""");
        List<Floor> floors = level.CreateFloors();
        PlaybackEvent[] events = PlaybackEventBinder.Bind(level);
        FloorTrackState[] preview = FloorTrackStateResolver.Resolve(level, floors, events, true);
        FloorTrackState[] runtime = FloorTrackStateResolver.Resolve(level, floors, events, false);
        float spacing = Floor.length * 2f;

        Assert.That(preview[2].Transform.Offset.X, Is.EqualTo(2 * spacing).Within(.001));
        Assert.That(preview[3].Transform.Offset, Is.EqualTo(new Vector2(2 * spacing, spacing)));
        Assert.That(runtime[3].Transform.Offset, Is.EqualTo(new Vector2(2 * spacing, 0)));
        Assert.That(preview[4].Transform.Offset, Is.EqualTo(runtime[4].Transform.Offset));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MidspinAndItsSuccessorKeepThePivotIdentity(bool threePlanets)
    {
        Level level = ChartWithAngles(new JArray(0, 0, 999, 0, 0, 0), threePlanets
            ? ["""{"floor":1,"eventType":"MultiPlanet","planets":"ThreePlanets"}"""] : []);
        LevelRenderer.PlaybackTimeline timeline = LevelRenderer.BuildTimeline(level, level.CreateFloors());

        Assert.That(timeline.EntryTimes[3], Is.EqualTo(timeline.EntryTimes[2]).Within(1e-7));
        Assert.That(LevelRenderer.FloorAtTime(timeline.EntryTimes, timeline.EntryTimes[2]), Is.EqualTo(3));
        Assert.That(timeline.PlanetTrack[3].Pivot, Is.EqualTo(timeline.PlanetTrack[1].Pivot));
        Assert.That(timeline.PlanetTrack[3].Orbit, Is.EqualTo(timeline.PlanetTrack[1].Orbit));
        Assert.That(timeline.PlanetTrack[4].Pivot, Is.EqualTo(timeline.PlanetTrack[3].Orbit));
    }

    [TestCase("Forward", 90)]
    [TestCase("Backward", -90)]
    [TestCase("None", 0)]
    public void FractionalPauseAnchorsTheExitAndEasesTheCosmeticAngle(string direction, double initialOffset)
    {
        Level level = Chart($$$"""{"floor":1,"eventType":"Pause","duration":0.5,"angleCorrectionDir":"{{{direction}}}"}""");
        List<Floor> floors = level.CreateFloors();
        LevelRenderer.PlaybackTimeline timeline = LevelRenderer.BuildTimeline(level, floors);

        Assert.That(timeline.EntryTimes[2] - timeline.EntryTimes[1], Is.EqualTo(.75).Within(1e-7));
        Assert.That(timeline.SweepStart[1] + timeline.SweepDelta[1], Is.EqualTo(floors[1].angle).Within(1e-7));
        if (initialOffset == 0)
        {
            Assert.That(timeline.PauseAngles, Is.Null);
            return;
        }

        PauseAngleCorrection correction = timeline.PauseAngles![1];
        Assert.That(correction.At(0), Is.EqualTo(initialOffset).Within(1e-7));
        Assert.That(correction.At(.25), Is.EqualTo(initialOffset * (1 - Math.Sqrt(.5))).Within(1e-7));
        Assert.That(correction.At(.5), Is.Zero.Within(1e-7));
    }

    private static Level Chart(params string[] actions) => ChartWithAngles(new JArray(0, 0, 0, 0, 0), actions);

    private static Level ChartWithAngles(JArray angles, params string[] actions) => new(new Dictionary<string, object>
    {
        ["angleData"] = angles,
        ["settings"] = new JObject
        {
            ["version"] = 15, ["bpm"] = 120, ["pitch"] = 100, ["offset"] = 0,
            ["countdownTicks"] = 1, ["separateCountdownTime"] = false,
        },
        ["actions"] = new JArray(actions.Select(JObject.Parse)),
    });
}
