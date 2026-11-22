#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 u_Time;
uniform 	vec4 _Glow;
uniform 	float _FadeBottom;
uniform 	float _CropBottom;
uniform 	float _FadeTop;
uniform 	float _CropTop;
uniform 	float _FadeRight;
uniform 	float _CropRight;
uniform 	float _FadeLeft;
uniform 	float _CropLeft;
uniform 	vec2 _ScrollSpeed;
uniform 	vec4 _TexOffset;
uniform  sampler2D _MainTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
vec2 u_xlat2;
vec2 u_xlat4;
void main()
{
    u_xlat0.x = (-_CropTop) + 1.0;
    u_xlat0.x = u_xlat0.x + (-vs_TEXCOORD0.y);
    u_xlat2.xy = max(vec2(_FadeBottom, _FadeTop), vec2(9.99999975e-05, 9.99999975e-05));
    u_xlat2.xy = vec2(1.0, 1.0) / u_xlat2.xy;
    u_xlat0.x = u_xlat2.y * u_xlat0.x;
    u_xlat0.x = clamp(u_xlat0.x, 0.0, 1.0);
    u_xlat4.x = vs_TEXCOORD0.y + (-_CropBottom);
    u_xlat2.x = u_xlat2.x * u_xlat4.x;
    u_xlat2.x = clamp(u_xlat2.x, 0.0, 1.0);
    u_xlat0.x = u_xlat0.x * u_xlat2.x;
    u_xlat2.x = vs_TEXCOORD0.x + (-_CropLeft);
    u_xlat4.xy = max(vec2(_FadeLeft, _FadeRight), vec2(9.99999975e-05, 9.99999975e-05));
    u_xlat4.xy = vec2(1.0, 1.0) / u_xlat4.xy;
    u_xlat2.x = u_xlat4.x * u_xlat2.x;
    u_xlat2.x = clamp(u_xlat2.x, 0.0, 1.0);
    u_xlat0.x = u_xlat2.x * u_xlat0.x;
    u_xlat2.x = (-_CropRight) + 1.0;
    u_xlat2.x = u_xlat2.x + (-vs_TEXCOORD0.x);
    u_xlat2.x = u_xlat4.y * u_xlat2.x;
    u_xlat2.x = clamp(u_xlat2.x, 0.0, 1.0);
    u_xlat0.x = u_xlat2.x * u_xlat0.x;
    u_xlat2.xy = u_Time.xx * _ScrollSpeed.xy;
    u_xlat2.xy = fract(u_xlat2.xy);
    u_xlat2.xy = u_xlat2.xy + _TexOffset.xy;
    u_xlat2.xy = vs_TEXCOORD0.xy * _TexOffset.zw + u_xlat2.xy;
    u_xlat1 = texture(_MainTex, u_xlat2.xy);
    u_xlat1.w = u_xlat0.x * u_xlat1.w;
    u_xlat0 = u_xlat1 * vs_COLOR0;
    u_xlat1.xyz = (-u_xlat1.xyz) * vs_COLOR0.xyz + _Glow.xyz;
    u_xlat0.xyz = _Glow.www * u_xlat1.xyz + u_xlat0.xyz;
    SV_Target0.xyz = u_xlat0.www * u_xlat0.xyz;
    SV_Target0.w = u_xlat0.w;
    return;
}
