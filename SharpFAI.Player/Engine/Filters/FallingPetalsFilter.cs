using OpenTK.Mathematics;
using Thrust.Scene;
using Parameter = System.Numerics.Vector4;

namespace SharpFAI.Player.Engine.Filters;

internal sealed class FallingPetalsFilter(IFilterBackend backend) : IGeneratedFilter
{
    private readonly record struct Emitter(string Texture, int Rate, int Frames, bool RandomStartFrame);

    private static readonly Emitter[] Emitters =
    [
        new("Rotating_Petals_1", 1, 8, false),
        new("Rotating_Petals_2", 3, 6, false),
        new("Rotating_Petals_3", 2, 7, true),
    ];

    private const float SimulationSpeed = .3f;
    private const int MaxLifetime = 60;
    private readonly SpriteRenderer.Particle[] _particles = new SpriteRenderer.Particle[MaxLifetime * 3];
    private readonly int[] _textures = new int[Emitters.Length];
    private double _start;
    private float _prewarm;

    public IReadOnlyList<string> Parameters => [];
    public FilterParameterKind ParameterKind(string field) => FilterParameterKind.Float;
    public Parameter? GetParameter(string field) => null;
    public void SetParameter(string field, Parameter value) { }
    public void Enable() { }
    public void Dispose() { }
    public void Render(IFilterTarget source, IFilterTarget destination) => backend.Blit(source, destination);

    internal void Restart(double time, float prewarm)
    {
        _start = time;
        _prewarm = prewarm;
    }

    internal int Draw(SpriteRenderer sprites, Camera2D camera, double time, double pitch)
    {
        if (backend is not OpenGlFilterBackend gl)
            return 0;
        var projection = Matrix4.CreateRotationZ(-MathHelper.DegreesToRadians(camera.Rotation)) *
            Matrix4.CreateOrthographic(24f * camera.ViewportWidth / camera.ViewportHeight, 24f, -1f, 1f);
        float simulatedTime = (_prewarm + (float)Math.Max(0, (time - _start) / pitch)) * SimulationSpeed;
        int draws = 0;
        for (int index = 0; index < Emitters.Length; index++)
        {
            var emitter = Emitters[index];
            if (_textures[index] == 0)
            {
                int texture = gl.TextureHandle(emitter.Texture);
                _textures[index] = texture == 0 ? -1 : texture;
            }
            if (_textures[index] < 0)
                continue;
            int count = SampleEmitter(index, emitter, simulatedTime);
            if (count == 0)
                continue;
            sprites.DrawParticles(projection, _particles.AsSpan(0, count), _textures[index], emitter.Frames, straightAlpha: true);
            draws++;
        }
        return draws;
    }

    private int SampleEmitter(int index, Emitter emitter, float time)
    {
        const float edgeAngle = 147.9f * MathF.PI / 180f;
        Vector2 edge = new(MathF.Cos(edgeAngle), MathF.Sin(edgeAngle));
        Vector2 origin = new(-5.4f, 22.1f);
        int end = (int)(time * emitter.Rate);
        int start = Math.Max(1, end - MaxLifetime * emitter.Rate + 1);
        int count = 0;
        for (int id = start; id <= end; id++)
        {
            uint seed = (uint)(id * Emitters.Length + index);
            float age = time - (float)id / emitter.Rate;
            float lifetime = 30f * (1f + RandomUnit(ref seed));
            if (age >= lifetime)
                continue;
            Vector2 center = origin + edge * (60f * RandomUnit(ref seed) - 30f);
            float windPhase = MathF.Tau * RandomUnit(ref seed);
            center.X += (MathF.Sin(age * .6f + windPhase) - MathF.Sin(windPhase)) * 1.25f;
            center.Y -= .4905f * age * age;
            float halfSize = .25f + .75f * RandomUnit(ref seed);
            float startFrame = emitter.RandomStartFrame ? 5f * RandomUnit(ref seed) : 0f;
            int frame = (int)(age / lifetime * 60f * emitter.Frames + startFrame) % emitter.Frames;
            _particles[count++] = new SpriteRenderer.Particle
            {
                Center = center,
                HalfSize = new Vector2(halfSize, halfSize * (27f / 35f)),
                Tint = Vector4.One,
                Rotation = MathF.Tau * RandomUnit(ref seed),
                Frame = frame,
            };
        }
        return count;
    }

    private static float RandomUnit(ref uint seed)
    {
        seed = unchecked(seed * 747796405u + 2891336453u);
        return (seed >> 8) * (1f / 16777216f);
    }
}
