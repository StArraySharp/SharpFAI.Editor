using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

public sealed class TestPlanetTrail
{
    [Test]
    public void PaletteChangesRecolorExistingTrailsWithoutChangingTheirShape()
    {
        using var renderer = new LevelRenderer();
        var trail = (PlanetTrail)typeof(LevelRenderer).GetField("_trail", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        trail.RecordPlanet(1, new Vector2(0, 0), PlanetTrail.RedTint, .1);
        trail.RecordPlanet(1, new Vector2(10, 0), PlanetTrail.RedTint, .2);
        trail.RecordPlanet(0, new Vector2(0, 20), PlanetTrail.BlueTint, .1);
        trail.RecordPlanet(0, new Vector2(10, 20), PlanetTrail.BlueTint, .2);
        var original = trail.Build(.25).ToArray();

        renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults with
        {
            FirePlanetColor = "#00FF00",
            IcePlanetColor = "#FF00FF",
        });
        var recolored = trail.Build(.25).ToArray();

        Assert.That(recolored.Select(sample => (sample.Center, sample.Size, sample.Tint.W)),
            Is.EqualTo(original.Select(sample => (sample.Center, sample.Size, sample.Tint.W))));
        Assert.That(recolored.Where(sample => sample.Center.Y == 0)
            .All(sample => sample.Tint.X == 0f && sample.Tint.Y > 0f && sample.Tint.Z == 0f), Is.True);
        Assert.That(recolored.Where(sample => sample.Center.Y == 20)
            .All(sample => sample.Tint.X > 0f && sample.Tint.Y == 0f && sample.Tint.X == sample.Tint.Z), Is.True);

        renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults);
        Assert.That(trail.Build(.25).ToArray(), Is.EqualTo(original));
    }

    [TestCase(0, 0)]
    [TestCase(50, 6)]
    [TestCase(100, 1)]
    [TestCase(200, 0)]
    public void TrailLengthControlsRetainedHistory(int percent, int firstPosition)
    {
        var trail = new PlanetTrail { ActiveLifetime = PlanetTrail.Lifetime * percent / 100d };
        for (int i = 0; i <= 10; i++)
            trail.RecordPlanet(1, new Vector2(i, 0), PlanetTrail.RedTint, i / 10d);

        var samples = trail.Build(1);
        if (percent == 0)
            Assert.That(samples.Length, Is.Zero);
        else
            Assert.That(samples[0].Center.X, Is.EqualTo(firstPosition));
    }

    [Test]
    public void DenseTrailViewportKeepsTheSameVisibleSamplesInOrder()
    {
        var trail = new PlanetTrail();
        trail.RecordPlanet(0, new Vector2(-200000, 0), PlanetTrail.BlueTint, .1);
        trail.RecordPlanet(0, new Vector2(200000, 0), PlanetTrail.BlueTint, .4);
        var view = new FloorRegion(new Vector2(-1000), new Vector2(1000));
        PlanetTrail.Sample[] full = trail.Build(.5).ToArray();
        var expected = full.Where(sample =>
        {
            Vector2 half = new(sample.Size * .5f);
            return view.Intersects(new FloorRegion(sample.Center - half, sample.Center + half));
        }).ToArray();

        Assert.That(full, Has.Length.EqualTo(32768));
        Assert.That(expected, Is.Not.Empty);
        Assert.That(trail.Build(.5, view).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void DenseOfflineHistoryKeepsUnexpiredPointsAfterGrowing()
    {
        var trail = new PlanetTrail();
        for (int i = 0; i < 7000; i++)
            trail.RecordPlanet(1, new Vector2(i, 0), PlanetTrail.RedTint, i * .0002, preserveHistory: true);
        var samples = trail.Build(1.4);
        Assert.That(samples[0].Center.X, Is.EqualTo(2300).Within(1));
        Assert.That(samples[^1].Center.X, Is.EqualTo(6999).Within(.01));
    }

    [Test, Explicit("Requires desktop OpenGL."), NonParallelizable, Apartment(ApartmentState.STA)]
    public void OfflineFramesKeepTheTrailWhenThePlanetsReturnToTheirStartingPositions()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        var resolve = Shader.SourceResolver;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(640, 480), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            Shader.SourceResolver = ShaderLibrary.ResolveForEngine;
            using var renderer = new LevelRenderer { SkipAudio = true };
            var level = new Level(new Dictionary<string, object>
            {
                ["angleData"] = new JArray(Enumerable.Range(0, 400).Select(i => i % 2 * 180)),
                ["settings"] = JObject.Parse("""
                    {"version":19,"bpm":14400,"offset":0,"countdownTicks":1,"songFilename":"","zoom":100}
                    """),
                ["actions"] = new JArray(),
                ["decorations"] = new JArray(),
            });
            renderer.Resize(640, 480);
            Assert.That(renderer.Load(level, Path.Combine(Path.GetTempPath(), "offline-trail-loop.adofai")), Is.True);
            var clock = Stopwatch.StartNew();
            while (renderer.IsLoading && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                renderer.Tick(clock.Elapsed.TotalSeconds);
                Thread.Sleep(1);
            }
            Assert.That(renderer.LoadError, Is.Null);
            Assert.That(renderer.IsLoading, Is.False);

            PlanetTrail trail = (PlanetTrail)typeof(LevelRenderer)
                .GetField("_trail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
            renderer.SeekForOfflineRender(0);
            renderer.Render();
            Assert.That(trail.SampleCount, Is.Zero, "No trail has formed at the start of the chart.");
            for (int frame = 1; frame <= 12; frame++)
            {
                renderer.SeekForOfflineRender(frame / 60.0);
                renderer.Render();
                Assert.That(trail.SampleCount, Is.GreaterThan(0), $"Frame {frame} lost its trail.");
            }

            renderer.EndOfflineRender();
            renderer.SeekForOfflineRender(1.25);
            renderer.Render();
            int samples = trail.SampleCount;
            Assert.That(samples, Is.GreaterThan(0), "A late initial frame needs its recent trail history.");
            renderer.SeekForOfflineRender(1.25);
            renderer.Render();
            Assert.That(trail.SampleCount, Is.EqualTo(samples));
            renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults with
            {
                FirePlanetColor = "#00FF00", IcePlanetColor = "#FF00FF",
            });
            renderer.Render();
            Assert.That(trail.SampleCount, Is.EqualTo(samples));
            var recolored = trail.Build(1.25).ToArray();
            Assert.That(recolored.Any(sample => sample.Tint.Y > 0f && sample.Tint.X == 0f), Is.True);
            Assert.That(recolored.Any(sample => sample.Tint.X > 0f && sample.Tint.X == sample.Tint.Z), Is.True);

            renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults with { PlanetTrailPercent = 0 });
            renderer.SeekForOfflineRender(1.25);
            renderer.Render();
            Assert.That(trail.SampleCount, Is.Zero);
            renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults);
            renderer.SeekForOfflineRender(1.25);
            renderer.Render();
            Assert.That(trail.SampleCount, Is.EqualTo(samples));
            Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
        }
        finally
        {
            Shader.SourceResolver = resolve;
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }
}
