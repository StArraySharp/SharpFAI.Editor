#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform  sampler2D _MainTex;
uniform  sampler2D _BubbleTex;
uniform  sampler2D _BackgroundTex;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
float u_xlat6;
void main()
{
    u_xlat0 = texture(_BackgroundTex, vs_TEXCOORD0.xy);
    u_xlat1 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat6 = (-u_xlat1.w) + 1.0;
    u_xlat0.xyz = vec3(u_xlat6) * u_xlat0.xyz;
    SV_Target0.xyz = u_xlat1.xyz * u_xlat1.www + u_xlat0.xyz;
    u_xlat0 = texture(_BubbleTex, vs_TEXCOORD0.xy);
    SV_Target0.w = u_xlat0.w;
    return;
}
