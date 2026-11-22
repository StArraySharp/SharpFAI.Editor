#version 330 core
#extension GL_ARB_explicit_attrib_location : require

uniform 	vec4 u_ProjectionParams;
uniform 	vec4 uModel[4];
uniform 	vec4 u_WorldToObject[4];
uniform 	vec4 uViewProjection[4];
uniform 	vec4 _Texture1_ST;
uniform 	vec4 _Texture2_ST;
uniform 	vec4 _Texture3_ST;
uniform 	vec4 _Texture4_ST;
uniform 	vec4 _Texture5_ST;
layout(location = 0) in vec4 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aTexCoord;
out vec4 vs_TEXCOORD0;
out vec4 vs_TEXCOORD1;
out vec2 vs_TEXCOORD2;
out vec3 vs_TEXCOORD3;
out vec3 vs_TEXCOORD4;
out vec4 vs_TEXCOORD7;
out vec4 vs_TEXCOORD8;
vec4 u_xlat0;
vec4 u_xlat1;
float u_xlat7;
void main()
{
    u_xlat0 = aPosition.yyyy * uModel[1];
    u_xlat0 = uModel[0] * aPosition.xxxx + u_xlat0;
    u_xlat0 = uModel[2] * aPosition.zzzz + u_xlat0;
    u_xlat1 = u_xlat0 + uModel[3];
    vs_TEXCOORD4.xyz = uModel[3].xyz * aPosition.www + u_xlat0.xyz;
    u_xlat0 = u_xlat1.yyyy * uViewProjection[1];
    u_xlat0 = uViewProjection[0] * u_xlat1.xxxx + u_xlat0;
    u_xlat0 = uViewProjection[2] * u_xlat1.zzzz + u_xlat0;
    u_xlat0 = uViewProjection[3] * u_xlat1.wwww + u_xlat0;
    gl_Position = u_xlat0;
    vs_TEXCOORD0.xy = aTexCoord.xy * _Texture1_ST.xy + _Texture1_ST.zw;
    vs_TEXCOORD0.zw = aTexCoord.xy * _Texture2_ST.xy + _Texture2_ST.zw;
    vs_TEXCOORD1.xy = aTexCoord.xy * _Texture3_ST.xy + _Texture3_ST.zw;
    vs_TEXCOORD1.zw = aTexCoord.xy * _Texture4_ST.xy + _Texture4_ST.zw;
    vs_TEXCOORD2.xy = aTexCoord.xy * _Texture5_ST.xy + _Texture5_ST.zw;
    u_xlat1.x = dot(aNormal.xyz, u_WorldToObject[0].xyz);
    u_xlat1.y = dot(aNormal.xyz, u_WorldToObject[1].xyz);
    u_xlat1.z = dot(aNormal.xyz, u_WorldToObject[2].xyz);
    u_xlat7 = dot(u_xlat1.xyz, u_xlat1.xyz);
    u_xlat7 = inversesqrt(u_xlat7);
    vs_TEXCOORD3.xyz = vec3(u_xlat7) * u_xlat1.xyz;
    u_xlat0.y = u_xlat0.y * u_ProjectionParams.x;
    u_xlat1.xzw = u_xlat0.xwy * vec3(0.5, 0.5, 0.5);
    vs_TEXCOORD7.zw = u_xlat0.zw;
    vs_TEXCOORD7.xy = u_xlat1.zz + u_xlat1.xw;
    vs_TEXCOORD8 = vec4(0.0, 0.0, 0.0, 0.0);
    return;
}
