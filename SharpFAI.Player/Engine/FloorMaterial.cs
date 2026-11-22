using OpenTK.Mathematics;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

internal readonly record struct FloorMaterial(
    TrackStyleInfo Style,
    string? TextureName,
    float TextureScale,
    Vector4? ShadowColor,
    bool IconOutline,
    bool HideIcon,
    FloorIcon? Icon,
    bool ReverseIcon,
    Vector2 Dimensions,
    float LengthRatio);
