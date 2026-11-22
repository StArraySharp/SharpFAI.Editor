#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _Frame;
uniform 	float _Frames;
uniform  sampler2D _PlanetTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
float u_xlat1;
bool u_xlatb1;
void main()
{
    u_xlat0.x = floor(_Frame);
    u_xlat0.x = u_xlat0.x + vs_TEXCOORD0.x;
    u_xlat0.x = u_xlat0.x / _Frames;
    u_xlatb1 = u_xlat0.x>=(-u_xlat0.x);
    u_xlat1 = (u_xlatb1) ? 1.0 : -1.0;
    u_xlat0.x = u_xlat1 * u_xlat0.x;
    u_xlat0.x = fract(u_xlat0.x);
    u_xlat0.x = u_xlat0.x * u_xlat1;
    u_xlat0.y = vs_TEXCOORD0.y;
    u_xlat0 = texture(_PlanetTex, u_xlat0.xy);
    u_xlat0 = u_xlat0 * vs_COLOR0;
    SV_Target0.xyz = u_xlat0.www * u_xlat0.xyz;
    SV_Target0.w = u_xlat0.w;
    return;
}
