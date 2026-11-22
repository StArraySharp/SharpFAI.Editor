#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 _BaseTint;
uniform 	vec4 _BlurTint;
uniform  sampler2D _MainTex;
uniform  sampler2D _BaseTex;
uniform  sampler2D _TileTex;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
vec4 u_xlat2;
vec4 u_xlat3;
void main()
{
    u_xlat0 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat0.x = (-u_xlat0.w) + 0.920000017;
    u_xlat0.x = u_xlat0.x * 1.63043475;
    u_xlat0.x = min(u_xlat0.x, 0.550000012);
    u_xlat1 = texture(_BaseTex, vs_TEXCOORD0.xy);
    u_xlat2.w = u_xlat0.x + (-u_xlat1.w);
    u_xlat3 = texture(_TileTex, vs_TEXCOORD0.xy);
    u_xlat1.xyz = u_xlat3.xyz * _BaseTint.xyz;
    u_xlat2.xyz = (-_BaseTint.xyz) * u_xlat3.xyz + _BlurTint.xyz;
    u_xlat0 = u_xlat0.xxxx * u_xlat2 + u_xlat1;
    SV_Target0.w = u_xlat1.w * u_xlat0.w;
    SV_Target0.xyz = u_xlat0.xyz;
    return;
}
