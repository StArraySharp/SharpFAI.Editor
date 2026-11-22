#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform  sampler2D _MainTex;
uniform  sampler2D _MaskTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
void main()
{
    u_xlat0 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat0 = u_xlat0 * vs_COLOR0;
    u_xlat0.xyz = u_xlat0.www * u_xlat0.xyz;
    u_xlat1 = texture(_MaskTex, vs_TEXCOORD0.xy);
    SV_Target0 = u_xlat0 * u_xlat1.wwww;
    return;
}
