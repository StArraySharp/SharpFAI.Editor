using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Thrust.Graphics;
using Thrust.Scene;

namespace SharpFAI.Player.Engine;

internal sealed class BackgroundRenderer : IDisposable
{
    private readonly int _program = SpriteRenderer.LinkProgram("Background.vert", "Background.frag");
    private readonly int _vao = GL.GenVertexArray();
    private int _tutorialShapes;
    private int? _tutorialTile;
    private int _image;
    private Vector2 _imageSize;
    private int _white;
    private readonly Dictionary<(byte[] Pixels, bool Smoothing, bool Repeat), int> _textures = [];

    public void SetImage(ImageDecoder.RgbaImage? image, bool smoothing, bool repeat = false)
    {
        if (image is not { } pixels)
        {
            _image = 0;
            return;
        }

        _imageSize = new Vector2(pixels.Width, pixels.Height);
        var key = (pixels.Pixels, smoothing, repeat);
        if (_textures.TryGetValue(key, out _image))
            return;
        _image = Upload(pixels, smoothing, repeat);
        _textures.Add(key, _image);
    }

    public void PrepareTutorial(LevelPlaybackSettings settings)
    {
        if (settings.ShowPatternTiles)
            _tutorialTile ??= LoadTutorialTile();
        if (settings.PatternShape != BackgroundShape.Disabled && _tutorialShapes == 0)
            _tutorialShapes = SpriteRenderer.LinkProgram("TutorialShapes.vert", "TutorialShapes.frag");
    }

    public int Draw(LevelPlaybackSettings settings, Camera2D camera, Vector2 origin, ImageDecorationRenderer? sprites)
    {
        Vector2 viewport = new(camera.ViewportWidth, camera.ViewportHeight);
        Vector2 size = settings.Display == BackgroundDisplay.FitToScreen
            ? viewport * settings.BackgroundScale : _imageSize * camera.Zoom * settings.BackgroundScale;
        Vector2 movement = (camera.Position - origin) * (Vector2.One - settings.Parallax) * camera.Zoom;
        float angle = _image != 0 && settings.LockBackgroundRotation ? 0 : MathHelper.DegreesToRadians(camera.Rotation);
        bool repeat = settings.LoopBackground || settings.Display == BackgroundDisplay.Tiled;

        if (_image != 0 && sprites is not null)
        {
            DrawImage(sprites, settings.ImageTint, viewport, size, movement, angle, repeat);
            return 1;
        }

        int draws = 0;
        if (_image != 0 || settings.ShowPatternTiles)
        {
            DrawBackdrop(settings, viewport, size, movement, angle, repeat);
            draws++;
        }
        if (_image == 0 && settings.PatternShape != BackgroundShape.Disabled)
        {
            DrawTutorialShapes(settings, viewport, camera.Position, angle);
            draws++;
        }
        return draws;
    }

    private void DrawBackdrop(LevelPlaybackSettings settings, Vector2 viewport, Vector2 size, Vector2 movement,
        float angle, bool repeat)
    {
        int texture = _image != 0 ? _image : _tutorialTile ??= LoadTutorialTile();
        GL.UseProgram(_program);
        GL.Uniform2(GL.GetUniformLocation(_program, "uViewport"), viewport);
        GL.Uniform2(GL.GetUniformLocation(_program, "uSize"), size);
        GL.Uniform2(GL.GetUniformLocation(_program, "uMovement"), movement);
        GL.Uniform4(GL.GetUniformLocation(_program, "uTint"), settings.ImageTint);
        GL.Uniform4(GL.GetUniformLocation(_program, "uPattern"), settings.PatternColor);
        GL.Uniform1(GL.GetUniformLocation(_program, "uAngle"), angle);
        GL.Uniform1(GL.GetUniformLocation(_program, "uHasImage"), _image != 0 ? 1 : 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "uHasTutorialTile"), _image == 0 && texture != 0 ? 1 : 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "uRepeat"), repeat ? 1 : 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.Uniform1(GL.GetUniformLocation(_program, "uImage"), 0);
        DrawTriangles(3);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void DrawTutorialShapes(LevelPlaybackSettings settings, Vector2 viewport, Vector2 position, float angle)
    {
        if (_tutorialShapes == 0)
            PrepareTutorial(settings);
        GL.UseProgram(_tutorialShapes);
        // BGMovingCam uses a fixed orthographic size of 5. Scene coordinates are
        // 1/100 of SharpFAI world coordinates and the shapes follow half the camera delta.
        GL.Uniform2(GL.GetUniformLocation(_tutorialShapes, "uProjectionScale"), new Vector2(viewport.Y / viewport.X, 1f) / 5f);
        GL.Uniform2(GL.GetUniformLocation(_tutorialShapes, "uParallaxOffset"), position / 200f);
        GL.Uniform1(GL.GetUniformLocation(_tutorialShapes, "uAngle"), angle);
        GL.Uniform1(GL.GetUniformLocation(_tutorialShapes, "uSingleColor"), settings.PatternShape == BackgroundShape.SingleColor ? 1 : 0);
        GL.Uniform3(GL.GetUniformLocation(_tutorialShapes, "uShapeColor"), settings.PatternAccent.Xyz);
        DrawTriangles(8 * 6 + 3);
    }

    private void DrawTriangles(int count)
    {
        GL.Enable(EnableCap.Blend);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha,
            BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, count);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(0);
    }

    private static int LoadTutorialTile()
    {
        if (EmbeddedAssets.Read("Resources.Filters.tutorial_tile.png") is not { } bytes)
            return 0;
        var image = ImageDecoder.Decode(bytes);
        ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
        return Upload(image, smoothing: false, repeat: true);
    }

    private static int Upload(ImageDecoder.RgbaImage image, bool smoothing, bool repeat)
    {
        int texture = ImageDecoder.Upload(image, generateMipmaps: smoothing, repeat: repeat);
        if (!smoothing)
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
        return texture;
    }

    private void DrawImage(ImageDecorationRenderer sprites, Vector4 tint, Vector2 viewport,
        Vector2 size, Vector2 movement, float angle, bool repeat)
    {
        size = Vector2.ComponentMax(size, new Vector2(.001f));
        Vector2 repetitions = Vector2.One;
        if (repeat)
        {
            float diagonal = viewport.Length;
            // Odd counts keep the centre of the original tile aligned with the centre of the quad.
            repetitions = new Vector2(2f * MathF.Ceiling(diagonal / (2f * size.X)) + 3f,
                2f * MathF.Ceiling(diagonal / (2f * size.Y)) + 3f);
            movement = new Vector2(movement.X % size.X, movement.Y % size.Y);
        }
        var model = Matrix4.CreateScale(size.X * repetitions.X, size.Y * repetitions.Y, 1f) *
            Matrix4.CreateTranslation(-movement.X, -movement.Y, 0f) * Matrix4.CreateRotationZ(-angle) *
            Matrix4.CreateScale(2f / viewport.X, 2f / viewport.Y, 1f);
        sprites.DrawLayer(_image, model, tint, repetitions);
    }

    public void Flash(ImageDecorationRenderer sprites, Vector4 tint)
    {
        if (_white == 0)
            _white = ImageDecoder.Upload(new byte[] { 255, 255, 255, 255 }, 1, 1, generateMipmaps: false);
        sprites.DrawLayer(_white, Matrix4.CreateScale(2f, 2f, 1f), tint, Vector2.One);
    }

    public void Dispose()
    {
        foreach (int texture in _textures.Values)
            GL.DeleteTexture(texture);
        if (_white != 0)
            GL.DeleteTexture(_white);
        if (_tutorialTile is > 0)
            GL.DeleteTexture(_tutorialTile.Value);
        if (_tutorialShapes != 0)
            GL.DeleteProgram(_tutorialShapes);
        GL.DeleteVertexArray(_vao);
        GL.DeleteProgram(_program);
    }
}
