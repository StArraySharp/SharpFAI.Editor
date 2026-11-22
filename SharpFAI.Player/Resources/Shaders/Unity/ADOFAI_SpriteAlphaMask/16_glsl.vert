#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 uModel[4];
uniform 	vec4 u_WorldToObject[4];
uniform 	vec4 uViewProjection[4];
uniform 	vec4 _MainTex_ST;
uniform 	vec4 _AlphaTex_ST;
layout(location = 0) in vec4 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aTexCoord;
out vec4 vs_TEXCOORD0;
out vec3 vs_TEXCOORD1;
out vec3 vs_TEXCOORD2;
out vec4 vs_TEXCOORD4;
vec4 u_xlat0;
vec4 u_xlat1;
float u_xlat6;
void main()
{
    u_xlat0 = aPosition.yyyy * uModel[1];
    u_xlat0 = uModel[0] * aPosition.xxxx + u_xlat0;
    u_xlat0 = uModel[2] * aPosition.zzzz + u_xlat0;
    u_xlat1 = u_xlat0 + uModel[3];
    vs_TEXCOORD2.xyz = uModel[3].xyz * aPosition.www + u_xlat0.xyz;
    u_xlat0 = u_xlat1.yyyy * uViewProjection[1];
    u_xlat0 = uViewProjection[0] * u_xlat1.xxxx + u_xlat0;
    u_xlat0 = uViewProjection[2] * u_xlat1.zzzz + u_xlat0;
    gl_Position = uViewProjection[3] * u_xlat1.wwww + u_xlat0;
    vs_TEXCOORD0.xy = aTexCoord.xy * _MainTex_ST.xy + _MainTex_ST.zw;
    vs_TEXCOORD0.zw = aTexCoord.xy * _AlphaTex_ST.xy + _AlphaTex_ST.zw;
    u_xlat0.x = dot(aNormal.xyz, u_WorldToObject[0].xyz);
    u_xlat0.y = dot(aNormal.xyz, u_WorldToObject[1].xyz);
    u_xlat0.z = dot(aNormal.xyz, u_WorldToObject[2].xyz);
    u_xlat6 = dot(u_xlat0.xyz, u_xlat0.xyz);
    u_xlat6 = inversesqrt(u_xlat6);
    vs_TEXCOORD1.xyz = vec3(u_xlat6) * u_xlat0.xyz;
    vs_TEXCOORD4 = vec4(0.0, 0.0, 0.0, 0.0);
    return;
}
