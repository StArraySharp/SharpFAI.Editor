#version 330 core
#extension GL_ARB_explicit_attrib_location : require
#extension GL_OVR_multiview2 : require

uniform 	vec4 uModel[4];
uniform 	vec4 _MainTex_ST;
uniform 	vec4 _Color;
uniform vec4 u_StereoViewProjection[8];
layout(num_views = 2) in;
layout(location = 0) in vec4 aPosition;
layout(location = 4) in vec4 aVertexColor;
layout(location = 2) in vec2 aTexCoord;
out vec4 vs_COLOR0;
out vec2 vs_TEXCOORD0;
flat out uint vs_BLENDINDICES0;
vec4 u_xlat0;
int u_xlati1;
vec4 u_xlat2;
void main()
{
    u_xlat0 = aPosition.yyyy * uModel[1];
    u_xlat0 = uModel[0] * aPosition.xxxx + u_xlat0;
    u_xlat0 = uModel[2] * aPosition.zzzz + u_xlat0;
    u_xlat0 = u_xlat0 + uModel[3];
    u_xlati1 = int(gl_ViewID_OVR) << (2 & int(0x1F));
    u_xlat2 = u_xlat0.yyyy * u_StereoViewProjection[(u_xlati1 + 1)];
    u_xlat2 = u_StereoViewProjection[u_xlati1] * u_xlat0.xxxx + u_xlat2;
    u_xlat2 = u_StereoViewProjection[(u_xlati1 + 2)] * u_xlat0.zzzz + u_xlat2;
    gl_Position = u_StereoViewProjection[(u_xlati1 + 3)] * u_xlat0.wwww + u_xlat2;
    vs_COLOR0 = aVertexColor * _Color;
    vs_TEXCOORD0.xy = aTexCoord.xy * _MainTex_ST.xy + _MainTex_ST.zw;
    vs_BLENDINDICES0 = gl_ViewID_OVR;
    return;
}
