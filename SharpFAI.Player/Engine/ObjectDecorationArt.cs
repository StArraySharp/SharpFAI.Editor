using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal static class ObjectDecorationArt
{
    internal const string PlanetKey = "\0object-planet";
    internal const string FloorKey = "\0object-floor";
    internal const string FloorGlowKey = "\0object-floor-glow";
    private const int PlanetFrames = 11;
    private const int PlanetFps = 12;
    private static readonly Lazy<ImageDecoder.RgbaImage[]> Planet = new(LoadPlanet);
    private static readonly Lazy<ImageDecoder.RgbaImage> FloorGlow = new(() => Load("Resources.PlayerIcons.light_white.png"));
    internal static readonly ImageDecoder.RgbaImage Particle = new(1, 1, [255, 255, 255, 255]);

    private static string PlanetFrameKey(int frame) => frame == 0 ? PlanetKey : $"{PlanetKey}-{frame}";

    internal static string PlanetImageAt(double time) =>
        PlanetFrameKey((int)(Math.Max(time, 0) * PlanetFps % PlanetFrames));

    internal static (string Key, Vector4 Tint, float Opacity) Resolve(AddObjectDecoration source,
        Dictionary<string, DecorationImage> images)
    {
        string kind = source.ObjectType ?? "Floor";
        string key = kind switch
        {
            "Planet" => PlanetKey,
            "PlayerBubble" => "\0object-bubble",
            _ => FloorKey,
        };
        if (!images.ContainsKey(key))
        {
            if (kind == "Planet")
            {
                var frames = Planet.Value;
                for (int i = 0; i < PlanetFrames; i++)
                    images.Add(PlanetFrameKey(i), frames[i]);
            }
            else
                images.Add(key, kind == "PlayerBubble" ? Bubble() : Particle);
        }
        if (key == FloorKey && !images.ContainsKey(FloorGlowKey))
            images.Add(FloorGlowKey, FloorGlow.Value);
        Vector4 tint = kind switch
        {
            "Planet" => source.PlanetColorType switch
            {
                "Custom" => FloorTrackStateResolver.ParseColor(source.PlanetColor) ?? Vector4.One,
                "DefaultBlue" => new(0f, 0f, 1f, 1f),
                "Gold" => new(1f, .78f, .23f, 1f),
                "Overseer" => new(.62f, .88f, 1f, 1f),
                _ => new(1f, 0f, 0f, 1f),
            },
            "PlayerBubble" => Vector4.One,
            _ => ObjectFloorState.ReadTint(source),
        };
        return (key, tint, key == FloorKey ? ObjectFloorState.ReadOpacity(source) : 1f);
    }

    private static ImageDecoder.RgbaImage Bubble() => Raster(100, (x, y) =>
    {
        float radius = MathF.Sqrt(x * x + y * y);
        return (1f, .65f * (1f - Math.Clamp(MathF.Abs(radius - .81f) / .052f, 0f, 1f)));
    });

    private static ImageDecoder.RgbaImage[] LoadPlanet()
    {
        var atlas = Load("Resources.planet.png");
        if (atlas.Height % PlanetFrames != 0)
            throw new InvalidDataException("The planet atlas does not contain 11 equal rows.");
        int frameHeight = atlas.Height / PlanetFrames;
        int frameBytes = atlas.Width * frameHeight * 4;
        var frames = new ImageDecoder.RgbaImage[PlanetFrames];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = new(atlas.Width, frameHeight, atlas.Pixels.AsSpan(i * frameBytes, frameBytes).ToArray());
        return frames;
    }

    private static ImageDecoder.RgbaImage Raster(int size, Func<float, float, (float Value, float Alpha)> pixel)
    {
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var (value, alpha) = pixel((x + .5f) * 2 / size - 1, (y + .5f) * 2 / size - 1);
            int offset = (y * size + x) * 4;
            rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = (byte)(value * 255);
            rgba[offset + 3] = (byte)(alpha * 255);
        }
        return new(size, size, rgba);
    }

    private static ImageDecoder.RgbaImage Load(string resource)
    {
        var image = ImageDecoder.Decode(EmbeddedAssets.Read(resource) ?? throw new InvalidOperationException(resource));
        ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
        return image;
    }
}
