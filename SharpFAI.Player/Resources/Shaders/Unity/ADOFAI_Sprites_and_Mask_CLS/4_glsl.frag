#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	float _Grayscale;
uniform 	float _Ratio;
uniform  sampler2D _MainTex;
uniform  sampler2D _AlphaTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
bool u_xlatb0;
vec4 u_xlat1;
vec4 u_xlat2;
vec2 u_xlat3;
void main()
{
    u_xlatb0 = 1.0<_Ratio;
    u_xlat3.xy = vs_TEXCOORD0.xy + vec2(-0.5, -0.5);
    u_xlat3.x = u_xlat3.x / _Ratio;
    u_xlat1.w = u_xlat3.y * _Ratio + 0.5;
    u_xlat1.x = u_xlat3.x + 0.5;
    u_xlat1.yz = vs_TEXCOORD0.yx;
    u_xlat0.xy = (bool(u_xlatb0)) ? u_xlat1.xy : u_xlat1.zw;
    u_xlat0 = texture(_MainTex, u_xlat0.xy);
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
