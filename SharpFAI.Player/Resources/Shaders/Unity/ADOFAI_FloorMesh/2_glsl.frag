#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

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

layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
bool u_xlatb0;
vec4 u_xlat1;
vec4 u_xlat2;
vec4 u_xlat3;
vec4 u_xlat4;
float u_xlat6;
float u_xlat7;
vec2 u_xlat8;
vec2 u_xlat12;
bool u_xlatb12;
vec2 u_xlat13;
bvec2 u_xlatb13;
float u_xlat18;
bool u_xlatb18;
float u_xlat19;
float u_xlat20;
void main()
{
    u_xlat0.xy = (int(vs_STYLEPARAMS.y) != 0) ? vec2(0.5, 0.5) : _TileTex_ST.zw;
    u_xlat1.xyz = (int(vs_STYLEPARAMS.y) != 0) ? vec3(0.0, 0.0, 0.0) : vs_TILEOFFSET;
    u_xlat12.x = (vs_STYLEPARAMS.y != 0) ? 1.0 : vs_ICONDATA.w;
    u_xlat18 = u_xlat1.z * 3.14152908;
    u_xlat2.x = sin(u_xlat18);
    u_xlat3.x = cos(u_xlat18);
    u_xlat13.xy = u_xlat2.xx * vs_TEXCOORD1.yx;
    u_xlat2.x = vs_TEXCOORD1.x * u_xlat3.x + (-u_xlat13.x);
    u_xlat2.y = vs_TEXCOORD1.y * u_xlat3.x + u_xlat13.y;
    u_xlat0.xy = u_xlat0.xy + u_xlat1.xy;
    u_xlat12.xy = u_xlat2.xy / u_xlat12.xx;
    u_xlat1.xy = u_xlat0.xy + u_xlat12.xy;
    int tileSlot = vs_TEXTURESLOTS.z;
    u_xlat1.z = u_xlat1.y * (tileSlot >= 0 ? uTextureRatio[tileSlot] : 1.0);
    vec2 edgeUv = vs_TEXCOORD0.xy;
    vec2 tileUv = u_xlat1.xz;
    vec2 perlinUv = u_xlat0.xy + vs_TEXCOORD1.xy / _PerlinScale;
    u_xlat2.x = sin(vs_ICONDATA.x);
    u_xlat3.x = cos(vs_ICONDATA.x);
    u_xlat13.xy = u_xlat2.xx * vs_TEXCOORD1.yx;
    u_xlat8.x = vs_TEXCOORD1.x * u_xlat3.x + (-u_xlat13.x);
    u_xlat8.y = vs_TEXCOORD1.y * u_xlat3.x + u_xlat13.y;
    u_xlat13.xy = u_xlat8.xy / vec2(vec2(vs_ICONDATA.y, vs_ICONDATA.y));
    u_xlat2.yz = u_xlat13.xy + vec2(0.5, 0.5);
    u_xlat13.x = (-u_xlat2.y) + 1.0;
    u_xlat20 = vs_ICONDATA.z;
    u_xlat13.x = (-u_xlat2.y) + u_xlat13.x;
    u_xlat2.x = u_xlat20 * u_xlat13.x + u_xlat2.y;
    u_xlat13.x = u_xlat2.x + -0.5;
    u_xlatb13.xy = lessThan(vec4(0.5, 0.5, 0.5, 0.5), abs(u_xlat13.xyxy)).xy;
    u_xlatb13.x = u_xlatb13.y || u_xlatb13.x;
    vec2 iconUv = u_xlat2.xz;
    vec2 edgeDx = dFdx(edgeUv);
    vec2 edgeDy = dFdy(edgeUv);
    vec2 tileDx = dFdx(tileUv);
    vec2 tileDy = dFdy(tileUv);
    vec2 perlinDx = dFdx(perlinUv);
    vec2 perlinDy = dFdy(perlinUv);
    vec2 iconDx = dFdx(iconUv);
    vec2 iconDy = dFdy(iconUv);
    u_xlatb0 = 1.0<vs_TEXCOORD0.y;
    if(u_xlatb0){
        u_xlat0.x = (-vs_TEXCOORD0.y) + 2.0;
        u_xlat0.x = log2(u_xlat0.x);
        u_xlat0.x = u_xlat0.x * 1.29999995;
        u_xlat0.x = exp2(u_xlat0.x);
        u_xlat1.xyz = vs_SHADOWCOLOR.xyz;
        u_xlat1.w = 1.0;
        u_xlat0 = u_xlat0.xxxx * u_xlat1;
        u_xlat0 = u_xlat0 * vs_SHADOWCOLOR.wwww;
        u_xlat0 = u_xlat0 * vec4(vs_OPACITY);
        u_xlat1.x = float(_Active);
        u_xlat0 = u_xlat0 * u_xlat1.xxxx;
        SV_Target0 = u_xlat0 * vs_COLOR0.wwww;
        return;
    }
    u_xlatb12 = _Inset<vs_TEXCOORD0.y;
    u_xlatb18 = _AbsoluteInset!=0;
    u_xlatb12 = u_xlatb18 || u_xlatb12;
    if(u_xlatb12){
        u_xlat2 = sampleFloorTexture(vs_TEXTURESLOTS.x, edgeUv, edgeDx, edgeDy, vec4(1.0));
        u_xlat2 = u_xlat2 * _Tint;
    } else {
        u_xlat2.x = float(1.0);
        u_xlat2.y = float(1.0);
        u_xlat2.z = float(1.0);
        u_xlat2.w = float(1.0);
    }
    u_xlat3.xyz = u_xlat2.xyz * vec3(vec3(vs_STYLEPARAMS.x, vs_STYLEPARAMS.x, vs_STYLEPARAMS.x));
    u_xlat0 = sampleFloorTexture(vs_TEXTURESLOTS.y, perlinUv, perlinDx, perlinDy, vec4(0.0));
    u_xlat0.x = u_xlat0.x * _PerlinStrength;
    u_xlat6 = u_xlat0.x * u_xlat0.x;
    u_xlat0.xyz = vec3(u_xlat6) * u_xlat0.xxx + u_xlat3.xyz;
    u_xlat2.xyz = min(u_xlat0.xyz, vec3(1.0, 1.0, 1.0));
    u_xlat0 = sampleFloorTexture(vs_TEXTURESLOTS.z, tileUv, tileDx, tileDy, vec4(1.0));
    u_xlat1 = u_xlat0 * vs_COLOR0;
    u_xlat0.x = (-u_xlat0.w) * vs_COLOR0.w + 1.0;
    u_xlat0.w = _CommentColor.w * u_xlat0.x + u_xlat1.w;
    u_xlat19 = (-_CommentColor.w) + 1.0;
    u_xlat1.xyz = vec3(u_xlat19) * u_xlat1.xyz;
    u_xlat0.xyz = _CommentColor.xyz * _CommentColor.www + u_xlat1.xyz;
    u_xlat0 = u_xlat2 * u_xlat0;
    u_xlat1.x = float(_Active);
    u_xlat7 = u_xlat0.w * u_xlat1.x;
    u_xlat4 = vec4(0.0);
    bool outlineIcon = uForceIconOutline || vs_STYLEPARAMS.z > 0.5;
    bool hasOutlineTexture = vs_OUTLINESLOT >= 0 && uTextureRatio[vs_OUTLINESLOT] > 0.0;
    if (!u_xlatb13.x && vs_TEXTURESLOTS.w >= 0)
    {
        u_xlat4 = sampleFloorTexture(vs_TEXTURESLOTS.w, iconUv, iconDx, iconDy, vec4(0.0)) * _IconColor;
    }
    else if (vs_STYLEPARAMS.w > 0.5)
    {
        vec2 marker = abs(iconUv - vec2(0.5));
        u_xlat4 = vec4(0.98, 0.94, 0.74, 1.0 - smoothstep(0.15, 0.18, length(marker)));
    }
    if (outlineIcon && hasOutlineTexture && !u_xlatb13.x && u_xlat4.a < 1.0)
    {
        vec4 outline = sampleFloorTexture(vs_OUTLINESLOT, iconUv, iconDx, iconDy, vec4(0.0));
        u_xlat4 = addIconOutline(u_xlat4, outline);
    }

    u_xlat19 = u_xlat4.w;
    u_xlat2.xyz = u_xlat4.xyz;
    u_xlat18 = (-u_xlat0.w) * u_xlat1.x + 1.0;
    u_xlat18 = u_xlat19 * u_xlat18 + u_xlat7;
    u_xlat1.x = (-u_xlat19) + 1.0;
    u_xlat0.xyz = u_xlat0.xyz * u_xlat1.xxx;
    u_xlat0.xyz = u_xlat2.xyz * vec3(u_xlat19) + u_xlat0.xyz;
    u_xlat1.x = vs_COLOR0.w * vs_OPACITY;
    u_xlat18 = u_xlat18 * u_xlat1.x;
    u_xlat1.xyz = (-u_xlat0.xyz) + vec3(1.0, 1.0, 1.0);
    u_xlat0.xyz = vec3(_Flash) * u_xlat1.xyz + u_xlat0.xyz;
    float glow = clamp(vs_GLOW, 0.0, 4.0) * smoothstep(0.65, 1.0, edgeUv.y) * 0.12;
    u_xlat0.rgb += glow * vs_COLOR0.rgb;
    SV_Target0.xyz = vec3(u_xlat18) * u_xlat0.xyz;
    SV_Target0.w = u_xlat18;
    return;
}
