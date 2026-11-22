#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _Grayscale;
uniform  sampler2D _MainTex;
uniform  sampler2D _AlphaTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
vec4 u_xlat2;
void main()
{
    u_xlat0 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat1 = texture(_AlphaTex, vs_TEXCOORD0.xy);
    u_xlat0.w = u_xlat1.x;
    u_xlat0 = u_xlat0 * vs_COLOR0;
    u_xlat1.xyz = u_xlat0.www * u_xlat0.xyz;
    u_xlat2.x = dot(u_xlat1.xzy, vec3(0.300000012, 0.300000012, 0.400000006));
    u_xlat2.xyz = (-u_xlat0.xyz) * u_xlat0.www + u_xlat2.xxx;
    u_xlat1.w = u_xlat0.w;
    u_xlat2.w = 0.0;
    SV_Target0 = vec4(_Grayscale) * u_xlat2 + u_xlat1;
    return;
}
