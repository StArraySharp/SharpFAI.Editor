using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

/// <summary>
/// The parameters that make up a track style.
/// </summary>
/// <remarks>
/// The only values that branch on <c>TrackStyle</c> are the border texture, the tile/perlin
/// textures, the tile scale, the shadow tune and colour, and the tile dimensions.
/// </remarks>
public sealed record TrackStyleInfo(
    string Name,
    string EdgeTextureSuffix,
    string? TileTextureSuffix,
    string? PerlinTextureSuffix,
    float TileScale,
    float ShadowTune,
    Vector4 ShadowColor,
    float TileWidth,
    float TileLength,
    bool Tinted,
    int Index,
    bool Sprite = false)
{
    /// <summary>
    /// The long tile dimensions: 0.75 by 0.4125. The Standard style uses these directly.
    /// </summary>
    private const float LongWidth = 0.4125f;
    private const float LongLength = 0.75f;

    /// <summary>
    /// Inset compensation applied by every non-Standard style, making neighbouring tiles overlap
    /// slightly and produce the dark seam between them.
    /// </summary>
    private const float InsetLength = 0.03f;
    private const float InsetWidth = 0.0375f;

    /// <summary>Resource name of the border strip.</summary>
    public string EdgeResolved => "Resources.Floor." + EdgeTextureSuffix;

    /// <summary>Tile texture resource, null meaning 1x1 white.</summary>
    public string? TileResolved => TileTextureSuffix is null ? null : "Resources.Floor." + TileTextureSuffix;

    /// <summary>Perlin resource, null meaning 1x1 black.</summary>
    public string? PerlinResolved => PerlinTextureSuffix is null ? null : "Resources.Floor." + PerlinTextureSuffix;

    /// <summary>summary>
    public static readonly TrackStyleInfo Standard = new(
        "Standard",
        "Edge.png",
        "Tile.png",
        "Perlin.png",
        TileScale: 10f,
        ShadowTune: 1.23f,
        ShadowColor: new Vector4(0f, 0f, 0f, 0.45f),
        TileWidth: LongWidth,
        TileLength: LongLength,
        Tinted: true,
        Index: 0);

    /// <summary>summary>
    public static readonly TrackStyleInfo Neon = new(
        "Neon",
        "EdgeNeon.png",
        TileTextureSuffix: null,
        PerlinTextureSuffix: null,
        TileScale: 10f,
        ShadowTune: 1f,
        ShadowColor: new Vector4(1f, 1f, 1f, 0.35f),
        TileWidth: LongWidth + InsetWidth,
        TileLength: LongLength + InsetLength,
        Tinted: true,
        Index: 1);

    /// <summary>summary>
    public static readonly TrackStyleInfo NeonLight = new(
        "NeonLight",
        "EdgeNeonLight.png",
        TileTextureSuffix: null,
        PerlinTextureSuffix: null,
        TileScale: 10f,
        ShadowTune: 1f,
        ShadowColor: new Vector4(1f, 1f, 1f, 0.35f),
        TileWidth: LongWidth + InsetWidth,
        TileLength: LongLength + InsetLength,
        Tinted: true,
        Index: 2);

    /// <summary>summary>
    public static readonly TrackStyleInfo Basic = new(
        "Basic",
        "EdgeBasic.png",
        TileTextureSuffix: null,
        PerlinTextureSuffix: null,
        TileScale: 10f,
        ShadowTune: 1f,
        ShadowColor: Vector4.Zero,
        TileWidth: LongWidth + InsetWidth,
        TileLength: LongLength + InsetLength,
        Tinted: true,
        Index: 3);

    /// <summary>
    /// Gems is the odd one out: the tile becomes a unit quad (<c>_Sprite = 1</c>) carrying the gem
    /// texture, while the border texture and shadow colour stay on the Standard branch. The game's
    /// <c>SetTrackStyle</c> only overrides <c>_Sprite</c> and the tile texture for this style, so the
    /// geometry is the same square a sprite floor uses and the style draws in Standard colours.
    /// </summary>
    public static readonly TrackStyleInfo Gems = new(
        "Gems",
        "Edge.png",
        "Gem.png",
        PerlinTextureSuffix: null,
        TileScale: 10f,
        ShadowTune: 1.23f,
        ShadowColor: new Vector4(0f, 0f, 0f, 0.45f),
        TileWidth: LongWidth,
        TileLength: LongLength,
        Tinted: true,
        Index: 4,
        Sprite: true);

    /// <summary>summary>
    public static readonly TrackStyleInfo Minimal = new(
        "Minimal",
        "EdgeMinimal.png",
        TileTextureSuffix: null,
        PerlinTextureSuffix: null,
        TileScale: 10f,
        ShadowTune: 1f,
        ShadowColor: Vector4.Zero,
        TileWidth: LongWidth + InsetWidth,
        TileLength: LongLength + InsetLength,
        Tinted: true,
        Index: 5);

    /// <summary>All known styles, in menu order. The order matches the game's TrackStyle enum.</summary>
    public static readonly TrackStyleInfo[] All = [Standard, Neon, NeonLight, Basic, Gems, Minimal];

    /// <summary>
    /// Resolve a style by the level's <c>trackStyle</c> text, falling back to Standard.
    /// </summary>
    public static TrackStyleInfo Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Standard;
        }

        foreach (TrackStyleInfo style in All)
        {
            if (string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return style;
            }
        }

        return Standard;
    }

    /// <summary>Resolve a style from the game's <c>TrackStyle</c> enum value.</summary>
    public static TrackStyleInfo FromIndex(int index)
    {
        foreach (TrackStyleInfo style in All)
        {
            if (style.Index == index)
            {
                return style;
            }
        }

        return Standard;
    }

    /// <summary>Whether the tile size matches.</summary>
    public bool SameGeometry(TrackStyleInfo other)
        => MathF.Abs(TileWidth - other.TileWidth) < 1e-4f
           && MathF.Abs(TileLength - other.TileLength) < 1e-4f;
}
