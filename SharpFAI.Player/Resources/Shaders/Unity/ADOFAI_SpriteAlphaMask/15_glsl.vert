#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#ifdef GL_ARB_shader_bit_encoding
#extension GL_ARB_shader_bit_encoding : enable
#endif

uniform 	vec4 uModel[4];
uniform 	vec4 u_WorldToObject[4];
uniform 	vec4 uViewProjection[4];
uniform 	vec4 u_WorldToLight[4];
uniform 	vec4 _MainTex_ST;
uniform 	vec4 _AlphaTex_ST;
layout(location = 0) in vec4 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aTexCoord;
out vec4 vs_TEXCOORD0;
out vec3 vs_TEXCOORD1;
out vec3 vs_TEXCOORD2;
out vec3 vs_TEXCOORD3;
out vec4 vs_TEXCOORD4;
vec4 u_xlat0;
vec4 u_xlat1;
vec4 u_xlat2;
float u_xlat10;
void main()
{
    u_xlat0 = aPosition.yyyy * uModel[1];
    u_xlat0 = uModel[0] * aPosition.xxxx + u_xlat0;
    u_xlat0 = uModel[2] * aPosition.zzzz + u_xlat0;
    u_xlat1 = u_xlat0 + uModel[3];
    u_xlat2 = u_xlat1.yyyy * uViewProjection[1];
    u_xlat2 = uViewProjection[0] * u_xlat1.xxxx + u_xlat2;
    u_xlat2 = uViewProjection[2] * u_xlat1.zzzz + u_xlat2;
    gl_Position = uViewProjection[3] * u_xlat1.wwww + u_xlat2;
    vs_TEXCOORD0.xy = aTexCoord.xy * _MainTex_ST.xy + _MainTex_ST.zw;
    vs_TEXCOORD0.zw = aTexCoord.xy * _AlphaTex_ST.xy + _AlphaTex_ST.zw;
    u_xlat1.x = dot(aNormal.xyz, u_WorldToObject[0].xyz);
    u_xlat1.y = dot(aNormal.xyz, u_WorldToObject[1].xyz);
    u_xlat1.z = dot(aNormal.xyz, u_WorldToObject[2].xyz);
    u_xlat10 = dot(u_xlat1.xyz, u_xlat1.xyz);
    u_xlat10 = inversesqrt(u_xlat10);
    vs_TEXCOORD1.xyz = vec3(u_xlat10) * u_xlat1.xyz;
    vs_TEXCOORD2.xyz = uModel[3].xyz * aPosition.www + u_xlat0.xyz;
    u_xlat0 = uModel[3] * aPosition.wwww + u_xlat0;
    u_xlat1.xyz = u_xlat0.yyy * u_WorldToLight[1].xyz;
    u_xlat1.xyz = u_WorldToLight[0].xyz * u_xlat0.xxx + u_xlat1.xyz;
    u_xlat0.xyz = u_WorldToLight[2].xyz * u_xlat0.zzz + u_xlat1.xyz;
    vs_TEXCOORD3.xyz = u_WorldToLight[3].xyz * u_xlat0.www + u_xlat0.xyz;
    vs_TEXCOORD4 = vec4(0.0, 0.0, 0.0, 0.0);
    return;
}
