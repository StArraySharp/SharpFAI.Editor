#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 u_Time;
uniform 	vec4 _FillColor;
uniform 	float _Completion;
uniform 	float _AngleLength;
uniform  sampler2D _MainTex;
in  vec4 vs_COLOR0;
in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;
vec4 u_xlat0;
vec4 u_xlat1;
vec4 u_xlat2;
bvec3 u_xlatb2;
float u_xlat5;
void main()
{
    u_xlat0.y = u_Time.x * -8.0 + vs_TEXCOORD0.y;
    u_xlat0.x = vs_TEXCOORD0.x;
    u_xlat0 = texture(_MainTex, u_xlat0.xy);
    u_xlat0 = u_xlat0.xwyz * vs_COLOR0.xwyz;
    u_xlat1.xyz = u_xlat0.yyy * u_xlat0.xzw;
    u_xlat0.x = dot(u_xlat1.xyz, vec3(0.298999995, 0.587000012, 0.114));
    u_xlat2 = u_xlat0.xxxy * _FillColor;
    u_xlat1.w = u_xlat0.y;
    u_xlat0 = u_xlat2 * vs_COLOR0;
    u_xlat2.x = vs_TEXCOORD0.y / _AngleLength;
    u_xlat5 = _Completion * 0.159154952;
    u_xlatb2.z = u_xlat2.x>=u_xlat5;
    u_xlatb2.x = u_xlat5>=u_xlat2.x;
    u_xlat2.x = u_xlatb2.x ? float(1.0) : 0.0;
    u_xlat2.y = u_xlatb2.z ? float(1.0) : 0.0;
;
    u_xlat1 = u_xlat1 * u_xlat2.yyyy;
    SV_Target0 = u_xlat0 * u_xlat2.xxxx + u_xlat1;
    return;
}
