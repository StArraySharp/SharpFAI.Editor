using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal static class ObjectDecorationArt
{
    internal const string PlanetKey = "\0object-planet";
    private const int PlanetFrames = 11;
    private const int PlanetFps = 12;
    private static readonly Lazy<ImageDecoder.RgbaImage[]> Planet = new(LoadPlanet);
    private static readonly Lazy<ImageDecoder.RgbaImage> StandardFloorTexture = new(() => Load(TrackStyleInfo.Standard.TileResolved!));
    internal static readonly ImageDecoder.RgbaImage Particle = new(1, 1, [255, 255, 255, 255]);

    private static string PlanetFrameKey(int frame) => frame == 0 ? PlanetKey : $"{PlanetKey}-{frame}";

    internal static string PlanetImageAt(double time) =>
        PlanetFrameKey((int)(Math.Max(time, 0) * PlanetFps % PlanetFrames));

    internal static (string Key, Vector4 Tint, float Opacity) Resolve(AddObjectDecoration source,
        Dictionary<string, DecorationImage> images)
    {
        string kind = source.ObjectType ?? "Floor";
        float turn = source.TrackAngle is { } angle && float.IsFinite(angle) ? angle % 360f : 180f;
        bool midspin = string.Equals(source.TrackType, "Midspin", StringComparison.OrdinalIgnoreCase);
        var style = TrackStyleInfo.Parse(source.TrackStyle);
        string key = kind switch
        {
            "Planet" => PlanetKey,
            "PlayerBubble" => "\0object-bubble",
            _ => FormattableString.Invariant($"\0object-floor-{style.Index}-{(midspin ? 0 : turn)}-{midspin}"),
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
                images.Add(key, kind == "PlayerBubble" ? Bubble() : Floor(turn, midspin, style));
        }
        Vector4 tint = kind switch
        {
            "Planet" => source.PlanetColorType switch
            {
                "Custom" => FloorTrackStateResolver.ParseColor(source.PlanetColor) ?? Vector4.One,
                "DefaultBlue" => new(.18f, .68f, 1f, 1f),
                "Gold" => new(1f, .78f, .23f, 1f),
                "Overseer" => new(.62f, .88f, 1f, 1f),
                _ => new(1f, .24f, .24f, 1f),
            },
            "PlayerBubble" => Vector4.One,
            _ => FloorTrackStateResolver.ParseColor(source.TrackColor) ?? new Vector4(.82f, .68f, .54f, 1f),
        };
        return (key, tint, kind == "Floor" ? Math.Clamp((source.TrackOpacity ?? 100) * .01f, 0, 1) : 1f);
    }

    private static ImageDecoder.RgbaImage Floor(float turn, bool midspin, TrackStyleInfo style)
    {
        var mesh = new FloorMesh(-180, midspin ? -180 : 180 - turn, style.TileWidth, style.TileLength,
            midspin ? 3 : 40, style.Sprite).Generate();
        var outline = mesh.Collider;
        if (outline.Length < 3)
            return new(1, 1, [0, 0, 0, 0]);

        // Keep the mesh pivot centered even when the polygon extends farther in one direction.
        int size = (int)MathF.Ceiling(outline.Max(p => MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y))) + 1f) * 2;
        var texture = style == TrackStyleInfo.Standard ? StandardFloorTexture.Value : default;
        return Raster(size, (x, y) =>
        {
            var point = new System.Numerics.Vector2(x * size * .5f, y * size * .5f);
            bool inside = false;
            float distanceSquared = float.PositiveInfinity;
            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                var a = outline[j];
                var b = outline[i];
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < a.X + (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y))
                    inside = !inside;
                var segment = b - a;
                float fraction = Math.Clamp(System.Numerics.Vector2.Dot(point - a, segment) /
                    Math.Max(segment.LengthSquared(), 1e-6f), 0f, 1f);
                distanceSquared = Math.Min(distanceSquared, System.Numerics.Vector2.DistanceSquared(point, a + fraction * segment));
            }
            float distance = MathF.Sqrt(distanceSquared) * (inside ? 1f : -1f);
            float alpha = Math.Clamp(distance + .5f, 0f, 1f);
            float edge = .66f + .34f * Math.Clamp(distance / 2.5f, 0f, 1f);
            float detail = texture.Width == 0 ? 1f : SampleFloorTexture(texture, point.X, point.Y, style.TileScale);
            return (edge * detail, alpha);
        });
    }

    private static float SampleFloorTexture(ImageDecoder.RgbaImage texture, float x, float y, float scale)
    {
        float u = .37f + x / (100f * scale);
        float v = .58f + y / (100f * scale);
        int tx = (int)((u - MathF.Floor(u)) * texture.Width);
        int ty = (int)((v - MathF.Floor(v)) * texture.Height);
        return texture.Pixels[(ty * texture.Width + tx) * 4] / 255f;
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
