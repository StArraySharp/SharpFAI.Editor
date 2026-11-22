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
vec3 u_xlat2;
bool u_xlatb10;
void main()
{
    u_xlat0 = texture(_AlphaTex, vs_TEXCOORD0.xy);
    u_xlat1 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat0.x = u_xlat0.x + (-u_xlat1.w);
    u_xlat0.w = _EnableExternalAlpha * u_xlat0.x + u_xlat1.w;
    u_xlatb10 = u_xlat0.w==1.0;
    u_xlat2.xyz = (-vs_COLOR0.xyz) + vec3(1.0, 1.0, 1.0);
    u_xlat2.xyz = u_xlat1.xxx * u_xlat2.xyz + vs_COLOR0.xyz;
    u_xlat0.xyz = (bool(u_xlatb10)) ? u_xlat2.xyz : u_xlat1.xyz;
    SV_Target0 = u_xlat0 * vs_COLOR0.wwww;
    return;
}
