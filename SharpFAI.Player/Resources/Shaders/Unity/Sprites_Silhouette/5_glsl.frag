#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _EnableExternalAlpha;
uniform  sampler2D _MainTex;
uniform  sampler2D _AlphaTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
void main()
{
    u_xlat0 = texture(_AlphaTex, vs_TEXCOORD0.xy);
    u_xlat1 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat0.x = u_xlat0.x + (-u_xlat1.w);
    u_xlat0.x = _EnableExternalAlpha * u_xlat0.x + u_xlat1.w;
    u_xlat0.x = u_xlat0.x * vs_COLOR0.w;
    SV_Target0.xyz = u_xlat0.xxx * vs_COLOR0.xyz;
    SV_Target0.w = u_xlat0.x;
    return;
}
