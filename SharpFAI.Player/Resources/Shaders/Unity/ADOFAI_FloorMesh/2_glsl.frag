#version 330 core

uniform vec4 _TileTex_ST;
uniform vec4 _Tint;
uniform vec4 _IconColor;
uniform vec4 _CommentColor;
uniform float _Flash;
uniform int _Active;
uniform float _Inset;
uniform int _AbsoluteInset;
uniform float _PerlinStrength;
uniform float _PerlinScale;

#ifndef FLOOR_TEXTURE_SLOTS
#define FLOOR_TEXTURE_SLOTS 16
#endif
uniform sampler2D uTextures[FLOOR_TEXTURE_SLOTS];
uniform float uTextureRatio[FLOOR_TEXTURE_SLOTS];
uniform bool uForceIconOutline;
uniform int uDecorationMaskMode;

in vec4 vs_COLOR0;
in vec2 vs_TEXCOORD0;
in vec2 vs_TEXCOORD1;
flat in vec4 vs_ICONDATA;
flat in vec4 vs_STYLEPARAMS;
flat in vec4 vs_SHADOWCOLOR;
flat in ivec4 vs_TEXTURESLOTS;
flat in vec3 vs_TILEOFFSET;
flat in int vs_OUTLINESLOT;
flat in float vs_OPACITY;
flat in float vs_GLOW;

// GLSL 3.30 requires constant sampler array indices. Slot selection may vary per primitive.
vec4 sampleFloorTexture(int slot, vec2 uv, vec2 dx, vec2 dy, vec4 fallback)
{
    switch (slot)
    {
        case 0: return textureGrad(uTextures[0], uv, dx, dy);
        case 1: return textureGrad(uTextures[1], uv, dx, dy);
        case 2: return textureGrad(uTextures[2], uv, dx, dy);
        case 3: return textureGrad(uTextures[3], uv, dx, dy);
        case 4: return textureGrad(uTextures[4], uv, dx, dy);
        case 5: return textureGrad(uTextures[5], uv, dx, dy);
        case 6: return textureGrad(uTextures[6], uv, dx, dy);
        case 7: return textureGrad(uTextures[7], uv, dx, dy);
        case 8: return textureGrad(uTextures[8], uv, dx, dy);
        case 9: return textureGrad(uTextures[9], uv, dx, dy);
        case 10: return textureGrad(uTextures[10], uv, dx, dy);
        case 11: return textureGrad(uTextures[11], uv, dx, dy);
        case 12: return textureGrad(uTextures[12], uv, dx, dy);
        case 13: return textureGrad(uTextures[13], uv, dx, dy);
        case 14: return textureGrad(uTextures[14], uv, dx, dy);
        case 15: return textureGrad(uTextures[15], uv, dx, dy);
#if FLOOR_TEXTURE_SLOTS > 16
        case 16: return textureGrad(uTextures[16], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 17
        case 17: return textureGrad(uTextures[17], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 18
        case 18: return textureGrad(uTextures[18], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 19
        case 19: return textureGrad(uTextures[19], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 20
        case 20: return textureGrad(uTextures[20], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 21
        case 21: return textureGrad(uTextures[21], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 22
        case 22: return textureGrad(uTextures[22], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 23
        case 23: return textureGrad(uTextures[23], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 24
        case 24: return textureGrad(uTextures[24], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 25
        case 25: return textureGrad(uTextures[25], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 26
        case 26: return textureGrad(uTextures[26], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 27
        case 27: return textureGrad(uTextures[27], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 28
        case 28: return textureGrad(uTextures[28], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 29
        case 29: return textureGrad(uTextures[29], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 30
        case 30: return textureGrad(uTextures[30], uv, dx, dy);
#endif
#if FLOOR_TEXTURE_SLOTS > 31
        case 31: return textureGrad(uTextures[31], uv, dx, dy);
#endif
        default: return fallback;
    }
}

vec4 addIconOutline(vec4 icon, vec4 outline)
{
    float behindIcon = outline.a * (1.0 - icon.a);
    float alpha = icon.a + behindIcon;
    vec3 premultiplied = icon.rgb * icon.a + outline.rgb * behindIcon;
    return vec4(premultiplied / max(alpha, 0.00001), alpha);
}

layout(location = 0) out vec4 outColor;

vec2 rotateUv(vec2 uv, float angle)
{
    float s = sin(angle);
    float c = cos(angle);
    return mat2(c, s, -s, c) * uv;
}

void main()
{
    bool sprite = vs_STYLEPARAMS.y != 0.0;
    vec2 localUv = vs_TEXCOORD1;
    vec2 edgeUv = vs_TEXCOORD0;
    vec3 tileTransform = sprite ? vec3(0.0) : vs_TILEOFFSET;
    vec2 tileOrigin = (sprite ? vec2(0.5) : _TileTex_ST.zw) + tileTransform.xy;
    float tileScale = sprite ? 1.0 : vs_ICONDATA.w;
    const float tileRotationScale = 3.14152908;
    vec2 tileUv = tileOrigin + rotateUv(localUv, tileTransform.z * tileRotationScale) / tileScale;
    int tileSlot = vs_TEXTURESLOTS.z;
    tileUv.y *= tileSlot >= 0 ? uTextureRatio[tileSlot] : 1.0;
    vec2 perlinUv = tileOrigin + localUv / _PerlinScale;

    vec2 iconLocalUv = rotateUv(localUv, vs_ICONDATA.x) / vs_ICONDATA.y;
    vec2 iconUv = iconLocalUv + vec2(0.5);
    iconUv.x += vs_ICONDATA.z * ((1.0 - iconUv.x) - iconUv.x);
    bool iconInBounds = abs(iconUv.x - 0.5) <= 0.5 && abs(iconLocalUv.y) <= 0.5;

    // Compute derivatives before any branches that can diverge within a fragment quad.
    vec2 edgeDx = dFdx(edgeUv);
    vec2 edgeDy = dFdy(edgeUv);
    vec2 tileDx = dFdx(tileUv);
    vec2 tileDy = dFdy(tileUv);
    vec2 perlinDx = dFdx(perlinUv);
    vec2 perlinDy = dFdy(perlinUv);
    vec2 iconDx = dFdx(iconUv);
    vec2 iconDy = dFdy(iconUv);

    float opacity = vs_COLOR0.a * vs_OPACITY;
    if (uDecorationMaskMode >= 3)
    {
        if (edgeUv.y > 1.0)
            discard;
        outColor = vec4(opacity);
        return;
    }
    if (uDecorationMaskMode != 0)
    {
        const int maskSlot = FLOOR_TEXTURE_SLOTS - 1;
        vec2 dimensions = vec2(textureSize(uTextures[maskSlot], 0));
        float mask = texture(uTextures[maskSlot], gl_FragCoord.xy / dimensions).a;
        opacity *= uDecorationMaskMode == 1 ? mask : 1.0 - mask;
    }
    if (edgeUv.y > 1.0)
    {
        float falloff = pow(2.0 - edgeUv.y, 1.3);
        float shadowAlpha = falloff * vs_SHADOWCOLOR.a * float(_Active) * opacity;
        outColor = vec4(vs_SHADOWCOLOR.rgb * shadowAlpha, shadowAlpha);
        return;
    }

    vec4 edge = vec4(1.0);
    if (_AbsoluteInset != 0 || edgeUv.y > _Inset)
    {
        edge = sampleFloorTexture(vs_TEXTURESLOTS.x, edgeUv, edgeDx, edgeDy, vec4(1.0)) * _Tint;
    }
    float noise = sampleFloorTexture(vs_TEXTURESLOTS.y, perlinUv, perlinDx, perlinDy, vec4(0.0)).r;
    noise *= _PerlinStrength;
    edge.rgb = min(edge.rgb * vs_STYLEPARAMS.x + vec3(noise * noise * noise), vec3(1.0));

    vec4 tile = sampleFloorTexture(tileSlot, tileUv, tileDx, tileDy, vec4(1.0)) * vs_COLOR0;
    tile.rgb = mix(tile.rgb, _CommentColor.rgb, _CommentColor.a);
    tile.a += _CommentColor.a * (1.0 - tile.a);
    tile *= edge;
    float tileAlpha = tile.a * float(_Active);

    vec4 icon = vec4(0.0);
    if (iconInBounds && vs_TEXTURESLOTS.w >= 0)
    {
        icon = sampleFloorTexture(vs_TEXTURESLOTS.w, iconUv, iconDx, iconDy, vec4(0.0)) * _IconColor;
    }
    else if (vs_STYLEPARAMS.w > 0.5)
    {
        float radius = length(iconUv - vec2(0.5));
        icon = vec4(0.98, 0.94, 0.74, 1.0 - smoothstep(0.15, 0.18, radius));
    }
    bool outlineIcon = uForceIconOutline || vs_STYLEPARAMS.z > 0.5;
    bool hasOutlineTexture = vs_OUTLINESLOT >= 0 && uTextureRatio[vs_OUTLINESLOT] > 0.0;
    if (outlineIcon && hasOutlineTexture && iconInBounds && icon.a < 1.0)
    {
        vec4 outline = sampleFloorTexture(vs_OUTLINESLOT, iconUv, iconDx, iconDy, vec4(0.0));
        icon = addIconOutline(icon, outline);
    }

    float alpha = (tileAlpha + icon.a * (1.0 - tileAlpha)) * opacity;
    vec3 color = mix(tile.rgb, icon.rgb, icon.a);
    color += _Flash * (vec3(1.0) - color);
    float glow = clamp(vs_GLOW, 0.0, 4.0) * smoothstep(0.65, 1.0, edgeUv.y) * 0.12;
    color += glow * vs_COLOR0.rgb;
    outColor = vec4(color * alpha, alpha);
}
