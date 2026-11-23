using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using Thrust.Scene;

namespace SharpFAI.Player.Tests;

[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class TestDecorationMaskRendering
{
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
                    index * 7, .3f, -1, true, index % 2 == 0, "Camera")
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
