using OpenTK.Graphics.OpenGL4;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Editor.Widgets;

internal sealed class EventPaletteIcons : IDisposable
{
    private const string Prefix = "Resources.LevelEditor.LevelEvents.";
    private static readonly Dictionary<string, string> Resources = typeof(EmbeddedAssets).Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal))
        .ToDictionary(name => name[Prefix.Length..^4], StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon?> _textures = new(StringComparer.OrdinalIgnoreCase);

    internal readonly record struct Icon(int Texture, int Width, int Height);

    internal Icon? Get(string eventType)
    {
        if (!Resources.TryGetValue(eventType, out string? resource))
            return null;
        if (_textures.TryGetValue(eventType, out Icon? cached))
            return cached;

        Icon? result = null;
        if (EmbeddedAssets.Read(resource) is { } bytes)
        {
            int previous = GL.GetInteger(GetPName.TextureBinding2D);
            try
            {
                var image = ImageDecoder.Decode(bytes);
                int texture = ImageDecoder.Upload(image, generateMipmaps: false, repeat: false);
                if (texture != 0)
                    result = new Icon(texture, image.Width, image.Height);
            }
            finally { GL.BindTexture(TextureTarget.Texture2D, previous); }
        }
        _textures[eventType] = result;
        return result;
    }

    public void Dispose()
    {
        foreach (Icon? icon in _textures.Values)
            if (icon is { } loaded)
                GL.DeleteTexture(loaded.Texture);
        _textures.Clear();
    }
}
