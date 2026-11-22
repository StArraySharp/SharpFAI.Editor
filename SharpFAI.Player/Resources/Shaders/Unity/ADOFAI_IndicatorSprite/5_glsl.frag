#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform  sampler2D _MainTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec3 u_xlat0;
vec4 u_xlat1;
bool u_xlatb6;
void main()
{
    u_xlat0.xyz = (-vs_COLOR0.xyz) + vec3(1.0, 1.0, 1.0);
    u_xlat1 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat0.xyz = u_xlat1.xxx * u_xlat0.xyz + vs_COLOR0.xyz;
    u_xlatb6 = u_xlat1.w==1.0;
    u_xlat1.xyz = (bool(u_xlatb6)) ? u_xlat0.xyz : u_xlat1.xyz;
    SV_Target0 = u_xlat1 * vs_COLOR0.wwww;
    return;
}
