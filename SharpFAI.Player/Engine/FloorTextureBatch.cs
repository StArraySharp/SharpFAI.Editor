using System.Collections.Concurrent;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

internal enum FloorTextureFilter
{
    Trilinear,
    BilinearMipmapped,
    Bilinear,
    Point,
}

internal enum FloorTextureWrap
{
    Clamp,
    Repeat,
    Mirror,
}

internal readonly record struct FloorTexture(
    string Resource, FloorTextureWrap Wrap = FloorTextureWrap.Clamp, bool FlipVertically = true,
    bool External = false, FloorTextureFilter Filter = FloorTextureFilter.Trilinear, bool Optional = false);

internal sealed record FloorTextureBatch(int IndexStart, int IndexCount, FloorTexture[] Textures);

/// <summary>Assigns texture slots without changing the incoming primitive order.</summary>
internal sealed class FloorTextureBatchBuilder(int capacity)
{
    private readonly Dictionary<FloorTexture, int> _slots = [];
    private readonly List<FloorTexture> _textures = [];
    private FloorTexture[] _previousTextures = [];
    private int[] _previousSlots = [];
    private bool _hasPreviousFloor;
    private int _indexStart;

    public List<FloorTextureBatch> Batches { get; } = [];

    public void AddFloor(ReadOnlySpan<FloorTexture> textures, Span<int> slots, int indexStart)
    {
        if (_hasPreviousFloor && textures.SequenceEqual(_previousTextures))
        {
            _previousSlots.AsSpan().CopyTo(slots);
            return;
        }

        int needed = 0;
        for (int i = 0; i < textures.Length; i++)
        {
            if (textures[i] != default && !_slots.ContainsKey(textures[i]) && !textures[..i].Contains(textures[i]))
            {
                needed++;
            }
        }

        if (_textures.Count + needed > capacity)
        {
            Finish(indexStart);
        }

        for (int i = 0; i < textures.Length; i++)
        {
            FloorTexture texture = textures[i];
            if (texture == default)
            {
                slots[i] = -1;
                continue;
            }

            if (!_slots.TryGetValue(texture, out int slot))
            {
                slot = _textures.Count;
                if (slot >= capacity)
                {
                    throw new InvalidOperationException("The floor material exceeds the fragment texture slots.");
                }

                _textures.Add(texture);
                _slots.Add(texture, slot);
            }

            slots[i] = slot;
        }

        if (_previousTextures.Length != textures.Length)
        {
            _previousTextures = new FloorTexture[textures.Length];
            _previousSlots = new int[textures.Length];
        }
        textures.CopyTo(_previousTextures);
        slots[..textures.Length].CopyTo(_previousSlots);
        _hasPreviousFloor = true;
    }

    public void Finish(int end)
    {
        if (end > _indexStart)
        {
            Batches.Add(new FloorTextureBatch(_indexStart, end - _indexStart, _textures.ToArray()));
        }

        _indexStart = end;
        _slots.Clear();
        _textures.Clear();
        _hasPreviousFloor = false;
    }
}

internal static class FloorIconTextures
{
    private const string ResourcePrefix = "Resources.PlayerIcons.";

    public static FloorTexture Resolve(Floor floor, FloorMaterial state)
    {
        if (state.HideIcon)
        {
            return default;
        }

        string? icon = (state.Icon ?? floor.icon) switch
        {
            FloorIcon.Swirl => floor.swirlRed ? ResourcePrefix + "swirl_red.png" : ResourcePrefix + "swirl_blue.png",
            FloorIcon.Rabbit => ResourcePrefix + "icon_rabbit.png",
            FloorIcon.DoubleRabbit => ResourcePrefix + "icon_doublerabbit.png",
            FloorIcon.Snail => ResourcePrefix + "icon_snail.png",
            FloorIcon.DoubleSnail => ResourcePrefix + "icon_doublesnail.png",
            FloorIcon.SameSpeed => ResourcePrefix + "tile_samespeed.png",
            FloorIcon.HoldArrowShort => ResourcePrefix + "tile_hold_arrowshort.png",
            FloorIcon.HoldArrowLong => ResourcePrefix + "tile_hold_arrowlong.png",
            FloorIcon.Checkpoint => ResourcePrefix + "tile_diamond_gray.png",
            FloorIcon.MultiPlanetTwo => ResourcePrefix + "tile_multiplanet_two.png",
            FloorIcon.MultiPlanetThreeMore => ResourcePrefix + "tile_multiplanet_three_more.png",
            FloorIcon.MultiPlanetThreeLess => ResourcePrefix + "tile_multiplanet_three_less.png",
            FloorIcon.Portal => ResourcePrefix + "portal.png",
            _ => null,
        };

        return icon is null ? default : new FloorTexture(icon);
    }

    public static (FloorTexture Icon, FloorTexture Outline)? ResolveSpeedIcon(FloorIcon icon, bool gray,
        float bpm, double time, bool editor)
    {
        string? prefix = icon switch
        {
            FloorIcon.Rabbit => "tile_rabbit_light_",
            FloorIcon.DoubleRabbit => "tile_rabbit_double_light_",
            FloorIcon.Snail => "tile_snail_light_",
            FloorIcon.DoubleSnail => "tile_snail_double_light_",
            _ => null,
        };
        if (prefix is null || (editor && !gray))
            return null;
        prefix = ResourcePrefix + prefix;
        if (gray)
            return (new FloorTexture(prefix + "gray.png"), default);
        bool alternate = bpm > 0 && Math.Floor(Math.Max(0d, time) * bpm / 60d) % 2 == 0;
        if (!alternate)
            return (new FloorTexture(prefix + "new0.png"), new FloorTexture(prefix + "new0_outline.png"));
        string outline = icon == FloorIcon.Snail ? "new1_outlines.png" : "new1_outline.png";
        return (new FloorTexture(prefix + "new1.png"), new FloorTexture(prefix + outline));
    }

    public static (FloorTexture Icon, FloorTexture Outline) HoldRelease(bool longHold) =>
        (new FloorTexture(ResourcePrefix + (longHold ? "tile_hold_release_long.png" : "tile_hold_release_short.png")),
            new FloorTexture(ResourcePrefix + "outline_hold_release.png"));

    public static FloorTexture ResolveOutline(FloorTexture icon, FloorIcon? kind, string? levelDirectory,
        ConcurrentDictionary<string, FloorTexture> cache)
    {
        string? key = kind switch
        {
            FloorIcon.Checkpoint => "checkpoint",
            FloorIcon.Vfx => "vfx",
            _ => icon.Resource,
        };
        if (key is null)
        {
            return default;
        }

        if (!cache.TryGetValue(key, out FloorTexture outline))
        {
            string name = kind is FloorIcon.Checkpoint or FloorIcon.Vfx ? key : key[ResourcePrefix.Length..^4];
            string fileName = name + "_outline.png";
            string? path = levelDirectory is null ? null : Path.Combine(levelDirectory, "IconOutlines", fileName);
            if (!File.Exists(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "Resources", "PlayerIcons", fileName);
            }

            if (File.Exists(path))
            {
                outline = new FloorTexture(path!, External: true, Optional: true);
            }
            else
            {
                string? bundled = kind switch
                {
                    FloorIcon.Swirl => "swirl_outline.png",
                    FloorIcon.Rabbit => "tile_rabbit_light_new0_outline.png",
                    FloorIcon.DoubleRabbit => "tile_rabbit_double_light_new0_outline.png",
                    FloorIcon.Snail => "tile_snail_light_new0_outline.png",
                    FloorIcon.DoubleSnail => "tile_snail_double_light_new0_outline.png",
                    FloorIcon.SameSpeed => "outline_samespeed.png",
                    FloorIcon.HoldArrowShort => "outline_hold_arrowshort.png",
                    FloorIcon.HoldArrowLong => "outline_hold_arrowlong.png",
                    FloorIcon.Checkpoint => "outline_diamond.png",
                    FloorIcon.MultiPlanetTwo => "outline_multiplanet_two.png",
                    FloorIcon.MultiPlanetThreeMore or FloorIcon.MultiPlanetThreeLess => "outline_multiplanet_three.png",
                    _ => null,
                };
                outline = bundled is null ? default : new FloorTexture(ResourcePrefix + bundled);
            }
            cache.TryAdd(key, outline);
        }

        return outline;
    }
}
