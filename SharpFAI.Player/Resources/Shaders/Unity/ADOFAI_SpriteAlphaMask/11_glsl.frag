#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _ARot;
uniform 	float _VisibleOutsideMask;
uniform  sampler2D _MainTex;
uniform  sampler2D _AlphaTex;
in  vec4 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
vec3 u_xlat2;
vec2 u_xlat3;
bool u_xlatb6;
void main()
{
    u_xlat0.x = _ARot * 0.0174532924;
    u_xlat1.x = cos(u_xlat0.x);
    u_xlat0.x = sin(u_xlat0.x);
    u_xlat2.x = (-u_xlat0.x);
    u_xlat3.xy = vs_TEXCOORD0.zw + vec2(-0.5, -0.5);
    u_xlat2.y = u_xlat1.x;
    u_xlat2.z = u_xlat0.x;
    u_xlat1.y = dot(u_xlat2.zy, u_xlat3.xy);
    u_xlat1.x = dot(u_xlat2.yx, u_xlat3.xy);
    u_xlat0.xy = u_xlat1.xy + vec2(0.5, 0.5);
    u_xlat0 = texture(_AlphaTex, u_xlat0.xy);
    u_xlat0.x = (-u_xlat0.w) + 1.0;
    u_xlat1 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat0.xy = u_xlat0.xw * u_xlat1.ww;
    SV_Target0.xyz = u_xlat1.xyz;
    u_xlatb6 = 0.0<_VisibleOutsideMask;
    SV_Target0.w = (u_xlatb6) ? u_xlat0.x : u_xlat0.y;
    return;
}
