using System.Diagnostics;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Filters;
using SharpFAI.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Thrust.Graphics;
using Thrust.Scene;

namespace SharpFAI.Player.Tests;

[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class TestCameraFilterRendering
{
    [Test, Explicit("Requires desktop OpenGL.")]
    public void FilterUniformsUseTheLinkedVectorWidthAndIntegerType()
    {
        var fixture = Directory.CreateTempSubdirectory("sharpfai-uniforms-");
        string previousDirectory = ShaderLibrary.DiskDirectory;
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            var filters = fixture.CreateSubdirectory("Filters");
            File.WriteAllText(Path.Combine(filters.FullName, "UniformTypes.frag"), """
                #version 410 core
                out vec4 color;
                uniform vec2 _MainTex_TexelSize;
                uniform int _NumberOfWaves;
                void main() { color = vec4(_MainTex_TexelSize, float(_NumberOfWaves) / 8.0, 1.0); }
                """);
            File.WriteAllText(Path.Combine(filters.FullName, "UniformTypes.shaderlab"), """
                Shader "UniformTypes" {
                    Properties {
                        _NumberOfWaves ("Waves", Range(0, 8)) = 2
                    }
                }
                """);
            ShaderLibrary.DiskDirectory = fixture.FullName;
            ShaderLibrary.InvalidateCache();

            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(8, 4), StartVisible = false, StartFocused = false,
                APIVersion = new Version(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Disable(EnableCap.Dither);
            using var backend = new OpenGlFilterBackend();
            var source = backend.RentTarget(8, 4, 0);
            var destination = (OpenGlFilterBackend.Target)backend.RentTarget(8, 4, 0);
            var shader = backend.FindShader("UniformTypes");
            var defaults = backend.CreateMaterial(shader);
            var overrideMaterial = backend.CreateMaterial(shader);
            overrideMaterial.SetInt("_NumberOfWaves", 5);

            DrawAndCheck(defaults, 64);
            DrawAndCheck(overrideMaterial, 159);
            DrawAndCheck(defaults, 64);

            void DrawAndCheck(IFilterMaterial material, byte blue)
            {
                backend.Blit(source, destination, material);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, destination.Framebuffer);
                byte[] pixel = new byte[4];
                GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                Assert.That(pixel, Is.EqualTo(new byte[] { 32, 64, blue, 255 }));
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
            ShaderLibrary.DiskDirectory = previousDirectory;
            ShaderLibrary.InvalidateCache();
            fixture.Delete(recursive: true);
        }
    }

    [Test, Explicit("Requires desktop OpenGL and the locally imported game shaders and textures.")]
    public void CameraPlanesMotionBlurAndPetalsRenderThroughTheOpenGlBackend()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new Vector2i(128, 96), StartVisible = false, StartFocused = false,
                APIVersion = new Version(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, 128, 96);
            GL.Disable(EnableCap.Dither);
            using var backend = new OpenGlFilterBackend();
            using var invert = GeneratedFilterRegistry.Factories["CameraFilterPack_Color_Invert"](backend);
            invert.SetParameter("_Fade", new System.Numerics.Vector4(1, 0, 0, 0));
            invert.Enable();
            FilterTimeline.Entry[] background = [new("Invert", invert, 0)];
            Clear(1, 0, 0);
            backend.Draw(0, 10, 0, background, () => { }, background: true);
            PaintRectangle(0, 0, 32, 96, 0, 1, 0);
            byte[] pixels = ReadPixels();
            Assert.That(pixels[..3], Is.EqualTo(new byte[] { 0, 255, 0 }));
            Assert.That(pixels.AsSpan(64 * 4, 3).ToArray(), Is.EqualTo(new byte[] { 0, 255, 255 }));

            using var blur = new CameraMotionBlurFilter(backend);
            FilterTimeline.Entry[] motion = [new("MotionBlur", blur, 0)];
            backend.CameraView = Matrix4.Identity;
            backend.Draw(1, 11, .016f, motion, DrawEdge);
            byte[] stationary = ReadPixels();
            backend.CameraView = Matrix4.CreateTranslation(.5f, 0, 0);
            backend.Draw(1.016, 11.016, .016f, motion, DrawEdge);
            byte[] moving = ReadPixels();
            Assert.That(stationary[63 * 4], Is.EqualTo(0));
            Assert.That(stationary[64 * 4], Is.EqualTo(255));
            Assert.That(moving.Where((value, index) => index % 4 == 0 && value is > 0 and < 255).Count(),
                Is.GreaterThan(96), "A moving camera should soften the edge.");

            using var sprites = new SpriteRenderer();
            using var petals = new FallingPetalsFilter(backend);
            petals.Restart(0, 28);
            Clear(0, 0, 0);
            var camera = new Camera2D { ViewportWidth = 128, ViewportHeight = 96 };
            Assert.That(petals.Draw(sprites, camera, 0, 1), Is.EqualTo(3));
            Assert.That(ReadPixels().Where((value, index) => index % 4 != 3).Any(value => value > 0), Is.True);
            Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    public enum CameraCase { Background, ZeroZoom, BothFilters, Feedback, TileAnchor, EffectsDisabled }

    [TestCase(CameraCase.Background, TestName = "BackgroundFilterDoesNotAffectPositiveDepthDecorations", Explicit = true)]
    [TestCase(CameraCase.ZeroZoom, TestName = "ZeroZoomKeepsFilteredBackgroundButHidesDecorations", Explicit = true)]
    [TestCase(CameraCase.BothFilters, TestName = "ForegroundFilterUsesBackgroundOutputAndMainCameraDecorations", Explicit = true)]
    [TestCase(CameraCase.Feedback, TestName = "HallOfMirrorsPreservesTheFrameBeforeActivation", Explicit = true)]
    [TestCase(CameraCase.TileAnchor, TestName = "TileRelativeDecorationIgnoresEditorOnlyOffsetDuringPlayback", Explicit = true)]
    [TestCase(CameraCase.EffectsDisabled, TestName = "DisabledCameraEffectsDoNotAlterOfflineFrames", Explicit = true)]
    public void RenderCameraPlanes(CameraCase mode)
    {
        bool disableMainCamera = mode == CameraCase.ZeroZoom;
        bool invertForeground = mode == CameraCase.BothFilters;
        bool enableFeedback = mode == CameraCase.Feedback;
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        var resolve = Shader.SourceResolver;
        var fixture = Directory.CreateTempSubdirectory("sharpfai-flash-");
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(128, 96), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            Shader.SourceResolver = ShaderLibrary.ResolveForEngine;
            using var renderer = new LevelRenderer { SkipAudio = true };
            using (var image = new Image<Rgba32>(100, 100, new Rgba32(255, 0, 0)))
                image.SaveAsPng(Path.Combine(fixture.FullName, "red.png"));
            var root = JObject.Parse("""
                {"angleData":[0,0], "settings":{"version":19,"bpm":120,"offset":0,"songFilename":"",
                   "separateCountdownTime":true,"backgroundColor":"000000","defaultBGShapeType":"Disabled"},
                 "actions":[
                   {"floor":0,"eventType":"SetFilterAdvanced","filter":"CameraFilterPack_Color_Invert",
                    "enabled":true,"targetType":"Camera","plane":"Background","filterProperties":"\"filter__Fade\":100"},
                   {"floor":0,"eventType":"Flash","plane":"Background","duration":0,
                    "startColor":"000000ff","endColor":"000000ff","startOpacity":70,"endOpacity":70}
                 ],"decorations":[{"floor":0,"eventType":"AddDecoration","decorationImage":"red.png",
                   "relativeTo":"Camera","position":[6,-5],"scale":[200,200],"depth":10}]}
                """);
            if (disableMainCamera)
                root["settings"]!["zoom"] = 0;
            if (invertForeground)
            {
                var actions = (JArray)root["actions"]!;
                var invert = (JObject)actions[0].DeepClone();
                invert["plane"] = "Foreground";
                actions.Add(invert);
            }
            if (enableFeedback)
            {
                var actions = (JArray)root["actions"]!;
                var stopInvert = (JObject)actions[0].DeepClone();
                stopInvert["enabled"] = false;
                stopInvert["angleOffset"] = 180;
                actions.Add(stopInvert);
                var stopFlash = (JObject)actions[1].DeepClone();
                stopFlash["startOpacity"] = stopFlash["endOpacity"] = 0;
                stopFlash["angleOffset"] = 180;
                actions.Add(stopFlash);
                actions.Add(JObject.Parse("""{"floor":0,"eventType":"HallOfMirrors","enabled":true,"angleOffset":180}"""));
            }
            if (mode == CameraCase.TileAnchor)
            {
                var actions = (JArray)root["actions"]!;
                actions.Add(JObject.Parse("""{"floor":0,"eventType":"PositionTrack","positionOffset":[0,1],"editorOnly":true}"""));
                root["decorations"]![0]!["relativeTo"] = "Tile";
            }
            var level = new Level(root.Properties().ToDictionary(p => p.Name, p => (object)p.Value));
            renderer.Resize(128, 96);
            Assert.That(renderer.Load(level, Path.Combine(fixture.FullName, "level.adofai")), Is.True);
            var clock = Stopwatch.StartNew();
            while (renderer.IsLoading && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                renderer.Tick(clock.Elapsed.TotalSeconds);
                Thread.Sleep(5);
            }
            Assert.That(renderer.LoadError, Is.Null);
            Assert.That(renderer.IsLoading, Is.False);

            if (mode == CameraCase.EffectsDisabled)
            {
                renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults with
                {
                    CameraFilters = false,
                    ScreenEffects = false,
                    ScreenFlash = false,
                });
                renderer.SeekForOfflineRender(.01);
                GL.Viewport(0, 0, 128, 96);
                Clear(0, 0, 0);
                renderer.Render();
                byte[] unfiltered = ReadPixels();
                Assert.That(unfiltered[..3], Is.EqualTo(new byte[] { 0, 0, 0 }));
                Assert.That(unfiltered.AsSpan((24 * 128 + 102) * 4, 3).ToArray(),
                    Is.EqualTo(new byte[] { 255, 0, 0 }));
                renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults with
                {
                    CameraFilters = false, ScreenEffects = false, ScreenFlash = false,
                    VisibleDecorations = DecorationKinds.All & ~DecorationKinds.Image,
                });
                Clear(0, 0, 0);
                renderer.Render();
                Assert.That(ReadPixels().AsSpan((24 * 128 + 102) * 4, 3).ToArray(),
                    Is.EqualTo(new byte[] { 0, 0, 0 }));
                renderer.ApplyVisualSettings(ApplicationSettings.PlayerDefaults);
            }

            renderer.SeekForOfflineRender(.01);
            GL.Viewport(0, 0, 128, 96);
            Clear(0, 0, 0);
            renderer.Render();
            if (mode == CameraCase.TileAnchor)
            {
                float playbackY = renderer.DecorationFloor(0, dynamic: false).Center.Y;
                renderer.SetRuntimeTrackMode(false);
                float editorY = renderer.DecorationFloor(0, dynamic: false).Center.Y;
                Assert.That(editorY - playbackY, Is.EqualTo(renderer.TileSpacing).Within(.0001f),
                    "The editor-only offset moves the decoration up only in the editor.");
                return;
            }
            if (enableFeedback)
            {
                renderer.SeekForOfflineRender(.51);
                Clear(0, 0, 0);
                renderer.Render();
            }
            var pixels = ReadPixels();
            byte[] white = [255, 255, 255];
            byte[] black = [0, 0, 0];
            byte[] red = [255, 0, 0];
            byte[] cyan = [0, 255, 255];
            Assert.Multiple(() =>
            {
                Assert.That(pixels[..3], Is.EqualTo(invertForeground ? black : white),
                    "The background filter sees the flash and the foreground filter sees that result.");
                Assert.That(pixels.AsSpan((24 * 128 + 102) * 4, 3).ToArray(),
                    Is.EqualTo(disableMainCamera ? white : invertForeground ? cyan : red),
                    "Positive-depth decorations are rendered by the main camera, before its filters.");
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
            });
        }
        finally
        {
            Shader.SourceResolver = resolve;
            GLFWProvider.CheckForMainThread = checkMainThread;
            fixture.Delete(recursive: true);
        }
    }

    private static void DrawEdge()
    {
        Clear(0, 0, 0);
        PaintRectangle(64, 0, 64, 96, 1, 1, 1);
    }

    private static void PaintRectangle(int x, int y, int width, int height, float red, float green, float blue)
    {
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(x, y, width, height);
        Clear(red, green, blue);
        GL.Disable(EnableCap.ScissorTest);
    }

    private static void Clear(float red, float green, float blue)
    {
        GL.ClearColor(red, green, blue, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit);
    }

    private static byte[] ReadPixels()
    {
        byte[] pixels = new byte[128 * 96 * 4];
        GL.ReadPixels(0, 0, 128, 96, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        return pixels;
    }
}
