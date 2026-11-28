using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using Thrust.Graphics;
using Thrust.Scene;

namespace SharpFAI.Player.Tests;

[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class TestDecorationMaskRendering
{
    [Test, Explicit("Requires desktop OpenGL.")]
    public void MovingPlanetObjectRendersItsOwnTrailAndHonorsTheLifetimeSetting()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            const int width = 320, height = 192;
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(width, height), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, width, height);
            GL.ClearColor(0, 0, 0, 1);
            using var renderer = new ImageDecorationRenderer();
            var camera = new Camera2D { ViewportWidth = width, ViewportHeight = height, Zoom = 1 };
            var timeline = new ImageDecorationTimeline();
            var source = new AddObjectDecoration
            {
                ObjectType = "Planet", PlanetColorType = "Custom", PlanetColor = "00ff00",
            };
            var art = ObjectDecorationArt.Resolve(source, timeline.Images);
            var pose = new ImageDecorationTimeline.Pose(art.Key, Vector2.Zero,
                Vector2.Zero, Vector2.One, Vector2.One, Vector2.Zero, Vector2.Zero, art.Tint,
                0, 1, -1, true, true, "RedPlanet");
            timeline.Items.Add(new(source, pose, Vector2.One, 0));

            Assert.That(DrawFrame(0, -100), Is.Zero);
            DrawFrame(.1, -20);
            byte trail = DrawFrame(.2, 100);
            Assert.That(trail, Is.GreaterThan(8), "A green trail should be visible outside the planet sprite.");
            Assert.That(DrawFrame(.2, 100), Is.EqualTo(trail), "Redrawing a paused frame must not add history.");
            Assert.That(DrawFrame(.05, 100), Is.Zero, "Rewinding must clear the earlier path.");
            DrawFrame(.10, -20);
            DrawFrame(.15, 20);
            Assert.That(DrawFrame(.20, 100, lifetime: 0), Is.Zero);

            byte DrawFrame(double time, float x, double lifetime = PlanetTrail.Lifetime)
            {
                GL.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Prepare(timeline, time, camera, 100, camera.Zoom, static (_, _) => default,
                    _ => new Vector2(x, 0), planetTrailLifetime: lifetime);
                renderer.Draw(timeline, background: true, camera);
                renderer.Draw(timeline, background: false, camera);
                byte[] pixel = new byte[4];
                GL.ReadPixels(198, height / 2, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                return pixel[1];
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void PlanetObjectTrailMatchesGameplayTrailForTheSamePath()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            const int width = 256, height = 128;
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(width, height), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, width, height);
            GL.ClearColor(.15f, .25f, .40f, .55f);
            using var decorations = new ImageDecorationRenderer();
            using var gameplay = new SpriteRenderer();
            var camera = new Camera2D { ViewportWidth = width, ViewportHeight = height, Zoom = 1 };

            foreach (string preset in new[] { "DefaultRed", "DefaultBlue" })
            {
                var timeline = new ImageDecorationTimeline();
                var source = new AddObjectDecoration { ObjectType = "Planet", PlanetColorType = preset };
                var art = ObjectDecorationArt.Resolve(source, timeline.Images);
                var pose = new ImageDecorationTimeline.Pose(art.Key, Vector2.Zero,
                    Vector2.Zero, Vector2.One, Vector2.One, Vector2.Zero, Vector2.Zero, art.Tint,
                    0, 1, -1, true, true, "RedPlanet");
                timeline.Items.Add(new(source, pose, Vector2.One, 0));
                var trail = new PlanetTrail();
                foreach (var (time, x) in new[] { (0d, -90f), (.1d, 0f), (.2d, 200f) })
                {
                    var center = new Vector2(x, 0);
                    trail.RecordPlanet(1, center, art.Tint, time);
                    decorations.Prepare(timeline, time, camera, 100, camera.Zoom, static (_, _) => default,
                        _ => center, planetTrailLifetime: PlanetTrail.Lifetime);
                }

                GL.Clear(ClearBufferMask.ColorBufferBit);
                byte[] background = Read();
                decorations.Draw(timeline, background: true, camera);
                decorations.Draw(timeline, background: false, camera);
                byte[] objectPixels = Read();
                Assert.That(objectPixels, Is.Not.EqualTo(background), preset);

                GL.Clear(ClearBufferMask.ColorBufferBit);
                var particles = trail.Build(.2).ToArray().Select(sample => new SpriteRenderer.ParticleInstance
                {
                    Center = sample.Center,
                    HalfSize = new Vector2(sample.Size * .5f),
                    Tint = sample.Tint,
                }).ToArray();
                gameplay.DrawParticleInstances(camera.ViewProjection, particles);
                Assert.That(objectPixels, Is.EqualTo(Read()), preset);
            }

            static byte[] Read()
            {
                byte[] pixels = new byte[width * height * 4];
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                return pixels;
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void FloorStreamTextureMatchesExpandedMeshAcrossTextureRows()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            const int width = 256, height = 192, floorCount = 177;
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(width, height), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, width, height);
            GL.ClearColor(0, 0, 0, 1);
            using var uploader = new GlMeshUploader();
            using var track = new OfficialFloorRenderer(uploader, OfficialFloorRenderer.GetTextureCapacity(),
                useStreamTexture2D: true);
            var camera = new Camera2D { ViewportWidth = width, ViewportHeight = height, Zoom = .08f };
            Floor[] floors = Enumerable.Range(0, floorCount).Select(i => new Floor(-180, 90 + i * .1,
                new System.Numerics.Vector2((i % 16 - 8) * 160, (i / 16 - 5) * 160)) { index = i }).ToArray();
            FloorTrackState state = new(TrackStyleInfo.Standard, Vector4.One, Vector4.One,
                TrackColorMode.Single, 0, 1, 1, 0, 0, 0);
            FloorTrackState[] states = Enumerable.Repeat(state, floorCount).ToArray();
            var shapes = new FloorShapeLibrary();
            foreach (Floor floor in floors)
                shapes.Add(OfficialFloorMeshBaker.Shape.Create(floor, state.Material));
            Assert.That(shapes.TexelCount, Is.GreaterThan(FloorStreamGpu.TextureRowWidth));

            track.SetGeometry(OfficialFloorMeshBaker.BakeLevel(floors, states, track.TextureSlots));
            byte[] expanded = Draw();
            Assert.That(expanded.Where((value, index) => index % 4 == 0 && value != 0).Count(), Is.GreaterThan(100));

            // Start with a partial row, grow past a row boundary, and reuse that allocation.
            track.SetGeometry(OfficialFloorMeshBaker.BakeLevel(floors, states, track.TextureSlots,
                floorIndices: [0], computeBounds: false, shapes: shapes));
            Draw();
            for (int pass = 0; pass < 2; pass++)
            {
                FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel(floors, states, track.TextureSlots,
                    computeBounds: false, shapes: shapes);
                Assert.That(geometry.Stream!.InstanceTexels, Is.GreaterThan(FloorStreamGpu.TextureRowWidth));
                track.SetGeometry(geometry);
                Assert.That(Draw(), Is.EqualTo(expanded));
            }

            byte[] Draw()
            {
                GL.Clear(ClearBufferMask.ColorBufferBit);
                track.Draw(camera.ViewProjection, camera.Position, width, height, false);
                byte[] pixels = new byte[width * height * 4];
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                return pixels;
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void FloorDecorationStylesMatchTheTrackPixelForPixel()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(160, 128), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, 160, 128);
            GL.ClearColor(0, 0, 0, 1);
            using var uploader = new GlMeshUploader();
            using var track = new OfficialFloorRenderer(uploader, OfficialFloorRenderer.GetTextureCapacity());
            using var decoration = new ImageDecorationRenderer(track);
            var camera = new Camera2D { ViewportWidth = 160, ViewportHeight = 128, Zoom = .8f };
            var distinct = new HashSet<string>();
            foreach (var style in TrackStyleInfo.All)
            {
                var source = new AddObjectDecoration
                {
                    ObjectType = "Floor", TrackAngle = 90f, TrackStyle = style.Name, TrackColor = "debb7b",
                    TrackIcon = "Swirl", TrackRedSwirl = true, TrackIconOutlines = true,
                };
                var timeline = new ImageDecorationTimeline();
                var art = ObjectDecorationArt.Resolve(source, timeline.Images);
                var pose = new ImageDecorationTimeline.Pose(art.Key, new(.18f, -.08f),
                    Vector2.Zero, Vector2.One, Vector2.One, Vector2.Zero, Vector2.Zero, art.Tint,
                    0, .75f, -1, true, true, "Global");
                timeline.Items.Add(new(source, pose, Vector2.One, 0));

                var floor = new Floor(-180, 90, new System.Numerics.Vector2(18, -8)) { swirlRed = true };
                var state = new FloorTrackState(style, art.Tint, Vector4.One, TrackColorMode.Single, 0, 2f, 0, -1, 0, 0)
                {
                    Icon = FloorIcon.Swirl, IconOutline = true,
                };
                track.SetGeometry(OfficialFloorMeshBaker.BakeLevel([floor], [state], track.TextureSlots));
                track.SetFloorVisual(0, state.Transform, new FloorAppearance(art.Tint, pose.Opacity));
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                track.Draw(camera.ViewProjection, camera.Position, 160, 128, false);
                byte[] trackPixels = Read();
                distinct.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(trackPixels)));

                GL.Clear(ClearBufferMask.ColorBufferBit);
                decoration.Prepare(timeline, 0, camera, 100, camera.Zoom,
                    static (_, _) => default, static _ => default);
                decoration.Draw(timeline, background: true, camera);
                Assert.That(decoration.Draw(timeline, background: false, camera), Is.EqualTo(1), style.Name);
                Assert.That(Read(), Is.EqualTo(trackPixels), style.Name);
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError), style.Name);
            }
            Assert.That(distinct.Count, Is.EqualTo(TrackStyleInfo.All.Length));
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }

        static byte[] Read()
        {
            byte[] pixels = new byte[160 * 128 * 4];
            GL.ReadPixels(0, 0, 160, 128, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            return pixels;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void AlphaScissorPreservesRotatedSamplesAndExistingGlScissor()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            const int width = 320, height = 224;
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(width, height), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, width, height);
            GL.Disable(EnableCap.Dither);
            GL.Scissor(10, 8, width - 20, height - 16);
            const int imageSize = 128;
            byte[] pixels = new byte[imageSize * imageSize * 4];
            for (int y = 0; y < imageSize; y++)
            for (int x = 0; x < imageSize; x++)
            {
                int index = (y * imageSize + x) * 4;
                pixels[index] = (byte)(x * 2);
                pixels[index + 1] = (byte)(y * 2);
                pixels[index + 2] = 173;
                if (x < 38 && y is > 20 and < 114)
                    pixels[index + 3] = (byte)(89 + x + y);
            }

            var original = new Thrust.Graphics.ImageDecoder.RgbaImage(imageSize, imageSize, pixels);
            var packed = DecorationImage.Pack(original);
            var camera = new Camera2D { ViewportWidth = width, ViewportHeight = height, Zoom = 1 };
            var timeline = new ImageDecorationTimeline();
            var pose = new ImageDecorationTimeline.Pose("test", Vector2.Zero, Vector2.Zero,
                new(3.5f, -3f), Vector2.One, Vector2.Zero, Vector2.Zero, Vector4.One, 19f, .8f, -1, true, true, "Camera");
            timeline.Items.Add(new(new AddImageDecoration(), pose, Vector2.One, 0));
            timeline.Items.Add(new(new AddImageDecoration(),
                pose with { Position = new(.85f, .6f), Blend = DecorationBlendMode.Screen }, new(2f, 1f), 1));

            Assert.That(packed.AlphaBounds.HasValue, Is.True);
            var scissor = new DecorationScissor();
            Assert.That(scissor.Clip(Matrix4.CreateScale(2f), packed.AlphaBounds!.Value,
                new Vector2(width, height), true).HasValue, Is.True);
            foreach (bool enabled in new[] { false, true })
            {
                byte[] expected = Render(original, enabled);
                Assert.That(Render(packed, enabled), Is.EqualTo(expected));
            }

            byte[] Render(DecorationImage image, bool enabled)
            {
                timeline.Images["test"] = image;
                using var renderer = new ImageDecorationRenderer();
                GL.Disable(EnableCap.ScissorTest);
                GL.ClearColor(.15f, .3f, .45f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                if (enabled)
                    GL.Enable(EnableCap.ScissorTest);
                renderer.Prepare(timeline, 0, camera, 100, 1, static (_, _) => default, static _ => default);
                renderer.Draw(timeline, false, camera);
                Assert.That(GL.IsEnabled(EnableCap.ScissorTest), Is.EqualTo(enabled));
                int[] state = new int[4];
                GL.GetInteger(GetPName.ScissorBox, state);
                Assert.That(state, Is.EqualTo(new[] { 10, 8, width - 20, height - 16 }));
                byte[] rgba = new byte[width * height * 4];
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                return rgba;
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void MultiTextureBatchesPreservePainterOrderAcrossTextureLimitsAndBlendModes()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(64, 64), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, 64, 64);
            GL.Disable(EnableCap.Dither);
            var camera = new Camera2D { ViewportWidth = 64, ViewportHeight = 64, Zoom = 1 };
            var timeline = new ImageDecorationTimeline();
            for (int index = 0; index < 32; index++)
            {
                string image = $"test-{index % 16}";
                timeline.Images.TryAdd(image, new(1, 1,
                    [(byte)(index * 11), (byte)(255 - index * 7), (byte)(index * 19), 190]));
                var pose = new ImageDecorationTimeline.Pose(image, new(index % 3, 0), Vector2.Zero,
                    new(45), Vector2.One, Vector2.Zero, Vector2.Zero, Vector4.One,
                    index * 7, .3f, -1, true, (index % 2 == 0) != (index >= 16), "Camera")
                {
                    Blend = index == 16 ? DecorationBlendMode.Difference : DecorationBlendMode.None
                };
                timeline.Items.Add(new(new AddImageDecoration(), pose, Vector2.One, index));
            }

            // The odd width exercises padded upload rows in the compact format.
            const int width = 513, height = 513;
            var grayPixels = new byte[width * height * 4];
            var opaquePixels = new byte[grayPixels.Length];
            for (int i = 0; i < grayPixels.Length; i += 4)
            {
                byte shade = (byte)(i / 4);
                grayPixels[i] = grayPixels[i + 1] = grayPixels[i + 2] = shade;
                grayPixels[i + 3] = (byte)(255 - shade);
                opaquePixels[i] = shade;
                opaquePixels[i + 1] = (byte)(shade / 2);
                opaquePixels[i + 2] = (byte)(255 - shade);
                opaquePixels[i + 3] = 255;
            }
            var gray = new Thrust.Graphics.ImageDecoder.RgbaImage(width, height, grayPixels);
            var opaque = new Thrust.Graphics.ImageDecoder.RgbaImage(width, height, opaquePixels);
            timeline.Images["test-0"] = gray;
            timeline.Images["test-1"] = opaque;
            byte[] scalar = Render(instanced: false, out int scalarDraws);
            timeline.Images["test-0"] = DecorationImage.Pack(gray);
            timeline.Images["test-1"] = DecorationImage.Pack(opaque);
            byte[] batched = Render(instanced: true, out int batchedDraws);
            Assert.Multiple(() =>
            {
                Assert.That(timeline.Images["test-0"].Channels, Is.EqualTo(DecorationChannels.GrayAlpha));
                Assert.That(timeline.Images["test-1"].Channels, Is.EqualTo(DecorationChannels.Rgb));
                Assert.That(batched, Is.EqualTo(scalar));
                Assert.That(batchedDraws, Is.LessThan(scalarDraws));
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
            });

            byte[] Render(bool instanced, out int draws)
            {
                using var renderer = new ImageDecorationRenderer { UseInstancing = instanced };
                GL.ClearColor(.1f, .2f, .3f, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Prepare(timeline, 0, camera, 100, 1, static (_, _) => default, static _ => default);
                draws = renderer.Draw(timeline, background: false, camera);
                byte[] rgba = new byte[64 * 64 * 4];
                GL.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
                return rgba;
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void DecorationsShareTheLayerGrabAndDrawTextAboveSpritesAtEqualDepth()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(64, 64), StartVisible = false, StartFocused = false,
                APIVersion = new Version(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, 64, 64);
            GL.Disable(EnableCap.Dither);
            var camera = new Camera2D { ViewportWidth = 64, ViewportHeight = 64, Zoom = 1 };
            using var renderer = new ImageDecorationRenderer();
            var timeline = new ImageDecorationTimeline();
            timeline.Images.Add("title", new(1, 1, [155, 0, 0, 255]));
            timeline.Images.Add("title-behind", new(1, 1, [245, 0, 0, 255]));
            timeline.Images.Add("overlay", new(1, 1, [220, 0, 0, 255]));
            timeline.Images.Add("black", new(1, 1, [0, 0, 0, 255]));
            var pose = new ImageDecorationTimeline.Pose("overlay", Vector2.Zero, Vector2.Zero,
                new(64), Vector2.One, Vector2.Zero, Vector2.Zero, Vector4.One,
                0, 1, -1, true, false, "Camera");
            Add(new AddTextDecoration(), pose with { Image = "title", Scale = new(8), Position = new(-2.5f, 0) });
            Add(new AddImageDecoration(), pose);
            var screen = pose with { Image = "black", Blend = DecorationBlendMode.Screen, Scale = new(32) };
            Add(new AddImageDecoration(), screen with { Position = new(-2.5f, 0) });
            Add(new AddImageDecoration(), screen with { Position = new(2.5f, 0), Opacity = .5f });
            Add(new AddTextDecoration(), pose with
            {
                Image = "title-behind", Scale = new(12), Position = new(-2.5f, 0),
            });

            foreach (int background in new[] { 32, 64 })
            {
                GL.ClearColor(background / 255f, 0, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Prepare(timeline, 0, camera, 100, 1, static (_, _) => default, static _ => default);
                Assert.That(renderer.Draw(timeline, background: false, camera), Is.EqualTo(4));
                Assert.Multiple(() =>
                {
                    Assert.That(Pixel(5, 32), Is.EqualTo(220));
                    Assert.That(Pixel(16, 32), Is.EqualTo(background));
                    Assert.That(Pixel(24, 32), Is.EqualTo(155), "The earlier text is in front.");
                    Assert.That(Pixel(19, 32), Is.EqualTo(245), "The later text shows around the edge.");
                    Assert.That(Pixel(32, 32), Is.EqualTo(background));
                    Assert.That(Pixel(48, 32), Is.EqualTo((background + 220) / 2).Within(1));
                    Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                });
            }

            void Add(AddImageDecoration source, ImageDecorationTimeline.Pose value) =>
                timeline.Items.Add(new(source, value, Vector2.One, timeline.Items.Count));
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void DepthAndTagMasksClipTheSameSpriteWithoutLeakingToOtherDepths()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(64, 64), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, 64, 64);
            GL.Disable(EnableCap.Dither);
            var camera = new Camera2D { ViewportWidth = 64, ViewportHeight = 64, Zoom = 1 };
            using var renderer = new ImageDecorationRenderer();
            var timeline = new ImageDecorationTimeline();
            var pixels = new byte[16 * 16 * 4];
            Array.Fill(pixels, byte.MaxValue);
            timeline.Images.Add("solid", new(16, 16, pixels));
            var basis = new ImageDecorationTimeline.Pose("solid", Vector2.Zero, Vector2.Zero,
                Vector2.One, Vector2.One, Vector2.Zero, Vector2.Zero, Vector4.One,
                0, 1, -1000, true, false, "Camera");
            Add(basis with { Position = new(-5, 0), Opacity = 0, Masking = DecorationMasking.Mask,
                MaskRange = new(true, -1000, -1000) });
            Add(basis with { Position = new(5, 0), Masking = DecorationMasking.Mask,
                MaskingTarget = "receiver", MaskRange = new(true, -1000, -1000) });
            Add(basis with { Position = new(0, 2.5f), Scale = new(4, 1),
                Masking = DecorationMasking.VisibleOutsideMask }, "receiver");
            Add(basis with { Position = new(0, -2.5f), Scale = new(4, 1), Depth = -999,
                Masking = DecorationMasking.VisibleOutsideMask }, "receiver");

            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Prepare(timeline, 1, camera, 100, 1, static (_, _) => default, static _ => default);
            renderer.Draw(timeline, background: true, camera);
            renderer.Draw(timeline, background: false, camera);

            Assert.Multiple(() =>
            {
                Assert.That(Pixel(16, 36), Is.Zero, "The depth mask clips at depth -1000, regardless of opacity.");
                Assert.That(Pixel(16, 28), Is.EqualTo(255), "The depth mask does not clip at depth -999.");
                Assert.That(Pixel(48, 36), Is.Zero, "The matching tag mask also clips at depth -1000.");
                Assert.That(Pixel(48, 28), Is.Zero, "A targeted mask ignores its legacy depth range.");
                Assert.That(Pixel(32, 36), Is.EqualTo(255), "The unmasked section stays visible.");
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
            });

            Add(basis with { Position = new(0, -2.5f), Scale = new(.5f, .5f), Opacity = .25f,
                Masking = DecorationMasking.Mask });
            GL.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Prepare(timeline, 0, camera, 100, 1, static (_, _) => default, static _ => default);
            renderer.Draw(timeline, background: true, camera);
            renderer.Draw(timeline, background: false, camera);

            Assert.Multiple(() =>
            {
                Assert.That(Pixel(32, 27), Is.Zero, "The untargeted mask without a custom range clips the interior.");
                Assert.That(Pixel(38, 27), Is.EqualTo(255), "The border outside the mask stays visible.");
                Assert.That(Pixel(48, 27), Is.Zero, "The tagged mask still clips independently.");
            });

            void Add(ImageDecorationTimeline.Pose pose, string? tag = null) =>
                timeline.Items.Add(new(new AddImageDecoration { Tag = tag }, pose, Vector2.One, timeline.Items.Count));
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void ObjectAndParticleRenderAndParticleClearSurvivesRewind()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(64, 64), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, 64, 64);
            using var renderer = new ImageDecorationRenderer();
            var camera = new Camera2D { ViewportWidth = 64, ViewportHeight = 64, Zoom = 1 };
            var timeline = new ImageDecorationTimeline();
            var obj = new AddObjectDecoration { ObjectType = "Floor", TrackColor = "ffffff" };
            var art = ObjectDecorationArt.Resolve(obj, timeline.Images);
            timeline.Images.Add("dust", new(1, 1, [255, 255, 255, 255]));
            var pose = new ImageDecorationTimeline.Pose(art.Key, new(-6, 0), Vector2.Zero, new(.12f),
                Vector2.One, Vector2.Zero, Vector2.Zero, Vector4.One, 0, 1, -1, true, false, "Camera");
            timeline.Items.Add(new(obj, pose, Vector2.One, 0));
            var particle = new AddParticleDecoration
            {
                ParticleFields = { ["autoPlay"] = false, ["shapeType"] = "Rectangle", ["particleSize"] = 12 },
            };
            var emitter = new ImageDecorationTimeline.Item(particle,
                pose with { Image = "dust", Position = new(6, 0), Scale = new(.01f) }, Vector2.One, 1);
            emitter.Particles!.Add(new() { EventType = SharpFAI.Serialization.EventType.EmitParticle }, 0);
            emitter.Particles.Add(new()
            {
                EventType = SharpFAI.Serialization.EventType.SetParticle,
                ParticleFields = { ["targetMode"] = "Clear" },
            }, 1);
            timeline.Items.Add(emitter);
            var bubble = new AddObjectDecoration { ObjectType = "PlayerBubble" };
            var bubbleArt = ObjectDecorationArt.Resolve(bubble, timeline.Images);
            timeline.Items.Add(new(bubble, pose with { Image = bubbleArt.Key, Position = Vector2.Zero,
                Scale = new(.2f) }, Vector2.One, 2));

            Render(.5);
            Assert.That(Pixel(13, 32), Is.GreaterThan(0), "Floor object must paint its own geometry.");
            Assert.That(Pixel(51, 32), Is.EqualTo(255), "The particle emitter must paint a particle.");
            Assert.That(Pixel(60, 32), Is.Zero, "A 12-unit particle must not cover pixels nine units from its center.");
            Assert.That(Pixel(40, 32), Is.Zero, "Player bubbles are hidden in solo playback.");
            Render(.5, editMode: true);
            Assert.That(Pixel(40, 32), Is.GreaterThan(0), "Player bubbles remain visible in edit mode.");
            Render(1.5);
            Assert.That(Pixel(51, 32), Is.Zero, "Clear removes already emitted particles.");
            Render(.5);
            Assert.That(Pixel(51, 32), Is.EqualTo(255), "Rewinding restores prior particles.");
            Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));

            void Render(double time, bool editMode = false)
            {
                GL.ClearColor(0, 0, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Prepare(timeline, time, camera, 100, 1, static (_, _) => default, static _ => default,
                    collectEditorQuads: editMode);
                renderer.Draw(timeline, background: true, camera);
                renderer.Draw(timeline, background: false, camera);
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    [Test, Explicit("Requires desktop OpenGL.")]
    public void BlendDecorationSamplesTheCurrentDrawFramebufferAndViewport()
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(64, 64), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Disable(EnableCap.Dither);
            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            int framebuffer = GL.GenFramebuffer();
            int texture = GL.GenTexture();
            try
            {
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 64, 64, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
                GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, texture, 0);
                GL.Viewport(8, 4, 48, 48);
                GL.ClearColor(.25f, 0, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.Enable(EnableCap.ScissorTest);
                GL.Scissor(32, 0, 32, 64);
                GL.ClearColor(.75f, 0, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.Disable(EnableCap.ScissorTest);

                using var renderer = new ImageDecorationRenderer();
                var camera = new Camera2D { ViewportWidth = 48, ViewportHeight = 48, Zoom = 1 };
                var timeline = new ImageDecorationTimeline();
                timeline.Images.Add("solid", new(1, 1, [255, 255, 255, 255]));
                var pose = new ImageDecorationTimeline.Pose("solid", Vector2.Zero, Vector2.Zero,
                    new(32), Vector2.One, Vector2.Zero, Vector2.Zero, Vector4.One,
                    0, 1, -1, true, false, "Camera") { Blend = DecorationBlendMode.Difference };
                timeline.Items.Add(new(new AddImageDecoration(), pose, Vector2.One, 0));
                renderer.Prepare(timeline, 0, camera, 100, 1, static (_, _) => default, static _ => default);
                renderer.Draw(timeline, background: false, camera);

                Assert.That(GL.GetInteger(GetPName.ReadFramebufferBinding), Is.Zero,
                    "Grabbing the scene must leave the caller's read framebuffer bound.");
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
                Assert.Multiple(() =>
                {
                    Assert.That(Pixel(28, 28), Is.EqualTo(191).Within(1), "Sample the left side of the draw target.");
                    Assert.That(Pixel(36, 28), Is.EqualTo(64).Within(1), "Sample the right side of the draw target.");
                    Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                });
            }
            finally
            {
                GL.DeleteFramebuffer(framebuffer);
                GL.DeleteTexture(texture);
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    private static byte Pixel(int x, int y)
    {
        byte[] rgba = new byte[4];
        GL.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        return rgba[0];
    }
}
