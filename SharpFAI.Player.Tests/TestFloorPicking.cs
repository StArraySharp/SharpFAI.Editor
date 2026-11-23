using System.Diagnostics;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

[NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class TestFloorPicking
{
    [Test, Explicit("Requires desktop OpenGL.")]
    public void OverlappingLoopsReturnFrontToBackHitsAndMatchingOutlines()
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
            using var renderer = new LevelRenderer();
            var root = JObject.Parse("""
                {"angleData":[0,90,180,270,0,90,180,270,0,90,180,270],
                 "settings":{"version":19,"bpm":120,"songFilename":"","trackStyle":"Standard",
                   "trackAnimation":"None","trackDisappearAnimation":"None","zoom":100},
                 "actions":[],"decorations":[]}
                """);
            var level = new Level(root.Properties().ToDictionary(p => p.Name, p => (object)p.Value));
            renderer.Resize(640, 480);
            Assert.That(renderer.Load(level, Path.Combine(Path.GetTempPath(), "floor-picking-test.adofai")), Is.True);
            var clock = Stopwatch.StartNew();
            while (renderer.IsLoading && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                renderer.Tick(clock.Elapsed.TotalSeconds);
                Thread.Sleep(5);
            }
            Assert.That(renderer.LoadError, Is.Null);
            Assert.That(renderer.IsLoading, Is.False);
            Assert.That(renderer.TryGetFloorCenter(1, out Vector2 center), Is.True);
            renderer.Camera.Position = center;
            renderer.Camera.Zoom = 1.5f;
            renderer.Camera.Rotation = 23;
            renderer.Camera.MarkDirty();
            renderer.Render();

            Vector2 cursor = renderer.Camera.WorldToScreen(center);
            List<int> hits = [];
            renderer.PickFloors(cursor, hits);
            Assert.That(hits, Is.EqualTo(new[] { 1, 5, 9 }));
            Assert.That(renderer.PickFloor(cursor), Is.EqualTo(1));

            Dictionary<int, Vector2[]> outlines = [];
            renderer.VisitFloorOutlines(1, 9, (floor, points) => outlines.Add(floor, points.ToArray()));
            Assert.That(outlines[1], Has.Length.GreaterThan(3));
            Assert.That(outlines[5].Length, Is.EqualTo(outlines[1].Length));
            for (int i = 0; i < outlines[1].Length; i++)
                Assert.That(Vector2.Distance(outlines[1][i], outlines[5][i]), Is.LessThan(.001f));

            renderer.PickFloors(Vector2.Zero, hits);
            Assert.That(hits, Is.Empty);
        }
        finally
        {
            Shader.SourceResolver = resolve;
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }
}
